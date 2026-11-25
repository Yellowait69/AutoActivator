using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using AutoActivator.Config;
using AutoActivator.Models;

namespace AutoActivator.Services
{
    public sealed class RawIgnoredCellDifference
    {
        public string ColumnName { get; init; } = string.Empty;

        public ExclusionRuleCategory Category { get; init; }

        public int RowNumber { get; init; }

        public string RowContextKey { get; init; } = "-";

        public string SourceValue { get; init; } = string.Empty;

        public string TargetValue { get; init; } = string.Empty;
    }

    public sealed class TableComparisonDetail
    {
        public string Status { get; init; } = "OK";

        public string Details { get; init; } = string.Empty;

        public int ErrorRows { get; init; }

        public int BaseRowCount { get; init; }

        public int TargetRowCount { get; init; }

        public List<string> DifferingColumns { get; init; } = new List<string>();

        public int DifferingColumnsCount => DifferingColumns.Count;

        public string DifferingColumnsSummary { get; init; } = "-";

        public int ActiveCellDifferencesCount { get; init; }

        public List<CellDifferenceItem> ActiveDifferences { get; init; } =
            new List<CellDifferenceItem>();

        public List<RawIgnoredCellDifference> IgnoredDifferences { get; init; } =
            new List<RawIgnoredCellDifference>();
    }

    public static class Comparator
    {
        private const int MAX_DIFFS_TO_REPORT = 100;
        private const int MAX_CELL_ITEMS_PER_TABLE = 600;
        private const int MAX_IGNORED_ITEMS_PER_TABLE = 1000;

        public static (string Status, string Details) CompareDataTables(
            DataTable dfRef,
            DataTable dfNew,
            string tableName)
        {
            var result = CompareDataTablesFull(dfRef, dfNew, tableName, ignoreTechnicalColumns: false);
            return (result.Status, result.Details);
        }

        public static (string Status, string Details, int ErrorRows) CompareDataTablesDetailed(
            DataTable dfRef,
            DataTable dfNew,
            string tableName)
        {
            var result = CompareDataTablesFull(dfRef, dfNew, tableName, ignoreTechnicalColumns: false);
            return (result.Status, result.Details, result.ErrorRows);
        }

        public static (string Status, string Details, int ErrorRows, int DiffColsCount, string DiffColsSummary) CompareDataTablesExtended(
            DataTable dfRef,
            DataTable dfNew,
            string tableName,
            bool ignoreTechnicalColumns = false)
        {
            var result = CompareDataTablesFull(dfRef, dfNew, tableName, ignoreTechnicalColumns);
            return (result.Status, result.Details, result.ErrorRows, result.DifferingColumnsCount, result.DifferingColumnsSummary);
        }

