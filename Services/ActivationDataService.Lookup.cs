using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Sql;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class ActivationDataService
    {
        public async Task<ActivationLookupResult> ResolveContractForActivationAsync(
            string rawInput,
            bool isDemandId,
            string envSuffix,
            CancellationToken token = default)
        {
            var stopwatch = Stopwatch.StartNew();
            string cleanInput = CleanInput(rawInput);
            var result = new ActivationLookupResult
            {
                RawInput = cleanInput,
                IsDemandId = isDemandId,
                DemandId = isDemandId ? cleanInput : "N/A",
                DbDiagnosticStatus = isDemandId
                    ? "DEMAND NOT FOUND"
                    : "CONTRACT NOT FOUND"
            };

            if (string.IsNullOrWhiteSpace(cleanInput))
            {
                stopwatch.Stop();
                result.LookupDurationMs = stopwatch.ElapsedMilliseconds;
                result.DbError = "Input value is empty.";
                return result;
            }

            string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(envSuffix);

            try
            {
                await TryResolveWithModeAsync(
                        cleanInput,
                        isDemandId,
                        normalizedEnvironment,
                        result,
                        token)
                    .ConfigureAwait(false);

                // Bidirectional auto-detection fallback:
                // If not found in ELIA with the initial mode (e.g. 9-digit JCL contract treated as Demand ID,
                // or Demand ID treated as Contract), automatically try the opposite mode on an isolated result
                // so a negative opposite-mode query never wipes out a valid ELIA match from the primary mode.
                if (!result.FoundInElia || IsMissingValue(result.ContractNumber))
                {
                    bool oppositeMode = !isDemandId;
                    var alternateResult = new ActivationLookupResult
                    {
                        RawInput = cleanInput,
                        IsDemandId = oppositeMode,
                        DemandId = oppositeMode ? cleanInput : "N/A",
                        DbDiagnosticStatus = oppositeMode
                            ? "DEMAND NOT FOUND"
                            : "CONTRACT NOT FOUND"
                    };

                    await TryResolveWithModeAsync(
                            cleanInput,
                            oppositeMode,
                            normalizedEnvironment,
                            alternateResult,
                            token)
                        .ConfigureAwait(false);

                    bool shouldAdoptAlternate =
                        alternateResult.FoundInElia &&
                        (!IsMissingValue(alternateResult.ContractNumber) || !result.FoundInElia);

                    if (shouldAdoptAlternate)
                    {
                        result = alternateResult;
                        if (!oppositeMode && string.Equals(result.DemandId, cleanInput, StringComparison.OrdinalIgnoreCase))
                        {
                            result.DemandId = "N/A";
                        }
                    }
                }

                await TryCompleteFromEliaUconSafeAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryEnrichLookupFromResolvedDemandSafeAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteDirectDemandRoutingAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteFromEliaUconSafeAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteContractFundCountSafeAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteLisaInformationAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                if (!result.FoundInElia && !result.IsDemandId && IsMissingValue(result.ContractNumber))
                {
                    result.ContractNumber = cleanInput;
                    result.JclContract = FormatContractStatic(cleanInput);
                }

                NormalizeLookupResult(result);
                DebugLookupResult("Resolved lookup result", result, normalizedEnvironment);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendDatabaseError(result, "Lookup pipeline", exception);
                Debug.WriteLine(
                    "[ActivationLookup] Lookup pipeline failed | " +
                    $"Input={cleanInput} | Environment={normalizedEnvironment} | Error={exception}");
            }
            finally
            {
                stopwatch.Stop();
                result.LookupDurationMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        private async Task TryResolveWithModeAsync(
            string cleanInput,
            bool asDemandId,
            string normalizedEnvironment,
            ActivationLookupResult result,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (asDemandId)
            {
                if (!decimal.TryParse(
                        cleanInput,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out _))
                {
                    return;
                }

                try
                {
                    bool routed = await TryResolveFromPreActivationRoutingAsync(
                            cleanInput,
                            normalizedEnvironment,
                            result,
                            token)
                        .ConfigureAwait(false);

                    if (routed && !IsMissingValue(result.ContractNumber))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendDatabaseError(result, "PreActivation routing", ex);
                    Debug.WriteLine($"[ActivationLookup] PreActivation routing warning: {ex.Message}");
                }
            }

            try
            {
                await ResolveUsingLegacyCombinedQueryAsync(
                        cleanInput,
                        asDemandId,
                        normalizedEnvironment,
                        result,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendDatabaseError(result, asDemandId ? "Combined demand lookup" : "Combined contract lookup", ex);
                Debug.WriteLine($"[ActivationLookup] Combined query warning ({asDemandId}): {ex.Message}");
            }

            if (!result.FoundInElia || IsMissingValue(result.ContractNumber))
            {
                try
                {
                    await ResolveUsingFallbackAsync(
                            cleanInput,
                            asDemandId,
                            normalizedEnvironment,
                            result,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendDatabaseError(result, asDemandId ? "Fallback demand lookup" : "Fallback contract lookup", ex);
                    Debug.WriteLine($"[ActivationLookup] Fallback lookup warning ({asDemandId}): {ex.Message}");
                }
            }
        }

        private async Task TryEnrichLookupFromResolvedDemandSafeAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            try
            {
                await TryEnrichLookupFromResolvedDemandAsync(result, environment, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ActivationLookup] Demand enrichment warning: {ex.Message}");
            }
        }

        private static async Task TryCompleteFromEliaUconSafeAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            if (IsMissingValue(result.EliaUconId) ||
                !decimal.TryParse(
                    result.EliaUconId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out decimal numericUconId))
            {
                return;
            }

            try
            {
                var database = new DatabaseManager(environment);
                var eliaParams = new Dictionary<string, object>
                {
                    ["@EliaId"] = numericUconId
                };

                result.FoundInElia = true;

                if (IsMissingValue(result.ContractNumber) &&
                    SqlQueries.ContainsQuery("FJ1.TB5UCON", environment))
                {
                    using DataTable uconTable = await database
                        .GetDataAsync(SqlQueries.GetQuery("FJ1.TB5UCON", environment), eliaParams, token)
                        .ConfigureAwait(false);
                    foreach (DataRow row in uconTable.Rows)
                    {
                        string? refExn = GetString(row, "IT5UCONLREFEXN");
                        if (!string.IsNullOrWhiteSpace(refExn))
                        {
                            result.ContractNumber = refExn.Trim();
                            result.JclContract = FormatContractStatic(result.ContractNumber);
                            break;
                        }
                    }
                }

                if ((IsMissingValue(result.RawPremium) || result.RawPremium == "0") &&
                    SqlQueries.ContainsQuery("FJ1.TB5UPRP", environment))
                {
                    using DataTable prpTable = await database
                        .GetDataAsync(SqlQueries.GetQuery("FJ1.TB5UPRP", environment), eliaParams, token)
                        .ConfigureAwait(false);
                    if (prpTable.Rows.Count > 0)
                    {
                        DataRow prpRow = prpTable.Rows[0];
                        AssignIfAvailable(prpRow, "IT5UAVENSEQ", v =>
                        {
                            if (IsMissingValue(result.PremiumSequence))
                                result.PremiumSequence = v.Trim();
                        });
                        AssignIfAvailable(prpRow, "IT5UPRPUBRU", v => result.RawPremium = v.Trim());
                        AssignIfAvailable(prpRow, "IT5UPRPUNET", v =>
                        {
                            if (IsMissingValue(result.NetPremium))
                                result.NetPremium = v.Trim();
                        });
                    }
                }

                if (result.FundFeatureCount == 0)
                {
                    int uconFundCount = 0;
                    if (SqlQueries.ContainsQuery("FJ1.TB5UGFF", environment))
                    {
                        using DataTable gffTable = await database
                            .GetDataAsync(SqlQueries.GetQuery("FJ1.TB5UGFF", environment), eliaParams, token)
                            .ConfigureAwait(false);
                        foreach (DataRow row in gffTable.Rows)
                        {
                            if (!string.IsNullOrWhiteSpace(GetString(row, "IT5UGFFCFND")) ||
                                !string.IsNullOrWhiteSpace(GetString(row, "IT5UGFFCFNDDSI")))
                            {
                                uconFundCount++;
                            }
                        }
                    }

                    if (SqlQueries.ContainsQuery("FJ1.TB5UPRF", environment))
                    {
                        using DataTable prfTable = await database
                            .GetDataAsync(SqlQueries.GetQuery("FJ1.TB5UPRF", environment), eliaParams, token)
                            .ConfigureAwait(false);
                        foreach (DataRow row in prfTable.Rows)
                        {
                            if (!string.IsNullOrWhiteSpace(GetString(row, "IT5UPRFCFND")))
                            {
                                uconFundCount++;
                            }
                        }
                    }

                    if (uconFundCount > 0)
                    {
                        result.FundFeatureCount = uconFundCount;
                    }
                }

                if (IsMissingValue(result.ProductCode) ||
                    IsMissingValue(result.ProcessCode) ||
                    IsMissingValue(result.ChannelCode))
                {
                    using DataTable demandTable = await database
                        .GetDataAsync(SqlQueries.GetQuery("GET_ELIA_DEMAND_IDS", environment), eliaParams, token)
                        .ConfigureAwait(false);
                    for (int i = demandTable.Rows.Count - 1; i >= 0; i--)
                    {
                        string? dId = GetString(demandTable.Rows[i], "IT5HDMDAIDN");
                        if (string.IsNullOrWhiteSpace(dId))
                            continue;

                        if (IsMissingValue(result.DemandId))
                        {
                            result.DemandId = dId.Trim();
                        }

                        using DataTable dRoutingTable = await database
                            .GetDataAsync(
                                SqlQueries.GetQuery("GET_ELIA_DEMAND_ROUTING", environment),
                                new Dictionary<string, object> { ["@DemandId"] = dId.Trim() },
                                token)
                            .ConfigureAwait(false);
                        if (dRoutingTable.Rows.Count > 0)
                        {
                            DataRow dRow = dRoutingTable.Rows[0];
                            AssignNormalizedIfMissing(result.ProductCode, GetString(dRow, "ProductCode"), v => result.ProductCode = v);
                            AssignNormalizedIfMissing(result.ProcessCode, GetString(dRow, "ProcessCode"), v => result.ProcessCode = v);
                            AssignNormalizedIfMissing(result.ChannelCode, GetString(dRow, "ChannelCode"), v => result.ChannelCode = v);
                            AssignNormalizedIfMissing(result.ApplicationChannelCode, GetString(dRow, "ApplicationChannelCode"), v => result.ApplicationChannelCode = v);
                            AssignNormalizedIfMissing(result.DemandStatus, GetString(dRow, "DemandStatus"), v => result.DemandStatus = v);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ActivationLookup] UCON completion warning: {ex.Message}");
            }
        }

        private static async Task TryCompleteContractFundCountSafeAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            if (result.FundFeatureCount > 0)
            {
                return;
            }

            try
            {
                var database = new DatabaseManager(environment);

                if (!IsMissingValue(result.DemandId) &&
                    decimal.TryParse(
                        result.DemandId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out decimal numericDemandId))
                {
                    string demandCountQuery = SqlQueries.GetQuery("GET_ELIA_FUND_FEATURE_COUNT_BY_DEMAND", environment);
                    using DataTable demandCountTable = await database
                        .GetDataAsync(
                            demandCountQuery,
                            new Dictionary<string, object> { ["@DemandId"] = numericDemandId },
                            token)
                        .ConfigureAwait(false);
                    if (demandCountTable.Rows.Count > 0 &&
                        demandCountTable.Columns.Contains("FundFeatureCount") &&
                        demandCountTable.Rows[0]["FundFeatureCount"] != DBNull.Value &&
                        int.TryParse(
                            demandCountTable.Rows[0]["FundFeatureCount"]?.ToString(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int demandFundCount) &&
                        demandFundCount > 0)
                    {
                        result.FundFeatureCount = demandFundCount;
                        return;
                    }
                }

                if (IsMissingValue(result.ContractNumber))
                {
                    return;
                }

                var parameters = new Dictionary<string, object>
                {
                    ["@ContractNumber"] = result.ContractNumber
                };

                string gffQuery = SqlQueries.GetQuery("GET_ELIA_FUND_FEATURES_BY_CONTRACT", environment);
                using DataTable gffTable = await database
                    .GetDataAsync(gffQuery, parameters, token)
                    .ConfigureAwait(false);

                int count = gffTable.Rows.Count;

                if (count == 0)
                {
                    string prfQuery = SqlQueries.GetQuery("GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT", environment);
                    using DataTable prfTable = await database
                        .GetDataAsync(prfQuery, parameters, token)
                        .ConfigureAwait(false);
                    count += prfTable.Rows.Count;
                }

                if (count > 0)
                {
                    result.FundFeatureCount = count;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ActivationLookup] Contract fund count warning: {ex.Message}");
            }
        }

        private async Task<bool> TryResolveFromPreActivationRoutingAsync(
            string demandId,
            string environment,
            ActivationLookupResult result,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);

            PreActivationRoutingInfo routing =
                await _preActivationRoutingService
                    .ResolveFromDemandInEnvironmentAsync(demandId, environment, token)
                    .ConfigureAwait(false);

            if (routing.RoutingStatus == PreActivationRoutingStatus.DemandNotFound)
            {
                return false;
            }

            result.FoundInElia = true;
            result.ResolvedEnvironment = routing.Environment;
            result.DemandId = routing.DemandId.ToString(CultureInfo.InvariantCulture);

            if (routing.EliaContractId.HasValue)
            {
                result.EliaUconId = routing.EliaContractId.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                result.ContractNumber = routing.ContractNumber.Trim();
                result.JclContract = FormatContractForJcl(result.ContractNumber);
            }

            result.ProductCode = routing.ProductCode;
            result.ProcessCode = routing.ProcessCode;
            result.ChannelCode = routing.ChannelCode;
            result.ApplicationChannelCode = routing.ApplicationChannelCode;
            result.DemandStatus = routing.DemandStatus;

            if (routing.PremiumSequence.HasValue)
            {
                result.PremiumSequence = routing.PremiumSequence.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                result.RawPremium = routing.GrossPremium.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                result.NetPremium = routing.NetPremium.Value.ToString(CultureInfo.InvariantCulture);
            }

            result.PaymentMethodCode = routing.PaymentMethodCode ?? string.Empty;
            result.PremiumGroupType = routing.PremiumGroupType ?? string.Empty;
            result.FundFeatureCount = routing.FundFeatureCount;
            result.BranchClassification = routing.BranchClassification;
            result.BranchDetectionSource = routing.BranchDetectionSource;
            result.BranchDetectionReason = routing.BranchDetectionReason;
            result.RoutingStatus = routing.RoutingStatus;
            result.RoutingDiagnostic = routing.RoutingDiagnostic;
            result.DbDiagnosticStatus = BuildLookupDiagnosticStatus(routing);

            return true;
        }

        private async Task TryEnrichLookupFromResolvedDemandAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);
            token.ThrowIfCancellationRequested();

            if (IsMissingValue(result.DemandId))
            {
                return;
            }

            string resolvedDemandId = result.DemandId.Trim();

            PreActivationRoutingInfo routing =
                await _preActivationRoutingService
                    .ResolveFromDemandInEnvironmentAsync(
                        resolvedDemandId,
                        environment,
                        token)
                    .ConfigureAwait(false);

            if (routing.RoutingStatus == PreActivationRoutingStatus.DemandNotFound)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Enrichment skipped. " +
                    $"Demand {resolvedDemandId} was not found in {environment}.");
                return;
            }

            ApplyRoutingToLookupResult(result, routing);

            Debug.WriteLine(
                "[PreActivationRouting] Lookup enriched. " +
                $"Demand={result.DemandId}, " +
                $"Contract={result.ContractNumber}, " +
                $"Product={DisplayValue(result.ProductCode)}, " +
                $"Process={DisplayValue(result.ProcessCode)}, " +
                $"Channel={DisplayValue(result.ChannelCode)}, " +
                $"Branch={result.BranchDisplay} ({result.BranchClassification}), " +
                $"Status={result.RoutingStatus}.");
        }

        private static async Task TryCompleteDirectDemandRoutingAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);
            token.ThrowIfCancellationRequested();

            if (IsMissingValue(result.DemandId))
            {
                return;
            }

            string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(environment);
            string demandId = result.DemandId.Trim();

            try
            {
                var database = new DatabaseManager(normalizedEnvironment);
                string query = SqlQueries.GetQuery("GET_ELIA_DEMAND_ROUTING", normalizedEnvironment);

                using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object> { ["@DemandId"] = demandId },
                        token)
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (table.Rows.Count == 0)
                {
                    return;
                }

                DataRow row = table.AsEnumerable()
                    .OrderByDescending(r => HasColumnValue(r, "ProductCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ProcessCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ChannelCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ApplicationChannelCode"))
                    .First();

                AssignNormalizedIfMissing(
                    result.ProductCode,
                    GetString(row, "ProductCode"),
                    value => result.ProductCode = value);
                AssignNormalizedIfMissing(
                    result.ProcessCode,
                    GetString(row, "ProcessCode"),
                    value => result.ProcessCode = value);
                AssignNormalizedIfMissing(
                    result.ChannelCode,
                    GetString(row, "ChannelCode"),
                    value => result.ChannelCode = value);
                AssignNormalizedIfMissing(
                    result.ApplicationChannelCode,
                    GetString(row, "ApplicationChannelCode"),
                    value => result.ApplicationChannelCode = value);
                AssignNormalizedIfMissing(
                    result.DemandStatus,
                    GetString(row, "DemandStatus"),
                    value => result.DemandStatus = value);

                result.FoundInElia = true;

                if (!string.IsNullOrWhiteSpace(result.ProductCode) &&
                    (string.IsNullOrWhiteSpace(result.DbDiagnosticStatus) ||
                     result.DbDiagnosticStatus.Contains("PRODUCT", StringComparison.OrdinalIgnoreCase) ||
                     result.DbDiagnosticStatus.Contains("DEMAND NOT FOUND", StringComparison.OrdinalIgnoreCase)))
                {
                    result.DbDiagnosticStatus = "ELIA DEMAND ROUTING RESOLVED";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendDatabaseError(result, "Direct TB5HDMD routing", exception);
            }
        }

        private static void ApplyRoutingToLookupResult(
            ActivationLookupResult result,
            PreActivationRoutingInfo routing)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(routing);

            if (!string.IsNullOrWhiteSpace(routing.Environment))
                result.ResolvedEnvironment = routing.Environment.Trim();
            if (routing.DemandId > 0)
                result.DemandId = routing.DemandId.ToString(CultureInfo.InvariantCulture);
            if (routing.EliaContractId.HasValue)
                result.EliaUconId = routing.EliaContractId.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                result.ContractNumber = routing.ContractNumber.Trim();
                result.JclContract = FormatContractStatic(result.ContractNumber);
            }
            if (!string.IsNullOrWhiteSpace(routing.ProductCode))
                result.ProductCode = routing.ProductCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.ProcessCode))
                result.ProcessCode = routing.ProcessCode.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(routing.ChannelCode))
                result.ChannelCode = routing.ChannelCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.ApplicationChannelCode))
                result.ApplicationChannelCode = routing.ApplicationChannelCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.DemandStatus))
                result.DemandStatus = routing.DemandStatus.Trim();
            if (routing.PremiumSequence.HasValue)
                result.PremiumSequence = routing.PremiumSequence.Value.ToString(CultureInfo.InvariantCulture);
            if (routing.GrossPremium.HasValue)
                result.RawPremium = routing.GrossPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (routing.NetPremium.HasValue)
                result.NetPremium = routing.NetPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);

            result.PaymentMethodCode = routing.PaymentMethodCode ?? string.Empty;
            result.PremiumGroupType = routing.PremiumGroupType ?? string.Empty;
            result.FundFeatureCount = Math.Max(result.FundFeatureCount, routing.FundFeatureCount);
            if (routing.BranchClassification == ContractBranchClassification.Branch23 ||
                result.BranchClassification != ContractBranchClassification.Branch23)
            {
                result.BranchClassification = routing.BranchClassification;
                result.BranchDetectionSource = routing.BranchDetectionSource;
                result.BranchDetectionReason = routing.BranchDetectionReason;
            }
            result.RoutingStatus = routing.RoutingStatus;
            result.RoutingDiagnostic = routing.RoutingDiagnostic;
            result.DbDiagnosticStatus = BuildLookupDiagnosticStatus(routing);
            result.FoundInElia = true;
        }

        private static string BuildLookupDiagnosticStatus(PreActivationRoutingInfo routing)
        {
            if (routing.RoutingStatus == PreActivationRoutingStatus.Ready)
            {
                return routing.HasPremium ? "OK" : "ELIA FOUND - PREMIUM NOT FOUND";
            }

            return routing.RoutingStatus switch
            {
                PreActivationRoutingStatus.DemandNotFound => "DEMAND NOT FOUND",
                PreActivationRoutingStatus.EliaContractNotLinked => "DEMAND FOUND - ELIA CONTRACT NOT LINKED",
                PreActivationRoutingStatus.ContractNumberNotAvailable => "ELIA FOUND - CONTRACT NUMBER NOT AVAILABLE",
                PreActivationRoutingStatus.ProductNotResolved => "ELIA FOUND - PRODUCT NOT RESOLVED",
                PreActivationRoutingStatus.ProcessNotResolved => "ELIA FOUND - PROCESS NOT RESOLVED",
                PreActivationRoutingStatus.ChannelNotResolved => "ELIA FOUND - CHANNEL NOT RESOLVED",
                PreActivationRoutingStatus.BranchNotResolved => "ELIA FOUND - BRANCH NOT RESOLVED",
                PreActivationRoutingStatus.EnvironmentAmbiguous => "ELIA FOUND - ENVIRONMENT AMBIGUOUS",
                _ => routing.RoutingStatus.ToString()
            };
        }

        private static async Task TryCompleteLisaInformationAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(result.ContractNumber))
            {
                return;
            }

            try
            {
                string lisaContractDatabase = Settings.DbConfig.GetLisaContractDatabase(environment);
                var database = new DatabaseManager(environment, lisaContractDatabase);
                string query = SqlQueries.GetQuery("GET_INTERNAL_ID", environment);

                using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object> { ["@ContractNumber"] = result.ContractNumber },
                        token)
                    .ConfigureAwait(false);

                if (table.Rows.Count == 0)
                {
                    return;
                }

                DataRow row = table.Rows[0];
                if (table.Columns.Contains("NO_CNT") && row["NO_CNT"] != DBNull.Value)
                {
                    result.LisaInternalId = row["NO_CNT"]?.ToString()?.Trim() ?? "N/A";
                }

                result.LisaContractNumber = result.ContractNumber;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"[DB Warning] LISA context could not be completed for {result.ContractNumber}: " +
                    exception.Message);
            }
        }

        private static async Task ResolveUsingLegacyCombinedQueryAsync(
            string cleanInput,
            bool isDemandId,
            string environment,
            ActivationLookupResult result,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(cleanInput))
                throw new ArgumentException("The contract number or Demand ID is required.", nameof(cleanInput));
            if (string.IsNullOrWhiteSpace(environment))
                throw new ArgumentException("The environment is required.", nameof(environment));

            var database = new DatabaseManager(environment);
            string queryKey = isDemandId ? "GET_FULL_CONTEXT_BY_DEMAND" : "GET_FULL_CONTEXT_BY_CONTRACT";
            string parameterName = isDemandId ? "@DemandId" : "@ContractNumber";
            string query = SqlQueries.GetQuery(queryKey, environment);

            using DataTable table = await database.GetDataAsync(
                    query,
                    new Dictionary<string, object> { [parameterName] = cleanInput },
                    token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (table.Rows.Count == 0)
            {
                if (!result.FoundInElia)
                {
                    result.FoundInElia = false;
                    result.DbDiagnosticStatus = isDemandId ? "DEMAND NOT FOUND" : "CONTRACT NOT FOUND";
                }
                return;
            }

            DataRow row = table.Rows[0];
            result.FoundInElia = true;

            string? contractNumber = GetString(row, "ContractNumber");
            if (!string.IsNullOrWhiteSpace(contractNumber))
            {
                result.ContractNumber = contractNumber.Trim();
            }
            else if (!isDemandId && IsMissingValue(result.ContractNumber))
            {
                result.ContractNumber = cleanInput;
            }

            if (!string.IsNullOrWhiteSpace(result.ContractNumber))
            {
                result.JclContract = FormatContractStatic(result.ContractNumber);
            }

            AssignIfAvailable(row, "UconId", value => result.EliaUconId = value.Trim());
            AssignIfAvailable(row, "DemandId", value => result.DemandId = value.Trim());
            AssignIfAvailable(row, "ProductCode", value => result.ProductCode = value.Trim().ToUpperInvariant());
            AssignIfAvailable(row, "ProcessCode", value => result.ProcessCode = value.Trim().ToUpperInvariant());
            AssignIfAvailable(row, "ChannelCode", value => result.ChannelCode = value.Trim().ToUpperInvariant());
            AssignIfAvailable(row, "ApplicationChannelCode", value => result.ApplicationChannelCode = value.Trim().ToUpperInvariant());
            AssignIfAvailable(row, "DemandStatus", value => result.DemandStatus = value.Trim().ToUpperInvariant());
            AssignIfAvailable(row, "InternalId", value => result.LisaInternalId = value.Trim());
            AssignIfAvailable(row, "LisaContractNumber", value => result.LisaContractNumber = value.Trim());
            AssignIfAvailable(row, "PremiumSequence", value => result.PremiumSequence = value.Trim());
            AssignIfAvailable(row, "GrossPremium", value => result.RawPremium = value.Trim());
            AssignIfAvailable(row, "NetPremium", value => result.NetPremium = value.Trim());
            AssignIfAvailable(row, "DiagnosticStatus", value => result.DbDiagnosticStatus = value.Trim());

            if (row.Table.Columns.Contains("FundFeatureCount") &&
                row["FundFeatureCount"] != DBNull.Value &&
                int.TryParse(
                    row["FundFeatureCount"]?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int fundCount))
            {
                result.FundFeatureCount = Math.Max(result.FundFeatureCount, fundCount);
            }

            if (string.IsNullOrWhiteSpace(result.DbDiagnosticStatus))
            {
                result.DbDiagnosticStatus = !string.IsNullOrWhiteSpace(result.ContractNumber)
                    ? "ELIA CONTEXT RESOLVED"
                    : isDemandId ? "DEMAND NOT FOUND" : "CONTRACT NOT FOUND";
            }
        }

        private async Task ResolveUsingFallbackAsync(
            string cleanInput,
            bool isDemandId,
            string environment,
            ActivationLookupResult result,
            CancellationToken token)
        {
            string? resolvedContract = null;
            var database = new DatabaseManager(environment);

            if (isDemandId)
            {
                resolvedContract = await GetContractFromDemandAsync(
                        cleanInput,
                        environment,
                        token)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resolvedContract))
                {
                    result.DemandId = cleanInput;
                }
            }
            else
            {
                using DataTable uconTable = await database.GetDataAsync(
                        SqlQueries.GetQuery("GET_ELIA_ID", environment),
                        new Dictionary<string, object> { ["@ContractNumber"] = cleanInput },
                        token)
                    .ConfigureAwait(false);

                if (uconTable.Rows.Count > 0)
                {
                    DataRow uconRow = uconTable.Rows[0];
                    string? uconId = GetString(uconRow, "IT5UCONAIDN");
                    if (!string.IsNullOrWhiteSpace(uconId))
                    {
                        result.EliaUconId = uconId.Trim();
                    }

                    string? dbContractNumber = GetString(uconRow, "ContractNumber");
                    resolvedContract = !string.IsNullOrWhiteSpace(dbContractNumber)
                        ? dbContractNumber.Trim()
                        : cleanInput;
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedContract))
            {
                return;
            }

            result.FoundInElia = true;
            result.ContractNumber = resolvedContract;
            result.JclContract = FormatContractForJcl(resolvedContract);
            result.RawPremium = await FetchPremiumAsync(
                    resolvedContract,
                    environment,
                    token)
                .ConfigureAwait(false);

            result.DbDiagnosticStatus = result.RawPremium != "0"
                ? "OK (FALLBACK)"
                : "CONTRACT FOUND - PREMIUM NOT FOUND";

            if (IsMissingValue(result.EliaUconId) || IsMissingValue(result.DemandId))
            {
                try
                {
                    if (IsMissingValue(result.EliaUconId))
                    {
                        using DataTable uconTable = await database.GetDataAsync(
                                SqlQueries.GetQuery("GET_ELIA_ID", environment),
                                new Dictionary<string, object> { ["@ContractNumber"] = resolvedContract },
                                token)
                            .ConfigureAwait(false);
                        if (uconTable.Rows.Count > 0)
                        {
                            string? uconId = GetString(uconTable.Rows[0], "IT5UCONAIDN");
                            if (!string.IsNullOrWhiteSpace(uconId))
                            {
                                result.EliaUconId = uconId.Trim();
                            }
                        }
                    }

                    if (IsMissingValue(result.DemandId) &&
                        !IsMissingValue(result.EliaUconId) &&
                        decimal.TryParse(
                            result.EliaUconId,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out decimal numericUconId))
                    {
                        using DataTable demandTable = await database.GetDataAsync(
                                SqlQueries.GetQuery("GET_ELIA_DEMAND_IDS", environment),
                                new Dictionary<string, object> { ["@EliaId"] = numericUconId },
                                token)
                            .ConfigureAwait(false);
                        if (demandTable.Rows.Count > 0)
                        {
                            DataRow lastRow = demandTable.Rows[demandTable.Rows.Count - 1];
                            string? demandId = GetString(lastRow, "IT5HDMDAIDN");
                            if (!string.IsNullOrWhiteSpace(demandId))
                            {
                                result.DemandId = demandId.Trim();
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[ActivationLookup] Fallback demand resolution warning: {ex.Message}");
                }
            }

            await TryCompleteDirectDemandRoutingAsync(result, environment, token)
                .ConfigureAwait(false);
        }

        public async Task<string?> GetContractFromDemandAsync(
            string demandId,
            string envSuffix,
            CancellationToken token = default)
        {
            string cleanDemandId = CleanInput(demandId);
            if (string.IsNullOrWhiteSpace(cleanDemandId))
            {
                return null;
            }

            if (!decimal.TryParse(
                    cleanDemandId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out decimal numericDemandId))
            {
                return null;
            }

            string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(envSuffix);
            var database = new DatabaseManager(normalizedEnvironment);
            string query = SqlQueries.GetQuery("GET_CONTRACT_BY_DEMAND", normalizedEnvironment);

            try
            {
                using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object> { ["@DemandId"] = numericDemandId },
                        token)
                    .ConfigureAwait(false);

                if (table.Rows.Count == 0)
                {
                    return null;
                }

                DataRow row = table.Rows[0];
                string? contractNumber = GetString(row, "ContractNumber")
                    ?? GetString(row, "IT5UCONLREFEXN");

                if (string.IsNullOrWhiteSpace(contractNumber))
                {
                    return null;
                }

                return contractNumber.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve the ELIA contract for Demand ID '{numericDemandId}' in environment '{normalizedEnvironment}'. SQL error: {exception.Message}",
                    exception);
            }
        }

        public async Task<string> FetchPremiumAsync(
            string contract,
            string envSuffix,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(contract))
            {
                return "0";
            }

            try
            {
                string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(envSuffix);
                var database = new DatabaseManager(normalizedEnvironment);
                ReadOnlyDictionary<string, string> environmentQueries =
                    SqlQueries.GetQueries(normalizedEnvironment);

                using DataTable eliaTable = await database.GetDataAsync(
                        environmentQueries["GET_ELIA_ID"],
                        new Dictionary<string, object> { ["@ContractNumber"] = CleanInput(contract) },
                        token)
                    .ConfigureAwait(false);

                if (eliaTable.Rows.Count == 0)
                {
                    return "0";
                }

                string? eliaUconId = eliaTable.Rows[0]["IT5UCONAIDN"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(eliaUconId) ||
                    !environmentQueries.TryGetValue("FJ1.TB5UPRP", out string? premiumQuery))
                {
                    return "0";
                }

                using DataTable premiumTable = await database.GetDataAsync(
                        premiumQuery,
                        new Dictionary<string, object> { ["@EliaId"] = eliaUconId },
                        token)
                    .ConfigureAwait(false);

                if (premiumTable.Rows.Count == 0 || !premiumTable.Columns.Contains("IT5UPRPUBRU"))
                {
                    return "0";
                }

                return premiumTable.Rows[0]["IT5UPRPUBRU"]?.ToString()?.Trim() ?? "0";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[DB Error] FetchPremiumAsync failed for {contract}: {exception.Message}");
                return "0";
            }
        }

        private static bool HasColumnValue(DataRow row, string columnName) =>
            !string.IsNullOrWhiteSpace(GetString(row, columnName));

        private static void AssignNormalizedIfMissing(
            string? currentValue,
            string? sourceValue,
            Action<string> assign)
        {
            ArgumentNullException.ThrowIfNull(assign);
            if (!string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(sourceValue))
            {
                return;
            }

            assign(sourceValue.Trim().ToUpperInvariant());
        }

        private static void NormalizeLookupResult(ActivationLookupResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            result.ProductCode = NormalizeCode(result.ProductCode);
            result.ProcessCode = NormalizeCode(result.ProcessCode);
            result.ChannelCode = NormalizeCode(result.ChannelCode);
            result.ApplicationChannelCode = NormalizeCode(result.ApplicationChannelCode);
            result.DemandStatus = NormalizeCode(result.DemandStatus);

            if (!IsMissingValue(result.ContractNumber))
            {
                result.ContractNumber = result.ContractNumber.Trim();
                if (IsMissingValue(result.JclContract))
                {
                    result.JclContract = FormatContractStatic(result.ContractNumber);
                }
            }

            // Classification Branche 21 / Branche 23 automatique
            if (Settings.PreActivationRouting.IsKnownBranch23Product(result.ProductCode))
            {
                result.BranchClassification = ContractBranchClassification.Branch23;
                result.BranchDetectionSource = BranchDetectionSource.EliaProductCode;
                result.BranchDetectionReason =
                    $"ELIA ProductCode {result.ProductCode} is configured as Branche 23.";
            }
            else if (result.FundFeatureCount > 0)
            {
                result.BranchClassification = ContractBranchClassification.Branch23;
                result.BranchDetectionSource = BranchDetectionSource.EliaFundFeatures;
                result.BranchDetectionReason =
                    $"ELIA contract has {result.FundFeatureCount} fund feature(s) in TB5UGFF/TB5UPRF -> Branche 23.";
            }
            else if (result.BranchClassification == ContractBranchClassification.Unknown &&
                     (!string.IsNullOrWhiteSpace(result.ProductCode) || result.FoundInElia))
            {
                result.BranchClassification = ContractBranchClassification.Standard;
                result.BranchDetectionSource = BranchDetectionSource.EliaProductCode;
                result.BranchDetectionReason =
                    $"ELIA ProductCode {DisplayValue(result.ProductCode)} with 0 fund features -> Branche 21.";
            }

            // Enrichissement automatique du canal de paiement (BANK / BROKER_NEW_GEN)
            string? candidateChannel = !string.IsNullOrWhiteSpace(result.ChannelCode)
                ? result.ChannelCode
                : result.ProcessCode;

            if (PaymentRoute.TryFromChannel(candidateChannel, out PaymentRoute? route) && route != null)
            {
                result.PaymentChannel = route.BusinessChannel == PreActivationBusinessChannel.Bank
                    ? ActivationPaymentChannel.Bank
                    : ActivationPaymentChannel.BrokerNewGeneration;
                if (string.IsNullOrWhiteSpace(result.BusinessChannel))
                {
                    result.BusinessChannel = route.BusinessChannelName;
                }
                if (string.IsNullOrWhiteSpace(result.Bucp))
                {
                    result.Bucp = route.Bucp;
                }
                if (string.IsNullOrWhiteSpace(result.Cmdpmt))
                {
                    result.Cmdpmt = route.Cmdpmt;
                }
                if (string.IsNullOrWhiteSpace(result.RoutingRule))
                {
                    result.RoutingRule = route.RuleDescription;
                }
            }
        }

        private static string NormalizeCode(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        private static void AppendDatabaseError(
            ActivationLookupResult result,
            string stage,
            Exception exception)
        {
            string error = $"{stage}: {exception.GetType().Name}: {exception.Message}";
            result.DbError = string.IsNullOrWhiteSpace(result.DbError)
                ? error
                : result.DbError + " | " + error;
        }

        private static void DebugLookupResult(
            string stage,
            ActivationLookupResult result,
            string environment)
        {
            Debug.WriteLine(
                "[ActivationLookup] " + stage + " | " +
                $"Environment={environment} | " +
                $"Demand={DisplayValue(result.DemandId)} | " +
                $"UCON={DisplayValue(result.EliaUconId)} | " +
                $"Contract={DisplayValue(result.ContractNumber)} | " +
                $"Product={DisplayValue(result.ProductCode)} | " +
                $"Process={DisplayValue(result.ProcessCode)} | " +
                $"Channel={DisplayValue(result.ChannelCode)} | " +
                $"Branch={result.BranchDisplay} ({result.BranchClassification}) | " +
                $"Premium={DisplayValue(result.RawPremium)} | " +
                $"Diagnostic={DisplayValue(result.DbDiagnosticStatus)}");
        }

        private static string FormatDataRow(DataRow row)
        {
            return string.Join(
                " | ",
                row.Table.Columns.Cast<DataColumn>()
                    .Select(column => $"{column.ColumnName}={FormatSqlValue(row[column])}"));
        }

        private static string FormatSqlValue(object? value)
        {
            if (value == null || value == DBNull.Value)
                return "<NULL>";
            string text = value.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(text) ? "<EMPTY>" : text.Trim();
        }

        private static string CleanInput(string? value)
        {
            string cleaned = (value ?? string.Empty)
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();

            if (cleaned.Contains(',') || cleaned.Contains(';'))
            {
                string[] parts = cleaned.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    cleaned = parts[parts.Length - 1].Trim();
                }
            }

            return cleaned;
        }

        private static string? GetString(DataRow row, string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
                return null;
            return row[columnName]?.ToString()?.Trim();
        }

        private static void AssignIfAvailable(
            DataRow row,
            string columnName,
            Action<string> assign)
        {
            string? value = GetString(row, columnName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(value);
            }
        }

        private static string FormatContractStatic(string? rawContract)
        {
            if (string.IsNullOrWhiteSpace(rawContract))
                return string.Empty;

            string cleaned = rawContract
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();

            return cleaned.Length == 12 ? cleaned.Substring(1, 9) : cleaned;
        }

        private static string NormalizeShortEnvironment(string? environment)
        {
            string value = (environment ?? string.Empty).Trim().ToUpperInvariant();
            if (value.StartsWith("D", StringComparison.Ordinal)) return "D";
            if (value.StartsWith("Q", StringComparison.Ordinal)) return "Q";
            if (value.StartsWith("A", StringComparison.Ordinal)) return "A";
            if (value.StartsWith("P", StringComparison.Ordinal)) return "P";

            throw new ArgumentException($"Unsupported environment: '{environment}'.", nameof(environment));
        }

        private static string DisplayValue(string? value) =>
            IsMissingValue(value) ? "-" : value!.Trim();

        private static bool IsMissingValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value.Trim(), "N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "Not found", StringComparison.OrdinalIgnoreCase);
        }
    }
}
