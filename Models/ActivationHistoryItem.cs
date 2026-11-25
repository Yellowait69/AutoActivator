namespace AutoActivator.Models
{
    /// <summary>
    /// Ligne synthétique affichée dans le tableau Live Execution Monitor (ActivationView).
    /// </summary>
    public sealed class ActivationHistoryItem
    {
        private string _branchDisplay = string.Empty;

        public string RowNum { get; set; } = "-";

        public string Time { get; set; } = string.Empty;

        public string Env { get; set; } = string.Empty;

        public string InputId { get; set; } = string.Empty;

        public string DemandId { get; set; } = "-";

        public string UconId { get; set; } = "-";

        public string LisaId { get; set; } = "-";

        public string ContractId { get; set; } = string.Empty;

        public string JclContract { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string BusinessChannel { get; set; } = string.Empty;

        public string Bucp { get; set; } = string.Empty;

        public string Cmdpmt { get; set; } = string.Empty;

        public string RunDate { get; set; } = string.Empty;

        public string RoutingRule { get; set; } = string.Empty;

        public string Premium { get; set; } = "0.00";

        public string FundsSummary { get; set; } = "-";

        public bool IsBranch23 { get; set; }

        public string BranchDisplay
        {
            get => !string.IsNullOrWhiteSpace(_branchDisplay)
                ? _branchDisplay
                : (IsBranch23 ? "Branche 23" : "Branche 21");
            set => _branchDisplay = value ?? string.Empty;
        }

        public string StepsInfo { get; set; } = string.Empty;

        public string Duration { get; set; } = "0.0s";

        public string Status { get; set; } = string.Empty;

        public string DiagnosticSummary { get; set; } = string.Empty;

        public ContractActivationTrace? FullTrace { get; set; }

        public bool IsFailed =>
            string.Equals(Status, "FAILED", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "CANCELLED", System.StringComparison.OrdinalIgnoreCase);

        public bool IsGreen => !IsFailed;

        public string ShortBranchDisplay => IsBranch23 ? "B23" : "B21";

        public string StatusBadgeText => Status switch
        {
            "SUCCESS" => "ACTIVÉ",
            "ALREADY ACTIVE" => "DÉJÀ ACTIF",
            "PENDING ASYNC LISA CREATION" => "EN COURS LISA",
            "CANCELLED" => "ANNULÉ",
            _ => "ÉCHEC"
        };

        public string LisaAndDemandDisplay
        {
            get
            {
                bool hasLisa = !string.IsNullOrWhiteSpace(LisaId) && LisaId != "-" && LisaId != "N/A";
                bool hasDem = !string.IsNullOrWhiteSpace(DemandId) && DemandId != "-" && DemandId != "N/A";
                if (hasLisa && hasDem) return $"{LisaId} / {DemandId}";
                if (hasLisa) return LisaId;
                if (hasDem) return DemandId;
                return "-";
            }
        }

        public string CompactProductChannelDisplay
        {
            get
            {
                string prod = ProductProcessDisplay;
                string chan = ChannelPaymentDisplay;
                if (prod != "-" && chan != "-") return $"{prod} · {chan}";
                if (prod != "-") return prod;
                return chan;
            }
        }

        public string ProductProcessDisplay
        {
            get
            {
                bool hasProd = !string.IsNullOrWhiteSpace(ProductCode) && ProductCode != "-";
                bool hasProc = !string.IsNullOrWhiteSpace(ProcessCode) && ProcessCode != "-";
                if (hasProd && hasProc) return $"{ProductCode} ({ProcessCode})";
                if (hasProd) return ProductCode;
                if (hasProc) return ProcessCode;
                return "-";
            }
        }

        public string ChannelPaymentDisplay
        {
            get
            {
                string channelLabel = BusinessChannel switch
                {
                    "BANK" => "BANK",
                    "BROKER_NEW_GEN" => "BROKER",
                    _ => !string.IsNullOrWhiteSpace(BusinessChannel) && BusinessChannel != "UNKNOWN"
                        ? BusinessChannel
                        : "-"
                };

                bool hasBucp = !string.IsNullOrWhiteSpace(Bucp) && Bucp != "-";
                bool hasCmd = !string.IsNullOrWhiteSpace(Cmdpmt) && Cmdpmt != "-";

                if (hasBucp || hasCmd)
                {
                    return $"{channelLabel} ({(hasBucp ? Bucp : "-")}/{(hasCmd ? Cmdpmt : "-")})";
                }

                return channelLabel;
            }
        }
    }
}