        /// <summary>
        /// Exécute la comparaison complète d'une table SQL (colonnes actives + capture séparée des différences ignorées).
        /// RÈGLE IMPÉRATIVE : les colonnes exclues servent uniquement de tie-breaker secondaire d'alignement et
        /// n'affectent JAMAIS le statut OK/KO, ErrorRows ni le taux de conformité.
        /// </summary>
        public static TableComparisonDetail CompareDataTablesFull(
            DataTable dfRef,
            DataTable dfNew,
            string tableName,
            bool ignoreTechnicalColumns = false)
        {
            if (dfRef == null || dfNew == null)
            {
                int fallbackErrors = Math.Max(dfRef?.Rows.Count ?? 0, dfNew?.Rows.Count ?? 0);
                return new TableComparisonDetail
                {
                    Status = "KO_NULL_DATA",
                    Details = $"One or both DataTables are null for table {tableName}.",
                    ErrorRows = fallbackErrors,
                    BaseRowCount = dfRef?.Rows.Count ?? 0,
                    TargetRowCount = dfNew?.Rows.Count ?? 0,
                    DifferingColumnsSummary = "Table NULL"
                };
            }

            int baseCount = dfRef.Rows.Count;
            int targetCount = dfNew.Rows.Count;

            if (baseCount == 0 && targetCount == 0)
            {
                return new TableComparisonDetail
                {
                    Status = "OK_EMPTY",
                    Details = string.Empty,
                    ErrorRows = 0,
                    BaseRowCount = 0,
                    TargetRowCount = 0,
                    DifferingColumnsSummary = "Vide (0/0)"
                };
            }

            if (baseCount == 0 || targetCount == 0)
            {
                return BuildMissingDataDetail(dfRef, dfNew, tableName, ignoreTechnicalColumns);
            }

            try
            {
                var allColsRef = dfRef.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                var allColsNew = dfNew.Columns.Cast<DataColumn>().Select(c => c.ColumnName);

                var commonAllCols = allColsRef
                    .Intersect(allColsNew, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var activeCols = new List<string>(commonAllCols.Count);
                var excludedCols = new List<(string Name, ExclusionRuleCategory Category)>();

                foreach (string col in commonAllCols)
                {
                    if (Exclusions.TryGetExclusionCategory(tableName, col, ignoreTechnicalColumns, out ExclusionRuleCategory cat))
                    {
                        excludedCols.Add((col, cat));
                    }
                    else
                    {
                        activeCols.Add(col);
                    }
                }

                if (activeCols.Count == 0)
                {
                    return new TableComparisonDetail
                    {
                        Status = "KO_NO_COMMON_COLS",
                        Details = "No active business columns found after applying exclusion filters.",
                        ErrorRows = Math.Max(baseCount, targetCount),
                        BaseRowCount = baseCount,
                        TargetRowCount = targetCount,
                        DifferingColumnsSummary = "Aucune colonne active"
                    };
                }

                int[] refActiveIndices = activeCols.Select(c => dfRef.Columns[c]!.Ordinal).ToArray();
                int[] newActiveIndices = activeCols.Select(c => dfNew.Columns[c]!.Ordinal).ToArray();
                int[] refExcludedIndices = excludedCols.Select(c => dfRef.Columns[c.Name]!.Ordinal).ToArray();
                int[] newExcludedIndices = excludedCols.Select(c => dfNew.Columns[c.Name]!.Ordinal).ToArray();

                List<NormalizedRow> refRows = ExtractNormalizedRows(dfRef, refActiveIndices, refExcludedIndices);
                List<NormalizedRow> newRows = ExtractNormalizedRows(dfNew, newActiveIndices, newExcludedIndices);

                refRows.Sort(NormalizedRowFullComparer.Instance);
                newRows.Sort(NormalizedRowFullComparer.Instance);

                if (refRows.Count == newRows.Count)
                {
                    return CompareEqualRowCounts(refRows, newRows, activeCols, excludedCols);
                }

                return CompareUnequalRowCounts(refRows, newRows, activeCols, excludedCols);
            }
            catch (Exception e)
            {
                return new TableComparisonDetail
                {
                    Status = "KO_ERROR",
                    Details = $"Technical error while generating the differential for {tableName}: {e.Message}",
                    ErrorRows = Math.Max(baseCount, targetCount),
                    BaseRowCount = baseCount,
                    TargetRowCount = targetCount,
                    DifferingColumnsSummary = "Erreur technique"
                };
            }
        }

        private static TableComparisonDetail BuildMissingDataDetail(
            DataTable dfRef,
            DataTable dfNew,
            string tableName,
            bool ignoreTechnicalColumns)
        {
            int baseCount = dfRef.Rows.Count;
            int targetCount = dfNew.Rows.Count;
            int missingRows = Math.Max(baseCount, targetCount);
            bool presentInBaseOnly = baseCount > 0;
            DataTable populated = presentInBaseOnly ? dfRef : dfNew;

            var missingSb = new StringBuilder();
            missingSb.AppendLine(
                $"ECART DE PRESENCE DE DONNEES : SOURCE = {baseCount} ligne(s) vs TARGET = {targetCount} ligne(s) pour {tableName}.");
            missingSb.AppendLine(presentInBaseOnly
                ? "Enregistrement(s) present(s) dans SOURCE mais absent(s) de TARGET :"
                : "Enregistrement(s) ajoute(s) dans TARGET (absent(s) de SOURCE) :");

            var cellItems = new List<CellDifferenceItem>(Math.Min(MAX_CELL_ITEMS_PER_TABLE, populated.Rows.Count));
            int rowLimit = Math.Min(MAX_CELL_ITEMS_PER_TABLE, populated.Rows.Count);

            for (int r = 0; r < rowLimit; r++)
            {
                DataRow row = populated.Rows[r];
                string contextKey = ExtractDataRowContextKey(row, populated.Columns);
                var previewFields = new List<string>(6);

                foreach (DataColumn col in populated.Columns)
                {
                    if (previewFields.Count >= 6)
                    {
                        break;
                    }

                    if (Exclusions.TryGetExclusionCategory(tableName, col.ColumnName, ignoreTechnicalColumns, out _))
                    {
                        continue;
                    }

                    string val = NormalizeSingleCell(row[col]);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        previewFields.Add($"{col.ColumnName}={val}");
                    }
                }

                if (previewFields.Count == 0)
                {
                    foreach (DataColumn col in populated.Columns)
                    {
                        if (previewFields.Count >= 4)
                        {
                            break;
                        }
                        string val = NormalizeSingleCell(row[col]);
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            previewFields.Add($"{col.ColumnName}={val}");
                        }
                    }
                }

                string rowSummary = previewFields.Count > 0
                    ? string.Join(" | ", previewFields)
                    : "(enregistrement présent)";

                cellItems.Add(new CellDifferenceItem
                {
                    RowNumber = r + 1,
                    RowLabel = presentInBaseOnly ? $"Source #{r + 1}" : $"Target #{r + 1}",
                    RowContextKey = contextKey,
                    ColumnName = "(Enregistrement complet)",
                    SourceValue = presentInBaseOnly ? rowSummary : "(absent dans Source)",
                    TargetValue = presentInBaseOnly ? "(absent dans Target)" : rowSummary,
                    DiffKind = presentInBaseOnly ? "MISSING_IN_TARGET" : "EXTRA_IN_TARGET"
                });

                if (r < 10)
                {
                    string keyPrefix = string.IsNullOrWhiteSpace(contextKey) || contextKey == "-"
                        ? string.Empty
                        : $" [{contextKey}]";
                    missingSb.AppendLine($"  - Ligne #{r + 1}{keyPrefix} : {rowSummary}");
                }
            }

