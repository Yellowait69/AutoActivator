using System;

namespace AutoActivator.Models
{
    /// <summary>
    /// Résultat complet de l'extraction SQL (LISA + ELIA + VNI Fonds) d'un contrat.
    /// </summary>
    public sealed class ExtractionResult
    {
        private string _branchDisplay = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public string StatusMessage { get; set; } = string.Empty;

        public string InternalId { get; set; } = "Not found";

        public string UconId { get; set; } = "Not found";

        public string DemandId { get; set; } = "Not found";

        public string LisaContent { get; set; } = string.Empty;

        public string EliaContent { get; set; } = string.Empty;

        public string Premium { get; set; } = "0";

        public string ContractReference { get; set; } = string.Empty;

        public string JclContract { get; set; } = "-";

        public string ProductCode { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string BusinessChannel { get; set; } = string.Empty;

        public string Bucp { get; set; } = "-";

        public string Cmdpmt { get; set; } = "-";

        public string RoutingRule { get; set; } = "-";

        public string RunDate { get; set; } = "-";

        public string RunDateSource { get; set; } = "-";

        public string ConsolidationDate { get; set; } = "-";

        public string AmountJcl { get; set; } = "-";

        public string BranchDetectionReason { get; set; } = "-";

        public string DemandStatus { get; set; } = string.Empty;

        public int FundFeatureCount { get; set; }

        public string FundsSummary { get; set; } = "-";

        public bool IsBranch23 { get; set; }

        public string BranchDisplay
        {
            get => !string.IsNullOrWhiteSpace(_branchDisplay)
                ? _branchDisplay
                : (IsBranch23 ? "Branche 23" : "Branche 21");
            set => _branchDisplay = value ?? string.Empty;
        }

        public int TablesExtractedCount { get; set; }
    }

    /// <summary>
    /// Événement de progression émis lors d'un batch d'extraction ou d'activation.
    /// </summary>
    public sealed class BatchProgressInfo
    {
        private string _branchDisplay = string.Empty;

        public int RowNum { get; set; }

        public string Environment { get; set; } = string.Empty;

        public string TestId { get; set; } = string.Empty;

        public string ContractId { get; set; } = string.Empty;

        public string JclContract { get; set; } = "-";

        public string InternalId { get; set; } = string.Empty;

        public string Product { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public string Premium { get; set; } = "0";

        public string UconId { get; set; } = string.Empty;

        public string DemandId { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string BusinessChannel { get; set; } = string.Empty;

        public string DemandStatus { get; set; } = string.Empty;

        public string Bucp { get; set; } = string.Empty;

        public string Cmdpmt { get; set; } = string.Empty;

        public string RoutingRule { get; set; } = "-";

        public string RunDate { get; set; } = string.Empty;

        public string RunDateSource { get; set; } = "-";

        public string ConsolidationDate { get; set; } = "-";

        public string AmountJcl { get; set; } = "-";

        public string BranchDetectionReason { get; set; } = "-";

        public int FundFeatureCount { get; set; }

        public string FundsSummary { get; set; } = "-";

        public bool IsBranch23 { get; set; }

        public string BranchDisplay
        {
            get => !string.IsNullOrWhiteSpace(_branchDisplay)
                ? _branchDisplay
                : (IsBranch23 ? "Branche 23" : "Branche 21");
            set => _branchDisplay = value ?? string.Empty;
        }

        public string FilePath { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int CurrentItem { get; set; }

        public int TotalItems { get; set; }

        public int TablesExtractedCount { get; set; }

        public double ProgressPercentage =>
            TotalItems <= 0
                ? 0.0
                : Math.Round((double)CurrentItem / TotalItems * 100.0, 1);
    }

    /// <summary>
    /// Ligne synthétique affichée dans le tableau d'historique des extractions (ExtractionView).
    /// </summary>
    public sealed class ExtractionItem
    {
        private string _branchDisplay = string.Empty;

        public string RowNum { get; set; } = "-";

        public string Env { get; set; } = string.Empty;

        public string ContractId { get; set; } = string.Empty;

        public string JclContract { get; set; } = "-";

        public string InternalId { get; set; } = string.Empty;

        public string Product { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string BusinessChannel { get; set; } = string.Empty;

        public string Bucp { get; set; } = "-";

        public string Cmdpmt { get; set; } = "-";

        public string RoutingRule { get; set; } = "-";

        public string RunDate { get; set; } = "-";

        public string RunDateSource { get; set; } = "-";

        public string ConsolidationDate { get; set; } = "-";

        public string AmountJcl { get; set; } = "-";

        public string BranchDetectionReason { get; set; } = "-";

        public string DemandStatus { get; set; } = "-";

        public string FundsSummary { get; set; } = "-";

        public bool IsBranch23 { get; set; }

        public string BranchDisplay
        {
            get => !string.IsNullOrWhiteSpace(_branchDisplay)
                ? _branchDisplay
                : (IsBranch23 ? "Branche 23" : "Branche 21");
            set => _branchDisplay = value ?? string.Empty;
        }

        public string Premium { get; set; } = "0";

        public string Ucon { get; set; } = string.Empty;

        public string Hdmd { get; set; } = string.Empty;

        public string Time { get; set; } = string.Empty;

        public string Test { get; set; } = string.Empty;

        public string Status { get; set; } = "OK";

        public int TablesExtractedCount { get; set; }

        public string FilePath { get; set; } = string.Empty;
    }
}
