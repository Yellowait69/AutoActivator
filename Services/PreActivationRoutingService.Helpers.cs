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
    public sealed partial class PreActivationRoutingService
    {
        private static async Task<FundFeatureLoadResult> TryGetFundFeaturesAsync(
            decimal demandId,
            string environment,
            CancellationToken cancellationToken)
        {
            try
            {
                IReadOnlyList<EliaFundFeature> features =
                    await GetFundFeaturesAsync(demandId, environment, cancellationToken)
                        .ConfigureAwait(false);

                return new FundFeatureLoadResult
                {
                    Features = features
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Unable to load ELIA fund features | " +
                    $"Demand={demandId} | Environment={environment} | Error={exception}");

                return new FundFeatureLoadResult
                {
                    Features = Array.Empty<EliaFundFeature>(),
                    Error = $"{exception.GetType().Name}: {exception.Message}"
                };
            }
        }

        private static async Task<IReadOnlyList<EliaFundFeature>> GetFundFeaturesAsync(
            decimal demandId,
            string environment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment = Settings.DbConfig.NormalizeEnvironment(environment);
            var database = new DatabaseManager(normalizedEnvironment);
            string query = SqlQueries.GetQuery("GET_ELIA_FUND_FEATURES_BY_DEMAND", normalizedEnvironment);

            var parameters = new Dictionary<string, object>
            {
                ["@DemandId"] = demandId
            };

            using DataTable table = await database
                .GetDataAsync(query, parameters, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var features = new List<EliaFundFeature>();

            foreach (DataRow row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var feature = new EliaFundFeature
                {
                    AddendumSequence = GetInt32(row, "AddendumSequence"),
                    GuaranteeCode = NormalizeCode(GetString(row, "GuaranteeCode")),
                    FundSequence = GetInt32(row, "FundSequence"),
                    FundCode = NormalizeNullableCode(GetString(row, "FundCode")),
                    SecondaryFundCode = NormalizeNullableCode(GetString(row, "SecondaryFundCode")),
                    FundActivationDate = GetDateTime(row, "FundActivationDate"),
                    FundEndDate = GetDateTime(row, "FundEndDate"),
                    FundPercentage = GetDecimal(row, "FundPercentage"),
                    FundIndicator = NormalizeNullableCode(GetString(row, "FundIndicator"))
                };

                if (feature.HasFundCode)
                {
                    features.Add(feature);
                }
            }

            // Also check TB5UPRF (premium funds) if TB5UGFF had no rows or additional funds
            try
            {
                string prfQuery = SqlQueries.GetQuery("GET_ELIA_PREMIUM_FUNDS_BY_DEMAND", normalizedEnvironment);
                using DataTable prfTable = await database
                    .GetDataAsync(prfQuery, parameters, cancellationToken)
                    .ConfigureAwait(false);

                foreach (DataRow row in prfTable.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? fundCode = NormalizeNullableCode(GetString(row, "FundCode"));
                    if (string.IsNullOrWhiteSpace(fundCode))
                        continue;

                    features.Add(new EliaFundFeature
                    {
                        AddendumSequence = GetInt32(row, "AddendumSequence"),
                        GuaranteeCode = NormalizeCode(GetString(row, "GuaranteeCode")),
                        FundCode = fundCode,
                        SecondaryFundCode = NormalizeNullableCode(GetString(row, "SecondaryFundCode")),
                        FundPercentage = GetDecimal(row, "FundProportionRaw")
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PreActivationRouting] TB5UPRF fund lookup warning: {ex.Message}");
            }

            if (features.Count == 0)
            {
                return Array.Empty<EliaFundFeature>();
            }

            return features
                .GroupBy(feature => new
                {
                    feature.AddendumSequence,
                    GuaranteeCode = NormalizeCode(feature.GuaranteeCode),
                    FundCode = NormalizeCode(feature.FundCode),
                    SecondaryFundCode = NormalizeCode(feature.SecondaryFundCode)
                })
                .Select(group => group.First())
                .OrderByDescending(feature => feature.AddendumSequence ?? int.MinValue)
                .ThenBy(feature => feature.GuaranteeCode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(feature => feature.FundSequence ?? int.MinValue)
                .ToList();
        }

        private static BranchResolution ResolveBranchClassification(
            string? productCode,
            IReadOnlyList<EliaFundFeature> fundFeatures)
        {
            string normalizedProduct = NormalizeCode(productCode);
            IReadOnlyList<EliaFundFeature> safeFundFeatures =
                fundFeatures ?? Array.Empty<EliaFundFeature>();

            if (Settings.PreActivationRouting.IsKnownBranch23Product(normalizedProduct))
            {
                return new BranchResolution
                {
                    Classification = ContractBranchClassification.Branch23,
                    Source = BranchDetectionSource.EliaProductCode,
                    Reason = $"ELIA ProductCode {normalizedProduct} is configured as Branch 23."
                };
            }

            if (safeFundFeatures.Count > 0)
            {
                return new BranchResolution
                {
                    Classification = ContractBranchClassification.Branch23,
                    Source = BranchDetectionSource.EliaFundFeatures,
                    Reason =
                        $"ELIA ProductCode {Display(normalizedProduct)} has " +
                        $"{safeFundFeatures.Count} fund feature(s) in TB5UGFF/TB5UPRF -> Classified as Branche 23."
                };
            }

            if (!string.IsNullOrWhiteSpace(normalizedProduct))
            {
                return new BranchResolution
                {
                    Classification = ContractBranchClassification.Standard,
                    Source = BranchDetectionSource.EliaProductCode,
                    Reason =
                        $"ELIA ProductCode {normalizedProduct} is not a Branch 23 product and has 0 fund features -> Classified as Branche 21 (Standard)."
                };
            }

            return new BranchResolution
            {
                Classification = ContractBranchClassification.Unknown,
                Source = BranchDetectionSource.Unknown,
                Reason = "No ELIA ProductCode and no ELIA fund feature were found."
            };
        }

        private static PreActivationRoutingStatus ResolveRoutingStatus(
            EnvironmentRoutingCandidate candidate,
            BranchResolution branchResolution)
        {
            if (!candidate.EliaContractId.HasValue)
                return PreActivationRoutingStatus.EliaContractNotLinked;
            if (string.IsNullOrWhiteSpace(candidate.ContractNumber))
                return PreActivationRoutingStatus.ContractNumberNotAvailable;
            if (string.IsNullOrWhiteSpace(candidate.ProductCode))
                return PreActivationRoutingStatus.ProductNotResolved;
            if (string.IsNullOrWhiteSpace(candidate.ProcessCode))
                return PreActivationRoutingStatus.ProcessNotResolved;
            if (string.IsNullOrWhiteSpace(candidate.ChannelCode))
                return PreActivationRoutingStatus.ChannelNotResolved;
            if (PreActivationRoutingInfo.ResolveBusinessChannel(candidate.ChannelCode) ==
                PreActivationBusinessChannel.Unknown)
                return PreActivationRoutingStatus.ChannelNotResolved;
            if (branchResolution.Classification == ContractBranchClassification.Unknown)
                return PreActivationRoutingStatus.BranchNotResolved;

            return PreActivationRoutingStatus.Ready;
        }

        private static string BuildRoutingDiagnostic(
            EnvironmentRoutingCandidate candidate,
            BranchResolution branchResolution,
            PreActivationRoutingStatus status,
            FundFeatureLoadResult fundFeatureLoadResult)
        {
            var messages = new List<string>
            {
                $"Demand {candidate.DemandId} found in {candidate.Environment}.",
                $"Product={Display(candidate.ProductCode)}, Process={Display(candidate.ProcessCode)}, " +
                $"Channel={Display(candidate.ChannelCode)}, ApplicationChannel={Display(candidate.ApplicationChannelCode)}.",
                $"DemandStatus={Display(candidate.DemandStatus)}.",
                $"EliaContractId={candidate.EliaContractId?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                $"ContractNumber={Display(candidate.ContractNumber)}.",
                $"PremiumSequence={candidate.PremiumSequence?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                $"GrossPremium={candidate.GrossPremium?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                $"NetPremium={candidate.NetPremium?.ToString(CultureInfo.InvariantCulture) ?? "-"}.",
                $"PaymentMethod={Display(candidate.PaymentMethodCode)}, " +
                $"PremiumGroupType={Display(candidate.PremiumGroupType)}, " +
                $"BusinessChannel={ResolveBusinessChannelName(candidate.ChannelCode)}, " +
                $"BUCP/CMDPMT={ResolvePaymentRoutingSummary(candidate.ChannelCode)}, " +
                $"FundFeatures={fundFeatureLoadResult.Features.Count}.",
                $"MainQuerySucceeded={candidate.MainQuerySucceeded}.",
                $"DemandRoutingFallbackUsed={candidate.DemandRoutingFallbackUsed}.",
                branchResolution.Reason,
                $"RoutingStatus={status}."
            };

            if (!string.IsNullOrWhiteSpace(candidate.RawRoutingStatus))
            {
                messages.Add($"SqlRoutingStatus={candidate.RawRoutingStatus.Trim()}.");
            }

            if (!string.IsNullOrWhiteSpace(candidate.ProcessCode) &&
                !IsSupportedActivationProcess(candidate.ProcessCode))
            {
                messages.Add(
                    $"Process {candidate.ProcessCode.Trim()} is not currently supported by the automatic activation workflow.");
            }

            if (!candidate.GrossPremium.HasValue && !candidate.NetPremium.HasValue)
            {
                messages.Add("No premium was found in TB5UPRP for this ELIA contract.");
            }

            if (!string.IsNullOrWhiteSpace(fundFeatureLoadResult.Error))
            {
                messages.Add("Fund-feature loading failed: " + fundFeatureLoadResult.Error);
            }

            return string.Join(" ", messages);
        }

        private static string BuildCandidateDebugSummary(EnvironmentRoutingCandidate candidate)
        {
            return
                $"Demand={candidate.DemandId} | Environment={candidate.Environment} | " +
                $"Product={Display(candidate.ProductCode)} | Process={Display(candidate.ProcessCode)} | " +
                $"Channel={Display(candidate.ChannelCode)} | " +
                $"BusinessChannel={ResolveBusinessChannelName(candidate.ChannelCode)} | " +
                $"BUCP/CMDPMT={ResolvePaymentRoutingSummary(candidate.ChannelCode)} | " +
                $"ApplicationChannel={Display(candidate.ApplicationChannelCode)} | " +
                $"DemandStatus={Display(candidate.DemandStatus)} | " +
                $"UCON={candidate.EliaContractId?.ToString(CultureInfo.InvariantCulture) ?? "-"} | " +
                $"Contract={Display(candidate.ContractNumber)} | " +
                $"GrossPremium={candidate.GrossPremium?.ToString(CultureInfo.InvariantCulture) ?? "-"} | " +
                $"MainQuerySucceeded={candidate.MainQuerySucceeded} | " +
                $"DemandFallbackUsed={candidate.DemandRoutingFallbackUsed}";
        }

        private static PreActivationRoutingInfo BuildNotFoundResult(
            decimal demandId,
            IReadOnlyList<string> lookupErrors)
        {
            string environments = string.Join(", ", Settings.PreActivationRouting.SupportedEnvironments);
            string diagnostic =
                $"Demand {demandId} was not found in the supported environments: {environments}.";

            if (lookupErrors.Count > 0)
            {
                diagnostic += " One or more environment lookups failed: " + string.Join(" | ", lookupErrors);
            }

            return new PreActivationRoutingInfo
            {
                DemandId = demandId,
                RoutingStatus = PreActivationRoutingStatus.DemandNotFound,
                BranchClassification = ContractBranchClassification.Unknown,
                BranchDetectionSource = BranchDetectionSource.Unknown,
                BranchDetectionReason = "The Demand ID was not found in any supported environment.",
                RoutingDiagnostic = diagnostic
            };
        }

        private static PreActivationRoutingInfo BuildNotFoundInEnvironmentResult(
            decimal demandId,
            string environment)
        {
            return new PreActivationRoutingInfo
            {
                Environment = Settings.NormalizeShortEnvironment(environment),
                DemandId = demandId,
                RoutingStatus = PreActivationRoutingStatus.DemandNotFound,
                BranchClassification = ContractBranchClassification.Unknown,
                BranchDetectionSource = BranchDetectionSource.Unknown,
                BranchDetectionReason = $"Demand {demandId} was not found in {environment}.",
                RoutingDiagnostic = $"Demand {demandId} was not found in {environment}."
            };
        }

        private static PreActivationRoutingInfo BuildAmbiguousEnvironmentResult(
            decimal demandId,
            IReadOnlyList<EnvironmentRoutingCandidate> candidates)
        {
            string environments = string.Join(
                ", ",
                candidates
                    .Select(candidate => candidate.Environment)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(environment => environment, StringComparer.OrdinalIgnoreCase));

            string candidateDetails = string.Join(
                " | ",
                candidates.Select(candidate =>
                    $"{candidate.Environment}: Product={Display(candidate.ProductCode)}, " +
                    $"Process={Display(candidate.ProcessCode)}, Contract={Display(candidate.ContractNumber)}"));

            return new PreActivationRoutingInfo
            {
                DemandId = demandId,
                RoutingStatus = PreActivationRoutingStatus.EnvironmentAmbiguous,
                BranchClassification = ContractBranchClassification.Unknown,
                BranchDetectionSource = BranchDetectionSource.Unknown,
                BranchDetectionReason =
                    "The branch was not resolved because the same Demand ID exists in multiple environments.",
                RoutingDiagnostic =
                    $"Demand {demandId} exists in multiple environments: {environments}. " +
                    $"No automatic environment selection was made. Candidates: {candidateDetails}."
            };
        }

        private static decimal ParseDemandId(string rawDemandId)
        {
            if (string.IsNullOrWhiteSpace(rawDemandId))
            {
                throw new ArgumentException("Le Demand ID est obligatoire.", nameof(rawDemandId));
            }

            string cleaned = rawDemandId
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();

            if (!decimal.TryParse(
                    cleaned,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out decimal demandId))
            {
                throw new ArgumentException($"Demand ID invalide : '{rawDemandId}'.", nameof(rawDemandId));
            }

            ValidateDemandId(demandId);
            return demandId;
        }

        private static void ValidateDemandId(decimal demandId)
        {
            if (demandId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(demandId),
                    demandId,
                    "Le Demand ID doit être strictement positif.");
            }

            if (decimal.Truncate(demandId) != demandId)
            {
                throw new ArgumentException(
                    "Le Demand ID ne peut pas contenir de décimales.",
                    nameof(demandId));
            }

            if (demandId > 999999999999999999m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(demandId),
                    demandId,
                    "Le Demand ID dépasse la précision SQL decimal(18, 0).");
            }
        }

        private static string GetString(DataRow row, string columnName)
        {
            ValidateDataRowAndColumnName(row, columnName);

            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
            {
                return string.Empty;
            }

            return row[columnName]?.ToString()?.Trim() ?? string.Empty;
        }

        private static decimal? GetDecimal(DataRow row, string columnName)
        {
            ValidateDataRowAndColumnName(row, columnName);

            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
            {
                return null;
            }

            object value = row[columnName];
            switch (value)
            {
                case decimal decimalValue: return decimalValue;
                case byte byteValue: return byteValue;
                case short shortValue: return shortValue;
                case int intValue: return intValue;
                case long longValue: return longValue;
                case float floatValue: return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
                case double doubleValue: return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
            }

            string text = value.ToString()?.Trim() ?? string.Empty;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal invariantResult))
                return invariantResult;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal currentCultureResult))
                return currentCultureResult;
            return null;
        }

        private static int? GetInt32(DataRow row, string columnName)
        {
            decimal? decimalValue = GetDecimal(row, columnName);
            if (!decimalValue.HasValue) return null;
            if (decimalValue.Value < int.MinValue || decimalValue.Value > int.MaxValue) return null;
            if (decimal.Truncate(decimalValue.Value) != decimalValue.Value) return null;
            return decimal.ToInt32(decimalValue.Value);
        }

        private static DateTime? GetDateTime(DataRow row, string columnName)
        {
            ValidateDataRowAndColumnName(row, columnName);

            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
            {
                return null;
            }

            object value = row[columnName];
            if (value is DateTime dateTime) return dateTime;
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.DateTime;

            string text = value.ToString()?.Trim() ?? string.Empty;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime invariantResult))
                return invariantResult;
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime currentCultureResult))
                return currentCultureResult;
            return null;
        }

        private static void ValidateDataRowAndColumnName(DataRow row, string columnName)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (string.IsNullOrWhiteSpace(columnName))
            {
                throw new ArgumentException("Le nom de colonne est obligatoire.", nameof(columnName));
            }
        }

        private static string GetColumnNames(DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return string.Join(", ", table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        }

        private static string FormatDataRow(DataRow row)
        {
            ArgumentNullException.ThrowIfNull(row);
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

        public static (string BusinessChannel, string Bucp, string Cmdpmt)
            ResolveConfirmedPaymentRouting(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank => ("BANK", "00382", "8"),
                PreActivationBusinessChannel.BrokerNewGeneration => ("BROKER_NEW_GEN", "80819", "H"),
                _ => ("UNKNOWN", string.Empty, string.Empty)
            };
        }

        private static string ResolveBusinessChannelName(string? channelCode) =>
            ResolveConfirmedPaymentRouting(channelCode).BusinessChannel;

        private static string ResolvePaymentRoutingSummary(string? channelCode)
        {
            (string businessChannel, string bucp, string cmdpmt) =
                ResolveConfirmedPaymentRouting(channelCode);
            return businessChannel == "UNKNOWN" ? "-/-" : $"{bucp}/{cmdpmt}";
        }

        private static string FirstNotEmpty(string? primary, string? fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary))
                return primary.Trim();
            return string.IsNullOrWhiteSpace(fallback) ? string.Empty : fallback.Trim();
        }

        private static string NormalizeCode(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        private static string? NormalizeNullableCode(string? value)
        {
            string normalized = NormalizeCode(value);
            return normalized.Length == 0 ? null : normalized;
        }

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private sealed class EnvironmentSearchResult
        {
            public IReadOnlyList<EnvironmentRoutingCandidate> Candidates { get; init; } =
                Array.Empty<EnvironmentRoutingCandidate>();
            public IReadOnlyList<string> LookupErrors { get; init; } =
                Array.Empty<string>();
        }

        private sealed class EnvironmentLookupResult
        {
            public string Environment { get; init; } = string.Empty;
            public EnvironmentRoutingCandidate? Candidate { get; init; }
            public string? Error { get; init; }
        }

        private sealed class FundFeatureLoadResult
        {
            public IReadOnlyList<EliaFundFeature> Features { get; init; } =
                Array.Empty<EliaFundFeature>();
            public string? Error { get; init; }
        }

        private sealed class EnvironmentRoutingCandidate
        {
            public string Environment { get; init; } = string.Empty;
            public decimal DemandId { get; init; }
            public DateTime? DemandCreationDate { get; init; }
            public string ProductCode { get; init; } = string.Empty;
            public string ProcessCode { get; init; } = string.Empty;
            public string ChannelCode { get; init; } = string.Empty;
            public string ApplicationChannelCode { get; init; } = string.Empty;
            public string DemandStatus { get; init; } = string.Empty;
            public decimal? ReferenceDemandId { get; init; }
            public decimal? LinkedDemandId { get; init; }
            public int? AddendumSequence { get; init; }
            public DateTime? LastEliaUpdate { get; init; }
            public decimal? EliaContractId { get; init; }
            public string ContractNumber { get; init; } = string.Empty;
            public int? PremiumSequence { get; init; }
            public decimal? GrossPremium { get; init; }
            public decimal? NetPremium { get; init; }
            public DateTime? FirstPaymentDate { get; init; }
            public DateTime? PaymentDate { get; init; }
            public DateTime? ContractStartDate { get; init; }
            public string PaymentMethodCode { get; init; } = string.Empty;
            public string PremiumGroupType { get; init; } = string.Empty;
            public string RawRoutingStatus { get; init; } = string.Empty;
            public bool MainQuerySucceeded { get; init; }
            public bool DemandRoutingFallbackUsed { get; init; }
        }

        private sealed class BranchResolution
        {
            public ContractBranchClassification Classification { get; init; } =
                ContractBranchClassification.Unknown;
            public BranchDetectionSource Source { get; init; } =
                BranchDetectionSource.Unknown;
            public string Reason { get; init; } = string.Empty;
        }
    }
}
