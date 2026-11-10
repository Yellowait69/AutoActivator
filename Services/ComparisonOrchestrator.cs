using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Utils;

namespace AutoActivator.Services
{
    public class ComparisonOrchestrator
    {
        private static readonly Regex HeaderContractRegex = new Regex(
            @"###\s*GLOBAL CONTRACT REPORT:\s*(?<contract>[^|]+?)\s*\|",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HeaderExcelRowRegex = new Regex(
            @"EXCEL ROW:\s*(?<row>#[0-9]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        public ComparisonReport RunFullComparison(
            string baseFile,
            string targetFile,
            bool ignoreTechnicalColumns = false)
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

                Dictionary<string, DataTable> baseTables = CsvFormatter.LoadAllTablesFromContent(baseContent);
                Dictionary<string, DataTable> targetTables = CsvFormatter.LoadAllTablesFromContent(targetContent);

                try
                {
                    string contractNumber = ResolveContractNumber(baseContent, targetContent, baseTables, targetTables, testId);
                    string excelRowDisplay = ResolveExcelRowDisplay(baseContent, targetContent);
                    string product = ResolveProductCode(baseTables, targetTables);
                    string processCode = ResolveProcessCode(baseTables, targetTables);
                    string channelCode = ResolveChannelCode(baseTables, targetTables);
                    var (businessChannel, bucpCmdpmt) = ResolveChannelAndPayment(channelCode, processCode, baseTables, targetTables);
                    string fundsSummary = ResolveFundsSummary(baseTables, targetTables);
                    bool isBranch23 = ResolveIsBranch23(product, baseTables, targetTables) ||
                                      (!string.IsNullOrWhiteSpace(fundsSummary) && fundsSummary != "-");
                    string branchDisplay = isBranch23 ? "Branche 23" : "Branche 21";

                    string productMetricKey = $"{product} ({branchDisplay})";

                    if (!report.TestMetrics.ContainsKey(testId))
                        report.TestMetrics[testId] = new ComparisonMetrics();
                    if (!report.ProductMetrics.ContainsKey(productMetricKey))
                        report.ProductMetrics[productMetricKey] = new ComparisonMetrics();

                    var tablesToCompare = baseTables.Keys
                        .Union(targetTables.Keys, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var tableName in tablesToCompare)
                    {
                        string tableType = tableName.StartsWith("LV.", StringComparison.OrdinalIgnoreCase)
                            ? "LISA"
                            : (tableName.StartsWith("FJ1.", StringComparison.OrdinalIgnoreCase) ? "ELIA" : "COMBINED");

                        string displayTableName = $"[{testId}] {tableName}";

                        baseTables.TryGetValue(tableName, out DataTable? df1);
                        targetTables.TryGetValue(tableName, out DataTable? df2);

                        CompareAndAppendToReport(
                            df1 ?? new DataTable(tableName),
                            df2 ?? new DataTable(tableName),
                            displayTableName,
                            tableType,
                            report,
                            baseFileName,
                            targetFileName,
                            testId,
                            excelRowDisplay,
                            contractNumber,
                            product,
                            processCode,
                            channelCode,
                            businessChannel,
                            bucpCmdpmt,
                            fundsSummary,
                            isBranch23,
                            branchDisplay,
                            productMetricKey,
                            ignoreTechnicalColumns);
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

        private static string ResolveContractNumber(
            string baseContent,
            string targetContent,
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables,
            string fallbackTestId)
        {
            string? fromUcon = TryGetColumnValue(baseTables, "FJ1.TB5UCON", "IT5UCONLREFEXN")
                            ?? TryGetColumnValue(targetTables, "FJ1.TB5UCON", "IT5UCONLREFEXN")
                            ?? TryGetColumnValue(baseTables, "LV.SCNTT0", "NO_CNT_EXTENDED")
                            ?? TryGetColumnValue(targetTables, "LV.SCNTT0", "NO_CNT_EXTENDED");
            if (!string.IsNullOrWhiteSpace(fromUcon))
                return fromUcon;

            Match m = HeaderContractRegex.Match(baseContent);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["contract"].Value))
                return m.Groups["contract"].Value.Trim();

            m = HeaderContractRegex.Match(targetContent);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["contract"].Value))
                return m.Groups["contract"].Value.Trim();

