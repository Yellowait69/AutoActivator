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
    }
}
