using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Sql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Résout le contexte nécessaire à l'activation d'un contrat directement depuis les données ELIA.
    /// Découpé en classes partielles :
    /// - PreActivationRoutingService.cs : API publique et résolution principale du contexte ELIA ;
    /// - PreActivationRoutingService.Helpers.cs : Résolution des fonds (TB5UGFF/TB5UPRF), classification Branche 21/23 et helpers SQL.
    /// </summary>
    public sealed partial class PreActivationRoutingService
    {
        public async Task<PreActivationRoutingInfo> ResolveFromDemandAsync(
            string rawDemandId,
            CancellationToken cancellationToken = default)
        {
            decimal demandId = ParseDemandId(rawDemandId);
            return await ResolveFromDemandAsync(demandId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PreActivationRoutingInfo> ResolveFromDemandAsync(
            decimal demandId,
            CancellationToken cancellationToken = default)
        {
            ValidateDemandId(demandId);
            cancellationToken.ThrowIfCancellationRequested();

            EnvironmentSearchResult environmentSearch =
                await FindEnvironmentCandidatesAsync(demandId, cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (environmentSearch.Candidates.Count == 0)
            {
                return BuildNotFoundResult(demandId, environmentSearch.LookupErrors);
            }

            if (environmentSearch.Candidates.Count > 1)
            {
                return BuildAmbiguousEnvironmentResult(demandId, environmentSearch.Candidates);
            }

            EnvironmentRoutingCandidate candidate = environmentSearch.Candidates[0];
            return await BuildRoutingInfoAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PreActivationRoutingInfo> ResolveFromDemandInEnvironmentAsync(
            string rawDemandId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            decimal demandId = ParseDemandId(rawDemandId);
            return await ResolveFromDemandInEnvironmentAsync(demandId, environment, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PreActivationRoutingInfo> ResolveFromDemandInEnvironmentAsync(
            decimal demandId,
            string environment,
            CancellationToken cancellationToken = default)
        {
            ValidateDemandId(demandId);
            string normalizedEnvironment = Settings.DbConfig.NormalizeEnvironment(environment);
            cancellationToken.ThrowIfCancellationRequested();

            EnvironmentRoutingCandidate? candidate =
                await TryLoadRoutingCandidateAsync(demandId, normalizedEnvironment, cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (candidate == null)
            {
                return BuildNotFoundInEnvironmentResult(demandId, normalizedEnvironment);
            }

            return await BuildRoutingInfoAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
        }

        public static bool IsSupportedActivationProcess(string? processCode)
        {
            return Settings.PreActivationRouting.IsSupportedActivationProcess(processCode);
        }

        public static bool IsKnownBranch23Product(string? productCode)
        {
            return Settings.PreActivationRouting.IsKnownBranch23Product(productCode);
        }

        private static async Task<PreActivationRoutingInfo> BuildRoutingInfoAsync(
            EnvironmentRoutingCandidate candidate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            cancellationToken.ThrowIfCancellationRequested();

            FundFeatureLoadResult fundFeatureLoadResult =
                await TryGetFundFeaturesAsync(candidate.DemandId, candidate.Environment, cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            BranchResolution branchResolution =
                ResolveBranchClassification(candidate.ProductCode, fundFeatureLoadResult.Features);

            PreActivationRoutingStatus routingStatus =
                ResolveRoutingStatus(candidate, branchResolution);

            string routingDiagnostic =
                BuildRoutingDiagnostic(candidate, branchResolution, routingStatus, fundFeatureLoadResult);

            PreActivationRoutingInfo result = MapToRoutingInfo(
                candidate,
                fundFeatureLoadResult.Features,
                branchResolution,
                routingStatus,
                routingDiagnostic);

            Debug.WriteLine("[PreActivationRouting] Final routing result | " + result.ToDiagnosticSummary());
            return result;
        }

        private static PreActivationRoutingInfo MapToRoutingInfo(
            EnvironmentRoutingCandidate candidate,
            IReadOnlyList<EliaFundFeature> fundFeatures,
            BranchResolution branchResolution,
            PreActivationRoutingStatus routingStatus,
            string routingDiagnostic)
        {
            return new PreActivationRoutingInfo
            {
                Environment = Settings.NormalizeShortEnvironment(candidate.Environment),
                DemandId = candidate.DemandId,
                DemandCreationDate = candidate.DemandCreationDate,
                ProductCode = NormalizeCode(candidate.ProductCode),
                ProcessCode = NormalizeCode(candidate.ProcessCode),
                ChannelCode = NormalizeCode(candidate.ChannelCode),
                ApplicationChannelCode = NormalizeCode(candidate.ApplicationChannelCode),
                DemandStatus = NormalizeCode(candidate.DemandStatus),
                ReferenceDemandId = candidate.ReferenceDemandId,
                LinkedDemandId = candidate.LinkedDemandId,
                AddendumSequence = candidate.AddendumSequence,
                LastEliaUpdate = candidate.LastEliaUpdate,
                EliaContractId = candidate.EliaContractId,
                ContractNumber = NullIfWhiteSpace(candidate.ContractNumber),
                PremiumSequence = candidate.PremiumSequence,
                GrossPremium = candidate.GrossPremium,
                NetPremium = candidate.NetPremium,
                FirstPaymentDate = candidate.FirstPaymentDate,
                PaymentDate = candidate.PaymentDate,
                ContractStartDate = candidate.ContractStartDate,
                PaymentMethodCode = NullIfWhiteSpace(candidate.PaymentMethodCode),
                PremiumGroupType = NullIfWhiteSpace(candidate.PremiumGroupType),
                FundFeatures = fundFeatures ?? Array.Empty<EliaFundFeature>(),
                BranchClassification = branchResolution.Classification,
                BranchDetectionSource = branchResolution.Source,
                BranchDetectionReason = branchResolution.Reason,
                BusinessChannel = PreActivationRoutingInfo.ResolveBusinessChannel(candidate.ChannelCode),
                RoutingStatus = routingStatus,
                RoutingDiagnostic = routingDiagnostic
            };
        }

        private async Task<EnvironmentSearchResult> FindEnvironmentCandidatesAsync(
            decimal demandId,
            CancellationToken cancellationToken)
        {
            Task<EnvironmentLookupResult>[] lookupTasks =
                Settings.PreActivationRouting.SupportedEnvironments
                    .Select(environment => TryLoadEnvironmentAsync(demandId, environment, cancellationToken))
                    .ToArray();

            EnvironmentLookupResult[] results = await Task.WhenAll(lookupTasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            List<EnvironmentRoutingCandidate> candidates = results
                .Where(result => result.Candidate != null)
                .Select(result => result.Candidate!)
                .ToList();

            List<string> errors = results
                .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                .Select(result => $"{result.Environment}: {result.Error}")
                .ToList();

            foreach (string error in errors)
            {
                Debug.WriteLine("[PreActivationRouting] Environment lookup error | " + error);
            }

            return new EnvironmentSearchResult
            {
                Candidates = candidates,
                LookupErrors = errors
            };
        }

        private async Task<EnvironmentLookupResult> TryLoadEnvironmentAsync(
            decimal demandId,
            string environment,
            CancellationToken cancellationToken)
        {
            try
            {
                EnvironmentRoutingCandidate? candidate =
                    await TryLoadRoutingCandidateAsync(demandId, environment, cancellationToken)
                        .ConfigureAwait(false);

                return new EnvironmentLookupResult
                {
                    Environment = environment,
                    Candidate = candidate
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Environment lookup failed | " +
                    $"Demand={demandId} | Environment={environment} | Error={exception}");

                return new EnvironmentLookupResult
                {
                    Environment = environment,
                    Error = $"{exception.GetType().Name}: {exception.Message}"
                };
            }
        }

        private static async Task<EnvironmentRoutingCandidate?> TryLoadRoutingCandidateAsync(
            decimal demandId,
            string environment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment = Settings.DbConfig.NormalizeEnvironment(environment);
            var database = new DatabaseManager(normalizedEnvironment);
            string query = SqlQueries.GetQuery("GET_PRE_ACTIVATION_ROUTING", normalizedEnvironment);

            var parameters = new Dictionary<string, object>
            {
                ["@DemandId"] = demandId
            };

            using DataTable table = await database
                .GetDataAsync(query, parameters, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine(
                "[PreActivationRouting] Main SQL completed | " +
                $"Demand={demandId} | Environment={normalizedEnvironment} | " +
                $"Rows={table.Rows.Count} | Columns={GetColumnNames(table)}");

            if (table.Rows.Count == 0)
            {
                return null;
            }

            DataRow row = SelectBestRoutingRow(table);

            var candidate = new EnvironmentRoutingCandidate
            {
                Environment = normalizedEnvironment,
                DemandId = GetDecimal(row, "DemandId") ?? demandId,
                DemandCreationDate = GetDateTime(row, "DemandCreationDate"),
                ProductCode = GetString(row, "ProductCode"),
                ProcessCode = GetString(row, "ProcessCode"),
                ChannelCode = GetString(row, "ChannelCode"),
                ApplicationChannelCode = GetString(row, "ApplicationChannelCode"),
                DemandStatus = GetString(row, "DemandStatus"),
                EliaContractId = GetDecimal(row, "EliaContractId"),
                ContractNumber = GetString(row, "ContractNumber"),
                PremiumSequence = GetInt32(row, "PremiumSequence"),
                GrossPremium = GetDecimal(row, "GrossPremium"),
                NetPremium = GetDecimal(row, "NetPremium"),
                FirstPaymentDate = GetDateTime(row, "FirstPaymentDate"),
                PaymentDate = GetDateTime(row, "PaymentDate"),
                ContractStartDate = GetDateTime(row, "ContractStartDate"),
                PaymentMethodCode = GetString(row, "PaymentMethodCode"),
                PremiumGroupType = GetString(row, "PremiumGroupType"),
                ReferenceDemandId = GetDecimal(row, "ReferenceDemandId"),
                LinkedDemandId = GetDecimal(row, "LinkedDemandId"),
                AddendumSequence = GetInt32(row, "AddendumSequence"),
                LastEliaUpdate = GetDateTime(row, "LastEliaUpdate"),
                RawRoutingStatus = GetString(row, "RoutingStatus"),
                MainQuerySucceeded = true,
                DemandRoutingFallbackUsed = false
            };

            EnvironmentRoutingCandidate completedCandidate =
                await CompleteMissingDemandRoutingAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);

            return completedCandidate;
        }

        private static async Task<EnvironmentRoutingCandidate> CompleteMissingDemandRoutingAsync(
            EnvironmentRoutingCandidate candidate,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            bool hasProduct = !string.IsNullOrWhiteSpace(candidate.ProductCode);
            bool hasProcess = !string.IsNullOrWhiteSpace(candidate.ProcessCode);
            bool hasChannel = !string.IsNullOrWhiteSpace(candidate.ChannelCode);
            bool hasApplicationChannel = !string.IsNullOrWhiteSpace(candidate.ApplicationChannelCode);
            bool hasDemandStatus = !string.IsNullOrWhiteSpace(candidate.DemandStatus);

            if (hasProduct && hasProcess && hasChannel && hasApplicationChannel && hasDemandStatus)
            {
                return candidate;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment = Settings.DbConfig.NormalizeEnvironment(candidate.Environment);
            var database = new DatabaseManager(normalizedEnvironment);
            string query = SqlQueries.GetQuery("GET_ELIA_DEMAND_ROUTING", normalizedEnvironment);

            using DataTable table = await database
                .GetDataAsync(
                    query,
                    new Dictionary<string, object> { ["@DemandId"] = candidate.DemandId },
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (table.Rows.Count == 0)
            {
                return candidate;
            }

            DataRow bestRow = SelectBestDemandRoutingRow(table);

            string fallbackProductCode = GetString(bestRow, "ProductCode");
            string fallbackProcessCode = GetString(bestRow, "ProcessCode");
            string fallbackChannelCode = GetString(bestRow, "ChannelCode");
            string fallbackApplicationChannelCode = GetString(bestRow, "ApplicationChannelCode");
            string fallbackDemandStatus = GetString(bestRow, "DemandStatus");

            return new EnvironmentRoutingCandidate
            {
                Environment = candidate.Environment,
                DemandId = candidate.DemandId,
                DemandCreationDate = candidate.DemandCreationDate,
                ProductCode = FirstNotEmpty(candidate.ProductCode, fallbackProductCode),
                ProcessCode = FirstNotEmpty(candidate.ProcessCode, fallbackProcessCode),
                ChannelCode = FirstNotEmpty(candidate.ChannelCode, fallbackChannelCode),
                ApplicationChannelCode = FirstNotEmpty(candidate.ApplicationChannelCode, fallbackApplicationChannelCode),
                DemandStatus = FirstNotEmpty(candidate.DemandStatus, fallbackDemandStatus),
                ReferenceDemandId = candidate.ReferenceDemandId,
                LinkedDemandId = candidate.LinkedDemandId,
                AddendumSequence = candidate.AddendumSequence,
                LastEliaUpdate = candidate.LastEliaUpdate,
                EliaContractId = candidate.EliaContractId,
                ContractNumber = candidate.ContractNumber,
                PremiumSequence = candidate.PremiumSequence,
                GrossPremium = candidate.GrossPremium,
                NetPremium = candidate.NetPremium,
                FirstPaymentDate = candidate.FirstPaymentDate,
                PaymentDate = candidate.PaymentDate,
                ContractStartDate = candidate.ContractStartDate,
                PaymentMethodCode = candidate.PaymentMethodCode,
                PremiumGroupType = candidate.PremiumGroupType,
                RawRoutingStatus = candidate.RawRoutingStatus,
                MainQuerySucceeded = candidate.MainQuerySucceeded,
                DemandRoutingFallbackUsed = true
            };
        }

        private static DataRow SelectBestRoutingRow(DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (table.Rows.Count == 0)
            {
                throw new InvalidOperationException("No routing row is available.");
            }

            return table
                .AsEnumerable()
                .OrderByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ProductCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ProcessCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ChannelCode")))
                .ThenByDescending(row => GetDecimal(row, "EliaContractId").HasValue)
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ContractNumber")))
                .ThenByDescending(row => GetInt32(row, "PremiumSequence") ?? int.MinValue)
                .ThenByDescending(row => GetDateTime(row, "LastEliaUpdate") ?? DateTime.MinValue)
                .First();
        }

        private static DataRow SelectBestDemandRoutingRow(DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (table.Rows.Count == 0)
            {
                throw new InvalidOperationException("No Demand routing row is available.");
            }

            return table
                .AsEnumerable()
                .OrderByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ProductCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ProcessCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ChannelCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "ApplicationChannelCode")))
                .ThenByDescending(row => !string.IsNullOrWhiteSpace(GetString(row, "DemandStatus")))
                .First();
        }
    }
}
