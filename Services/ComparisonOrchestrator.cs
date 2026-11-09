using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using AutoActivator.Models;
using AutoActivator.Utils;

namespace AutoActivator.Services
{
    public class ComparisonOrchestrator
    {
        /// <summary>
        /// Extracts the environment, size, and signature from the file name for filtering purposes.
        /// </summary>
        public string[] GetFileIds(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return Array.Empty<string>();

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var parts = fileName.Split('_');

            if (parts.Length >= 4)
            {
                return new[] { parts[1], parts[2], parts[3] };
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// Returns a list of compatible target files (sharing the same Env, Size, and Signature).
        /// </summary>
        public List<string> GetCompatibleTargetFiles(string baseFilePath, IEnumerable<string> allAvailableFiles)
        {
            var baseIds = GetFileIds(baseFilePath);
            if (baseIds.Length != 3 || allAvailableFiles == null) return new List<string>();

            return allAvailableFiles.Where(file =>
            {
                if (file.Equals(baseFilePath, StringComparison.OrdinalIgnoreCase)) return false;

                var targetIds = GetFileIds(file);
                return targetIds.Length == 3 &&
                       string.Equals(targetIds[0], baseIds[0], StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(targetIds[1], baseIds[1], StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(targetIds[2], baseIds[2], StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        /// <summary>
        /// Runs the full comparison. Keeps only the most recent contract in memory for each Test ID to avoid duplication,
        /// and parses all tables per block in a single streaming pass.
        /// </summary>
        public ComparisonReport RunFullComparison(string baseFile, string targetFile)
        {
            var report = new ComparisonReport();

            var baseBlocks = CsvFormatter.SplitAndFilterMostRecentByTestId(baseFile);
            var targetBlocks = CsvFormatter.SplitAndFilterMostRecentByTestId(targetFile);

            if (baseBlocks.Count == 0 && targetBlocks.Count == 0)
            {
                throw new Exception("No data found in either file. Is the format correct?");
            }

            var baseIds = baseBlocks.Keys.ToList();
            var targetIds = targetBlocks.Keys.ToList();

            var commonTestIds = baseIds.Intersect(targetIds, StringComparer.OrdinalIgnoreCase).ToList();

            report.TotalBaseTests = baseIds.Count;
            report.TotalTargetTests = targetIds.Count;
            report.ComparedTestsCount = commonTestIds.Count;
            report.MissingInTarget = baseIds.Except(targetIds, StringComparer.OrdinalIgnoreCase).ToList();
            report.MissingInBase = targetIds.Except(baseIds, StringComparer.OrdinalIgnoreCase).ToList();

            if (commonTestIds.Count == 0)
            {
                throw new Exception($"No common Test IDs found. Comparison impossible. (Base: {baseIds.Count} tests, Target: {targetIds.Count} tests)");
            }

            string baseFileName = Path.GetFileName(baseFile);
            string targetFileName = Path.GetFileName(targetFile);

            foreach (var testId in commonTestIds)
            {
                string baseContent = baseBlocks[testId];
                string targetContent = targetBlocks[testId];

                // Single-pass extraction of all tables in Base and Target blocks
                Dictionary<string, DataTable> baseTables = CsvFormatter.LoadAllTablesFromContent(baseContent);
                Dictionary<string, DataTable> targetTables = CsvFormatter.LoadAllTablesFromContent(targetContent);

                try
                {
                    string product = "UNKNOWN";
                    if (baseTables.TryGetValue("FJ1.TB5UCON", out DataTable uconTable) &&
                        uconTable != null &&
                        uconTable.Rows.Count > 0 &&
                        uconTable.Columns.Contains("IT5UCONCASU"))
                    {
                        string rawProduct = uconTable.Rows[0]["IT5UCONCASU"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawProduct))
                            product = rawProduct;
                    }

                    if (!report.TestMetrics.ContainsKey(testId)) report.TestMetrics[testId] = new ComparisonMetrics();
                    if (!report.ProductMetrics.ContainsKey(product)) report.ProductMetrics[product] = new ComparisonMetrics();

                    var tablesToCompare = baseTables.Keys
                        .Union(targetTables.Keys, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var tableName in tablesToCompare)
                    {
                        string tableType = tableName.StartsWith("LV.", StringComparison.OrdinalIgnoreCase)
                            ? "LISA"
                            : (tableName.StartsWith("FJ1.", StringComparison.OrdinalIgnoreCase) ? "ELIA" : "COMBINED");

                        string displayTableName = $"[{testId}] {tableName}";

                        baseTables.TryGetValue(tableName, out DataTable df1);
                        targetTables.TryGetValue(tableName, out DataTable df2);

                        CompareAndAppendToReport(
                            df1 ?? new DataTable(tableName),
                            df2 ?? new DataTable(tableName),
                            displayTableName,
                            tableType,
                            report,
                            baseFileName,
                            targetFileName,
                            testId,
                            product);
                    }
                }
                finally
                {
                    foreach (var dt in baseTables.Values) dt?.Dispose();
                    foreach (var dt in targetTables.Values) dt?.Dispose();
                }
            }

            CalculateGlobalScore(report);

            return report;
        }

        private void CompareAndAppendToReport(
            DataTable df1,
            DataTable df2,
            string displayTableName,
            string type,
            ComparisonReport report,
            string baseFileName,
            string targetFileName,
            string testId,
            string product)
        {
            var (status, details, errorRows) = Comparator.CompareDataTablesDetailed(df1, df2, displayTableName);

            int rowsCount = Math.Max(df1?.Rows.Count ?? 0, df2?.Rows.Count ?? 0);
            int currentErrors = (status == "OK" || status == "OK_EMPTY") ? 0 : Math.Max(1, Math.Min(errorRows, Math.Max(rowsCount, 1)));

            var fileResult = new FileComparisonResult
            {
                FileType = type,
                BaseFileName = baseFileName,
                TargetFileName = targetFileName,
                TableName = displayTableName,
                Status = status,
                ErrorDetails = details,
                TotalRows = rowsCount,
                ErrorRows = currentErrors
            };

            report.FileResults.Add(fileResult);

            report.TotalRowsCompared += rowsCount;
            report.TotalDifferencesFound += currentErrors;

            if (rowsCount > 0)
            {
                report.TestMetrics[testId].TotalRows += rowsCount;
                report.TestMetrics[testId].ErrorRows += currentErrors;

                report.ProductMetrics[product].TotalRows += rowsCount;
                report.ProductMetrics[product].ErrorRows += currentErrors;
            }
        }

        private void CalculateGlobalScore(ComparisonReport report)
        {
            if (report.TotalRowsCompared == 0)
            {
                report.GlobalSuccessPercentage = 100.0;
                return;
            }

            int successfulRows = Math.Max(0, report.TotalRowsCompared - report.TotalDifferencesFound);
            report.GlobalSuccessPercentage = Math.Round(((double)successfulRows / report.TotalRowsCompared) * 100.0, 2);
        }
    }
}