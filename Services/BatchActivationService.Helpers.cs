using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class BatchActivationService
    {
        /// <summary>
        /// Lit le fichier CSV (ou Excel converti) en conservant le numéro de ligne exact (1-based)
        /// tel qu'il apparaît dans le fichier Excel d'origine.
        /// </summary>
        private static async Task<IReadOnlyList<BatchInputItem>> ReadInputFileAsync(
            string filePath,
            CancellationToken token)
        {
            var items = new List<BatchInputItem>();

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

            string? line;
            char delimiter = ';';

            int identifierIndex = -1;
            int branch23Index = -1;
            int fundsIndex = -1;
            var overrideIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            bool headerFound = false;
            int excelRowNumber = 0;

            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();
                excelRowNumber++;

                line = RemoveBom(line);

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Replace("\"", string.Empty).TrimStart()
                    .StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                delimiter = line.Count(character => character == ';') >
                            line.Count(character => character == ',')
                    ? ';'
                    : ',';

                List<string> headers = CsvFormatter.ParseCsvLine(line, delimiter);

                for (int index = 0; index < headers.Count; index++)
                {
                    string header = headers[index].Trim().ToLowerInvariant();

                    if (IsBranch23Header(header))
                    {
                        branch23Index = index;
                        continue;
                    }

                    if (IsFundsHeader(header))
                    {
                        fundsIndex = index;
                        continue;
                    }

                    string? overrideKey = GetOverrideKey(header);
                    if (overrideKey != null)
                    {
                        overrideIndexes[overrideKey] = index;
                        continue;
                    }
                    if (IsIdentifierHeader(header))
                    {
                        identifierIndex = index;
                    }
                }

                if (identifierIndex >= 0)
                {
                    headerFound = true;
                    break;
                }
            }

            if (!headerFound)
            {
                throw new InvalidOperationException(
                    "No Contract or Demand column was found in the input file.");
            }

            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();
                excelRowNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                List<string> columns = CsvFormatter.ParseCsvLine(line, delimiter);

                if (columns.Count <= identifierIndex)
                {
                    continue;
                }

                string rawInput = CleanCsvValue(columns[identifierIndex]);

                if (string.IsNullOrWhiteSpace(rawInput) ||
                    string.Equals(rawInput, "End of File", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool? branch23Override =
                    branch23Index >= 0 && columns.Count > branch23Index
                        ? ParseNullableBoolean(columns[branch23Index])
                        : null;

                string? funds =
                    fundsIndex >= 0 && columns.Count > fundsIndex
                        ? NullIfWhiteSpace(CleanCsvValue(columns[fundsIndex]))
                        : null;

                var rowOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> mapping in overrideIndexes)
                {
                    if (columns.Count <= mapping.Value)
                    {
                        continue;
                    }
                    string overrideValue = CleanCsvValue(columns[mapping.Value]);
                    if (!string.IsNullOrWhiteSpace(overrideValue))
                    {
                        rowOverrides[mapping.Key] = overrideValue;
                    }
                }

                items.Add(
                    new BatchInputItem
                    {
                        RawInput = rawInput,
                        RowNumber = excelRowNumber,
                        Branch23Override = branch23Override,
                        Funds = funds,
                        Overrides = rowOverrides
                    });
            }

            return items;
        }

        private static async Task AuthenticateAndPreloadAsync(
            string environment,
            string username,
            string password,
            bool preloadBranch23,
            Action<string>? onProgress,
            CancellationToken token)
        {
            var apiService = new MicroFocusApiService();
            var jclProcessor = new JclProcessorService(Settings.JclNetworkFolder);

            Task<bool> logonTask = apiService.LogonAsync(
                username,
                password,
                environment,
                message => onProgress?.Invoke(message),
                token);

            Task preloadTask = preloadBranch23
                ? jclProcessor.PreloadTemplatesAsync(
                    JclProcessorService.StandardActivationJobs
                        .Concat(ActivationOrchestrator.Branch23JclNames)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                : jclProcessor.PreloadTemplatesAsync();

            await Task.WhenAll(logonTask, preloadTask).ConfigureAwait(false);

            bool authenticated = await logonTask.ConfigureAwait(false);
            if (!authenticated)
            {
                throw new InvalidOperationException(
                    "Initial Mainframe authentication failed. Verify VPN connectivity, credentials and the selected environment.");
            }
        }

        private static ContractActivationTrace CreateInitialTrace(
            BatchInputItem item,
            bool isDemandId,
            string environment,
            string cus,
            string bucp,
            string cmdpmt,
            string process,
            bool skipPrime,
            string username)
        {
            return new ContractActivationTrace
            {
                RowNum = item.RowNumber,
                RawInput = item.RawInput,
                IsDemandId = isDemandId,
                Environment = environment,
                EnvIms = environment == "D" ? "T" : "C",
                Q2Region = environment == "D" ? "Q2T" : "Q2C",
                FastCtrlDsn = environment == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL",
                ServerNode = MicroFocusApiService.ActiveServer ?? "N/A",
                Channel = process,
                Cus = cus,
                Bucp = string.IsNullOrWhiteSpace(bucp) ? string.Empty : bucp.Trim().PadLeft(5, '0'),
                Cmdpmt = cmdpmt,
                SkipPrime = skipPrime,
                Username = username,
                StartTime = DateTime.Now,
                FinalStatus = "PENDING"
            };
        }

        private static void PopulateTraceFromLookup(
            ContractActivationTrace trace,
            ActivationLookupResult lookup)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(lookup);

            trace.DbLookupDurationMs = lookup.LookupDurationMs;
            trace.DbDiagnosticStatus = lookup.DbDiagnosticStatus;
            trace.DbError = lookup.DbError;
            trace.EliaUconId = lookup.EliaUconId;
            trace.DemandId = lookup.DemandId;
            trace.LisaInternalId = lookup.LisaInternalId;
            trace.LisaContractNumber = lookup.LisaContractNumber;
            trace.ResolvedContract = lookup.ContractNumber;
            trace.JclContract = lookup.JclContract;

            if (!string.IsNullOrWhiteSpace(lookup.ResolvedEnvironment))
            {
                trace.Environment = lookup.ResolvedEnvironment.Trim().ToUpperInvariant();
                trace.EnvIms = string.Equals(trace.Environment, "D", StringComparison.OrdinalIgnoreCase) ? "T" : "C";
                trace.Q2Region = string.Equals(trace.Environment, "D", StringComparison.OrdinalIgnoreCase) ? "Q2T" : "Q2C";
                trace.FastCtrlDsn = string.Equals(trace.Environment, "D", StringComparison.OrdinalIgnoreCase)
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL";
            }

            trace.ProductCode = lookup.ProductCode;
            trace.ProcessCode = lookup.ProcessCode;
            trace.EliaChannelCode = lookup.ChannelCode;
            trace.BusinessChannel = lookup.ResolvedBusinessChannel;
            trace.ApplicationChannelCode = lookup.ApplicationChannelCode;
            trace.DemandStatus = lookup.DemandStatus;

            if (!string.IsNullOrWhiteSpace(lookup.Bucp) && string.IsNullOrWhiteSpace(trace.Bucp))
            {
                trace.Bucp = lookup.Bucp;
            }
            if (!string.IsNullOrWhiteSpace(lookup.Cmdpmt) && string.IsNullOrWhiteSpace(trace.Cmdpmt))
            {
                trace.Cmdpmt = lookup.Cmdpmt;
            }

            if (PreActivationRoutingService.IsSupportedActivationProcess(lookup.ProcessCode))
            {
                trace.Channel = lookup.ProcessCode.Trim().ToUpperInvariant();
            }

            trace.PremiumSequence = lookup.PremiumSequence;
            trace.RawPremium = lookup.RawPremium;
            trace.NetPremium = lookup.NetPremium;
            trace.PaymentMethodCode = lookup.PaymentMethodCode;
            trace.PremiumGroupType = lookup.PremiumGroupType;
            trace.FundFeatureCount = lookup.FundFeatureCount;
            trace.BranchClassification = lookup.BranchClassification;
            trace.BranchDetectionSource = lookup.BranchDetectionSource;
            trace.BranchDetectionReason = lookup.BranchDetectionReason;

            if (lookup.IsBranch23 == true)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source = $"{lookup.BranchDetectionSource}: {lookup.BranchDetectionReason}";
            }
            else if (lookup.IsBranch23 == false)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source = $"{lookup.BranchDetectionSource}: {lookup.BranchDetectionReason}";
            }

            trace.RoutingStatus = lookup.RoutingStatus;
            trace.RoutingDiagnostic = lookup.RoutingDiagnostic;
        }

        private static void ReportBatchProgress(
            Action<BatchProgressInfo>? onBatchProgress,
            ContractActivationTrace trace,
            int currentItem,
            int totalItems,
            string? statusOverride = null)
        {
            onBatchProgress?.Invoke(
                new BatchProgressInfo
                {
                    RowNum = trace.RowNum,
                    Environment = trace.Environment,
                    ContractId = trace.ResolvedContract,
                    JclContract = trace.JclContract,
                    InternalId = trace.LisaInternalId,
                    Product = trace.ProductCode,
                    ProductCode = trace.ProductCode,
                    Premium = trace.RawPremium,
                    UconId = trace.EliaUconId,
                    DemandId = trace.DemandId,
                    ProcessCode = !string.IsNullOrWhiteSpace(trace.ProcessCode)
                        ? trace.ProcessCode
                        : trace.Channel,
                    ChannelCode = trace.EliaChannelCode,
                    BusinessChannel = trace.BusinessChannel,
                    Bucp = trace.Bucp,
                    Cmdpmt = trace.Cmdpmt,
                    FundsSummary = trace.Branch23Funds.Count > 0
                        ? string.Join(", ", trace.Branch23Funds)
                        : "-",
                    IsBranch23 = trace.IsBranch23,
                    BranchDisplay = trace.BranchDisplay,
                    Status = string.IsNullOrWhiteSpace(statusOverride)
                        ? trace.FinalStatus
                        : statusOverride,
                    CurrentItem = currentItem,
                    TotalItems = totalItems
                });
        }

        private static string ResolvePremium(
            ActivationLookupResult lookup,
            PreActivationRoutingInfo? routing)
        {
            if (HasPositivePremium(lookup.RawPremium))
            {
                return NormalizePremium(lookup.RawPremium);
            }

            if (routing?.GrossPremium is > 0m)
            {
                return routing.GrossPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string NormalizePremium(string? value)
        {
            string cleaned = (value ?? "0").Replace(",", ".").Trim();
            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount)
                ? amount.ToString("0.00", CultureInfo.InvariantCulture)
                : "0";
        }

        private static bool HasPositivePremium(string? value)
        {
            string cleaned = (value ?? "0").Replace(",", ".").Trim();
            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount) &&
                   amount > 0m;
        }

        private static IBranch23FundSource? BuildBranch23FundSource(
            bool isBranch23,
            string? rowFunds,
            string? defaultFunds)
        {
            if (!isBranch23)
            {
                return null;
            }

            string? configuredFunds = !string.IsNullOrWhiteSpace(rowFunds)
                ? rowFunds
                : defaultFunds;

            IReadOnlyList<string> parsedFunds = ConfiguredBranch23FundSource.Parse(configuredFunds);
            return new ConfiguredBranch23FundSource(parsedFunds);
        }

        private static void CompleteFailedTrace(ContractActivationTrace trace, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            trace.EndTime = DateTime.Now;
            trace.TotalDurationMs = stopwatch.ElapsedMilliseconds;
            trace.FinalStatus = "FAILED";
        }

        private static void AddTraceOnce(
            ConcurrentBag<ContractActivationTrace> traces,
            ContractActivationTrace trace,
            ref bool traceAdded)
        {
            if (traceAdded)
            {
                return;
            }

            traces.Add(trace);
            traceAdded = true;
        }

        private static void ValidateArguments(
            string filePath,
            string environment,
            string username,
            string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("The input file path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The batch input file does not exist.", filePath);
            if (string.IsNullOrWhiteSpace(environment))
                throw new ArgumentException("The environment is required.", nameof(environment));
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("The Mainframe username is required.", nameof(username));
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("The output directory is required.", nameof(outputDirectory));
        }

        private static string NormalizeShortEnvironment(string environment)
        {
            string normalized = (environment ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.StartsWith("D", StringComparison.Ordinal)) return "D";
            if (normalized.StartsWith("Q", StringComparison.Ordinal)) return "Q";

            throw new ArgumentException(
                $"Unsupported batch environment: '{environment}'. Only D and Q are currently supported.",
                nameof(environment));
        }

        private static string CleanCsvValue(string? value)
        {
            string cleaned = (value ?? string.Empty)
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();

            if (cleaned.StartsWith("=", StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(1);
            }

            if (cleaned.Length >= 2 &&
                cleaned.StartsWith("\"", StringComparison.Ordinal) &&
                cleaned.EndsWith("\"", StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(1, cleaned.Length - 2);
            }

            return cleaned.Replace("\"\"", "\"").Trim();
        }

        private static string RemoveBom(string line) =>
            line.StartsWith("\uFEFF", StringComparison.Ordinal) ? line.Substring(1) : line;

        private static bool? ParseNullableBoolean(string? value)
        {
            string normalized = CleanCsvValue(value).Trim().ToUpperInvariant();
            if (normalized is "Y" or "YES" or "O" or "OUI" or "1" or "TRUE" or "BR23" or "BRANCHE 23")
                return true;
            if (normalized is "N" or "NO" or "NON" or "0" or "FALSE" or "BR21" or "BRANCHE 21")
                return false;
            return null;
        }

        private static string? GetOverrideKey(string header)
        {
            string normalized = (header ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToUpperInvariant();

            return normalized switch
            {
                "AMOUNT" or "AMOUNTOVERRIDE" or "PREMIUM" => "AMOUNT",
                "RUNDATE" or "DRUN" or "DATEOVERRIDE" => "DRUN",
                "BUCP" or "BUCPOVERRIDE" => "BUCP",
                "CMDPMT" or "CMD" or "PAYMENTCOMMAND" => "CMDPMT",
                "PROCESS" or "PROCESSCODE" => "PROCESS",
                "CHANNEL" or "CHANNELCODE" => "CHANNEL",
                _ => null
            };
        }

        private static bool IsIdentifierHeader(string header) =>
            header.Contains("contract") ||
            header.Contains("contrat") ||
            header.Contains("lisa") ||
            header.Contains("value") ||
            header.Contains("demand");

        private static bool IsBranch23Header(string header) =>
            header == "br23" ||
            header.Contains("branch23") ||
            header.Contains("branch 23") ||
            header.Contains("branche23") ||
            header.Contains("branche 23");

        private static bool IsFundsHeader(string header) =>
            header == "funds" ||
            header == "fund" ||
            header == "fonds" ||
            header.Contains("br23 fund") ||
            header.Contains("fund codes");

        private static string ResolveBusinessChannelName(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank => "BANK",
                PreActivationBusinessChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }

        private static string DisplayValue(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed class BatchInputItem
        {
            public string RawInput { get; init; } = string.Empty;
            public int RowNumber { get; init; }
            public bool? Branch23Override { get; init; }
            public string? Funds { get; init; }
            public IReadOnlyDictionary<string, string> Overrides { get; init; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