            if (populated.Rows.Count > 10)
            {
                missingSb.AppendLine($"  ... (+{populated.Rows.Count - 10} autre(s) enregistrement(s))");
            }

            string summary = presentInBaseOnly
                ? $"Absent dans Target ({baseCount} -> 0)"
                : $"Ajouté dans Target (0 -> {targetCount})";

            return new TableComparisonDetail
            {
                Status = "KO_MISSING_DATA",
                Details = missingSb.ToString().TrimEnd(),
                ErrorRows = missingRows,
                BaseRowCount = baseCount,
                TargetRowCount = targetCount,
                DifferingColumns = new List<string>(),
                DifferingColumnsSummary = summary,
                ActiveCellDifferencesCount = missingRows,
                ActiveDifferences = cellItems
            };
        }

        private static TableComparisonDetail CompareEqualRowCounts(
            List<NormalizedRow> refRows,
            List<NormalizedRow> newRows,
            List<string> activeCols,
            List<(string Name, ExclusionRuleCategory Category)> excludedCols)
        {
            var bodyReport = new StringBuilder();
            int totalDiffRows = 0;
            int totalActiveCellDiffs = 0;
            bool truncatedMessageAdded = false;
            var diffColumnsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeCellItems = new List<CellDifferenceItem>();
            var ignoredCellItems = new List<RawIgnoredCellDifference>();

            for (int i = 0; i < refRows.Count; i++)
            {
                NormalizedRow rRow = refRows[i];
                NormalizedRow nRow = newRows[i];
                string? lazyRowContext = null;

                bool rowHasActiveDiff = false;
                StringBuilder? rowDiffs = totalDiffRows < MAX_DIFFS_TO_REPORT ? new StringBuilder() : null;

                for (int j = 0; j < activeCols.Count; j++)
                {
                    string val1 = rRow.ActiveValues[j];
                    string val2 = nRow.ActiveValues[j];

                    if (!string.Equals(val1, val2, StringComparison.Ordinal))
                    {
                        rowHasActiveDiff = true;
                        totalActiveCellDiffs++;
                        string colName = activeCols[j];
                        diffColumnsSet.Add(colName);

                        lazyRowContext ??= BuildRowContextKey(rRow, nRow, activeCols, excludedCols);

                        if (activeCellItems.Count < MAX_CELL_ITEMS_PER_TABLE)
                        {
                            activeCellItems.Add(new CellDifferenceItem
                            {
                                RowNumber = i + 1,
                                RowLabel = $"#{i + 1}",
                                RowContextKey = lazyRowContext,
                                ColumnName = colName,
                                SourceValue = val1,
                                TargetValue = val2,
                                DiffKind = "VALUE_DIFF"
                            });
                        }

                        if (rowDiffs != null)
                        {
                            rowDiffs.AppendLine(
                                $"    - [{colName}] : SOURCE = '{val1}'  |  TARGET = '{val2}'");
                        }
                    }
                }

                if (rowHasActiveDiff)
                {
                    totalDiffRows++;
                    if (rowDiffs != null)
                    {
                        lazyRowContext ??= BuildRowContextKey(rRow, nRow, activeCols, excludedCols);
                        bodyReport.AppendLine(
                            string.IsNullOrWhiteSpace(lazyRowContext) || lazyRowContext == "-"
                                ? $"  > Ligne #{i + 1} en écart :"
                                : $"  > Ligne #{i + 1} ({lazyRowContext}) :");
                        bodyReport.Append(rowDiffs);
                        bodyReport.AppendLine();
                    }
                    else if (!truncatedMessageAdded)
                    {
                        truncatedMessageAdded = true;
                    }
                }

                CollectIgnoredDiffsForPair(
                    rRow,
                    nRow,
                    i + 1,
                    activeCols,
                    excludedCols,
                    ref lazyRowContext,
                    ignoredCellItems);
            }

            if (totalDiffRows == 0)
            {
                return new TableComparisonDetail
                {
                    Status = "OK",
                    Details = string.Empty,
                    ErrorRows = 0,
                    BaseRowCount = refRows.Count,
                    TargetRowCount = newRows.Count,
                    DifferingColumnsSummary = "Identique",
                    ActiveCellDifferencesCount = 0,
                    ActiveDifferences = activeCellItems,
                    IgnoredDifferences = ignoredCellItems
                };
            }

            List<string> orderedDiffCols = diffColumnsSet
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string colsSummary = FormatColumnsSummary(orderedDiffCols);

            var finalReport = new StringBuilder();
            finalReport.AppendLine(
                $"SYNTHESE : {totalDiffRows} ligne(s) en écart sur {refRows.Count} | " +
                $"{orderedDiffCols.Count} colonne(s) métier impactée(s) ({totalActiveCellDiffs} cellule(s) différente(s))");
            finalReport.AppendLine($"Colonnes modifiées : {string.Join(", ", orderedDiffCols)}");
            finalReport.AppendLine("--------------------------------------------------------------------------------");
            finalReport.Append(bodyReport);

            if (truncatedMessageAdded)
            {
                finalReport.AppendLine(
                    $"\n[WARNING] Maximum number of reportable differences ({MAX_DIFFS_TO_REPORT}) reached ({totalDiffRows} total differing rows).");
            }

            return new TableComparisonDetail
            {
                Status = "KO",
                Details = finalReport.ToString().TrimEnd(),
                ErrorRows = totalDiffRows,
                BaseRowCount = refRows.Count,
                TargetRowCount = newRows.Count,
                DifferingColumns = orderedDiffCols,
                DifferingColumnsSummary = colsSummary,
                ActiveCellDifferencesCount = totalActiveCellDiffs,
                ActiveDifferences = activeCellItems,
                IgnoredDifferences = ignoredCellItems
            };
        }

