using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Models
{
    public enum ContractBranchClassification
    {
        Unknown = 0,
        Standard = 1,
        Branch23 = 23,
        CompositeWithFunds = 100
    }

    public enum BranchDetectionSource
    {
        Unknown = 0,
        EliaProductCode = 1,
        EliaFundFeatures = 2,
        ProductReference = 3,
        ManualOverride = 4
    }

    public enum PreActivationRoutingStatus
    {
        Unknown = 0,
        Ready = 1,
        DemandNotFound = 2,
        EliaContractNotLinked = 3,
        ContractNumberNotAvailable = 4,
        ProductNotResolved = 5,
        ProcessNotResolved = 6,
        ChannelNotResolved = 7,
        BranchNotResolved = 8,
        EnvironmentAmbiguous = 9
    }

    public enum PreActivationBusinessChannel
    {
        Unknown = 0,
        Bank = 1,
        BrokerNewGeneration = 2
    }

    public sealed class EliaFundFeature
    {
        public int? AddendumSequence { get; init; }
        public string GuaranteeCode { get; init; } = string.Empty;
        public int? FundSequence { get; init; }
        public string? FundCode { get; init; }
        public string? SecondaryFundCode { get; init; }
        public DateTime? FundActivationDate { get; init; }
        public DateTime? FundEndDate { get; init; }
        public decimal? FundPercentage { get; init; }
        public string? FundIndicator { get; init; }

        public bool HasFundCode =>
            !string.IsNullOrWhiteSpace(FundCode) ||
            !string.IsNullOrWhiteSpace(SecondaryFundCode);

        public string NormalizedFundCode => NormalizeCode(FundCode);
        public string NormalizedSecondaryFundCode => NormalizeCode(SecondaryFundCode);

        public override string ToString() =>
            $"Addendum={FormatNullableNumber(AddendumSequence)}, " +
            $"Guarantee={Display(GuaranteeCode)}, " +
            $"Sequence={FormatNullableNumber(FundSequence)}, " +
            $"Fund={Display(FundCode)}, " +
            $"SecondaryFund={Display(SecondaryFundCode)}, " +
            $"Percentage={FormatNullableDecimal(FundPercentage)}, " +
            $"Indicator={Display(FundIndicator)}, " +
            $"ActivationDate={FormatNullableDate(FundActivationDate)}, " +
            $"EndDate={FormatNullableDate(FundEndDate)}";

        private static string NormalizeCode(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string FormatNullableNumber(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "-";

        private static string FormatNullableDecimal(decimal? value) =>
            value?.ToString(
                "0.############################",
                CultureInfo.InvariantCulture) ?? "-";

        private static string FormatNullableDate(DateTime? value) =>
            value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
    }

    /// <summary>
    /// Complete read-only ELIA context resolved before LISA activation.
    /// ChannelCode is authoritative for BANK/BROKER_NEW_GEN payment routing.
    /// </summary>
    public sealed class PreActivationRoutingInfo
    {
        private static readonly IReadOnlyList<EliaFundFeature> EmptyFundFeatures =
            Array.Empty<EliaFundFeature>();

        private static readonly IReadOnlySet<string> SupportedProcessCodes =
            new HashSet<string>(
                new[] { "C01", "C03", "C05" },
                StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlySet<string> BankChannelCodes =
            new HashSet<string>(
                new[] { "01", "02", "04", "05", "13", "18", "22", "23", "24" },
                StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlySet<string> BrokerNewGenerationChannelCodes =
            new HashSet<string>(
                new[]
                {
                    "03", "06", "07", "08", "09", "10", "11", "12",
                    "14", "15", "16", "17", "19", "20", "21"
                },
                StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<EliaFundFeature> _fundFeatures = EmptyFundFeatures;

        public string Environment { get; init; } = string.Empty;
        public bool HasResolvedEnvironment => !string.IsNullOrWhiteSpace(Environment);

        public decimal DemandId { get; init; }
        public bool HasDemandId => DemandId > 0m;
        public DateTime? DemandCreationDate { get; init; }
        public string ProductCode { get; init; } = string.Empty;
        public bool HasProductCode => !string.IsNullOrWhiteSpace(ProductCode);
        public string NormalizedProductCode => NormalizeCode(ProductCode);
        public string ProcessCode { get; init; } = string.Empty;
        public bool HasProcessCode => !string.IsNullOrWhiteSpace(ProcessCode);
        public string NormalizedProcessCode => NormalizeCode(ProcessCode);
        public bool IsSupportedProcess => SupportedProcessCodes.Contains(NormalizedProcessCode);
        public string ChannelCode { get; init; } = string.Empty;
        public bool HasChannelCode => !string.IsNullOrWhiteSpace(ChannelCode);
        public string NormalizedChannelCode => NormalizeChannelCode(ChannelCode);
        public string ApplicationChannelCode { get; init; } = string.Empty;
        public bool HasApplicationChannelCode =>
            !string.IsNullOrWhiteSpace(ApplicationChannelCode);
        public string NormalizedApplicationChannelCode => NormalizeCode(ApplicationChannelCode);
        public string DemandStatus { get; init; } = string.Empty;
        public bool HasDemandStatus => !string.IsNullOrWhiteSpace(DemandStatus);
        public string NormalizedDemandStatus => NormalizeCode(DemandStatus);
        public decimal? ReferenceDemandId { get; init; }
        public decimal? LinkedDemandId { get; init; }
        public int? AddendumSequence { get; init; }
        public DateTime? LastEliaUpdate { get; init; }

        public bool HasResolvedDemandRouting =>
            HasDemandId && HasProductCode && HasProcessCode && HasChannelCode;

        public decimal? EliaContractId { get; init; }
        public bool HasEliaContractId => EliaContractId is > 0m;
        public string? ContractNumber { get; init; }
        public bool HasContractNumber => !string.IsNullOrWhiteSpace(ContractNumber);

        public int? PremiumSequence { get; init; }
        public decimal? GrossPremium { get; init; }
        public decimal? NetPremium { get; init; }
        public decimal? TaxAmount { get; init; }
        public decimal? TaxRate { get; init; }
        public DateTime? FirstPaymentDate { get; init; }
        public DateTime? PaymentDate { get; init; }
        public DateTime? LastPaymentDate { get; init; }
        public bool HasPremium => GrossPremium.HasValue || NetPremium.HasValue;
        public bool HasPositiveGrossPremium => GrossPremium is > 0m;
        public bool HasPositiveNetPremium => NetPremium is > 0m;

        public string? PaymentMethodCode { get; init; }
        public string? PremiumGroupType { get; init; }
        public bool HasPaymentMethod => !string.IsNullOrWhiteSpace(PaymentMethodCode);

        public IReadOnlyList<EliaFundFeature> FundFeatures
        {
            get => _fundFeatures;
            init => _fundFeatures = value ?? EmptyFundFeatures;
        }

        public int FundFeatureCount => _fundFeatures.Count;
        public bool HasFundFeatures => FundFeatureCount > 0;

        public IReadOnlyList<string> DistinctFundCodes =>
            _fundFeatures
                .SelectMany(feature => new[]
                {
                    feature.NormalizedFundCode,
                    feature.NormalizedSecondaryFundCode
                })
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public ContractBranchClassification BranchClassification { get; init; } =
            ContractBranchClassification.Unknown;
        public BranchDetectionSource BranchDetectionSource { get; init; } =
            BranchDetectionSource.Unknown;
        public string BranchDetectionReason { get; init; } = string.Empty;
        public bool IsBranch23 =>
            BranchClassification == ContractBranchClassification.Branch23;
        public bool IsStandard =>
            BranchClassification == ContractBranchClassification.Standard;
        public bool HasFundComponent =>
            IsBranch23 ||
            BranchClassification == ContractBranchClassification.CompositeWithFunds ||
            HasFundFeatures;
        public bool HasResolvedBranchClassification =>
            BranchClassification != ContractBranchClassification.Unknown;

        /// <summary>
        /// Nullable for compatibility with resolvers that explicitly assign null.
        /// When null, the effective channel is calculated from ChannelCode.
        /// </summary>
        public PreActivationBusinessChannel? BusinessChannel { get; init; }

        public PreActivationBusinessChannel EffectiveBusinessChannel =>
            BusinessChannel ?? ResolveBusinessChannel(NormalizedChannelCode);

        public string BusinessChannelName => EffectiveBusinessChannel switch
        {
            PreActivationBusinessChannel.Bank => "BANK",
            PreActivationBusinessChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
            _ => "UNKNOWN"
        };

        public bool HasResolvedBusinessChannel =>
            EffectiveBusinessChannel != PreActivationBusinessChannel.Unknown;

        public string ProposedBucp => EffectiveBusinessChannel switch
        {
            PreActivationBusinessChannel.Bank => "00382",
            PreActivationBusinessChannel.BrokerNewGeneration => "80819",
            _ => string.Empty
        };

        public string ProposedPaymentCommand => EffectiveBusinessChannel switch
        {
            PreActivationBusinessChannel.Bank => "8",
            PreActivationBusinessChannel.BrokerNewGeneration => "H",
            _ => string.Empty
        };

        public string PaymentRoutingRule => EffectiveBusinessChannel switch
        {
            PreActivationBusinessChannel.Bank =>
                $"CHANNEL_{NormalizedChannelCode}_BANK_00382_8",
            PreActivationBusinessChannel.BrokerNewGeneration =>
                $"CHANNEL_{NormalizedChannelCode}_BROKER_NEW_GEN_80819_H",
            _ => "NO_CHANNEL_RULE"
        };

        public bool IsPaymentRoutingResolved =>
            HasResolvedBusinessChannel &&
            !string.IsNullOrWhiteSpace(ProposedBucp) &&
            !string.IsNullOrWhiteSpace(ProposedPaymentCommand) &&
            !string.Equals(
                PaymentRoutingRule,
                "NO_CHANNEL_RULE",
                StringComparison.OrdinalIgnoreCase);

        public PreActivationRoutingStatus RoutingStatus { get; init; } =
            PreActivationRoutingStatus.Unknown;
        public string RoutingDiagnostic { get; init; } = string.Empty;
        public bool IsDemandFound =>
            RoutingStatus != PreActivationRoutingStatus.DemandNotFound && HasDemandId;
        public bool IsEnvironmentAmbiguous =>
            RoutingStatus == PreActivationRoutingStatus.EnvironmentAmbiguous;
        public bool HasRoutingError =>
            RoutingStatus != PreActivationRoutingStatus.Unknown &&
            RoutingStatus != PreActivationRoutingStatus.Ready;

        public bool IsReadyForActivation =>
            RoutingStatus == PreActivationRoutingStatus.Ready &&
            HasResolvedEnvironment &&
            HasResolvedDemandRouting &&
            IsSupportedProcess &&
            HasEliaContractId &&
            HasContractNumber &&
            HasResolvedBranchClassification &&
            IsPaymentRoutingResolved;

        public IReadOnlyList<string> ValidateForActivation()
        {
            var issues = new List<string>();

            if (!HasResolvedEnvironment)
                issues.Add("Environment is missing.");
            if (!HasDemandId)
                issues.Add("Demand ID is missing.");
            if (!HasProductCode)
                issues.Add("ProductCode is missing.");
            if (!HasProcessCode)
                issues.Add("ProcessCode is missing.");
            else if (!IsSupportedProcess)
                issues.Add($"ProcessCode '{ProcessCode}' is not supported.");
            if (!HasChannelCode)
                issues.Add("ChannelCode is missing.");
            else if (!IsPaymentRoutingResolved)
                issues.Add($"ChannelCode '{ChannelCode}' is not mapped to BANK or BROKER_NEW_GEN.");

            PreActivationBusinessChannel mappedChannel =
                ResolveBusinessChannel(NormalizedChannelCode);
            if (BusinessChannel.HasValue &&
                mappedChannel != PreActivationBusinessChannel.Unknown &&
                BusinessChannel.Value != mappedChannel)
            {
                issues.Add(
                    $"Assigned BusinessChannel '{BusinessChannel.Value}' conflicts " +
                    $"with ChannelCode '{NormalizedChannelCode}'.");
            }

            if (!HasEliaContractId)
                issues.Add("ELIA contract ID is missing.");
            if (!HasContractNumber)
                issues.Add("Contract number is missing.");
            if (!HasResolvedBranchClassification)
                issues.Add("Branch classification is unresolved.");
            if (RoutingStatus != PreActivationRoutingStatus.Ready)
                issues.Add($"RoutingStatus is {RoutingStatus}.");

            return issues;
        }

        public string ToDiagnosticSummary() =>
            $"Environment={Display(Environment)}, " +
            $"DemandId={DemandId.ToString(CultureInfo.InvariantCulture)}, " +
            $"Product={Display(ProductCode)}, " +
            $"Process={Display(ProcessCode)}, " +
            $"Channel={Display(NormalizedChannelCode)}, " +
            $"AppChannel={Display(ApplicationChannelCode)}, " +
            $"BusinessChannel={BusinessChannelName}, " +
            $"BusinessChannelSource={(BusinessChannel.HasValue ? "Resolver" : "ChannelCode")}, " +
            $"BUCP={Display(ProposedBucp)}, " +
            $"CMDPMT={Display(ProposedPaymentCommand)}, " +
            $"RoutingRule={PaymentRoutingRule}, " +
            $"DemandStatus={Display(DemandStatus)}, " +
            $"EliaContractId={FormatNullableDecimal(EliaContractId)}, " +
            $"Contract={Display(ContractNumber)}, " +
            $"GrossPremium={FormatNullableDecimal(GrossPremium)}, " +
            $"NetPremium={FormatNullableDecimal(NetPremium)}, " +
            $"PremiumSequence={FormatNullableNumber(PremiumSequence)}, " +
            $"PaymentMethod={Display(PaymentMethodCode)} (informational), " +
            $"PremiumGroupType={Display(PremiumGroupType)}, " +
            $"FundFeatures={FundFeatureCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"Branch={BranchClassification}, " +
            $"BranchSource={BranchDetectionSource}, " +
            $"RoutingStatus={RoutingStatus}, " +
            $"ReadyForActivation={(IsReadyForActivation ? "YES" : "NO")}";

        public override string ToString() => ToDiagnosticSummary();

        public static PreActivationBusinessChannel ResolveBusinessChannel(
            string? channelCode)
        {
            string normalized = NormalizeChannelCode(channelCode);

            if (BankChannelCodes.Contains(normalized))
                return PreActivationBusinessChannel.Bank;

            if (BrokerNewGenerationChannelCodes.Contains(normalized))
                return PreActivationBusinessChannel.BrokerNewGeneration;

            return PreActivationBusinessChannel.Unknown;
        }

        public static string NormalizeChannelCode(string? value)
        {
            string normalized = NormalizeCode(value);
            return normalized.Length == 1
                ? normalized.PadLeft(2, '0')
                : normalized;
        }

        private static string NormalizeCode(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        private static string FormatNullableNumber(int? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "-";

        private static string FormatNullableDecimal(decimal? value) =>
            value?.ToString(
                "0.############################",
                CultureInfo.InvariantCulture) ?? "-";
    }
}