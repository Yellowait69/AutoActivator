using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Services.Branch23Activation
{
    /// <summary>
    /// Business failure of the Branch 23 activation workflow.
    /// The code is stored in the activation trace as FailureStage.
    /// </summary>
    public sealed class Branch23ActivationException : Exception
    {
        public Branch23ActivationException(string code, string message)
            : base($"{NormalizeRequired(code, nameof(code))}: {NormalizeRequired(message, nameof(message))}")
        {
            Code = NormalizeRequired(code, nameof(code));
        }

        public Branch23ActivationException(
            string code,
            string message,
            Exception innerException)
            : base(
                $"{NormalizeRequired(code, nameof(code))}: {NormalizeRequired(message, nameof(message))}",
                innerException)
        {
            Code = NormalizeRequired(code, nameof(code));
        }

        public string Code { get; }

        private static string NormalizeRequired(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A non-empty value is required.", parameterName);
            }

            return value.Trim();
        }
    }

    /// <summary>
    /// Current payment channels accepted by Branch 23 activation.
    /// Historical Broker and TwoP values remain as obsolete aliases so older code and
    /// serialized data continue to compile, but new routing must use
    /// BrokerNewGeneration.
    /// </summary>
    public enum Branch23PaymentChannel
    {
        Unknown = 0,
        Bank = 1,
        BrokerNewGeneration = 2,

        [Obsolete("Use BrokerNewGeneration. Old Broker routing is no longer active.")]
        Broker = BrokerNewGeneration,

        [Obsolete("Use BrokerNewGeneration. TwoP is handled by Broker New Generation routing.")]
        TwoP = BrokerNewGeneration
    }

    public enum Branch23RoutingSource
    {
        Unknown = 0,
        EliaChannelCode = 1,
        ManualOverride = 2,
        PaymentPair = 3
    }

    public sealed class Branch23StartEnd
    {
        public string FullContractNumber { get; init; } = string.Empty;
        public string DigitsOnly { get; init; } = string.Empty;
        public string Start { get; init; } = string.Empty;
        public string End { get; init; } = string.Empty;
        public string Rule { get; init; } = string.Empty;

        public bool IsResolved =>
            !string.IsNullOrWhiteSpace(FullContractNumber) &&
            !string.IsNullOrWhiteSpace(Start) &&
            !string.IsNullOrWhiteSpace(End);
    }

    public sealed class Branch23RunDate
    {
        public DateTime RunDate { get; init; }
        public string Source { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;
        public DateTime? PaymentDate { get; init; }
        public DateTime? FirstPremiumDate { get; init; }
        public DateTime? ContractStartDate { get; init; }
        public string PaymentDateRejection { get; init; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; init; } =
            Array.Empty<string>();

        public bool IsResolved => RunDate != default;
        public string Drun => IsResolved
            ? RunDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : string.Empty;
        public string Yymmdd => IsResolved
            ? RunDate.ToString("yyMMdd", CultureInfo.InvariantCulture)
            : string.Empty;
        public string Mmdd => IsResolved
            ? RunDate.ToString("MMdd", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    /// <summary>
    /// One row of FJ1.TB5UPRP.
    /// </summary>
    public sealed class Branch23PremiumRow
    {
        public string EliaId { get; init; } = string.Empty;
        public string AmendmentSequence { get; init; } = string.Empty;
        public string Guarantee { get; init; } = string.Empty;
        public string PremiumGroup { get; init; } = string.Empty;
        public string PremiumType { get; init; } = string.Empty;
        public decimal? GrossPremium { get; init; }
        public decimal? NetPremium { get; init; }
        public decimal? Tax { get; init; }
        public DateTime? FirstPremiumDate { get; init; }
        public DateTime? PaymentDate { get; init; }
        public DateTime? ContractStartDate { get; init; }
        public string ContractNumber { get; init; } = string.Empty;

        public bool HasPositiveGrossPremium => GrossPremium is > 0m;

        public override string ToString() =>
            $"Avenant={AmendmentSequence}; Garantie={Guarantee}; " +
            $"Groupe={PremiumGroup}; Type={PremiumType}; " +
            $"Brute={Display(GrossPremium)}; Nette={Display(NetPremium)}; " +
            $"Taxe={Display(Tax)}; DPMRPAI={Display(FirstPremiumDate)}; " +
            $"DVRS={Display(PaymentDate)}";

        private static string Display(decimal? value) =>
            value.HasValue
                ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "NULL";

        private static string Display(DateTime? value) =>
            value.HasValue
                ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "NULL";
    }

    public sealed class Branch23PremiumSelection
    {
        public Branch23PremiumRow Selected { get; init; } =
            new Branch23PremiumRow();
        public int TotalRows { get; init; }
        public IReadOnlyList<string> Candidates { get; init; } =
            Array.Empty<string>();
        public IReadOnlyList<string> Rejected { get; init; } =
            Array.Empty<string>();
        public string Amount { get; init; } = string.Empty;
        public string Reason { get; init; } = string.Empty;

        public bool IsResolved =>
            Selected != null &&
            Selected.HasPositiveGrossPremium &&
            !string.IsNullOrWhiteSpace(Amount);
    }

    /// <summary>
    /// Payment parameters resolved from ELIA ChannelCode or an explicit validated
    /// manual override.
    /// </summary>
    public sealed class Branch23PaymentParameters
    {
        public Branch23PaymentChannel Channel { get; init; } =
            Branch23PaymentChannel.Unknown;
        public string ChannelCode { get; init; } = string.Empty;
        public string Bucp { get; init; } = string.Empty;
        public string Cmdpmt { get; init; } = string.Empty;
        public string RoutingRule { get; init; } = string.Empty;
        public Branch23RoutingSource RoutingSource { get; init; } =
            Branch23RoutingSource.Unknown;
        public string Source { get; init; } = string.Empty;
        public bool OverrideApplied { get; init; }
        public string OriginalBucp { get; init; } = string.Empty;
        public string OriginalCmdpmt { get; init; } = string.Empty;

        public string NormalizedBucp => NormalizeBucp(Bucp);
        public string NormalizedCmdpmt =>
            (Cmdpmt ?? string.Empty).Trim().ToUpperInvariant();

        public string BusinessChannelName => Channel switch
        {
            Branch23PaymentChannel.Bank => "BANK",
            Branch23PaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
            _ => "UNKNOWN"
        };

        public bool IsResolved =>
            IsSupportedPair(Channel, NormalizedBucp, NormalizedCmdpmt);

        public IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();

            if (Channel == Branch23PaymentChannel.Unknown)
            {
                issues.Add("Payment channel is unresolved.");
            }

            if (string.IsNullOrWhiteSpace(NormalizedBucp))
            {
                issues.Add("BUCP is missing.");
            }

            if (string.IsNullOrWhiteSpace(NormalizedCmdpmt))
            {
                issues.Add("CMDPMT is missing.");
            }

            if (Channel != Branch23PaymentChannel.Unknown &&
                !IsSupportedPair(Channel, NormalizedBucp, NormalizedCmdpmt))
            {
                issues.Add(
                    $"Unsupported payment pair {NormalizedBucp}/" +
                    $"{NormalizedCmdpmt} for {BusinessChannelName}.");
            }

            return issues;
        }

        public override string ToString() =>
            $"ChannelCode={Display(ChannelCode)}; " +
            $"Channel={BusinessChannelName}; " +
            $"BUCP={Display(NormalizedBucp)}; " +
            $"CMDPMT={Display(NormalizedCmdpmt)}; " +
            $"Rule={Display(RoutingRule)}; " +
            $"Source={RoutingSource}; " +
            $"Override={(OverrideApplied ? "YES" : "NO")}";

        public static Branch23PaymentParameters ResolveFromChannelCode(
            string? channelCode)
        {
            string code = NormalizeChannelCode(channelCode);

            if (BankChannelCodes.Contains(code))
            {
                return CreateResolved(
                    Branch23PaymentChannel.Bank,
                    code,
                    "00382",
                    "8",
                    $"CHANNEL_{code}_BANK_00382_8",
                    Branch23RoutingSource.EliaChannelCode,
                    "ELIA ChannelCode");
            }

            if (BrokerChannelCodes.Contains(code))
            {
                return CreateResolved(
                    Branch23PaymentChannel.BrokerNewGeneration,
                    code,
                    "80819",
                    "H",
                    $"CHANNEL_{code}_BROKER_NEW_GEN_80819_H",
                    Branch23RoutingSource.EliaChannelCode,
                    "ELIA ChannelCode");
            }

            return new Branch23PaymentParameters
            {
                ChannelCode = code,
                RoutingRule = "NO_CHANNEL_RULE",
                Source = "ELIA ChannelCode"
            };
        }

        public static Branch23PaymentParameters ResolveFromPaymentPair(
            string? bucp,
            string? cmdpmt,
            string? channelCode = null,
            bool overrideApplied = false)
        {
            string normalizedBucp = NormalizeBucp(bucp);
            string normalizedCommand =
                (cmdpmt ?? string.Empty).Trim().ToUpperInvariant();
            string code = NormalizeChannelCode(channelCode);

            Branch23PaymentChannel channel =
                normalizedBucp == "00382" && normalizedCommand == "8"
                    ? Branch23PaymentChannel.Bank
                    : normalizedBucp == "80819" && normalizedCommand == "H"
                        ? Branch23PaymentChannel.BrokerNewGeneration
                        : Branch23PaymentChannel.Unknown;

            string channelName = channel switch
            {
                Branch23PaymentChannel.Bank => "BANK",
                Branch23PaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };

            return new Branch23PaymentParameters
            {
                Channel = channel,
                ChannelCode = code,
                Bucp = normalizedBucp,
                Cmdpmt = normalizedCommand,
                RoutingRule = channel == Branch23PaymentChannel.Unknown
                    ? "INVALID_PAYMENT_PAIR"
                    : $"PAYMENT_PAIR_{channelName}_{normalizedBucp}_" +
                      normalizedCommand,
                RoutingSource = overrideApplied
                    ? Branch23RoutingSource.ManualOverride
                    : Branch23RoutingSource.PaymentPair,
                Source = overrideApplied
                    ? "Manual override"
                    : "BUCP/CMDPMT",
                OverrideApplied = overrideApplied
            };
        }

        private static Branch23PaymentParameters CreateResolved(
            Branch23PaymentChannel channel,
            string channelCode,
            string bucp,
            string cmdpmt,
            string rule,
            Branch23RoutingSource source,
            string sourceLabel)
        {
            return new Branch23PaymentParameters
            {
                Channel = channel,
                ChannelCode = channelCode,
                Bucp = bucp,
                Cmdpmt = cmdpmt,
                RoutingRule = rule,
                RoutingSource = source,
                Source = sourceLabel
            };
        }

        private static bool IsSupportedPair(
            Branch23PaymentChannel channel,
            string bucp,
            string cmdpmt)
        {
            return channel switch
            {
                Branch23PaymentChannel.Bank =>
                    bucp == "00382" && cmdpmt == "8",
                Branch23PaymentChannel.BrokerNewGeneration =>
                    bucp == "80819" && cmdpmt == "H",
                _ => false
            };
        }

        private static string NormalizeBucp(string? value)
        {
            string result = (value ?? string.Empty).Trim();
            return result.Length == 0
                ? string.Empty
                : result.PadLeft(5, '0');
        }

        private static string NormalizeChannelCode(string? value)
        {
            string result = (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            return result.Length == 1
                ? result.PadLeft(2, '0')
                : result;
        }

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static readonly HashSet<string> BankChannelCodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "01", "02", "04", "05", "13", "18", "22", "23", "24"
            };

        private static readonly HashSet<string> BrokerChannelCodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "03", "06", "07", "08", "09", "10", "11", "12",
                "14", "15", "16", "17", "19", "20", "21"
            };
    }

    /// <summary>
    /// Complete set of business parameters for PREMELIA jobs.
    /// </summary>
    public sealed class Branch23ActivationPlan
    {
        public string DemandId { get; init; } = string.Empty;
        public string EliaId { get; init; } = string.Empty;
        public string ProductCode { get; init; } = string.Empty;
        public string ProcessCode { get; init; } = string.Empty;
        public string ChannelCode { get; init; } = string.Empty;
        public string ApplicationChannel { get; init; } = string.Empty;
        public bool ForceBranch23 { get; init; }
        public Branch23StartEnd StartEnd { get; init; } =
            new Branch23StartEnd();
        public Branch23RunDate RunDate { get; init; } =
            new Branch23RunDate();
        public Branch23PremiumSelection Premium { get; init; } =
            new Branch23PremiumSelection();
        public Branch23PaymentParameters Payment { get; init; } =
            new Branch23PaymentParameters();
        public string Diagnostic { get; init; } = string.Empty;

        public bool IsReady =>
            !string.IsNullOrWhiteSpace(DemandId) &&
            !string.IsNullOrWhiteSpace(EliaId) &&
            StartEnd.IsResolved &&
            RunDate.IsResolved &&
            Premium.IsResolved &&
            Payment.IsResolved;

        public IReadOnlyList<string> Validate()
        {
            var issues = new List<string>();

            if (string.IsNullOrWhiteSpace(DemandId))
                issues.Add("Demand ID is missing.");
            if (string.IsNullOrWhiteSpace(EliaId))
                issues.Add("ELIA ID is missing.");
            if (!StartEnd.IsResolved)
                issues.Add("Contract start/end values are unresolved.");
            if (!RunDate.IsResolved)
                issues.Add("Business run date is unresolved.");
            if (!Premium.IsResolved)
                issues.Add("Premium selection is unresolved.");

            issues.AddRange(Payment.Validate());
            return issues;
        }
    }

    public enum PrctCheckStatus
    {
        ADDPRCT_CONFIRMED,
        ADDPRCT_NOT_FOUND,
        ADDPRCT_MISMATCH,
        ADDPRCT_DUPLICATE,
        ADDPRCT_AMBIGUOUS,
        ADDPRCT_CONFLICTING_RECEIPTS,
        ADDPRCT_SQL_ERROR
    }

    /// <summary>
    /// One row of [LV000PD0_x].[LV].[PRCTT0].
    /// </summary>
    public sealed class PrctRow
    {
        public string Ste { get; init; } = string.Empty;
        public decimal? ContractNumber { get; init; }
        public string PaymentMode { get; init; } = string.Empty;
        public DateTime? ReferenceDate { get; init; }
        public decimal? Amount { get; init; }
        public decimal? Bucp { get; init; }
        public string ReceiptState { get; init; } = string.Empty;
        public string ReceiptOrder { get; init; } = string.Empty;
        public string CreatedTimestamp { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public string LastJob { get; init; } = string.Empty;
        public string LastJobDate { get; init; } = string.Empty;

        public override string ToString() =>
            $"C_STE={Ste}; NO_CNT={ContractNumber}; C_MD_PMT={PaymentMode}; " +
            $"D_REF_PRM={ReferenceDate:yyyy-MM-dd}; M_PAY={Amount}; " +
            $"NO_BISM_BUCP={Bucp}; C_ETAT_RCP={ReceiptState}; " +
            $"NO_ORD_RCP={ReceiptOrder}; TSTAMP_CRT_RCT={CreatedTimestamp}; " +
            $"NM_AUTEUR_CRT={CreatedBy}; NM_JOB_DMOD={LastJob}; " +
            $"D_JOB_DMOD={LastJobDate}";
    }

    public sealed class PrctCheckResult
    {
        public PrctCheckStatus Status { get; init; }
        public IReadOnlyList<PrctRow> MatchingRows { get; init; } =
            Array.Empty<PrctRow>();
        public IReadOnlyList<PrctRow> AllRows { get; init; } =
            Array.Empty<PrctRow>();
        public string Detail { get; init; } = string.Empty;
    }

    public enum ContractActivationStatus
    {
        ACTIVATED,
        NOT_ACTIVATED,
        BLOCKED,
        TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT,
        AMBIGUOUS,
        SQL_ERROR,
        PENDING_ASYNC_LISA_CREATION
    }

    /// <summary>
    /// One row of [LV000SD0_x].[LV].[SCNTT0].
    /// </summary>
    public sealed class ScnttRow
    {
        public string Ste { get; init; } = string.Empty;
        public string ContractNumber { get; init; } = string.Empty;
        public string ExtendedNumber { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string StateDate { get; init; } = string.Empty;
        public string TreatmentCode { get; init; } = string.Empty;
        public string TreatmentDate { get; init; } = string.Empty;
        public string ErrorMessages { get; init; } = string.Empty;
        public string PremiumBlocked { get; init; } = string.Empty;
        public string InvalidRequest { get; init; } = string.Empty;
        public string MovementBlocked { get; init; } = string.Empty;
        public string ModificationTimestamp { get; init; } = string.Empty;
        public string LastJob { get; init; } = string.Empty;
        public string LastJobDate { get; init; } = string.Empty;
    }

    public sealed class ScnttCheckResult
    {
        public ContractActivationStatus Status { get; init; }
        public ScnttRow? Row { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    public enum Pe01CheckStatus
    {
        CONFIRMED,
        NOT_FOUND,
        NOT_AVAILABLE,
        ERROR
    }

    public sealed class Pe01CheckResult
    {
        public Pe01CheckStatus Status { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    public enum SpoolCounterStatus
    {
        COUNTERS_FOUND,
        COUNTERS_NOT_AVAILABLE
    }

    public sealed class SpoolCounterResult
    {
        public SpoolCounterStatus Status { get; init; }
        public IReadOnlyDictionary<string, long> Counters { get; init; } =
            new Dictionary<string, long>();
        public IReadOnlyList<string> DiagnosticLines { get; init; } =
            Array.Empty<string>();
    }
}