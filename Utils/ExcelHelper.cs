using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using ExcelDataReader;

namespace AutoActivator.Utils
{
    public static class ExcelHelper
    {
        static ExcelHelper()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Converts an Excel file (.xls or .xlsx) to a semicolon-separated CSV file
        /// using forward-only row streaming (no DataSet allocation in memory).
        /// </summary>
        public static string ConvertExcelToCsv(string excelPath, string outputFolder, string filePrefix = "Converted")
        {
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
            {
                throw new FileNotFoundException($"Excel file not found: {excelPath}", excelPath);
            }

            if (excelPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return excelPath;
            }

            Directory.CreateDirectory(outputFolder);
            string baseFileName = Path.GetFileNameWithoutExtension(excelPath);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string csvPath = Path.Combine(outputFolder, $"{filePrefix}_{baseFileName}_{timestamp}.csv");

            using var stream = File.Open(excelPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            int fieldCount = 0;
            while (reader.Read())
            {
                if (reader.FieldCount <= 0)
                {
                    continue;
                }

                if (fieldCount == 0)
                {
                    fieldCount = reader.FieldCount;
                }

                var rowValues = new string[reader.FieldCount];
                bool hasNonEmptyCell = false;

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string cell = SanitizeCsvField(reader.GetValue(i));
                    if (!string.IsNullOrEmpty(cell))
                    {
                        hasNonEmptyCell = true;
                    }
                    rowValues[i] = cell;
                }

                if (hasNonEmptyCell)
                {
                    writer.WriteLine(string.Join(";", rowValues));
                }
                else
                {
                    // Preserve exact 1-based Excel row numbering in the generated CSV
                    writer.WriteLine();
                }
            }

            return csvPath;
        }

        /// <summary>
        /// Inspects a CSV or Excel batch file and returns the exact 1-based Excel/CSV row numbers
        /// and identifiers detected, without modifying any state.
        /// </summary>
        public static List<(int RowNumber, string Identifier, string SecondaryInfo)> InspectBatchFileRows(string filePath)
        {
            var detected = new List<(int RowNumber, string Identifier, string SecondaryInfo)>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return detected;
            }

            string ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".xls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                int rowNum = 0;
                int idCol = -1;
                int infoCol = -1;
                bool headerFound = false;

                while (reader.Read())
                {
                    rowNum++;
                    if (reader.FieldCount <= 0)
                    {
                        continue;
                    }

                    if (!headerFound)
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string cell = Convert.ToString(reader.GetValue(i))?.Trim() ?? string.Empty;
                            string lower = cell.ToLowerInvariant();
                            if (lower.Contains("contract") || lower.Contains("contrat") || lower.Contains("demand") || lower.Contains("value") || lower.Contains("lisa"))
                            {
                                idCol = i;
                            }
                            else if (lower.Contains("test") || lower == "key" || lower.Contains("product") || lower.Contains("process") || lower.Contains("br23"))
                            {
                                infoCol = i;
                            }
                        }

                        if (idCol >= 0)
                        {
                            headerFound = true;
                            continue;
                        }

                        string firstCell = Convert.ToString(reader.GetValue(0))?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(firstCell) && !firstCell.StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsHeaderLabel(firstCell))
                            {
                                idCol = 0;
                                headerFound = true;
                                continue;
                            }

