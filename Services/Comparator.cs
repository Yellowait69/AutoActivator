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
        public static (string Status, string Details, int ErrorRows) CompareDataTablesDetailed(DataTable dfRef, DataTable dfNew, string tableName)
        {
            if (dfRef == null || dfNew == null)
            {
                int fallbackErrors = Math.Max(dfRef?.Rows.Count ?? 0, dfNew?.Rows.Count ?? 0);
                return ("KO_NULL_DATA", $"One or both DataTables are null for table {tableName}.", fallbackErrors);
            }

            if (dfRef.Rows.Count == 0 && dfNew.Rows.Count == 0)
            {
                return ("OK_EMPTY", null, 0);
            }

            if (dfRef.Rows.Count == 0 || dfNew.Rows.Count == 0)
            {
                int missingRows = Math.Max(dfRef.Rows.Count, dfNew.Rows.Count);
                return ("KO_MISSING_DATA", $"Discrepancy: Source has {dfRef.Rows.Count} rows, Target has {dfNew.Rows.Count} rows for table {tableName}.", missingRows);
            }

            try
            {
                var exclusions = Exclusions.GetExclusionsForTable(tableName);

                var allColsRef = dfRef.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                var allColsNew = dfNew.Columns.Cast<DataColumn>().Select(c => c.ColumnName);

                var commonCols = allColsRef
                    .Intersect(allColsNew, StringComparer.OrdinalIgnoreCase)
                    .Except(exclusions, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (commonCols.Count == 0)
                {
                    return ("KO_NO_COMMON_COLS", "No common columns found after applying exclusion filters.", Math.Max(dfRef.Rows.Count, dfNew.Rows.Count));
                }

                int[] refIndices = commonCols.Select(c => dfRef.Columns[c].Ordinal).ToArray();
                int[] newIndices = commonCols.Select(c => dfNew.Columns[c].Ordinal).ToArray();

                List<string[]> refRows = ExtractAndNormalize(dfRef, refIndices);
                List<string[]> newRows = ExtractAndNormalize(dfNew, newIndices);

                var rowComparer = StringArrayComparer.Instance;
                refRows.Sort(rowComparer);
                newRows.Sort(rowComparer);

                if (refRows.Count != newRows.Count)
                {
                    int rowCountDiff = Math.Abs(refRows.Count - newRows.Count);
                    return ("KO_ROW_COUNT", $"Discrepancy in data volume: Source = {refRows.Count} rows vs Target = {newRows.Count} rows.", Math.Max(rowCountDiff, 1));
                }

                return GenerateDiffReport(refRows, newRows, commonCols);
            }
            catch (Exception e)
            {
                return ("KO_ERROR", $"Technical error while generating the differential for {tableName}: {e.Message}", Math.Max(dfRef.Rows.Count, dfNew.Rows.Count));
            }
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

                    string value = cellValue.ToString().Trim();

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
            normalized = null;

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

            public int Compare(string[] x, string[] y)
            {
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
        private static (string Status, string Details, int ErrorRows) GenerateDiffReport(List<string[]> refRows, List<string[]> newRows, List<string> commonCols)
        {
            var diffReport = new StringBuilder();
            int totalDiffRows = 0;
            bool truncatedMessageAdded = false;

            for (int i = 0; i < refRows.Count; i++)
            {
                bool rowHasDiff = false;
                StringBuilder rowDiffs = totalDiffRows < MAX_DIFFS_TO_REPORT ? new StringBuilder() : null;

                for (int j = 0; j < commonCols.Count; j++)
                {
                    string val1 = refRows[i][j];
                    string val2 = newRows[i][j];

                    if (!string.Equals(val1, val2, StringComparison.Ordinal))
                    {
                        rowHasDiff = true;
                        if (rowDiffs != null)
                        {
                            rowDiffs.AppendLine($"    -> {commonCols[j]} : Source = '{val1}' | Target = '{val2}'");
                        }
                        else
                        {
                            // Once report output is capped, a single column mismatch is enough to count the row as erroneous
                            break;
                        }
                    }
                }

                if (rowHasDiff)
                {
                    totalDiffRows++;

                    if (rowDiffs != null)
                    {
                        diffReport.AppendLine($"Row #{i + 1} is different:");
                        diffReport.Append(rowDiffs.ToString());
                        diffReport.AppendLine();
                    }
                    else if (!truncatedMessageAdded)
                    {
                        truncatedMessageAdded = true;
                    }
                }
            }

            if (totalDiffRows == 0)
            {
                return ("OK", null, 0);
            }

            if (truncatedMessageAdded)
            {
                diffReport.AppendLine($"\n[WARNING] Maximum number of reportable differences ({MAX_DIFFS_TO_REPORT}) reached ({totalDiffRows} total differing rows). Truncating report...");
            }

            return ("KO", diffReport.ToString(), totalDiffRows);
        }
    }
}