        /// <summary>
        /// Alignement robuste sans décalage en cascade lorsque le nombre de lignes diffère (KO_ROW_COUNT) :
        /// 1. Apparie d'abord par double pointeur toutes les lignes identiques sur leurs colonnes actives ;
        /// 2. Compare côte à côte les lignes modifiées restantes ;
        /// 3. Liste explicitement les lignes en surplus ou manquantes sans aucun risque d'IndexOutOfRangeException.
        /// </summary>
        private static TableComparisonDetail CompareUnequalRowCounts(
            List<NormalizedRow> refRows,
            List<NormalizedRow> newRows,
            List<string> activeCols,
            List<(string Name, ExclusionRuleCategory Category)> excludedCols)
        {
            var unmatchedRef = new List<NormalizedRow>();
            var unmatchedNew = new List<NormalizedRow>();
            var ignoredCellItems = new List<RawIgnoredCellDifference>();
            var activeCellItems = new List<CellDifferenceItem>();
            var diffColumnsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int iRef = 0;
            int iNew = 0;
            int matchedPairNumber = 0;

            while (iRef < refRows.Count && iNew < newRows.Count)
            {
                int activeCmp = CompareOrdinalArrays(refRows[iRef].ActiveValues, newRows[iNew].ActiveValues);
                if (activeCmp == 0)
                {
                    matchedPairNumber++;
                    string? lazyCtx = null;
                    CollectIgnoredDiffsForPair(
                        refRows[iRef],
                        newRows[iNew],
                        matchedPairNumber,
                        activeCols,
                        excludedCols,
                        ref lazyCtx,
                        ignoredCellItems);
                    iRef++;
                    iNew++;
                }
                else if (activeCmp < 0)
                {
                    unmatchedRef.Add(refRows[iRef]);
                    iRef++;
                }
                else
                {
                    unmatchedNew.Add(newRows[iNew]);
                    iNew++;
                }
            }

            while (iRef < refRows.Count)
            {
                unmatchedRef.Add(refRows[iRef++]);
            }

            while (iNew < newRows.Count)
            {
                unmatchedNew.Add(newRows[iNew++]);
            }

            int pairedModifiedCount = Math.Min(unmatchedRef.Count, unmatchedNew.Count);
            int rowVolumeDelta = Math.Abs(refRows.Count - newRows.Count);
            int totalActiveCellDiffs = 0;

            var countReport = new StringBuilder();
            countReport.AppendLine(
                $"ECART DE VOLUME DE LIGNES : SOURCE = {refRows.Count} ligne(s) vs TARGET = {newRows.Count} ligne(s) " +
                $"(Écart volume : {rowVolumeDelta} ligne(s), Lignes communes identiques : {matchedPairNumber}).");

            for (int m = 0; m < pairedModifiedCount; m++)
            {
                NormalizedRow rRow = unmatchedRef[m];
                NormalizedRow nRow = unmatchedNew[m];
                int displayRowNo = matchedPairNumber + m + 1;
                string? ctx = BuildRowContextKey(rRow, nRow, activeCols, excludedCols);

                if (m < 25)
                {
                    countReport.AppendLine($"  > Ligne modifiée #{displayRowNo} ({ctx}) :");
                }

                for (int j = 0; j < activeCols.Count; j++)
                {
                    string val1 = rRow.ActiveValues[j];
                    string val2 = nRow.ActiveValues[j];
                    if (!string.Equals(val1, val2, StringComparison.Ordinal))
                    {
                        totalActiveCellDiffs++;
                        string colName = activeCols[j];
                        diffColumnsSet.Add(colName);

                        if (activeCellItems.Count < MAX_CELL_ITEMS_PER_TABLE)
                        {
                            activeCellItems.Add(new CellDifferenceItem
                            {
                                RowNumber = displayRowNo,
                                RowLabel = $"#{displayRowNo}",
                                RowContextKey = ctx,
                                ColumnName = colName,
                                SourceValue = val1,
                                TargetValue = val2,
                                DiffKind = "VALUE_DIFF"
                            });
                        }

                        if (m < 25)
                        {
                            countReport.AppendLine($"    - [{colName}] : SOURCE = '{val1}'  |  TARGET = '{val2}'");
                        }
                    }
                }

                CollectIgnoredDiffsForPair(
                    rRow,
                    nRow,
                    displayRowNo,
                    activeCols,
                    excludedCols,
                    ref ctx,
                    ignoredCellItems);
            }

            for (int m = pairedModifiedCount; m < unmatchedRef.Count; m++)
            {
                NormalizedRow extraBase = unmatchedRef[m];
                int displayRowNo = matchedPairNumber + m + 1;
                string ctx = BuildSingleRowContextKey(extraBase, activeCols, excludedCols);
                string summaryValues = BuildRowNonEmptyPreview(extraBase.ActiveValues, activeCols);
                totalActiveCellDiffs++;

                if (activeCellItems.Count < MAX_CELL_ITEMS_PER_TABLE)
                {
                    activeCellItems.Add(new CellDifferenceItem
                    {
                        RowNumber = displayRowNo,
                        RowLabel = $"Source #{extraBase.SourceRowIndex + 1}",
                        RowContextKey = ctx,
                        ColumnName = "(Enregistrement complet)",
                        SourceValue = summaryValues,
                        TargetValue = "(absent dans Target)",
                        DiffKind = "MISSING_IN_TARGET"
                    });
                }

                if (m - pairedModifiedCount < 15)
                {
                    string keyNote = string.IsNullOrWhiteSpace(ctx) || ctx == "-" ? string.Empty : $" [{ctx}]";
                    countReport.AppendLine($"  - Absent de TARGET (Source #{extraBase.SourceRowIndex + 1}{keyNote}) : {summaryValues}");
                }
            }

            for (int m = pairedModifiedCount; m < unmatchedNew.Count; m++)
            {
                NormalizedRow extraTarget = unmatchedNew[m];
                int displayRowNo = matchedPairNumber + m + 1;
                string ctx = BuildSingleRowContextKey(extraTarget, activeCols, excludedCols);
                string summaryValues = BuildRowNonEmptyPreview(extraTarget.ActiveValues, activeCols);
                totalActiveCellDiffs++;

                if (activeCellItems.Count < MAX_CELL_ITEMS_PER_TABLE)
                {
                    activeCellItems.Add(new CellDifferenceItem
                    {
                        RowNumber = displayRowNo,
                        RowLabel = $"Target #{extraTarget.SourceRowIndex + 1}",
                        RowContextKey = ctx,
                        ColumnName = "(Enregistrement complet)",
                        SourceValue = "(absent dans Source)",
                        TargetValue = summaryValues,
                        DiffKind = "EXTRA_IN_TARGET"
                    });
                }

                if (m - pairedModifiedCount < 15)
                {
                    string keyNote = string.IsNullOrWhiteSpace(ctx) || ctx == "-" ? string.Empty : $" [{ctx}]";
                    countReport.AppendLine($"  - Ajouté dans TARGET (Target #{extraTarget.SourceRowIndex + 1}{keyNote}) : {summaryValues}");
                }
            }

            List<string> orderedDiffCols = diffColumnsSet
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string volumeLabel = $"Volume ({refRows.Count} vs {newRows.Count} lig.)";
            string colSummary = orderedDiffCols.Count > 0
                ? $"{volumeLabel} | {FormatColumnsSummary(orderedDiffCols)}"
                : volumeLabel;

            int effectiveErrorRows = Math.Max(1, rowVolumeDelta + pairedModifiedCount);

            return new TableComparisonDetail
            {
                Status = "KO_ROW_COUNT",
                Details = countReport.ToString().TrimEnd(),
                ErrorRows = effectiveErrorRows,
                BaseRowCount = refRows.Count,
                TargetRowCount = newRows.Count,
                DifferingColumns = orderedDiffCols,
                DifferingColumnsSummary = colSummary,
                ActiveCellDifferencesCount = Math.Max(totalActiveCellDiffs, rowVolumeDelta),
                ActiveDifferences = activeCellItems,
                IgnoredDifferences = ignoredCellItems
            };
        }

