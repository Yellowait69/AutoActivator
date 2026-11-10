using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using AutoActivator.Config;

namespace AutoActivator.Services
{
    public static class Comparator
    {
        private const int MAX_DIFFS_TO_REPORT = 100;

        /// <summary>
        /// Central function to compare two datasets (DataTables).
        /// MEMORY OPTIMIZED: Uses lightweight structures instead of cloning DataTables.
        /// </summary>
        public static (string Status, string Details) CompareDataTables(DataTable dfRef, DataTable dfNew, string tableName)
        {
            var result = CompareDataTablesDetailed(dfRef, dfNew, tableName);
            return (result.Status, result.Details);
        }

        /// <summary>
        /// Compares two DataTables and returns the status, detailed diff report, and the exact count of differing rows.
        /// </summary>
        public static (string Status, string Details, int ErrorRows) CompareDataTablesDetailed(
            DataTable dfRef,
            DataTable dfNew,
            string tableName)
        {
            var ext = CompareDataTablesExtended(dfRef, dfNew, tableName, ignoreTechnicalColumns: false);
            return (ext.Status, ext.Details, ext.ErrorRows);
        }

        /// <summary>
        /// Extended comparison returning column-level discrepancy summaries and supporting optional technical/timestamp column filtering.
        /// </summary>
        public static (string Status, string Details, int ErrorRows, int DiffColsCount, string DiffColsSummary) CompareDataTablesExtended(
            DataTable dfRef,
            DataTable dfNew,
            string tableName,
            bool ignoreTechnicalColumns = false)
        {
            if (dfRef == null || dfNew == null)
            {
                int fallbackErrors = Math.Max(dfRef?.Rows.Count ?? 0, dfNew?.Rows.Count ?? 0);
                return ("KO_NULL_DATA", $"One or both DataTables are null for table {tableName}.", fallbackErrors, 0, "Table NULL");
            }

            if (dfRef.Rows.Count == 0 && dfNew.Rows.Count == 0)
            {
                return ("OK_EMPTY", string.Empty, 0, 0, "✓ Vide (0/0)");
            }

            if (dfRef.Rows.Count == 0 || dfNew.Rows.Count == 0)
            {
                int missingRows = Math.Max(dfRef.Rows.Count, dfNew.Rows.Count);
                var missingSb = new StringBuilder();
                missingSb.AppendLine(
                    $"⚠️ ÉCART DE PRÉSENCE DE DONNÉES : BASELINE = {dfRef.Rows.Count} ligne(s) vs TARGET = {dfNew.Rows.Count} ligne(s) pour {tableName}.");
                DataTable populated = dfRef.Rows.Count > 0 ? dfRef : dfNew;
                string sideLabel = dfRef.Rows.Count > 0
                    ? "📘 Lignes présentes uniquement dans BASELINE (absentes de TARGET) :"
                    : "🎯 Lignes présentes uniquement dans TARGET (absentes de BASELINE) :";
                missingSb.AppendLine(sideLabel);

                int previewCount = Math.Min(5, populated.Rows.Count);
                for (int r = 0; r < previewCount; r++)
                {
                    DataRow row = populated.Rows[r];
                    var nonEmptyCells = new List<string>();
                    foreach (DataColumn col in populated.Columns)
                    {
                        string val = row[col]?.ToString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(val) &&
                            !string.Equals(val, "NULL", StringComparison.OrdinalIgnoreCase))
                        {
                            nonEmptyCells.Add($"{col.ColumnName}='{val}'");
                            if (nonEmptyCells.Count >= 8) break;
                        }
                    }
                    missingSb.AppendLine($"  • Ligne #{r + 1} : {string.Join(" | ", nonEmptyCells)}");
                }
                if (populated.Rows.Count > previewCount)
                {
                    missingSb.AppendLine($"  ... (+{populated.Rows.Count - previewCount} autre(s) ligne(s))");
                }

                string summary = dfRef.Rows.Count == 0
                    ? $"Absent de Base (0 vs {dfNew.Rows.Count} lig.)"
                    : $"Absent de Target ({dfRef.Rows.Count} vs 0 lig.)";

                return ("KO_MISSING_DATA", missingSb.ToString().TrimEnd(), missingRows, populated.Columns.Count, summary);
            }

