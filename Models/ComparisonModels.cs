using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoActivator.Models
{
    /// <summary>
    /// Catégorie exacte de la règle ayant ignoré une différence colonne.
    /// </summary>
    public enum ExclusionRuleCategory
    {
        SpecificTable,
        Global,
        TechnicalAudit
    }

    /// <summary>
    /// Événement de progression temps réel pour l'orchestrateur de comparaison.
    /// </summary>
    public sealed class ComparisonProgressInfo
    {
        public int CurrentStep { get; init; }

        public int TotalSteps { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Écart métier actif cellule par cellule (affiché côte à côte SOURCE vs TARGET).
    /// </summary>
    public sealed class CellDifferenceItem
    {
        public int RowNumber { get; init; }

        public string RowLabel { get; init; } = string.Empty;

        public string RowContextKey { get; init; } = "-";

        public string ColumnName { get; init; } = string.Empty;

        public string SourceValue { get; init; } = string.Empty;

        public string TargetValue { get; init; } = string.Empty;

        public string DiffKind { get; init; } = "VALUE_DIFF";

        public string SourceDisplay =>
            string.IsNullOrEmpty(SourceValue) ? "(vide / NULL)" : SourceValue;

        public string TargetDisplay =>
            string.IsNullOrEmpty(TargetValue) ? "(vide / NULL)" : TargetValue;

        public string RowAndKeyDisplay =>
            string.IsNullOrWhiteSpace(RowContextKey) || RowContextKey == "-"
                ? RowLabel
                : $"{RowLabel} · {RowContextKey}";

        public string DiffKindBadge => DiffKind switch
        {
            "MISSING_IN_TARGET" => "Absent Target",
            "EXTRA_IN_TARGET" => "Ajout Target",
            _ => "Écart valeur"
        };
    }

    /// <summary>
    /// Différence détectée sur une colonne exclue par les règles d'exclusion.
    /// RÈGLE IMPÉRATIVE : n'impacte JAMAIS le score de comparaison, le taux de conformité ni le statut OK/KO.
    /// </summary>
    public sealed class IgnoredDifferenceItem
    {
        public string TestId { get; init; } = string.Empty;

        public string ExcelRowDisplay { get; init; } = "-";

        public string ContractNumber { get; init; } = "-";

        public string SystemType { get; init; } = string.Empty;

        public string BranchDisplay { get; init; } = string.Empty;

        public string ProductCode { get; init; } = "-";

        public string TableName { get; init; } = string.Empty;

        public string CleanTableName { get; init; } = string.Empty;

        public string ColumnName { get; init; } = string.Empty;

        public int RowNumber { get; init; }

        public string RowContextKey { get; init; } = "-";

        public string SourceValue { get; init; } = string.Empty;

        public string TargetValue { get; init; } = string.Empty;

        public ExclusionRuleCategory Category { get; init; } = ExclusionRuleCategory.Global;

        public bool IsSpecificTableRule => Category == ExclusionRuleCategory.SpecificTable;

        public bool IsTechnicalAuditRule => Category == ExclusionRuleCategory.TechnicalAudit;

        public string SourceDisplay =>
            string.IsNullOrEmpty(SourceValue) ? "(vide / NULL)" : SourceValue;

        public string TargetDisplay =>
            string.IsNullOrEmpty(TargetValue) ? "(vide / NULL)" : TargetValue;

        public string ContractAndTestDisplay =>
            !string.IsNullOrWhiteSpace(ContractNumber) &&
            ContractNumber != "-" &&
            !string.Equals(ContractNumber, TestId, StringComparison.OrdinalIgnoreCase)
                ? $"{ContractNumber} [{TestId}]"
                : TestId;

        public string RowAndKeyDisplay =>
            string.IsNullOrWhiteSpace(RowContextKey) || RowContextKey == "-"
                ? $"#{RowNumber}"
                : $"#{RowNumber} · {RowContextKey}";

        public string CategoryBadge => Category switch
        {
            ExclusionRuleCategory.SpecificTable => "Spécifique table",
            ExclusionRuleCategory.TechnicalAudit => "Audit / Timestamp",
            _ => "Exclusion globale"
        };
    }

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

        public int TotalActiveCellDifferences { get; set; }

        public int TotalTablesCompared { get; set; }

        public int TotalTablesWithDifferences { get; set; }

        public int TotalDifferingColumnsDistinct { get; set; }

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

        /// <summary>
        /// Liste exhaustive des écarts filtrés par les règles d'exclusion (0 impact sur le score).
        /// </summary>
        public List<IgnoredDifferenceItem> IgnoredDifferences { get; set; } =
            new List<IgnoredDifferenceItem>();

        public int IgnoredSpecificTableCount =>
            IgnoredDifferences.Count(item => item.Category == ExclusionRuleCategory.SpecificTable);

        public int IgnoredGlobalCount =>
            IgnoredDifferences.Count(item => item.Category == ExclusionRuleCategory.Global);

        public int IgnoredTechnicalAuditCount =>
            IgnoredDifferences.Count(item => item.Category == ExclusionRuleCategory.TechnicalAudit);
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

        public List<string> DifferingColumns { get; set; } = new List<string>();

        public List<CellDifferenceItem> ActiveDifferences { get; set; } =
            new List<CellDifferenceItem>();

        public int ActiveCellDifferencesCount { get; set; }

        public int IgnoredDifferencesCount { get; set; }

        public int BaseRowCount { get; set; }

        public int TargetRowCount { get; set; }

        public int TotalRows { get; set; }

        public int ErrorRows { get; set; }

        public int SuccessfulRows => Math.Max(0, TotalRows - ErrorRows);

        public string RowsSummary => $"{SuccessfulRows}/{TotalRows}";

        public string VolumeDisplay => $"{BaseRowCount} -> {TargetRowCount}";

        public bool IsMatch =>
            string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "OK_EMPTY", StringComparison.OrdinalIgnoreCase);

        public bool HasError => !IsMatch;

        public string StatusBadge => Status switch
        {
            "OK" => "OK",
            "OK_EMPTY" => "Vide (0/0)",
            "KO_ROW_COUNT" => "Écart volume",
            "KO_MISSING_DATA" => "Données manquantes",
            _ => "Écart métier"
        };
    }
}