        private static void CollectIgnoredDiffsForPair(
            NormalizedRow rRow,
            NormalizedRow nRow,
            int rowNumber,
            List<string> activeCols,
            List<(string Name, ExclusionRuleCategory Category)> excludedCols,
            ref string? lazyRowContext,
            List<RawIgnoredCellDifference> ignoredItems)
        {
            if (excludedCols.Count == 0 || ignoredItems.Count >= MAX_IGNORED_ITEMS_PER_TABLE)
            {
                return;
            }

            for (int k = 0; k < excludedCols.Count; k++)
            {
                string val1 = rRow.ExcludedValues[k];
                string val2 = nRow.ExcludedValues[k];

                if (!string.Equals(val1, val2, StringComparison.Ordinal))
                {
                    lazyRowContext ??= BuildRowContextKey(rRow, nRow, activeCols, excludedCols);
                    ignoredItems.Add(new RawIgnoredCellDifference
                    {
                        ColumnName = excludedCols[k].Name,
                        Category = excludedCols[k].Category,
                        RowNumber = rowNumber,
                        RowContextKey = lazyRowContext,
                        SourceValue = val1,
                        TargetValue = val2
                    });

                    if (ignoredItems.Count >= MAX_IGNORED_ITEMS_PER_TABLE)
                    {
                        break;
                    }
                }
            }
        }