            try
            {
                var exclusions = Exclusions.GetExclusionsForTable(tableName);

                var allColsRef = dfRef.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                var allColsNew = dfNew.Columns.Cast<DataColumn>().Select(c => c.ColumnName);

                var commonCols = allColsRef
                    .Intersect(allColsNew, StringComparer.OrdinalIgnoreCase)
                    .Except(exclusions, StringComparer.OrdinalIgnoreCase)
                    .Where(c => !ignoreTechnicalColumns || !IsTechnicalAuditColumn(c))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (commonCols.Count == 0)
                {
                    return ("KO_NO_COMMON_COLS", "No common columns found after applying exclusion filters.", Math.Max(dfRef.Rows.Count, dfNew.Rows.Count), 0, "Aucune colonne commune");
                }

                int[] refIndices = commonCols.Select(c => dfRef.Columns[c]!.Ordinal).ToArray();
                int[] newIndices = commonCols.Select(c => dfNew.Columns[c]!.Ordinal).ToArray();

                List<string[]> refRows = ExtractAndNormalize(dfRef, refIndices);
                List<string[]> newRows = ExtractAndNormalize(dfNew, newIndices);

                var rowComparer = StringArrayComparer.Instance;
                refRows.Sort(rowComparer);
                newRows.Sort(rowComparer);

                if (refRows.Count != newRows.Count)
                {
                    int rowCountDiff = Math.Abs(refRows.Count - newRows.Count);
                    var countReport = new StringBuilder();
                    countReport.AppendLine(
                        $"⚠️ ÉCART DE VOLUME DE LIGNES : BASELINE = {refRows.Count} ligne(s) vs TARGET = {newRows.Count} ligne(s) (Écart : {rowCountDiff} ligne(s)).");

                    int commonRowCount = Math.Min(refRows.Count, newRows.Count);
                    string colSummary = $"Volume ({refRows.Count} vs {newRows.Count} lig.)";
                    int diffColCount = 0;

                    if (commonRowCount > 0)
                    {
                        var (_, previewDetails, _, pDiffCols, pSummary) = GenerateDiffReport(
                            refRows.Take(commonRowCount).ToList(),
                            newRows.Take(commonRowCount).ToList(),
                            commonCols);
                        diffColCount = pDiffCols;
                        if (pDiffCols > 0)
                        {
                            colSummary = $"{colSummary} | {pSummary}";
                        }
                        if (!string.IsNullOrWhiteSpace(previewDetails))
                        {
                            countReport.AppendLine();
                            countReport.AppendLine("--- Comparaison des lignes communes ---");
                            countReport.Append(previewDetails);
                        }
                    }

                    return ("KO_ROW_COUNT", countReport.ToString().TrimEnd(), Math.Max(rowCountDiff, 1), diffColCount, colSummary);
                }

                return GenerateDiffReport(refRows, newRows, commonCols);
            }
            catch (Exception e)
            {
                return ("KO_ERROR", $"Technical error while generating the differential for {tableName}: {e.Message}", Math.Max(dfRef.Rows.Count, dfNew.Rows.Count), 0, "Erreur technique");
            }
        }

