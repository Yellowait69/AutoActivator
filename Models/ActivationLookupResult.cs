using System;
using System.Globalization;

namespace AutoActivator.Models
{
    /// <summary>
    /// Résultat de la résolution initiale d'un contrat ou d'un Demand ID.
    /// Contient les identifiants ELIA et LISA, le numéro de contrat JCL,
    /// les informations de prime et le contexte de routing ELIA (Branche 21 / 23).
    /// </summary>
    public sealed class ActivationLookupResult
    {
        public string RawInput { get; set; } = string.Empty;

        public bool IsDemandId { get; set; }

        public bool FoundInElia { get; set; }

        public string ContractNumber { get; set; } = string.Empty;

        public string JclContract { get; set; } = string.Empty;

        public string EliaUconId { get; set; } = "N/A";

        public string DemandId { get; set; } = "N/A";

        public string LisaInternalId { get; set; } = "N/A";

        public string LisaContractNumber { get; set; } = "N/A";

        public string PremiumSequence { get; set; } = "N/A";

        public string RawPremium { get; set; } = "0";

        public string NetPremium { get; set; } = "0";

        public string ResolvedEnvironment { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string ApplicationChannelCode { get; set; } = string.Empty;

        public string DemandStatus { get; set; } = string.Empty;

        public string PaymentMethodCode { get; set; } = string.Empty;

        public ActivationPaymentChannel PaymentChannel { get; set; } =
            ActivationPaymentChannel.Unknown;

        public string BusinessChannel { get; set; } = string.Empty;

        public string Bucp { get; set; } = string.Empty;

        public string Cmdpmt { get; set; } = string.Empty;

        public string RoutingRule { get; set; } = string.Empty;

        public string RoutingSource { get; set; } = string.Empty;

        public string RunDate { get; set; } = string.Empty;

        public string RunDateSource { get; set; } = string.Empty;

        public string PremiumGroupType { get; set; } = string.Empty;

        public int FundFeatureCount { get; set; }

        public ContractBranchClassification BranchClassification { get; set; } =
            ContractBranchClassification.Unknown;

        public BranchDetectionSource BranchDetectionSource { get; set; } =
            BranchDetectionSource.Unknown;

        public string BranchDetectionReason { get; set; } = string.Empty;

        public PreActivationRoutingStatus RoutingStatus { get; set; } =
            PreActivationRoutingStatus.Unknown;

        public string RoutingDiagnostic { get; set; } = string.Empty;

        public bool? IsBranch23
        {
            get
            {
                return BranchClassification switch
                {
                    ContractBranchClassification.Branch23 => true,
                    ContractBranchClassification.CompositeWithFunds => true,
                    ContractBranchClassification.Standard => false,
                    _ => FundFeatureCount > 0 ? true : null
                };
            }
            set
            {
                if (!value.HasValue)
                {
                    return;
                }

                BranchClassification = value.Value
                    ? ContractBranchClassification.Branch23
                    : ContractBranchClassification.Standard;

                if (BranchDetectionSource == BranchDetectionSource.Unknown)
                {
                    BranchDetectionSource = BranchDetectionSource.ManualOverride;
                }
            }
        }

        public string BranchDisplay =>
            IsBranch23 == true
                ? "Branche 23"
                : IsBranch23 == false || !string.IsNullOrWhiteSpace(ProductCode) || FoundInElia
                    ? "Branche 21"
                    : "N/A";

        public string DbDiagnosticStatus { get; set; } = "N/A";

        public long LookupDurationMs { get; set; }

        public string DbError { get; set; } = string.Empty;

        public bool HasContractNumber => !IsMissingValue(ContractNumber);

        public bool HasJclContract => !IsMissingValue(JclContract);

        public bool HasEliaContract => FoundInElia && !IsMissingValue(EliaUconId);

        public bool HasLisaContract => !IsMissingValue(LisaInternalId);

        public bool HasPremium =>
            HasPositiveAmount(RawPremium) ||
            HasPositiveAmount(NetPremium);

        public bool HasFundFeatures => FundFeatureCount > 0;

        public bool HasResolvedPaymentRouting =>
            PaymentChannel != ActivationPaymentChannel.Unknown &&
            !string.IsNullOrWhiteSpace(Bucp) &&
            !string.IsNullOrWhiteSpace(Cmdpmt);

        public string ResolvedBusinessChannel => PaymentChannel switch
        {
            ActivationPaymentChannel.Bank => "BANK",
            ActivationPaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
            _ => string.IsNullOrWhiteSpace(BusinessChannel)
                ? "UNKNOWN"
                : BusinessChannel.Trim().ToUpperInvariant()
        };

        public bool IsReadyForActivation =>
            HasContractNumber &&
            HasJclContract &&
            HasEliaContract;

        public string ToDiagnosticSummary()
        {
            return
                $"Input={Display(RawInput)}, " +
                $"IsDemand={IsDemandId}, " +
                $"Environment={Display(ResolvedEnvironment)}, " +
                $"Demand={Display(DemandId)}, " +
                $"UCON={Display(EliaUconId)}, " +
                $"Contract={Display(ContractNumber)}, " +
                $"JCL={Display(JclContract)}, " +
                $"LISA={Display(LisaInternalId)}, " +
                $"Product={Display(ProductCode)}, " +
                $"Process={Display(ProcessCode)}, " +
                $"Channel={Display(ChannelCode)}, " +
                $"BusinessChannel={ResolvedBusinessChannel}, " +
                $"BUCP/CMDPMT={Display(Bucp)}/{Display(Cmdpmt)}, " +
                $"RoutingRule={Display(RoutingRule)}, " +
                $"RunDate={Display(RunDate)}, " +
                $"RunDateSource={Display(RunDateSource)}, " +
                $"Premium={Display(RawPremium)}, " +
                $"PaymentMethod={Display(PaymentMethodCode)}, " +
                $"FundFeatures={FundFeatureCount}, " +
                $"Branch={BranchDisplay} ({BranchClassification}), " +
                $"Routing={RoutingStatus}, " +
                $"DBStatus={Display(DbDiagnosticStatus)}, " +
                $"Lookup={LookupDurationMs}ms";
        }

        public override string ToString() => ToDiagnosticSummary();

        private static bool HasPositiveAmount(string? value)
        {
            string normalized = (value ?? "0").Replace(",", ".").Trim();
            return decimal.TryParse(
                       normalized,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount) &&
                   amount > 0m;
        }

        private static bool IsMissingValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value.Trim(), "N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "Not found", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value.Trim(), "Not resolved", StringComparison.OrdinalIgnoreCase);
        }

        private static string Display(string? value) =>
            IsMissingValue(value) ? "-" : value!.Trim();
    }
}