                            idCol = 0;
                            headerFound = true;
                            detected.Add((rowNum, CleanIdentifier(firstCell), string.Empty));
                        }
                        continue;
                    }

                    if (idCol >= 0 && idCol < reader.FieldCount)
                    {
                        string rawId = CleanIdentifier(Convert.ToString(reader.GetValue(idCol)) ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(rawId) || rawId.Equals("End of File", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string sec = (infoCol >= 0 && infoCol < reader.FieldCount)
                            ? CleanIdentifier(Convert.ToString(reader.GetValue(infoCol)) ?? string.Empty)
                            : string.Empty;

                        detected.Add((rowNum, rawId, sec));
                    }
                }

                return detected;
            }
            else
            {
                int rowNum = 0;
                int idCol = -1;
                int infoCol = -1;
                char delimiter = ';';
                bool headerFound = false;

                foreach (string rawLine in File.ReadLines(filePath, Encoding.UTF8))
                {
                    rowNum++;
                    string line = rawLine.StartsWith("\uFEFF", StringComparison.Ordinal) ? rawLine.Substring(1) : rawLine;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (line.Replace("\"", "").TrimStart().StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!headerFound)
                    {
                        delimiter = line.Count(c => c == ';') >= line.Count(c => c == ',') ? ';' : ',';
                        var cols = CsvFormatter.ParseCsvLine(line, delimiter);
                        for (int i = 0; i < cols.Count; i++)
                        {
                            string lower = cols[i].Trim().ToLowerInvariant();
                            if (lower.Contains("contract") || lower.Contains("contrat") || lower.Contains("demand") || lower.Contains("value") || lower.Contains("lisa"))
                            {
                                idCol = i;
                            }
                            else if (lower.Contains("test") || lower == "key" || lower.Contains("product") || lower.Contains("process") || lower.Contains("br23"))
                            {
                                infoCol = i;
                            }
                        }

                        if (idCol >= 0)
                        {
                            headerFound = true;
                            continue;
                        }

                        string firstCell = cols.Count > 0 ? CleanIdentifier(cols[0]) : string.Empty;
                        if (!string.IsNullOrWhiteSpace(firstCell))
                        {
                            idCol = 0;
                            headerFound = true;
                            if (!IsHeaderLabel(firstCell))
                            {
                                detected.Add((rowNum, firstCell, string.Empty));
                            }
                        }
                        continue;
                    }

                    var columns = CsvFormatter.ParseCsvLine(line, delimiter);
                    if (idCol >= 0 && idCol < columns.Count)
                    {
                        string rawId = CleanIdentifier(columns[idCol]);
                        if (string.IsNullOrWhiteSpace(rawId) || rawId.Equals("End of File", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string sec = (infoCol >= 0 && infoCol < columns.Count)
                            ? CleanIdentifier(columns[infoCol])
                            : string.Empty;

                        detected.Add((rowNum, rawId, sec));
                    }
                }

                return detected;
            }
        }

        private static string CleanIdentifier(string value)
        {
            return (value ?? string.Empty)
                .Replace("=", string.Empty)
                .Replace("\"", string.Empty)
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();
        }

        /// <summary>
        /// Reads the first column of the first worksheet in an Excel file (.xlsx, .xls, or .csv)
        /// using forward-only row streaming to minimize memory allocations.
        /// Automatically skips common header labels on the first row.
        /// </summary>
        public static List<string> ReadFirstColumn(string filePath)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return results;
            }

            string extension = Path.GetExtension(filePath);
            if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return ReadFirstColumnFromTextFile(filePath);
            }

            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            bool isFirstRow = true;
            while (reader.Read())
            {
                if (reader.FieldCount <= 0)
                {
                    isFirstRow = false;
                    continue;
                }

                string value = Convert.ToString(reader.GetValue(0))?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    isFirstRow = false;
                    continue;
                }

                if (isFirstRow && IsHeaderLabel(value))
                {
                    isFirstRow = false;
                    continue;
                }

                isFirstRow = false;
                results.Add(value);
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ReadFirstColumnFromTextFile(string filePath)
        {
            var results = new List<string>();
            bool isFirstRow = true;

            foreach (string rawLine in File.ReadLines(filePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string firstToken = rawLine
                    .Split(new[] { ';', ',', '\t' }, 2)[0]
                    .Trim()
                    .Trim('"');

                if (string.IsNullOrWhiteSpace(firstToken))
                {
                    isFirstRow = false;
                    continue;
                }

                if (isFirstRow && IsHeaderLabel(firstToken))
                {
                    isFirstRow = false;
                    continue;
                }

                isFirstRow = false;
                results.Add(firstToken);
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsHeaderLabel(string value)
        {
            string lowerVal = value.Trim().ToLowerInvariant();
            return lowerVal.Contains("contract")
                || lowerVal.Contains("contrat")
                || lowerVal.Contains("demand")
                || lowerVal.Contains("demande")
                || lowerVal.Contains("reference")
                || lowerVal.Contains("référence")
                || lowerVal == "id"
                || lowerVal.Contains("num");
        }

        /// <summary>
        /// Exports a list of DataTables into a semicolon-delimited CSV file compatible with French/Belgian Excel.
        /// Includes UTF-8 BOM so Excel opens accented characters properly.
        /// </summary>
        public static void ExportDataTablesToCsv(List<DataTable> tables, string outputFilePath)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("Output file path cannot be empty.", nameof(outputFilePath));
            }

            string? directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputFilePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            if (tables == null || tables.Count == 0 || tables.All(t => t.Rows.Count == 0))
            {
                writer.WriteLine("Info;No data found for the provided inputs.");
                return;
            }

            DataTable referenceTable = tables.First(t => t.Columns.Count > 0);
            string[] columnNames = referenceTable.Columns.Cast<DataColumn>()
                .Select(column => SanitizeCsvField(column.ColumnName))
                .ToArray();
            writer.WriteLine(string.Join(";", columnNames));

            foreach (var table in tables)
            {
                foreach (DataRow row in table.Rows)
                {
                    string[] fields = row.ItemArray
                        .Select(SanitizeCsvField)
                        .ToArray();
                    writer.WriteLine(string.Join(";", fields));
                }
            }
        }

        private static string SanitizeCsvField(object? field)
        {
            if (field == null || field == DBNull.Value)
            {
                return string.Empty;
            }

            string normalized = field.ToString()!
                .Replace(";", ",")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (normalized.Contains('"'))
            {
                normalized = $"\"{normalized.Replace("\"", "\"\"")}\"";
            }

            return normalized;
        }
    }
}