        private static string FormatColumnsSummary(IReadOnlyList<string> orderedDiffCols)
        {
            if (orderedDiffCols.Count == 0)
            {
                return "-";
            }

            return orderedDiffCols.Count <= 4
                ? string.Join(", ", orderedDiffCols)
                : $"{string.Join(", ", orderedDiffCols.Take(4))} (+{orderedDiffCols.Count - 4})";
        }

        private sealed class NormalizedRow
        {
            public NormalizedRow(string[] activeValues, string[] excludedValues, int sourceRowIndex)
            {
                ActiveValues = activeValues;
                ExcludedValues = excludedValues;
                SourceRowIndex = sourceRowIndex;
            }

            public string[] ActiveValues { get; }

            public string[] ExcludedValues { get; }

            public int SourceRowIndex { get; }
        }

        private sealed class NormalizedRowFullComparer : IComparer<NormalizedRow>
        {
            public static readonly NormalizedRowFullComparer Instance = new NormalizedRowFullComparer();

            public int Compare(NormalizedRow? x, NormalizedRow? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                int activeCmp = CompareOrdinalArrays(x.ActiveValues, y.ActiveValues);
                if (activeCmp != 0)
                {
                    return activeCmp;
                }

                int excludedCmp = CompareOrdinalArrays(x.ExcludedValues, y.ExcludedValues);
                if (excludedCmp != 0)
                {
                    return excludedCmp;
                }

                return x.SourceRowIndex.CompareTo(y.SourceRowIndex);
            }
        }