        private static bool IsTechnicalAuditColumn(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName))
            {
                return false;
            }

            string upper = columnName.Trim().ToUpperInvariant();
            return upper.StartsWith("TSTAMP_", StringComparison.Ordinal) ||
                   upper.StartsWith("D_JOB_", StringComparison.Ordinal) ||
                   upper.StartsWith("NM_JOB_", StringComparison.Ordinal) ||
                   upper.Equals("D_GEST_DMOD", StringComparison.Ordinal) ||
                   upper.Equals("C_ID_GEST_DMOD", StringComparison.Ordinal) ||
                   upper.Equals("TY_DMOD", StringComparison.Ordinal) ||
                   upper.Contains("DTISMAJ", StringComparison.Ordinal) ||
                   upper.Contains("CUSMAJ", StringComparison.Ordinal) ||
                   upper.Contains("PGMMAJ", StringComparison.Ordinal) ||
                   upper.Contains("TIMMAJ", StringComparison.Ordinal);
        }

        /// <summary>
        /// Extracts only the required columns and normalizes the data into a lightweight string array format.
        /// </summary>
        private static List<string[]> ExtractAndNormalize(DataTable dt, int[] columnIndices)
        {
            var result = new List<string[]>(dt.Rows.Count);

            foreach (DataRow row in dt.Rows)
            {
                string[] stringRow = new string[columnIndices.Length];

                for (int i = 0; i < columnIndices.Length; i++)
                {
                    object cellValue = row[columnIndices[i]];

                    if (cellValue == DBNull.Value || cellValue == null)
                    {
                        stringRow[i] = string.Empty;
                        continue;
                    }

                    string value = (cellValue.ToString() ?? string.Empty).Trim();

                    if (value.Length == 0 ||
                        string.Equals(value, "nan", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        stringRow[i] = string.Empty;
                    }
                    else if (TryNormalizeNumericValue(value, out string normalizedNumber))
                    {
                        stringRow[i] = normalizedNumber;
                    }
                    else
                    {
                        stringRow[i] = value;
                    }
                }
                result.Add(stringRow);
            }

            return result;
        }

        /// <summary>
        /// Normalizes numeric values using 128-bit decimal precision (avoiding IEEE 754 float precision loss on long IDs),
        /// while preserving leading-zero alphanumeric codes (e.g. "00123").
        /// </summary>
        private static bool TryNormalizeNumericValue(string value, out string normalized)
        {
            normalized = string.Empty;

            bool hasDot = value.IndexOf('.') >= 0;
            bool hasComma = value.IndexOf(',') >= 0;

            // Preserve pure integer codes that start with leading zero(s) (e.g., "001", "05") unless they contain a decimal point
            if (!hasDot && !hasComma)
            {
                string unsignedVal = (value[0] == '+' || value[0] == '-') ? value.Substring(1) : value;
                if (unsignedVal.Length > 1 && unsignedVal[0] == '0')
                {
                    return false;
                }
            }

            // Do not convert strings containing both dot and comma (thousands separators)
            if (hasDot && hasComma) return false;

            string candidate = hasComma ? value.Replace(',', '.') : value;

            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
            if (decimal.TryParse(candidate, style, CultureInfo.InvariantCulture, out decimal decValue))
            {
                normalized = Math.Round(decValue, 4).ToString("0.####", CultureInfo.InvariantCulture);
                if (normalized == "-0") normalized = "0";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Customized comparator to quickly sort a list of string arrays.
        /// </summary>
        private sealed class StringArrayComparer : IComparer<string[]>
        {
            public static readonly StringArrayComparer Instance = new StringArrayComparer();

            public int Compare(string[]? x, string[]? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                int len = Math.Min(x.Length, y.Length);
                for (int i = 0; i < len; i++)
                {
                    int cmp = string.CompareOrdinal(x[i], y[i]);
                    if (cmp != 0) return cmp;
                }
                return x.Length.CompareTo(y.Length);
            }
        }

        /// <summary>
        /// Compares cell by cell from the lightweight lists and generates a report.
        /// Counts all differing rows accurately while capping the text report at MAX_DIFFS_TO_REPORT.
        /// </summary>
        private static (string Status, string Details, int ErrorRows, int DiffColsCount, string DiffColsSummary) GenerateDiffReport(
            List<string[]> refRows,
            List<string[]> newRows,
            List<string> commonCols)
        {
            var bodyReport = new StringBuilder();
            int totalDiffRows = 0;
            int totalCellDiffs = 0;
            bool truncatedMessageAdded = false;
            var diffColumnsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < refRows.Count; i++)
            {
                bool rowHasDiff = false;
                StringBuilder? rowDiffs = totalDiffRows < MAX_DIFFS_TO_REPORT ? new StringBuilder() : null;

                for (int j = 0; j < commonCols.Count; j++)
                {
                    string val1 = refRows[i][j];
                    string val2 = newRows[i][j];

                    if (!string.Equals(val1, val2, StringComparison.Ordinal))
                    {
                        rowHasDiff = true;
                        totalCellDiffs++;
                        diffColumnsSet.Add(commonCols[j]);
                        if (rowDiffs != null)
                        {
                            rowDiffs.AppendLine(
                                $"    🔸 [{commonCols[j]}] : 📘 BASELINE = '{val1}'  |  🎯 TARGET = '{val2}'");
                        }
                    }
                }

                if (rowHasDiff)
                {
                    totalDiffRows++;

                    if (rowDiffs != null)
                    {
                        string rowContext = BuildRowContextKey(refRows[i], newRows[i], commonCols);
                        bodyReport.AppendLine(
                            string.IsNullOrWhiteSpace(rowContext)
                                ? $"  ▶ Ligne #{i + 1} en écart :"
                                : $"  ▶ Ligne #{i + 1} ({rowContext}) :");
                        bodyReport.Append(rowDiffs.ToString());
                        bodyReport.AppendLine();
                    }
                    else if (!truncatedMessageAdded)
                    {
                        truncatedMessageAdded = true;
                    }
                }
            }

            if (totalDiffRows == 0)
            {
                return ("OK", string.Empty, 0, 0, "✓ Identique");
            }

            var orderedDiffCols = diffColumnsSet.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            string colsSummary = orderedDiffCols.Count <= 4
                ? string.Join(", ", orderedDiffCols)
                : $"{string.Join(", ", orderedDiffCols.Take(4))} (+{orderedDiffCols.Count - 4})";

            var finalReport = new StringBuilder();
            finalReport.AppendLine(
                $"📊 SYNTHÈSE : {totalDiffRows} ligne(s) en écart sur {refRows.Count} | " +
                $"{orderedDiffCols.Count} colonne(s) impactée(s) ({totalCellDiffs} cellule(s) différente(s))");
            finalReport.AppendLine($"📋 Colonnes modifiées : {string.Join(", ", orderedDiffCols)}");
            finalReport.AppendLine("--------------------------------------------------------------------------------");
            finalReport.Append(bodyReport.ToString());

            if (truncatedMessageAdded)
            {
                finalReport.AppendLine($"\n[WARNING] Maximum number of reportable differences ({MAX_DIFFS_TO_REPORT}) reached ({totalDiffRows} total differing rows). Truncating report...");
            }

            return ("KO", finalReport.ToString().TrimEnd(), totalDiffRows, orderedDiffCols.Count, colsSummary);
        }

        private static string BuildRowContextKey(string[] refRow, string[] newRow, List<string> commonCols)
        {
            var keys = new List<string>();
            for (int j = 0; j < commonCols.Count && keys.Count < 3; j++)
            {
                string col = commonCols[j];
                string val = !string.IsNullOrWhiteSpace(refRow[j]) ? refRow[j] : newRow[j];
                if (string.IsNullOrWhiteSpace(val)) continue;

                if (col.Contains("IDN", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("CNT", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("FONDS", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("FND", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("ORD", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("SEQ", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("COURS", StringComparison.OrdinalIgnoreCase) ||
                    col.Contains("PRD", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add($"{col}={val}");
                }
            }

            return string.Join(", ", keys);
        }
    }
}