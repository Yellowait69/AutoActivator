using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExcelDataReader;

namespace AutoActivator.Utils
{
    /// <summary>
    /// Ligne d'entrée unifiée issue d'un fichier Batch Excel (.xls/.xlsx) ou CSV,
    /// conservant le numéro de ligne exact 1-based tel qu'affiché dans Excel.
    /// </summary>
    public sealed class BatchFileRow
    {
        public int RowNumber { get; init; }
        public string RawInput { get; init; } = string.Empty;
        public string TestId { get; init; } = string.Empty;
        public bool? Branch23Override { get; init; }
        public string? Funds { get; init; }
        public IReadOnlyDictionary<string, string> Overrides { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static class ExcelHelper
    {
        static ExcelHelper()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Lit directement en streaming les lignes d'un fichier Excel (.xls, .xlsx) ou CSV/TXT
        /// sans générer aucun fichier CSV temporaire sur disque dans OutputDir.
        /// Préserve le numéro de ligne 1-based exact d'Excel et supporte l'annulation coopérative.
        /// </summary>
        public static async Task<IReadOnlyList<BatchFileRow>> ReadBatchRowsAsync(
            string filePath,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The batch input file path is required.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Batch file not found: {filePath}", filePath);
            }

            token.ThrowIfCancellationRequested();

            string ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".xls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return await Task.Run(() => ReadBatchRowsFromExcel(filePath, token), token).ConfigureAwait(false);
            }

            return await ReadBatchRowsFromCsvAsync(filePath, token).ConfigureAwait(false);
        }

        private static IReadOnlyList<BatchFileRow> ReadBatchRowsFromExcel(
            string filePath,
            CancellationToken token)
        {
            var rows = new List<BatchFileRow>();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16384);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            int excelRowNumber = 0;
            bool headerFound = false;
            int identifierIndex = -1;
            int testIdIndex = -1;
            int branch23Index = -1;
            int fundsIndex = -1;
            var overrideIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                excelRowNumber++;

                int fieldCount = reader.FieldCount;
                if (fieldCount <= 0)
                {
                    continue;
                }

                if (!headerFound)
                {
                    bool anyNonEmpty = false;
                    string firstNonEmptyValue = string.Empty;

                    for (int i = 0; i < fieldCount; i++)
                    {
                        string rawCell = Convert.ToString(reader.GetValue(i))?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(rawCell))
                        {
                            continue;
                        }

                        if (!anyNonEmpty)
                        {
                            anyNonEmpty = true;
                            firstNonEmptyValue = rawCell;
                        }

                        ClassifyHeaderColumn(
                            rawCell,
                            i,
                            ref identifierIndex,
                            ref testIdIndex,
                            ref branch23Index,
                            ref fundsIndex,
                            overrideIndexes);
                    }

                    if (!anyNonEmpty)
                    {
                        continue;
                    }

                    if (firstNonEmptyValue.StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                    {
                        identifierIndex = -1;
                        testIdIndex = -1;
                        branch23Index = -1;
                        fundsIndex = -1;
                        overrideIndexes.Clear();
                        continue;
                    }

                    if (identifierIndex >= 0)
                    {
                        headerFound = true;
                        continue;
                    }

                    // Fallback fichier 1 colonne sans en-tête explicite
                    if (fieldCount == 1)
                    {
                        identifierIndex = 0;
                        headerFound = true;
                        if (!IsHeaderLabel(firstNonEmptyValue))
                        {
                            string cleanedFirst = CleanIdentifier(firstNonEmptyValue);
                            if (IsValidDataIdentifier(cleanedFirst))
                            {
                                rows.Add(new BatchFileRow
                                {
                                    RowNumber = excelRowNumber,
                                    RawInput = cleanedFirst,
                                    TestId = cleanedFirst
                                });
                            }
                        }
                    }

                    continue;
                }

                if (identifierIndex < 0 || identifierIndex >= fieldCount)
                {
                    continue;
                }

                string rawInput = CleanIdentifier(Convert.ToString(reader.GetValue(identifierIndex)) ?? string.Empty);
                if (!IsValidDataIdentifier(rawInput))
                {
                    continue;
                }

                string testId = testIdIndex >= 0 && testIdIndex < fieldCount
                    ? CleanIdentifier(Convert.ToString(reader.GetValue(testIdIndex)) ?? string.Empty)
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(testId))
                {
                    testId = rawInput;
                }

                bool? branch23Override = branch23Index >= 0 && branch23Index < fieldCount
                    ? ParseNullableBoolean(Convert.ToString(reader.GetValue(branch23Index)))
                    : null;

                string? funds = fundsIndex >= 0 && fundsIndex < fieldCount
                    ? NullIfWhiteSpace(CleanIdentifier(Convert.ToString(reader.GetValue(fundsIndex)) ?? string.Empty))
                    : null;

