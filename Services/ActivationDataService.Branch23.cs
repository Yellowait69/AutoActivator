using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services.Branch23Activation;
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
    public sealed partial class ActivationDataService
    {
        private async Task<string?> ResolveBranch23LisaInternalIdAsync(
            ContractActivationTrace? trace,
            string jclContract,
            string envSuffix,
            CancellationToken token)
        {
            var candidates = new List<(string Value, bool IsDemand)>();

            if (trace != null)
            {
                if (!IsMissingValue(trace.ResolvedContract))
                {
                    candidates.Add((trace.ResolvedContract, false));
                }

                if (!IsMissingValue(trace.RawInput))
                {
                    candidates.Add((trace.RawInput, trace.IsDemandId));
                }
            }

            if (!string.IsNullOrWhiteSpace(jclContract))
            {
                candidates.Add((jclContract, false));
            }

            foreach (var candidate in candidates.Distinct())
            {
                ActivationLookupResult lookup =
                    await ResolveContractForActivationAsync(
                            candidate.Value,
                            candidate.IsDemand,
                            envSuffix,
                            token)
                        .ConfigureAwait(false);

                if (!IsMissingValue(lookup.LisaInternalId))
                {
                    if (trace != null)
                    {
                        trace.LisaInternalId = lookup.LisaInternalId;
                    }

                    return lookup.LisaInternalId;
                }
            }

            return null;
        }

        private async Task<string?> WaitForFinalLisaCreationAsync(
            ContractActivationTrace? trace,
            string jclContract,
            string envSuffix,
            Action<string>? onProgress,
            CancellationToken token)
        {
            const int maxAttempts = 60;
            const int delayMilliseconds = 5000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                string? lisaInternalId =
                    await ResolveBranch23LisaInternalIdAsync(
                            trace,
                            jclContract,
                            envSuffix,
                            token)
                        .ConfigureAwait(false);

                if (!IsMissingValue(lisaInternalId))
                {
                    if (trace != null)
                    {
                        trace.LisaInternalId = lisaInternalId!;
                        if (!IsMissingValue(trace.ResolvedContract))
                        {
                            trace.LisaContractNumber = trace.ResolvedContract;
                        }
                    }

                    onProgress?.Invoke($"Final LISA validation succeeded. NO_CNT={lisaInternalId}.");
                    return lisaInternalId;
                }

                onProgress?.Invoke(
                    $"Waiting for final LISA creation in LV.SCNTT0 ({attempt}/{maxAttempts}).");

                if (attempt < maxAttempts)
                {
                    await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
                }
            }

            return null;
        }

        private static async Task<(IReadOnlyList<string> Funds, string Source)>
            ResolveBranch23PreActivationFundsAsync(
                ContractActivationTrace? trace,
                string jclContract,
                string envSuffix,
                IBranch23FundSource? fallbackSource,
                CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string environment = Settings.NormalizeEnvironmentSuffix(envSuffix);
            var database = new DatabaseManager(environment);
            string? demandId = !IsMissingValue(trace?.DemandId)
                ? trace!.DemandId.Trim()
                : trace != null && trace.IsDemandId && !IsMissingValue(trace.RawInput)
                    ? trace.RawInput.Trim() : null;
            string? external = !IsMissingValue(trace?.ResolvedContract)
                ? trace!.ResolvedContract.Trim() : null;
            decimal? selectedSequence = null;
            string stage = "initialization";

            try
            {
                if (!string.IsNullOrWhiteSpace(demandId))
                {
                    if (!decimal.TryParse(demandId, NumberStyles.None,
                            CultureInfo.InvariantCulture, out decimal numericDemand) || numericDemand <= 0)
                        throw new InvalidOperationException("Invalid ELIA Demand ID.");

                    stage = "TB5HELT fund attachment context";
                    var attachment = await ResolveBranch23FundAttachmentAsync(
                        database, environment, numericDemand, jclContract, token)
                        .ConfigureAwait(false);
                    selectedSequence = attachment.Sequence;
                    string attachmentDiagnostic =
                        "TB5HELT: ELIA=" + attachment.EliaContractId.ToString(CultureInfo.InvariantCulture) +
                        "; contract/reference/new sequence=" +
                        attachment.Sequence.ToString(CultureInfo.InvariantCulture);
                    Debug.WriteLine("[BR23 FUND CONTEXT] " + attachmentDiagnostic);

                    stage = "GET_CONTRACT_BY_DEMAND";
                    using DataTable contracts = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    if (contracts.Rows.Count > 0)
                    {
                        string? linked = GetString(contracts.Rows[0], "ContractNumber")
                            ?? GetString(contracts.Rows[0], "IT5UCONLREFEXN");
                        if (!IsMissingValue(linked))
                        {
                            if (!string.Equals(FormatContractStatic(linked),
                                    FormatContractStatic(jclContract), StringComparison.Ordinal))
                                throw new InvalidOperationException(
                                    "Demand ID and activation contract do not match.");
                            external = linked;
                        }
                    }

                    stage = "GET_ELIA_FUND_FEATURES_BY_DEMAND";
                    using DataTable table = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    ValidateBranch23FundAttachment(table, attachment.EliaContractId);
                    var automatic = SelectAutomaticBranch23Funds(table, selectedSequence);
                    if (automatic.Count > 0)
                        return (automatic, "ELIA TB5UGFF by Demand ID " + demandId + "; " + attachmentDiagnostic);

                    stage = "GET_ELIA_PREMIUM_FUNDS_BY_DEMAND";
                    using DataTable premiumFunds = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    ValidateBranch23FundAttachment(premiumFunds, attachment.EliaContractId);
                    var premiumAutomatic = SelectAutomaticBranch23PremiumFunds(
                        premiumFunds, selectedSequence);
                    if (premiumAutomatic.Count > 0)
                        return (premiumAutomatic, "ELIA TB5UPRF by Demand ID " + demandId + "; " + attachmentDiagnostic);
                }

                string compact = CompactBranch23External(external);
                if (compact.Length != 12)
                {
                    string supplied = CompactBranch23External(jclContract);
                    if (supplied.Length == 12)
                        external = jclContract;
                    else
                    {
                        string technical = FormatContractStatic(jclContract);
                        if (technical.Length != 9 || technical.Any(c => c < '0' || c > '9'))
                            throw new InvalidOperationException("Invalid technical contract number.");
                        string life = Settings.DbConfig.GetLifeDatabase(environment);
                        string qualifiedLife = "[" + life.Replace("]", "]]") + "]";
                        stage = "Resolve exact external ELIA contract";
                        string query = $@"
SELECT DISTINCT LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS ContractNumber
FROM {qualifiedLife}.[FJ1].[TB5UCON] AS u
CROSS APPLY
(
    SELECT REPLACE(REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', ''), ' ', '') AS Compact
) AS normalized
WHERE LEN(normalized.Compact) = 12
  AND SUBSTRING(normalized.Compact, 2, 9) = @TechnicalContract;";
                        using DataTable matches = await database.GetDataAsync(query,
                            new Dictionary<string, object> { ["@TechnicalContract"] = technical }, token)
                            .ConfigureAwait(false);
                        if (matches.Rows.Count > 1)
                            throw new InvalidOperationException(
                                "Multiple external ELIA contracts match the technical number; provide the full contract.");
                        external = matches.Rows.Count == 1
                            ? GetString(matches.Rows[0], "ContractNumber") : null;
                    }
                }

                if (!IsMissingValue(external))
                {
                    if (!string.Equals(FormatContractStatic(external),
                            FormatContractStatic(jclContract), StringComparison.Ordinal))
                        throw new InvalidOperationException("Resolved external contract does not match activation target.");
                    stage = "GET_ELIA_FUND_FEATURES_BY_CONTRACT";
                    using DataTable table = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@ContractNumber"] = external! }, token)
                        .ConfigureAwait(false);
                    var automatic = SelectAutomaticBranch23Funds(table, selectedSequence);
                    if (automatic.Count > 0)
                        return (automatic, "ELIA TB5UGFF by contract " + external);

                    stage = "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT";
                    using DataTable premiumFunds = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@ContractNumber"] = external! }, token)
                        .ConfigureAwait(false);
                    var premiumAutomatic = SelectAutomaticBranch23PremiumFunds(
                        premiumFunds, selectedSequence);
                    if (premiumAutomatic.Count > 0)
                        return (premiumAutomatic, "ELIA TB5UPRF by contract " + external);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Debug.WriteLine($"[BR23 FUND RESOLUTION] Stage={stage}; Environment={environment}; Error={exception}");
            }

            try
            {
                var fundService = new Branch23FundService(environment);
                IReadOnlyList<string> lisaFunds = await fundService
                    .DiscoverContractFundsFromLisaAsync(jclContract, token)
                    .ConfigureAwait(false);
                if (lisaFunds.Count > 0)
                {
                    return (lisaFunds, "LISA LV000FD0.LV.FMVDT0");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BR23 LISA FUND FALLBACK] {ex.Message}");
            }

            if (fallbackSource != null)
            {
                IReadOnlyList<string> configured = await fallbackSource.GetContractFundsAsync(
                    jclContract, environment, token).ConfigureAwait(false);
                var fallback = new List<string>();
                foreach (string code in configured) AddBranch23Fund(fallback, code);
                if (fallback.Count > 0)
                    return (fallback.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        "Configured fallback: no automatic ELIA fund resolved");
            }
            return (Array.Empty<string>(), "No fund resolved from ELIA, LISA or configured fallback");
        }

        private static async Task<(decimal EliaContractId, decimal Sequence)>
            ResolveBranch23FundAttachmentAsync(
                DatabaseManager database,
                string environment,
                decimal demandId,
                string targetContract,
                CancellationToken token)
        {
            string life = Settings.DbConfig.GetLifeDatabase(environment);
            if (string.IsNullOrWhiteSpace(life))
                throw new InvalidOperationException("LIFE database is not configured.");
            string qualifiedLife = "[" + life.Replace("]", "]]") + "]";
            string query = $@"
SELECT DISTINCT
    h.IT5UCONAIDN AS EliaContractId,
    h.IT5HELTNSEQCON AS ContractSequence,
    h.IT5HELTNSEQAVEREF AS ReferenceAddendumSequence,
    h.IT5HELTNSEQAVENOU AS NewAddendumSequence,
    u.IT5UCONLREFEXN AS ContractNumber
FROM {qualifiedLife}.[FJ1].[TB5HELT] AS h
LEFT JOIN {qualifiedLife}.[FJ1].[TB5UCON] AS u
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId;";
            using DataTable context = await database.GetDataAsync(
                query,
                new Dictionary<string, object> { ["@DemandId"] = demandId },
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (context.Rows.Count != 1)
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_AMBIGUOUS: expected exactly one distinct " +
                    "contract attachment for this demand; found " + context.Rows.Count +
                    ". No historical addendum was selected.");

            DataRow row = context.Rows[0];
            string? linkedContract = GetString(row, "ContractNumber");
            string target = FormatContractStatic(targetContract);
            if (target.Length != 9 || target.Any(c => c < '0' || c > '9') ||
                (!IsMissingValue(linkedContract) &&
                 !string.Equals(FormatContractStatic(linkedContract), target, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_CONTRACT_MISMATCH: the demand attachment does not confirm the activation target.");

            decimal id = ReadBranch23AttachmentInteger(row, "EliaContractId");
            decimal contractSequence = ReadBranch23AttachmentInteger(row, "ContractSequence");
            decimal referenceSequence = ReadBranch23AttachmentInteger(row, "ReferenceAddendumSequence");
            decimal newSequence = ReadBranch23AttachmentInteger(row, "NewAddendumSequence");
            if (id <= 0)
                throw new InvalidOperationException("Invalid ELIA attachment contract identifier.");
            if (contractSequence != referenceSequence || contractSequence != newSequence)
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_SEQUENCE_AMBIGUOUS: contract=" + contractSequence +
                    ", reference=" + referenceSequence + ", new=" + newSequence +
                    ". A process-specific mapping is required; no maximum sequence was selected.");

            Debug.WriteLine("[BR23 ATTACHMENT] " + FormatDataRow(row));
            return (id, contractSequence);
        }

        private static decimal ReadBranch23AttachmentInteger(DataRow row, string column)
        {
            string? text = GetString(row, column);
            if (!decimal.TryParse(text, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out decimal value) ||
                value < 0 || decimal.Truncate(value) != value)
                throw new InvalidOperationException("Invalid or missing attachment integer: " + column);
            return value;
        }

        private static void ValidateBranch23FundAttachment(DataTable table, decimal expectedId)
        {
            foreach (DataRow row in table.Rows)
            {
                if (ReadBranch23AttachmentInteger(row, "EliaContractId") != expectedId)
                    throw new InvalidOperationException(
                        "BR23_FUND_CONTRACT_MISMATCH: fund rows belong to a different " +
                        "ELIA contract than the verified demand attachment.");
            }
        }

        private static string CompactBranch23External(string? value) =>
            CleanInput(value).Replace("-", string.Empty).Replace(" ", string.Empty);

        private static IReadOnlyList<string> SelectAutomaticBranch23Funds(
            DataTable table, decimal? requestedSequence)
        {
            if (table.Rows.Count == 0) return Array.Empty<string>();
            foreach (string column in new[] { "EliaContractId", "AddendumSequence", "FundCode", "SecondaryFundCode" })
                if (!table.Columns.Contains(column))
                    throw new InvalidOperationException("Missing ELIA fund result column: " + column);

            var rows = table.AsEnumerable().ToList();
            var identities = rows.Select(row => GetString(row, "EliaContractId"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (identities.Count != 1 || IsMissingValue(identities[0]))
                throw new InvalidOperationException("Ambiguous or missing ELIA contract identity in fund results.");

            var sequences = new Dictionary<decimal, List<DataRow>>();
            foreach (DataRow row in rows)
            {
                if (!decimal.TryParse(GetString(row, "AddendumSequence"), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out decimal sequence))
                    throw new InvalidOperationException("Missing or invalid fund addendum sequence.");
                if (!sequences.TryGetValue(sequence, out List<DataRow>? group))
                {
                    group = new List<DataRow>();
                    sequences.Add(sequence, group);
                }
                group.Add(row);
            }
            decimal selected;
            if (requestedSequence.HasValue && sequences.ContainsKey(requestedSequence.Value))
            {
                selected = requestedSequence.Value;
            }
            else
            {
                selected = sequences.Keys.Max();
            }
            var funds = new List<string>();
            foreach (DataRow row in sequences[selected])
            {
                string? primary = GetString(row, "FundCode");
                string? secondary = GetString(row, "SecondaryFundCode");
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    AddBranch23Fund(funds, primary);
                }
                if (!string.IsNullOrWhiteSpace(secondary))
                {
                    AddBranch23Fund(funds, secondary);
                }
                Debug.WriteLine("[BR23 FUND] " + FormatDataRow(row));
            }
            return funds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyList<string> SelectAutomaticBranch23PremiumFunds(
            DataTable table,
            decimal? requestedSequence)
        {
            if (table.Rows.Count == 0)
                return Array.Empty<string>();

            foreach (string column in new[]
            {
                "EliaContractId", "AddendumSequence", "GuaranteeCode",
                "PremiumGroupType", "FundCode", "SecondaryFundCode"
            })
            {
                if (!table.Columns.Contains(column))
                    throw new InvalidOperationException("Missing TB5UPRF result column: " + column);
            }

            return SelectAutomaticBranch23Funds(table, requestedSequence);
        }

        private static void AddBranch23Fund(ICollection<string> funds, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                funds.Add(value.Trim().ToUpperInvariant());
            }
        }

        private async Task<PreActivationRoutingInfo?> TryResolveExecutionRoutingAsync(
            ContractActivationTrace? trace,
            string environment,
            CancellationToken token)
        {
            if (trace == null)
            {
                return null;
            }

            string? demandId = null;

            if (!IsMissingValue(trace.DemandId))
            {
                demandId = trace.DemandId.Trim();
            }
            else if (trace.IsDemandId && !IsMissingValue(trace.RawInput))
            {
                demandId = trace.RawInput.Trim();
            }

            if (string.IsNullOrWhiteSpace(demandId))
            {
                return null;
            }

            try
            {
                PreActivationRoutingInfo routing =
                    await _preActivationRoutingService
                        .ResolveFromDemandInEnvironmentAsync(demandId, environment, token)
                        .ConfigureAwait(false);

                if (routing.RoutingStatus == PreActivationRoutingStatus.DemandNotFound)
                {
                    return null;
                }

                return routing;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"[PreActivationRouting] Unable to resolve Demand ID '{demandId}' in '{environment}': {exception.Message}");
                return null;
            }
        }

        private static void ApplyRoutingToTrace(
            ContractActivationTrace trace,
            PreActivationRoutingInfo routing)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(routing);

            if (!string.IsNullOrWhiteSpace(routing.ProductCode))
                trace.ProductCode = routing.ProductCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.ProcessCode))
                trace.ProcessCode = routing.ProcessCode.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(routing.ChannelCode))
                trace.EliaChannelCode = routing.ChannelCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.ApplicationChannelCode))
                trace.ApplicationChannelCode = routing.ApplicationChannelCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.DemandStatus))
                trace.DemandStatus = routing.DemandStatus.Trim();
            if (routing.DemandId > 0)
                trace.DemandId = routing.DemandId.ToString(CultureInfo.InvariantCulture);
            if (routing.EliaContractId.HasValue)
                trace.EliaUconId = routing.EliaContractId.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
                trace.ResolvedContract = routing.ContractNumber.Trim();
            if (routing.PremiumSequence.HasValue)
                trace.PremiumSequence = routing.PremiumSequence.Value.ToString(CultureInfo.InvariantCulture);
            if (routing.GrossPremium.HasValue)
                trace.RawPremium = routing.GrossPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (routing.NetPremium.HasValue)
                trace.NetPremium = routing.NetPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(routing.PaymentMethodCode))
                trace.PaymentMethodCode = routing.PaymentMethodCode.Trim();
            if (!string.IsNullOrWhiteSpace(routing.PremiumGroupType))
                trace.PremiumGroupType = routing.PremiumGroupType.Trim();

            trace.FundFeatureCount = routing.FundFeatureCount;
            trace.BranchClassification = routing.BranchClassification;
            trace.BranchDetectionSource = routing.BranchDetectionSource;
            trace.BranchDetectionReason = routing.BranchDetectionReason;
            trace.RoutingStatus = routing.RoutingStatus;

            if (!string.IsNullOrWhiteSpace(routing.RoutingDiagnostic))
            {
                trace.RoutingDiagnostic = routing.RoutingDiagnostic;
            }

            if (routing.IsBranch23)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source = $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}";
            }
            else if (routing.BranchClassification == ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source = $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}";
            }
        }

        private async Task ExecuteBranch23ActivationOnlyAsync(
            string contract,
            string normalizedEnvironment,
            string shortEnvironment,
            string q2,
            string envIms,
            string fastCtrl,
            string cus,
            string bucp,
            string cmdpmt,
            bool skipPrime,
            bool skipLogon,
            string username,
            string password,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken token,
            IBranch23FundSource? branch23FundSource = null,
            ActivationJobSelection? jobSelection = null,
            bool branch23ActivationOnly = false,
            bool manualPaymentOverride = false,
            string? amountOverride = null)
        {
            ArgumentNullException.ThrowIfNull(trace);
            token.ThrowIfCancellationRequested();

            ActivationJobSelection effectiveSelection = (jobSelection ??
                (branch23ActivationOnly
                    ? ActivationJobSelection.PremeliaOnly()
                    : ActivationJobSelection.FullChain())).Clone();

            if (skipPrime)
            {
                effectiveSelection.RunAddPrct = false;
            }

            trace.IsBranch23 = true;
            trace.SkipPrime = skipPrime;
            trace.Channel = string.IsNullOrWhiteSpace(trace.Channel) ? "C01" : trace.Channel;
            trace.Branch23FundsSource = effectiveSelection.HasAnyBranch23Step
                ? "ELIA TB5UGFF/TB5UPRF + LISA LV000FD0.LV.FMVDT0"
                : "Not used (PREMELIA only)";
            trace.Branch23MovementState =
                $"Branch23 Workflow ({effectiveSelection.FormatSummary()})";
            trace.Branch23Source = string.IsNullOrWhiteSpace(trace.Branch23Source)
                ? "Branch23Workflow"
                : trace.Branch23Source + " | Workflow=" + effectiveSelection.FormatSummary();

            onProgress?.Invoke(
                $"ExecutionMode=Branch23Workflow ({effectiveSelection.FormatSummary()}); " +
                $"PaymentDatabase={Settings.DbConfig.GetLisaPaymentDatabase(normalizedEnvironment)}; " +
                $"ContractDatabase={Settings.DbConfig.GetLisaContractDatabase(normalizedEnvironment)}; " +
                $"FundMovementDatabase={Settings.DbConfig.GetFundMovementDatabase(normalizedEnvironment)}; " +
                $"FundValuationDatabase={Settings.DbConfig.GetFundValuationDatabase(normalizedEnvironment)}.");

            Branch23PaymentChannel paymentChannel =
                Branch23ActivationResolver.IdentifyChannel(bucp, cmdpmt);

            var verifier = new Branch23ActivationVerifier(
                new Branch23SqlReader(normalizedEnvironment));

            Branch23ActivationPlan plan;
            try
            {
                string? demandId = IsMissingValue(trace.DemandId) ? null : trace.DemandId;
                string lookupContract = !IsMissingValue(trace.ResolvedContract)
                    ? trace.ResolvedContract
                    : contract;

                DateTime? parsedOverrideDate = null;
                string[] dateFormats = { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy" };
                if (string.Equals(trace.RunDateSource, "Manual override", StringComparison.OrdinalIgnoreCase) &&
                    !IsMissingValue(trace.RunDate) &&
                    DateTime.TryParseExact(
                        trace.RunDate.Trim(),
                        dateFormats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime overriddenDate))
                {
                    parsedOverrideDate = overriddenDate.Date;
                }

                decimal? parsedOverrideAmount = null;
                if (!string.IsNullOrWhiteSpace(amountOverride) &&
                    TryParsePositiveAmount(amountOverride, out decimal overriddenDecAmount))
                {
                    parsedOverrideAmount = overriddenDecAmount;
                }

                plan = await verifier.BuildPlanAsync(
                        demandId,
                        lookupContract,
                        bucp,
                        cmdpmt,
                        manualPaymentOverride,
                        parsedOverrideDate,
                        parsedOverrideAmount,
                        token)
                    .ConfigureAwait(false);
            }
            catch (Branch23ActivationException exception)
            {
                PublishBranch23Report(trace, new Branch23ActivationReport
                {
                    DemandId = trace.DemandId ?? "-",
                    ContractNumber372 = !IsMissingValue(trace.ResolvedContract) ? trace.ResolvedContract : contract,
                    Channel = paymentChannel.ToString(),
                    FinalDiagnostic = Branch23FinalDiagnostic.BLOCKED,
                    FailureCode = exception.Code,
                    FinalReason = exception.Message + " Aucun job soumis; aucune connexion ESCWA ouverte."
                }, onProgress);
                return;
            }

            var baseVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENV"] = shortEnvironment,
                ["ENVIMS"] = envIms,
                ["CUS"] = cus ?? string.Empty,
                ["Q2"] = q2,
                ["CM"] = string.Empty,
                ["NREMB"] = "20",
                ["CONTR-EX"] = "Y",
                ["CONTR-RE"] = "Y",
                ["CONTR-UN"] = "Y",
                ["NJJART72"] = "5",
                ["JOBNAMI"] = "LVPP06U$",
                ["FASTCTRL"] = fastCtrl,
                ["USERNAME"] = username ?? string.Empty
            };

            trace.Environment = shortEnvironment;
            trace.EnvIms = envIms;
            trace.Q2Region = q2;
            trace.FastCtrlDsn = fastCtrl;
            trace.Cus = cus ?? string.Empty;
            trace.Bucp = plan.Payment.Bucp;
            trace.Cmdpmt = plan.Payment.Cmdpmt;
            trace.JclContract = plan.StartEnd.Start;
            trace.FormattedAmount = plan.Premium.Amount;
            trace.Username = username ?? string.Empty;
            trace.RunDate = plan.RunDate.RunDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(trace.RunDateSource))
            {
                trace.RunDateSource = plan.RunDate.Source;
            }
            trace.Branch23ConsolidationDate = Branch23ParameterBuilder.GetConsolidationDate(plan.RunDate.RunDate);

            // Pré-résolution des fonds avant exécution afin d'afficher les fonds dès le démarrage
            try
            {
                var initialFunds = await ResolveBranch23PreActivationFundsAsync(
                    trace, plan.StartEnd.Start, normalizedEnvironment, branch23FundSource, token)
                    .ConfigureAwait(false);
                if (initialFunds.Funds.Count > 0)
                {
                    trace.Branch23Funds = initialFunds.Funds.ToList();
                    trace.Branch23FundsSource = initialFunds.Source;
                    onProgress?.Invoke(
                        $"[BR23 FONDS] Fonds détectés pour le contrat {plan.StartEnd.FullContractNumber} : " +
                        $"{string.Join(", ", initialFunds.Funds)} ({initialFunds.Source}).");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BR23 INITIAL FUND LOOKUP] {ex.Message}");
            }

            var orchestrator = new ActivationOrchestrator(Settings.JclNetworkFolder);
            await orchestrator.PrepareBranch23ActivationSessionAsync(
                    username ?? string.Empty, password ?? string.Empty, shortEnvironment,
                    skipLogon, onProgress, trace, token)
                .ConfigureAwait(false);

            Func<DateTime, DateTime, CancellationToken, Task<(IReadOnlyList<string> Funds, string Source, IReadOnlyList<string> NavSummary)>> prepareFundsAndVniAsync =
                async (runDateArg, consolidationDateArg, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var fundService = new Branch23FundService(normalizedEnvironment);
                    var resolvedFunds = new List<string>();
                    string fundSourceDescription = trace.Branch23FundsSource ?? string.Empty;

                    foreach (string existingFund in trace.Branch23Funds)
                    {
                        AddBranch23Fund(resolvedFunds, existingFund);
                    }

                    if (resolvedFunds.Count == 0)
                    {
                        var preResolved = await ResolveBranch23PreActivationFundsAsync(
                            trace, plan.StartEnd.Start, normalizedEnvironment, branch23FundSource, ct)
                            .ConfigureAwait(false);
                        foreach (string f in preResolved.Funds)
                            AddBranch23Fund(resolvedFunds, f);
                        fundSourceDescription = preResolved.Source;
                    }

                    var lisaFunds = await fundService.DiscoverContractFundsFromLisaAsync(
                        plan.StartEnd.Start, ct).ConfigureAwait(false);
                    foreach (string lf in lisaFunds)
                        AddBranch23Fund(resolvedFunds, lf);
                    if (lisaFunds.Count > 0 && !fundSourceDescription.Contains("FMVDT0", StringComparison.OrdinalIgnoreCase))
                    {
                        fundSourceDescription = string.IsNullOrWhiteSpace(fundSourceDescription)
                            ? $"LISA LV000FD0.LV.FMVDT0 ({string.Join(", ", lisaFunds)})"
                            : $"{fundSourceDescription} + LISA FMVDT0 ({string.Join(", ", lisaFunds)})";
                    }

                    var distinctFunds = resolvedFunds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctFunds.Count == 0)
                    {
                        onProgress?.Invoke("[BR23 VNI] Aucun code fonds détecté dans ELIA/LISA ; poursuite vers la consolidation.");
                        return (Array.Empty<string>(), fundSourceDescription, Array.Empty<string>());
                    }

                    DateTime runDate = runDateArg.Date;
                    DateTime consolidationDate = consolidationDateArg.Date;
                    onProgress?.Invoke(
                        $"[BR23 VNI] Vérification/insertion des VNI dans {Settings.DbConfig.GetFundValuationDatabase(normalizedEnvironment)} " +
                        $"(LV.GFCRT0 / LV.GFEVT0) pour [{string.Join(", ", distinctFunds)}] " +
                        $"du {runDate:yyyy-MM-dd} au {consolidationDate.AddDays(2):yyyy-MM-dd}...");

                    var navResults = await fundService.EnsureNavWindowWithBufferAsync(
                        distinctFunds, runDate, consolidationDate, extraCalendarDaysAfterConsolidation: 2, ct)
                        .ConfigureAwait(false);

                    var navSummaryList = navResults
                        .GroupBy(r => r.FundCode, StringComparer.OrdinalIgnoreCase)
                        .Select(g =>
                        {
                            var last = g.OrderBy(x => x.NavDate).Last();
                            string kind = last.Kind.HasValue ? last.Kind.Value.ToString() : "Open";
                            return $"{g.Key} ({kind}, {last.KindSource}): {g.Count(x => x.IsReady)}/{g.Count()} dates VNI prêtes jusqu'au {last.NavDate:yyyy-MM-dd}";
                        })
                        .ToList();

                    trace.Branch23Funds = distinctFunds;
                    trace.Branch23FundsSource = fundSourceDescription;
                    trace.Branch23ConsolidationDate = consolidationDate;
                    trace.Branch23NavSummary = navResults.Select(r => r.ToString()).ToList();

                    return (distinctFunds, fundSourceDescription, navSummaryList);
                };

            var workflow = new Branch23ActivationWorkflow(
                verifier,
                orchestrator.CreateBranch23JobRunner(trace, onProgress));

            Branch23ActivationReport report = await workflow.RunAsync(
                    plan,
                    baseVariables,
                    onProgress,
                    token,
                    effectiveSelection,
                    prepareFundsAndVniAsync)
                .ConfigureAwait(false);

            if (report.FinalDiagnostic == Branch23FinalDiagnostic.ACTIVATED &&
                IsMissingValue(trace.LisaInternalId))
            {
                trace.LisaInternalId = plan.StartEnd.Start;
                trace.LisaContractNumber = plan.StartEnd.FullContractNumber;
            }

            PublishBranch23Report(trace, report, onProgress);
        }

        private static void PublishBranch23Report(
            ContractActivationTrace trace,
            Branch23ActivationReport report,
            Action<string>? onProgress)
        {
            trace.Branch23ActivationReport = report.Render();
            string mvtAfterFg01 = !string.IsNullOrWhiteSpace(report.MovementStateAfterFg01) && report.MovementStateAfterFg01 != "-"
                ? report.MovementStateAfterFg01
                : report.MovementStateAfterFg03;
            string mvtSuffix = !string.IsNullOrWhiteSpace(mvtAfterFg01) && mvtAfterFg01 != "-"
                ? $" | {mvtAfterFg01}"
                : string.Empty;
            trace.Branch23MovementState = $"{report.FinalDiagnostic}{mvtSuffix}";
            if (trace.Branch23NavSummary.Count == 0)
            {
                trace.Branch23NavSummary = report.Lines.ToList();
            }
            trace.EndTime = DateTime.Now;

            onProgress?.Invoke(trace.Branch23ActivationReport);
            foreach (string warning in report.Warnings)
                onProgress?.Invoke("BR23 AVERTISSEMENT: " + warning);

            if (report.FinalDiagnostic == Branch23FinalDiagnostic.ACTIVATED)
            {
                trace.FinalStatus = "SUCCESS";
                return;
            }

            if (report.FinalDiagnostic == Branch23FinalDiagnostic.CANCELLED)
            {
                trace.FinalStatus = "CANCELLED";
                throw new OperationCanceledException(report.FinalReason);
            }

            trace.FinalStatus = "FAILED";
            trace.FailureStage = string.IsNullOrWhiteSpace(report.FailureCode)
                ? "BR23_" + report.FinalDiagnostic
                : report.FailureCode;
            trace.FailureReason = $"{report.FinalDiagnostic}: {report.FinalReason}";
            throw new InvalidOperationException(
                $"Branch 23 activation not confirmed: {report.FinalDiagnostic} ({trace.FailureStage}).");
        }
    }
}