            return fallbackTestId;
        }

        private static string ResolveProductCode(
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            string? product = TryGetColumnValue(baseTables, "FJ1.TB5HDMD", "IT5HDMDCPRD")
                           ?? TryGetColumnValue(targetTables, "FJ1.TB5HDMD", "IT5HDMDCPRD")
                           ?? TryGetColumnValue(baseTables, "FJ1.TB5UCON", "IT5UCONCASU")
                           ?? TryGetColumnValue(targetTables, "FJ1.TB5UCON", "IT5UCONCASU");

            return string.IsNullOrWhiteSpace(product) ? "UNKNOWN" : product.ToUpperInvariant();
        }

        private static string ResolveProcessCode(
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            string? process = TryGetColumnValue(baseTables, "FJ1.TB5HDMD", "IT5HDMDCPCS")
                           ?? TryGetColumnValue(targetTables, "FJ1.TB5HDMD", "IT5HDMDCPCS");

            return string.IsNullOrWhiteSpace(process) ? "-" : process.ToUpperInvariant();
        }

        private static string ResolveExcelRowDisplay(string baseContent, string targetContent)
        {
            Match m = HeaderExcelRowRegex.Match(targetContent);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["row"].Value))
                return m.Groups["row"].Value.Trim();

            m = HeaderExcelRowRegex.Match(baseContent);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["row"].Value))
                return m.Groups["row"].Value.Trim();

            return "-";
        }

        private static string ResolveChannelCode(
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            string? channel = TryGetColumnValue(baseTables, "FJ1.TB5HDMD", "IT5HDMDCCDI")
                           ?? TryGetColumnValue(targetTables, "FJ1.TB5HDMD", "IT5HDMDCCDI");

            return string.IsNullOrWhiteSpace(channel) ? "-" : channel.Trim().ToUpperInvariant();
        }

        private static (string BusinessChannel, string BucpCmdpmtDisplay) ResolveChannelAndPayment(
            string channelCode,
            string processCode,
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            string routingKey = !string.IsNullOrWhiteSpace(channelCode) && channelCode != "-" ? channelCode : processCode;
            var routing = PreActivationRoutingService.ResolveConfirmedPaymentRouting(routingKey);

            string businessChannel = !string.IsNullOrWhiteSpace(routing.BusinessChannel) ? routing.BusinessChannel : "-";
            string bucp = !string.IsNullOrWhiteSpace(routing.Bucp) ? routing.Bucp : "-";
            string cmdpmt = !string.IsNullOrWhiteSpace(routing.Cmdpmt) ? routing.Cmdpmt : "-";

            string? prctBucp = TryGetColumnValue(targetTables, "LV.PRCTT0", "NO_BISM_BUCP")
                            ?? TryGetColumnValue(baseTables, "LV.PRCTT0", "NO_BISM_BUCP");
            string? prctCmd = TryGetColumnValue(targetTables, "LV.PRCTT0", "C_MD_PMT")
                           ?? TryGetColumnValue(baseTables, "LV.PRCTT0", "C_MD_PMT");

            if (!string.IsNullOrWhiteSpace(prctBucp))
            {
                bucp = prctBucp.Trim().PadLeft(5, '0');
            }
            if (!string.IsNullOrWhiteSpace(prctCmd))
            {
                cmdpmt = prctCmd.Trim().ToUpperInvariant();
            }

            if (businessChannel == "-" && bucp != "-")
            {
                businessChannel = bucp switch
                {
                    "00382" or "00182" => "BANK",
                    "80819" => "BROKER_NEW_GEN",
                    "49797" => "BROKER_LEGACY",
                    _ => "-"
                };
            }

            string bucpCmdDisplay = bucp != "-" || cmdpmt != "-" ? $"{bucp} / {cmdpmt}" : "-";
            return (businessChannel, bucpCmdDisplay);
        }

        private static string ResolveFundsSummary(
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            var funds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CollectFromTables(IReadOnlyDictionary<string, DataTable> tables)
            {
                if (tables.TryGetValue("FJ1.TB5UGFF", out DataTable? ugff) && ugff != null)
                {
                    foreach (DataRow row in ugff.Rows)
                    {
                        if (ugff.Columns.Contains("IT5UGFFCFND"))
                        {
                            string? f1 = row["IT5UGFFCFND"]?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(f1)) funds.Add(f1.ToUpperInvariant());
                        }
                        if (ugff.Columns.Contains("IT5UGFFCFNDDSI"))
                        {
                            string? f2 = row["IT5UGFFCFNDDSI"]?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(f2)) funds.Add(f2.ToUpperInvariant());
                        }
                    }
                }
                if (tables.TryGetValue("FJ1.TB5UPRF", out DataTable? uprf) && uprf != null && uprf.Columns.Contains("IT5UPRFCFND"))
                {
                    foreach (DataRow row in uprf.Rows)
                    {
                        string? f = row["IT5UPRFCFND"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(f)) funds.Add(f.ToUpperInvariant());
                    }
                }
                if (tables.TryGetValue("LV.FMVDT0", out DataTable? fmvd) && fmvd != null && fmvd.Columns.Contains("C_FONDS"))
                {
                    foreach (DataRow row in fmvd.Rows)
                    {
                        string? f = row["C_FONDS"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(f)) funds.Add(f.ToUpperInvariant());
                    }
                }
            }

            CollectFromTables(targetTables);
            CollectFromTables(baseTables);

            return funds.Count > 0
                ? string.Join(", ", funds.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                : "-";
        }

        private static bool ResolveIsBranch23(
            string productCode,
            IReadOnlyDictionary<string, DataTable> baseTables,
            IReadOnlyDictionary<string, DataTable> targetTables)
        {
            if (Settings.PreActivationRouting.IsKnownBranch23Product(productCode))
                return true;

            string[] fundTables = { "FJ1.TB5UGFF", "FJ1.TB5UPRF", "LV.FMVGT0", "LV.FMVDT0" };
            foreach (string table in fundTables)
            {
                if ((baseTables.TryGetValue(table, out DataTable? bTable) && bTable != null && bTable.Rows.Count > 0) ||
                    (targetTables.TryGetValue(table, out DataTable? tTable) && tTable != null && tTable.Rows.Count > 0))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryGetColumnValue(
            IReadOnlyDictionary<string, DataTable> tables,
            string tableName,
            string columnName)
        {
            if (!tables.TryGetValue(tableName, out DataTable? dt) ||
                dt == null ||
                dt.Rows.Count == 0 ||
                !dt.Columns.Contains(columnName))
            {
                return null;
            }

            for (int i = dt.Rows.Count - 1; i >= 0; i--)
            {
                string? val = dt.Rows[i][columnName]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }

            return null;
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
            string excelRowDisplay,
            string contractNumber,
            string product,
            string processCode,
            string channelCode,
            string businessChannel,
            string bucpCmdpmtDisplay,
            string fundsSummary,
            bool isBranch23,
            string branchDisplay,
            string productMetricKey,
            bool ignoreTechnicalColumns)
        {
            var (status, details, errorRows, diffColsCount, diffColsSummary) =
                Comparator.CompareDataTablesExtended(df1, df2, displayTableName, ignoreTechnicalColumns);

            int rowsCount = Math.Max(df1?.Rows.Count ?? 0, df2?.Rows.Count ?? 0);
            int currentErrors = (status == "OK" || status == "OK_EMPTY")
                ? 0
                : Math.Max(1, Math.Min(errorRows, Math.Max(rowsCount, 1)));

            var fileResult = new FileComparisonResult
            {
                TestId = testId,
                ExcelRowDisplay = excelRowDisplay,
                ContractNumber = contractNumber,
                ProductCode = product,
                ProcessCode = processCode,
                ChannelCode = channelCode,
                BusinessChannel = businessChannel,
                BucpCmdpmtDisplay = bucpCmdpmtDisplay,
                FundsSummary = fundsSummary,
                IsBranch23 = isBranch23,
                BranchDisplay = branchDisplay,
                FileType = type,
                BaseFileName = baseFileName,
                TargetFileName = targetFileName,
                TableName = displayTableName,
                Status = status,
                ErrorDetails = details,
                DifferingColumnsCount = diffColsCount,
                DifferingColumnsSummary = diffColsSummary,
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

                report.ProductMetrics[productMetricKey].TotalRows += rowsCount;
                report.ProductMetrics[productMetricKey].ErrorRows += currentErrors;
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