        private static int CompareOrdinalArrays(string[] x, string[] y)
        {
            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                int cmp = string.CompareOrdinal(x[i], y[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return x.Length.CompareTo(y.Length);
        }

        private static List<NormalizedRow> ExtractNormalizedRows(
            DataTable dt,
            int[] activeIndices,
            int[] excludedIndices)
        {
            var result = new List<NormalizedRow>(dt.Rows.Count);

            for (int rowIndex = 0; rowIndex < dt.Rows.Count; rowIndex++)
            {
                DataRow row = dt.Rows[rowIndex];
                string[] activeValues = new string[activeIndices.Length];
                for (int i = 0; i < activeIndices.Length; i++)
                {
                    activeValues[i] = NormalizeSingleCell(row[activeIndices[i]]);
                }

                string[] excludedValues = excludedIndices.Length == 0
                    ? Array.Empty<string>()
                    : new string[excludedIndices.Length];

                for (int k = 0; k < excludedIndices.Length; k++)
                {
                    excludedValues[k] = NormalizeSingleCell(row[excludedIndices[k]]);
                }

                result.Add(new NormalizedRow(activeValues, excludedValues, rowIndex));
            }

            return result;
        }

        private static string NormalizeSingleCell(object? cellValue)
        {
            if (cellValue == DBNull.Value || cellValue == null)
            {
                return string.Empty;
            }

            string value = (cellValue.ToString() ?? string.Empty).Trim();

            if (value.Length == 0 ||
                string.Equals(value, "nan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (TryNormalizeNumericValue(value, out string normalizedNumber))
            {
                return normalizedNumber;
            }

            return value;
        }

        private static bool TryNormalizeNumericValue(string value, out string normalized)
        {
            normalized = string.Empty;

            bool hasDot = value.IndexOf('.') >= 0;
            bool hasComma = value.IndexOf(',') >= 0;

            if (!hasDot && !hasComma)
            {
                string unsignedVal = (value[0] == '+' || value[0] == '-') ? value.Substring(1) : value;
                if (unsignedVal.Length > 1 && unsignedVal[0] == '0')
                {
                    return false;
                }
            }

            if (hasDot && hasComma)
            {
                return false;
            }

            string candidate = hasComma ? value.Replace(',', '.') : value;

            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
            if (decimal.TryParse(candidate, style, CultureInfo.InvariantCulture, out decimal decValue))
            {
                normalized = Math.Round(decValue, 4).ToString("0.####", CultureInfo.InvariantCulture);
                if (normalized == "-0")
                {
                    normalized = "0";
                }
                return true;
            }

            return false;
        }

        private static string BuildRowContextKey(
            NormalizedRow refRow,
            NormalizedRow newRow,
            List<string> activeCols,
            List<(string Name, ExclusionRuleCategory Category)> excludedCols)
        {
            var keys = new List<string>(4);

            for (int j = 0; j < activeCols.Count && keys.Count < 3; j++)
            {
                string col = activeCols[j];
                if (!IsBusinessKeyColumn(col))
                {
                    continue;
                }

                string val = !string.IsNullOrWhiteSpace(refRow.ActiveValues[j])
                    ? refRow.ActiveValues[j]
                    : newRow.ActiveValues[j];
                if (!string.IsNullOrWhiteSpace(val))
                {
                    keys.Add($"{col}={val}");
                }
            }

            for (int k = 0; k < excludedCols.Count && keys.Count < 3; k++)
            {
                string col = excludedCols[k].Name;
                if (!IsIdentifierExcludedColumn(col))
                {
                    continue;
                }

                string val = !string.IsNullOrWhiteSpace(refRow.ExcludedValues[k])
                    ? refRow.ExcludedValues[k]
                    : newRow.ExcludedValues[k];
                if (!string.IsNullOrWhiteSpace(val))
                {
                    keys.Add($"{col}={val}");
                }
            }

            return keys.Count > 0 ? string.Join(", ", keys) : "-";
        }

        private static string BuildSingleRowContextKey(
            NormalizedRow row,
            List<string> activeCols,
            List<(string Name, ExclusionRuleCategory Category)> excludedCols)
        {
            return BuildRowContextKey(row, row, activeCols, excludedCols);
        }

        private static string ExtractDataRowContextKey(DataRow row, DataColumnCollection columns)
        {
            var keys = new List<string>(3);
            foreach (DataColumn col in columns)
            {
                if (!IsBusinessKeyColumn(col.ColumnName) && !IsIdentifierExcludedColumn(col.ColumnName))
                {
                    continue;
                }

                string val = NormalizeSingleCell(row[col]);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    keys.Add($"{col.ColumnName}={val}");
                    if (keys.Count >= 3)
                    {
                        break;
                    }
                }
            }

            if (keys.Count == 0)
            {
                foreach (DataColumn col in columns)
                {
                    string val = NormalizeSingleCell(row[col]);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        keys.Add($"{col.ColumnName}={val}");
                        if (keys.Count >= 2)
                        {
                            break;
                        }
                    }
                }
            }

            return keys.Count > 0 ? string.Join(", ", keys) : "-";
        }

        private static string BuildRowNonEmptyPreview(string[] activeValues, List<string> activeCols)
        {
            var items = new List<string>(6);
            for (int j = 0; j < activeCols.Count && items.Count < 6; j++)
            {
                if (!string.IsNullOrWhiteSpace(activeValues[j]))
                {
                    items.Add($"{activeCols[j]}='{activeValues[j]}'");
                }
            }

            return items.Count > 0 ? string.Join(" | ", items) : "(toutes colonnes actives vides)";
        }

        private static bool IsBusinessKeyColumn(string col)
        {
            return col.Contains("FONDS", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("FND", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("GAR", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("ORD", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("SEQ", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("COURS", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("PRD", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("TY_", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("CTYP", StringComparison.OrdinalIgnoreCase) ||
                   col.Contains("ETAT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIdentifierExcludedColumn(string col)
        {
            return string.Equals(col, "NO_CNT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(col, "NO_AVT", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(col, "IT5UCONAIDN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(col, "IT5HDMDAIDN", StringComparison.OrdinalIgnoreCase);
        }
    }
}