                var rowOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in overrideIndexes)
                {
                    if (kvp.Value < fieldCount)
                    {
                        string val = CleanIdentifier(Convert.ToString(reader.GetValue(kvp.Value)) ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            rowOverrides[kvp.Key] = val;
                        }
                    }
                }

                rows.Add(new BatchFileRow
                {
                    RowNumber = excelRowNumber,
                    RawInput = rawInput,
                    TestId = testId,
                    Branch23Override = branch23Override,
                    Funds = funds,
                    Overrides = rowOverrides
                });
            }

            if (!headerFound)
            {
                throw new InvalidOperationException(
                    "Unable to find a 'Contract', 'Value' or 'Demand' column in the Excel file.");
            }

            return rows;
        }

        private static async Task<IReadOnlyList<BatchFileRow>> ReadBatchRowsFromCsvAsync(
            string filePath,
            CancellationToken token)
        {
            var rows = new List<BatchFileRow>();

            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 16384,
                useAsync: true);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            int lineNumber = 0;
            char delimiter = ';';
            bool headerFound = false;
            int identifierIndex = -1;
            int testIdIndex = -1;
            int branch23Index = -1;
            int fundsIndex = -1;
            var overrideIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();
                lineNumber++;

                if (line.StartsWith("\uFEFF", StringComparison.Ordinal))
                {
                    line = line.Substring(1);
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Replace("\"", string.Empty).TrimStart()
                    .StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!headerFound)
                {
                    delimiter = line.Count(c => c == ';') >= line.Count(c => c == ',') ? ';' : ',';
                    List<string> headers = CsvFormatter.ParseCsvLine(line, delimiter);

                    for (int i = 0; i < headers.Count; i++)
                    {
                        ClassifyHeaderColumn(
                            headers[i],
                            i,
                            ref identifierIndex,
                            ref testIdIndex,
                            ref branch23Index,
                            ref fundsIndex,
                            overrideIndexes);
                    }

                    if (identifierIndex >= 0)
                    {
                        headerFound = true;
                        continue;
                    }

                    if (headers.Count == 1)
                    {
                        string firstCell = CleanIdentifier(headers[0]);
                        if (!string.IsNullOrWhiteSpace(firstCell))
                        {
                            identifierIndex = 0;
                            headerFound = true;
                            if (!IsHeaderLabel(firstCell) && IsValidDataIdentifier(firstCell))
                            {
                                rows.Add(new BatchFileRow
                                {
                                    RowNumber = lineNumber,
                                    RawInput = firstCell,
                                    TestId = firstCell
                                });
                            }
                        }
                    }

                    continue;
                }

                List<string> columns = CsvFormatter.ParseCsvLine(line, delimiter);
                if (identifierIndex < 0 || identifierIndex >= columns.Count)
                {
                    continue;
                }

                string rawInput = CleanIdentifier(columns[identifierIndex]);
                if (!IsValidDataIdentifier(rawInput))
                {
                    continue;
                }

                string testId = testIdIndex >= 0 && testIdIndex < columns.Count
                    ? CleanIdentifier(columns[testIdIndex])
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(testId))
                {
                    testId = rawInput;
                }

                bool? branch23Override = branch23Index >= 0 && branch23Index < columns.Count
                    ? ParseNullableBoolean(columns[branch23Index])
                    : null;

                string? funds = fundsIndex >= 0 && fundsIndex < columns.Count
                    ? NullIfWhiteSpace(CleanIdentifier(columns[fundsIndex]))
                    : null;

                var rowOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in overrideIndexes)
                {
                    if (kvp.Value < columns.Count)
                    {
                        string val = CleanIdentifier(columns[kvp.Value]);
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            rowOverrides[kvp.Key] = val;
                        }
                    }
                }

                rows.Add(new BatchFileRow
                {
                    RowNumber = lineNumber,
                    RawInput = rawInput,
                    TestId = testId,
                    Branch23Override = branch23Override,
                    Funds = funds,
                    Overrides = rowOverrides
                });
            }

            if (!headerFound)
            {
                throw new InvalidOperationException(
                    "Unable to find a 'Contract', 'Value' or 'Demand' column in the CSV file.");
            }

            return rows;
        }

        private static void ClassifyHeaderColumn(
            string rawHeader,
            int columnIndex,
            ref int identifierIndex,
            ref int testIdIndex,
            ref int branch23Index,
            ref int fundsIndex,
            IDictionary<string, int> overrideIndexes)
        {
            string header = (rawHeader ?? string.Empty).Trim().ToLowerInvariant();
            if (header.Length == 0)
            {
                return;
            }

            if (header == "br23" ||
                header.Contains("branch23") ||
                header.Contains("branch 23") ||
                header.Contains("branche23") ||
                header.Contains("branche 23"))
            {
                branch23Index = columnIndex;
                return;
            }

            if (header == "funds" ||
                header == "fund" ||
                header == "fonds" ||
                header.Contains("br23 fund") ||
                header.Contains("fund codes"))
            {
                fundsIndex = columnIndex;
                return;
            }

            string? overrideKey = GetOverrideKey(header);
            if (overrideKey != null)
            {
                overrideIndexes[overrideKey] = columnIndex;
                return;
            }

            if (header.Contains("test") || header.Contains("id test") || header.Contains("idtest") || header == "key")
            {
                testIdIndex = columnIndex;
            }

            if (header.Contains("contract") ||
                header.Contains("contrat") ||
                header.Contains("lisa") ||
                header.Contains("value") ||
                header.Contains("demand"))
            {
                identifierIndex = columnIndex;
            }
        }

        private static string? GetOverrideKey(string header)
        {
            string normalized = (header ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToUpperInvariant();

            return normalized switch
            {
                "AMOUNT" or "AMOUNTOVERRIDE" or "PREMIUM" => "AMOUNT",
                "RUNDATE" or "DRUN" or "DATEOVERRIDE" => "DRUN",
                "BUCP" or "BUCPOVERRIDE" => "BUCP",
                "CMDPMT" or "CMD" or "PAYMENTCOMMAND" => "CMDPMT",
                "PROCESS" or "PROCESSCODE" => "PROCESS",
                "CHANNEL" or "CHANNELCODE" => "CHANNEL",
                _ => null
            };
        }

        private static bool? ParseNullableBoolean(string? value)
        {
            string normalized = CleanIdentifier(value ?? string.Empty).ToUpperInvariant();
            if (normalized is "Y" or "YES" or "O" or "OUI" or "1" or "TRUE" or "BR23" or "BRANCHE 23")
            {
                return true;
            }
            if (normalized is "N" or "NO" or "NON" or "0" or "FALSE" or "BR21" or "BRANCHE 21")
            {
                return false;
            }
            return null;
        }

        private static bool IsValidDataIdentifier(string cleanedValue)
        {
            return !string.IsNullOrWhiteSpace(cleanedValue) &&
                   !string.Equals(cleanedValue, "End of File", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Converts an Excel file (.xls or .xlsx) to a semicolon-separated CSV file
        /// using forward-only row streaming (no DataSet allocation in memory).
        /// </summary>
        public static string ConvertExcelToCsv(
            string excelPath,
            string outputFolder,
            string filePrefix = "Converted",
            CancellationToken token = default)
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
                token.ThrowIfCancellationRequested();

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

            IReadOnlyList<BatchFileRow> rows = ReadBatchRowsAsync(filePath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            foreach (BatchFileRow row in rows)
            {
                string sec = !string.Equals(row.TestId, row.RawInput, StringComparison.OrdinalIgnoreCase)
                    ? row.TestId
                    : string.Empty;
                detected.Add((row.RowNumber, row.RawInput, sec));
            }

            return detected;
        }

        private static string CleanIdentifier(string value)
        {
            string cleaned = (value ?? string.Empty)
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();

            if (cleaned.StartsWith("=", StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(1).Trim();
            }

            if (cleaned.Length >= 2 &&
                cleaned.StartsWith("\"", StringComparison.Ordinal) &&
                cleaned.EndsWith("\"", StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(1, cleaned.Length - 2);
            }

            return cleaned.Replace("\"\"", "\"").Replace("\"", string.Empty).Trim();
        }

        /// <summary>
        /// Détermine automatiquement si un identifiant saisi (ou lu depuis un fichier Excel/CSV)
        /// correspond à un Demand ID ELIA ou à un Numéro de Contrat (3-7-2 / 12 chiffres).
        /// </summary>
        public static bool IsDemandIdInput(string? rawInput)
        {
            string cleaned = CleanIdentifier(rawInput ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return false;
            }

            if (cleaned.Contains('-'))
            {
                return false;
            }

            string digitsOnly = ContractNumberHelper.ExtractDigits(cleaned);
            if (digitsOnly.Length >= 11)
            {
                return false;
            }

            return true;
        }

        public static string DescribeIdentifierType(string? rawInput)
        {
            string cleaned = CleanIdentifier(rawInput ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return "Auto-détection : Contrat (12 chiffres / 3-7-2) ou Demand ID";
            }

            string digitsOnly = ContractNumberHelper.ExtractDigits(cleaned);
            bool isDemand = IsDemandIdInput(cleaned);
            return isDemand
                ? $"Demand ID détecté ({digitsOnly.Length} chiffres)"
                : $"Numéro de Contrat détecté ({digitsOnly.Length} chiffres)";
        }

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
