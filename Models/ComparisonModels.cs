using System;
using System.Collections.Generic;

namespace AutoActivator.Models
{
    /// <summary>
    /// Métriques d'évaluation d'un test ou d'un produit lors de la comparaison SQL/CSV.
    /// </summary>
    public sealed class ComparisonMetrics
    {
        public int TotalRows { get; set; }

        public int ErrorRows { get; set; }

        public int SuccessfulRows => Math.Max(0, TotalRows - ErrorRows);

        public double SuccessPercentage =>
            TotalRows == 0
                ? 100.0
                : Math.Round((double)SuccessfulRows / TotalRows * 100.0, 2);
    }

    /// <summary>
    /// Rapport consolidé d'une comparaison entre deux snapshots (Baseline vs Target).
    /// </summary>
    public sealed class ComparisonReport
    {
        public double GlobalSuccessPercentage { get; set; }

        public int TotalRowsCompared { get; set; }

        public int TotalDifferencesFound { get; set; }

        public int TotalBaseTests { get; set; }

        public int TotalTargetTests { get; set; }

        public int ComparedTestsCount { get; set; }

        public List<string> MissingInTarget { get; set; } = new List<string>();

        public List<string> MissingInBase { get; set; } = new List<string>();

        public Dictionary<string, ComparisonMetrics> TestMetrics { get; set; } =
            new Dictionary<string, ComparisonMetrics>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ComparisonMetrics> ProductMetrics { get; set; } =
            new Dictionary<string, ComparisonMetrics>(StringComparer.OrdinalIgnoreCase);

        public List<FileComparisonResult> FileResults { get; set; } =
            new List<FileComparisonResult>();
    }

    /// <summary>
    /// Résultat de comparaison table par table pour un test/contrat donné.
    /// </summary>
    public sealed class FileComparisonResult
    {
        private string _branchDisplay = string.Empty;

        public string TestId { get; set; } = string.Empty;

        public string ExcelRowDisplay { get; set; } = "-";

        public string ContractNumber { get; set; } = "-";

        public string ProductCode { get; set; } = "-";

        public string ProcessCode { get; set; } = "-";

        public string ChannelCode { get; set; } = "-";

        public string BusinessChannel { get; set; } = "-";

        public string BucpCmdpmtDisplay { get; set; } = "-";

        public string FundsSummary { get; set; } = "-";

        public bool IsBranch23 { get; set; }

        public string BranchDisplay
        {
            get => !string.IsNullOrWhiteSpace(_branchDisplay)
                ? _branchDisplay
                : (IsBranch23 ? "Branche 23" : "Branche 21");
            set => _branchDisplay = value ?? string.Empty;
        }

        public string FileType { get; set; } = string.Empty;

        public string BaseFileName { get; set; } = string.Empty;

        public string TargetFileName { get; set; } = string.Empty;

        public string TableName { get; set; } = string.Empty;

        public string CleanTableName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(TableName))
                {
                    return string.Empty;
                }

                if (TableName.StartsWith("[", StringComparison.Ordinal))
                {
                    int closeBracket = TableName.IndexOf(']');
                    if (closeBracket >= 0 && closeBracket + 1 < TableName.Length)
                    {
                        return TableName.Substring(closeBracket + 1).Trim();
                    }
                }

                return TableName.Trim('[', ']');
            }
        }

        public string Status { get; set; } = string.Empty;

        public string ErrorDetails { get; set; } = string.Empty;

        public int DifferingColumnsCount { get; set; }

        public string DifferingColumnsSummary { get; set; } = "-";

        public int TotalRows { get; set; }

        public int ErrorRows { get; set; }

        public int SuccessfulRows => Math.Max(0, TotalRows - ErrorRows);

        public string RowsSummary => $"{SuccessfulRows}/{TotalRows}";

        public bool IsMatch =>
            string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "OK_EMPTY", StringComparison.OrdinalIgnoreCase);
    }
}
