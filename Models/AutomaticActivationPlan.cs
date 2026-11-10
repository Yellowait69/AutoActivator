using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Models
{
    public enum ActivationPaymentChannel
    {
        Unknown = 0,
        Bank = 1,
        BrokerNewGeneration = 2
    }

    public enum ActivationResumeStrategy
    {
        Unknown = 0,
        FullActivation = 1,
        ResumeWithoutAddPrct = 2,
        AlreadyActive = 3,
        Blocked = 4
    }

    public sealed record ActivationPlanIssue(
        string Code,
        string Message,
        bool IsBlocking);

    /// <summary>
    /// Complete activation plan resolved from SQL or manual input.
    /// ActivationInputMode is declared in Models/ActivationInputMode.cs.
    /// </summary>
    public sealed record AutomaticActivationPlan
    {
        private static readonly HashSet<string> SupportedProcesses =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "C01", "C02", "C03", "C05"
            };

        private static readonly IReadOnlyDictionary<string, ActivationPaymentChannel>
            ChannelRouting = new ReadOnlyDictionary<string, ActivationPaymentChannel>(
                new Dictionary<string, ActivationPaymentChannel>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["01"] = ActivationPaymentChannel.Bank,
                    ["02"] = ActivationPaymentChannel.Bank,
                    ["04"] = ActivationPaymentChannel.Bank,
                    ["05"] = ActivationPaymentChannel.Bank,
                    ["13"] = ActivationPaymentChannel.Bank,
                    ["18"] = ActivationPaymentChannel.Bank,
                    ["22"] = ActivationPaymentChannel.Bank,
                    ["23"] = ActivationPaymentChannel.Bank,
                    ["24"] = ActivationPaymentChannel.Bank,
                    ["C02"] = ActivationPaymentChannel.Bank,
                    ["BANK"] = ActivationPaymentChannel.Bank,

                    ["03"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["06"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["07"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["08"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["09"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["10"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["11"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["12"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["14"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["15"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["16"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["17"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["19"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["20"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["21"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["C01"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["C03"] = ActivationPaymentChannel.BrokerNewGeneration,
                    ["BROKER_NEW_GEN"] = ActivationPaymentChannel.BrokerNewGeneration
                });

        public ActivationInputMode InputMode { get; init; } =
            ActivationInputMode.SqlAutocomplete;

        public string InputValue { get; init; } = string.Empty;
        public bool InputIsDemandId { get; init; }
        public bool ForceBranch23 { get; init; }

        public string Environment { get; init; } = string.Empty;
        public string Ims { get; init; } = string.Empty;
        public string Db2 { get; init; } = string.Empty;
        public string Cus { get; init; } = string.Empty;
        public string FastCtrl { get; init; } = string.Empty;
        public string EliaDatabase { get; init; } = string.Empty;
        public string PaymentDatabase { get; init; } = string.Empty;
        public string ContractDatabase { get; init; } = string.Empty;

        public string ContractNumber { get; init; } = string.Empty;
        public string MainframeContractNumber { get; init; } = string.Empty;
        public string EliaId { get; init; } = string.Empty;
        public string DemandId { get; init; } = string.Empty;

        public string ProductCode { get; init; } = string.Empty;
        public string ProcessCode { get; init; } = string.Empty;
        public string ChannelCode { get; init; } = string.Empty;
        public string ApplicationChannel { get; init; } = string.Empty;
        public string DemandStatus { get; init; } = string.Empty;
        public string EliaContractStatus { get; init; } = string.Empty;
        public string EliaPaymentCode { get; init; } = string.Empty;

        public int DemandLinkCount { get; init; }
        public int PremiumCandidateCount { get; init; }
        public int PaymentCandidateCount { get; init; }

        public decimal GrossPremium { get; init; }
        public decimal? NetPremium { get; init; }
        public decimal? TaxAmount { get; init; }
        public DateTime RunDate { get; init; }
        public string RunDateSource { get; init; } = string.Empty;

        public ActivationPaymentChannel PaymentChannel { get; init; } =
            ActivationPaymentChannel.Unknown;

        public string ResolvedChannel => PaymentChannel switch
        {
            ActivationPaymentChannel.Bank => "BANK",
            ActivationPaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
            _ => "UNKNOWN"
        };

        public bool IsPaymentRoutingResolved =>
            PaymentChannel != ActivationPaymentChannel.Unknown &&
            !string.IsNullOrWhiteSpace(NormalizeBucp(Bucp)) &&
            !string.IsNullOrWhiteSpace(PaymentCommand) &&
            !string.Equals(
                RoutingRule,
                "NO_CHANNEL_RULE",
                StringComparison.OrdinalIgnoreCase);

        public string Bucp { get; init; } = string.Empty;
        public string PaymentCommand { get; init; } = string.Empty;
        public string RoutingRule { get; init; } = string.Empty;
        public string RoutingStatus { get; init; } = string.Empty;

        public string LisaInternalId { get; init; } = string.Empty;
        public int FundFeatureCount { get; init; }
        public IReadOnlyList<string> ResolvedFunds { get; init; } = Array.Empty<string>();
        public string FundsSource { get; init; } = string.Empty;
        public ContractBranchClassification BranchClassification { get; init; } =
            ContractBranchClassification.Unknown;
        public BranchDetectionSource BranchDetectionSource { get; init; } =
            BranchDetectionSource.Unknown;
        public string BranchDetectionReason { get; init; } = string.Empty;

        public bool IsBranch23 { get; init; }
        public string BranchDisplay => IsBranch23 ? "Branche 23" : "Branche 21";
        public DateTime ConsolidationDate => RunDate == default ? default : RunDate.AddDays(2);
        public bool ReceiptAlreadyExists { get; init; }
        public int ExistingReceiptCount { get; init; }
        public bool IsAlreadyActive { get; init; }

        public ActivationResumeStrategy ResumeStrategy { get; init; } =
            ActivationResumeStrategy.Unknown;

        public IReadOnlyDictionary<string, string> ValueSources { get; init; } =
            EmptyReadOnlyDictionary();

        public IReadOnlyDictionary<string, string> Overrides { get; init; } =
            EmptyReadOnlyDictionary();

        public string Diagnostic { get; init; } = string.Empty;

        public string AmountJcl => FormatAmountForJcl(GrossPremium);

        public string Drun => RunDate == default
            ? string.Empty
            : RunDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        public string Yymmdd => RunDate == default
            ? string.Empty
            : RunDate.ToString("yyMMdd", CultureInfo.InvariantCulture);

        public string Mmdd => RunDate == default
            ? string.Empty
            : RunDate.ToString("MMdd", CultureInfo.InvariantCulture);

        public bool IsReadyForExecution =>
            !IsAlreadyActive &&
            (ResumeStrategy is ActivationResumeStrategy.FullActivation
                or ActivationResumeStrategy.ResumeWithoutAddPrct) &&
            Validate().All(issue => !issue.IsBlocking);

        public Dictionary<string, string> CreateJclVariables()
        {
            var variables = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["CNTBEG"] = NormalizeRequired(MainframeContractNumber),
                ["CNTEND"] = NormalizeRequired(MainframeContractNumber),
                ["STE"] = "A",
                ["BUCP"] = NormalizeBucp(Bucp),
                ["CMDPMT"] = NormalizeRequired(PaymentCommand).ToUpperInvariant(),
                ["AMOUNT"] = AmountJcl,
                ["DRUN"] = Drun,
                ["CYMD"] = Drun,
                ["YYMMDD"] = Yymmdd,
                ["MMDD"] = Mmdd,
                ["YYYY"] = RunDate == default
                    ? string.Empty
                    : RunDate.ToString("yyyy", CultureInfo.InvariantCulture),
                ["MM"] = RunDate == default
                    ? string.Empty
                    : RunDate.ToString("MM", CultureInfo.InvariantCulture),
                ["DD"] = RunDate == default
                    ? string.Empty
                    : RunDate.ToString("dd", CultureInfo.InvariantCulture),
                ["CUS"] = NormalizeRequired(Cus),
                ["Q2"] = NormalizeRequired(Db2),
                ["ENVIMS"] = NormalizeRequired(Ims),
                ["FASTCTRL"] = NormalizeRequired(FastCtrl),
                ["NREMB"] = "20",
                ["CONTR-EX"] = "Y",
                ["CONTR-RE"] = "Y",
                ["CONTR-UN"] = "Y",
                ["NJJART72"] = "5",
                ["JOBNAMI"] = "LVPP06U$",
                ["CM"] = string.Empty,
                ["CHANNEL_CODE"] = NormalizeChannelCode(ChannelCode),
                ["BUSINESS_CHANNEL"] = ResolvedChannel,
                ["ROUTING_RULE"] = RoutingRule
            };

            if (InputMode.AllowsManualOverrides())
            {
                foreach (KeyValuePair<string, string> item in Overrides)
                {
                    string key = (item.Key ?? string.Empty).Trim();
                    if (key.Length > 0)
                    {
                        variables[key] = item.Value ?? string.Empty;
                    }
                }
            }

            return variables;
        }

        public IReadOnlyList<ActivationPlanIssue> Validate()
        {
            var issues = new List<ActivationPlanIssue>();

            AddRequired(issues, "ENVIRONMENT_MISSING", "Environment", Environment);
            AddRequired(issues, "CUS_MISSING", "CUS", Cus);
            AddRequired(issues, "CONTRACT_MISSING", "Contract number", ContractNumber);
            AddRequired(issues, "MAINFRAME_ID_MISSING", "Mainframe contract number", MainframeContractNumber);
            AddRequired(issues, "ELIA_ID_MISSING", "ELIA ID", EliaId);
            AddRequired(issues, "DEMAND_ID_MISSING", "Demand ID", DemandId);
            AddRequired(issues, "PRODUCT_MISSING", "Product code", ProductCode);
            AddRequired(issues, "PROCESS_MISSING", "Process code", ProcessCode);
            if (!InputMode.AllowsManualOverrides())
            {
                AddRequired(issues, "CHANNEL_MISSING", "Channel code", ChannelCode);
            }
            AddRequired(issues, "BUCP_MISSING", "BUCP", Bucp);
            AddRequired(issues, "CMDPMT_MISSING", "Payment command", PaymentCommand);
            AddRequired(issues, "ROUTING_RULE_MISSING", "Routing rule", RoutingRule);

            if (DemandLinkCount != 1)
            {
                issues.Add(new ActivationPlanIssue(
                    "DEMAND_LINK_COUNT_INVALID",
                    $"Exactly one Demand link is required; found {DemandLinkCount}.",
                    true));
            }

            if (PremiumCandidateCount != 1)
            {
                issues.Add(new ActivationPlanIssue(
                    "PREMIUM_CANDIDATE_COUNT_INVALID",
                    $"Exactly one premium candidate is required; found {PremiumCandidateCount}.",
                    true));
            }

            if (PaymentCandidateCount < 1 && !IsPaymentRoutingResolved)
            {
                issues.Add(new ActivationPlanIssue(
                    "PAYMENT_CANDIDATE_MISSING",
                    "No ELIA payment candidate was found and payment routing " +
                    "could not be resolved from ChannelCode.",
                    true));
            }
            else if (PaymentCandidateCount < 1)
            {
                issues.Add(new ActivationPlanIssue(
                    "PAYMENT_CANDIDATE_MISSING_INFORMATIONAL",
                    "No ELIA payment candidate was found, but BUCP/CMDPMT were " +
                    "resolved from the confirmed ChannelCode mapping.",
                    false));
            }

            if (!SupportedProcesses.Contains(ProcessCode ?? string.Empty))
            {
                issues.Add(new ActivationPlanIssue(
                    "PROCESS_NOT_SUPPORTED",
                    $"Process '{ProcessCode}' is not supported. Expected C01, C02, C03 or C05.",
                    true));
            }

            if (GrossPremium <= 0m)
            {
                issues.Add(new ActivationPlanIssue(
                    "PREMIUM_NOT_POSITIVE",
                    "Gross premium must be positive.",
                    true));
            }

            if (RunDate == default)
            {
                issues.Add(new ActivationPlanIssue(
                    "RUN_DATE_MISSING",
                    "Business run date is missing.",
                    true));
            }

            ValidateRouting(issues);

            if (ExistingReceiptCount > 1)
            {
                issues.Add(new ActivationPlanIssue(
                    "MULTIPLE_EXISTING_RECEIPTS",
                    $"Multiple existing receipts were found: {ExistingReceiptCount}.",
                    true));
            }

            if (IsAlreadyActive)
            {
                issues.Add(new ActivationPlanIssue(
                    "CONTRACT_ALREADY_ACTIVE",
                    "The contract is already active. No job must be submitted.",
                    false));
            }

            if (InputMode == ActivationInputMode.SqlAutocompleteWithOverrides &&
                Overrides.Count > 0)
            {
                issues.Add(new ActivationPlanIssue(
                    "MANUAL_OVERRIDES_APPLIED",
                    $"{Overrides.Count} manual override(s) will replace SQL values.",
                    false));
            }

            return issues;
        }

        public string ToPreviewText()
        {
            string issues = string.Join(
                System.Environment.NewLine,
                Validate().Select(issue =>
                    $"{(issue.IsBlocking ? "BLOCK" : "INFO")} " +
                    $"{issue.Code}: {issue.Message}"));

            string fundsText = ResolvedFunds != null && ResolvedFunds.Count > 0
                ? $"{string.Join(", ", ResolvedFunds)} ({FundsSource})"
                : FundFeatureCount > 0
                    ? $"Branche 23 ({FundFeatureCount} feature(s) fonds détecté(s))"
                    : "Aucun (Branche 21 Standard)";

            string lisaStatus = !string.IsNullOrWhiteSpace(LisaInternalId) &&
                                !string.Equals(LisaInternalId, "N/A", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(LisaInternalId, "Not found", StringComparison.OrdinalIgnoreCase)
                ? $"DÉJÀ ACTIF EN LISA (NO_CNT = {LisaInternalId})"
                : "NON ACTIF EN LISA (Prêt pour activation)";

            return
                $"Contrat (3-7-2)      : {ContractNumber}  |  JCL (9 chiffres / NO_CNT) : {MainframeContractNumber}{System.Environment.NewLine}" +
                $"Demand ID / ELIA ID  : {DemandId}  /  UCON {EliaId}{System.Environment.NewLine}" +
                $"État LISA actuel     : {lisaStatus}{System.Environment.NewLine}" +
                $"Produit / Process    : {ProductCode}  /  {ProcessCode}  (ChannelCode ELIA : {ChannelCode}){System.Environment.NewLine}" +
                $"Canal & Paiement     : {ResolvedChannel}  ->  BUCP = {NormalizeBucp(Bucp)} | CMDPMT = {PaymentCommand} ({RoutingRule}){System.Environment.NewLine}" +
                $"Prime Brute / JCL    : {GrossPremium.ToString("0.00", CultureInfo.InvariantCulture)} €  ->  AMOUNT = {AmountJcl}{System.Environment.NewLine}" +
                $"Date N (Run Date)    : {(RunDate == default ? "-" : RunDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))} ({RunDateSource}){System.Environment.NewLine}" +
                $"Date N+2 (Consolid.) : {(ConsolidationDate == default ? "-" : ConsolidationDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))} (LVDQFG01 / VNI LV000GD0.LV.GFCRT0){System.Environment.NewLine}" +
                $"Branche & Fonds      : {BranchDisplay}  |  Fonds : {fundsText}{System.Environment.NewLine}" +
                $"Diagnostic           : {Diagnostic}  |  Prêt : {(IsReadyForExecution ? "OUI" : IsAlreadyActive ? "CONTRAT DÉJÀ ACTIF (ou Consolidation BR23 seule)" : "NON")}" +
                (issues.Length == 0
                    ? string.Empty
                    : System.Environment.NewLine + issues);
        }

        public static ActivationPaymentChannel ResolvePaymentChannel(
            string? channelCode)
        {
            string code = NormalizeChannelCode(channelCode);
            return ChannelRouting.TryGetValue(
                code,
                out ActivationPaymentChannel channel)
                    ? channel
                    : ActivationPaymentChannel.Unknown;
        }

        public static (string Bucp, string PaymentCommand, string Rule)
            ResolveRouting(string? channelCode)
        {
            string code = NormalizeChannelCode(channelCode);

            return ResolvePaymentChannel(code) switch
            {
                ActivationPaymentChannel.Bank =>
                    ("00382", "8", $"CHANNEL_{code}_BANK_00382_8"),

                ActivationPaymentChannel.BrokerNewGeneration =>
                    ("80819", "H", $"CHANNEL_{code}_BROKER_NEW_GEN_80819_H"),

                _ => (string.Empty, string.Empty, "NO_CHANNEL_RULE")
            };
        }

        public static string FormatAmountForJcl(decimal amount)
        {
            if (amount <= 0m)
            {
                return string.Empty;
            }

            string value = amount.ToString(
                "0.00",
                CultureInfo.InvariantCulture);

            if (value.Length > 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    amount,
                    "The JCL amount cannot exceed 10 characters including decimals.");
            }

            return value.PadLeft(10, '0');
        }

        public static string NormalizeBucp(string? bucp)
        {
            string value = (bucp ?? string.Empty).Trim();
            return value.Length == 0
                ? string.Empty
                : value.PadLeft(5, '0');
        }

        public static string NormalizeChannelCode(string? channelCode)
        {
            string value = (channelCode ?? string.Empty).Trim();
            return value.Length == 1
                ? value.PadLeft(2, '0')
                : value;
        }

        public static string NormalizePaymentCommand(string? paymentCommand) =>
            (paymentCommand ?? string.Empty).Trim().ToUpperInvariant();

        private void ValidateRouting(ICollection<ActivationPlanIssue> issues)
        {
            string normalizedBucp = NormalizeBucp(Bucp);
            string normalizedCmd = NormalizePaymentCommand(PaymentCommand);

            bool isValidPair =
                ((normalizedBucp == "00382" || normalizedBucp == "00182") && normalizedCmd == "8") ||
                (normalizedBucp == "80819" && normalizedCmd == "H") ||
                (normalizedBucp == "49797" && (normalizedCmd == "X" || normalizedCmd == "H"));

            if (InputMode.AllowsManualOverrides() && isValidPair)
            {
                return;
            }

            ActivationPaymentChannel expected =
                ResolvePaymentChannel(ChannelCode);

            if (expected == ActivationPaymentChannel.Unknown)
            {
                if (isValidPair)
                {
                    return;
                }

                issues.Add(new ActivationPlanIssue(
                    "CHANNEL_NOT_MAPPED",
                    $"ChannelCode '{ChannelCode}' has no confirmed routing rule.",
                    true));
                return;
            }

            if (PaymentChannel != expected)
            {
                issues.Add(new ActivationPlanIssue(
                    "PAYMENT_CHANNEL_MISMATCH",
                    $"Resolved channel '{PaymentChannel}' does not match " +
                    $"ChannelCode '{ChannelCode}' ({expected}).",
                    true));
            }

            (string expectedBucp, string expectedCommand, _) =
                ResolveRouting(ChannelCode);

            if (!string.Equals(
                    normalizedBucp,
                    expectedBucp,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    normalizedCmd,
                    expectedCommand,
                    StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ActivationPlanIssue(
                    "PAYMENT_ROUTING_MISMATCH",
                    $"ChannelCode '{ChannelCode}' expects BUCP/CMDPMT " +
                    $"{expectedBucp}/{expectedCommand}, but the plan contains " +
                    $"{normalizedBucp}/{normalizedCmd}.",
                    true));
            }
        }

        private static void AddRequired(
            ICollection<ActivationPlanIssue> issues,
            string code,
            string label,
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new ActivationPlanIssue(
                    code,
                    $"{label} is required.",
                    true));
            }
        }

        private static string NormalizeRequired(string? value) =>
            (value ?? string.Empty).Trim();

        private static IReadOnlyDictionary<string, string>
            EmptyReadOnlyDictionary()
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase));
        }
    }
}