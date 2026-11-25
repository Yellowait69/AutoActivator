using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AutoActivator.Config;

namespace AutoActivator.Utils
{
    /// <summary>
    /// Utility class for formatting and parsing CSV extraction data, optimized for low memory consumption and high speed.
    /// </summary>
    public static class CsvFormatter
    {
        public static void AddTableToBuffer(StringBuilder sb, string tableName, DataTable dt)
        {
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"### TABLE : {tableName} | Rows : {dt?.Rows.Count ?? 0}");
            sb.AppendLine("--------------------------------------------------------------------------------");

            if (dt == null || dt.Rows.Count == 0)
            {
                sb.AppendLine("NO DATA FOUND\n");
                return;
            }

            var columns = dt.Columns.Cast<DataColumn>().Select(c => EscapeCsvField(c.ColumnName));
            sb.AppendLine(string.Join(";", columns));

            foreach (DataRow row in dt.Rows)
            {
                var fields = row.ItemArray.Select(f =>
                    f == DBNull.Value || f == null ? string.Empty : EscapeCsvField(f.ToString()));
                sb.AppendLine(string.Join(";", fields));
            }
            sb.AppendLine();
        }

        public static string EscapeCsvField(string? field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            field = field.Trim();

            if (field.Contains(";") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                field = field.Replace("\"", "\"\"");
                return $"\"{field}\"";
            }
            return field;
        }

        /// <summary>
        /// Custom CSV parser to safely handle fields that contain the delimiter character inside quotes.
        /// </summary>
        public static List<string> ParseCsvLine(string line, char delimiter)
        {
            var result = new List<string>();
            if (line == null) return result;

            bool inQuotes = false;
            var currentField = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    result.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            result.Add(currentField.ToString());
            return result;
        }

        /// <summary>
        /// Converts an Excel file (.xls / .xlsx) to a local CSV file using ExcelHelper (ExcelDataReader).
        /// </summary>
        public static string ConvertExcelToCsv(string excelFilePath, string filePrefix = "Converted")
        {
            return ExcelHelper.ConvertExcelToCsv(excelFilePath, Settings.OutputDir, filePrefix);
        }

        private static DateTime ParseProcessIdToDate(string rawTestId)
        {
            try
            {
                int idx = rawTestId.IndexOf("ProcessID:", StringComparison.OrdinalIgnoreCase);
                if (idx != -1)
                {
                    string dateStr = rawTestId.Substring(idx + 10).Trim();
                    if (dateStr.Length >= 12)
                    {
                        dateStr = dateStr.Substring(0, 12);
                        if (DateTime.TryParseExact(dateStr, "ddMMyyHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)) return dt;
                        if (DateTime.TryParseExact(dateStr, "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt2)) return dt2;
                    }
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        /// <summary>
        /// Splits the full file and keeps ONLY the text block of the most recent contract for each Test ID.
        /// MEMORY OPTIMIZED (Line-by-line streaming with FileShare.ReadWrite).
        /// If no "TEST ID" is found (e.g., Single Extraction), treats the whole file as a single block.
        /// </summary>
        public static Dictionary<string, string> SplitAndFilterMostRecentByTestId(string filePath)
        {
            var blocks = new Dictionary<string, (DateTime date, StringBuilder content)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(filePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string defaultId = "SINGLE_CONTRACT"; // Fallback ID for unit extractions
            string currentRootTestId = defaultId;
            DateTime currentDate = DateTime.MinValue;
            StringBuilder currentBlock = new StringBuilder();
            long fallbackCounter = 1;

            bool hasGlobalReportMarkers = false;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("### GLOBAL CONTRACT REPORT:", StringComparison.Ordinal))
                    {
                        hasGlobalReportMarkers = true;

                        // Save the previous block if it existed
                        if (currentBlock.Length > 0 && currentRootTestId != "UNKNOWN")
                        {
                            if (!blocks.ContainsKey(currentRootTestId) || currentDate >= blocks[currentRootTestId].date)
                            {
                                blocks[currentRootTestId] = (currentDate, currentBlock);
                            }
                        }

                        // Reset for the new block
                        currentBlock = new StringBuilder();
                        currentDate = new DateTime(fallbackCounter++);
                        currentRootTestId = "UNKNOWN";

                        int idx = line.IndexOf("TEST ID:", StringComparison.OrdinalIgnoreCase);
                        if (idx != -1)
                        {
                            int endIdx = line.IndexOf("|", idx, StringComparison.Ordinal);
                            if (endIdx != -1)
                            {
                                string rawTestId = line.Substring(idx + 8, endIdx - (idx + 8)).Trim();
                                currentRootTestId = rawTestId.Contains("_") ? rawTestId.Split('_')[0].Trim() : rawTestId;

                                DateTime parsedDate = ParseProcessIdToDate(rawTestId);
                                if (parsedDate != DateTime.MinValue) currentDate = parsedDate;
                            }
                        }
                    }

                    currentBlock.AppendLine(line);
                }
            }

            // Save the last (or only) block
            if (currentBlock.Length > 0)
            {
                if (!hasGlobalReportMarkers)
                {
                    blocks[defaultId] = (DateTime.Now, currentBlock);
                }
                else if (currentRootTestId != "UNKNOWN")
                {
                    if (!blocks.ContainsKey(currentRootTestId) || currentDate >= blocks[currentRootTestId].date)
                    {
                        blocks[currentRootTestId] = (currentDate, currentBlock);
                    }
                }
            }

            return blocks.ToDictionary(k => k.Key, v => v.Value.content.ToString(), StringComparer.OrdinalIgnoreCase);
        }

        public static List<string> GetAllTableNamesFromContent(string fileContent)
        {
            if (string.IsNullOrEmpty(fileContent)) return new List<string>();
            using (var reader = new StringReader(fileContent))
            {
                return GetAllTableNamesFromReader(reader);
            }
        }

        public static List<string> GetAllTableNames(string filePath)
        {
            if (!File.Exists(filePath)) return new List<string>();

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return GetAllTableNamesFromReader(reader);
            }
        }

        private static List<string> GetAllTableNamesFromReader(TextReader reader)
        {
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("### TABLE : ", StringComparison.Ordinal))
                {
                    int startIndex = 12;
                    int endIndex = line.IndexOf(" |", startIndex, StringComparison.Ordinal);
                    if (endIndex > startIndex)
                    {
                        tableNames.Add(line.Substring(startIndex, endIndex - startIndex).Trim());
                    }
                }
            }
            return tableNames.ToList();
        }

        /// <summary>
        /// Parses all tables contained in a contract block in a single streaming pass (O(N) instead of O(N * T)).
        /// Includes full backward compatibility for legacy files where an EXTRACTION ERROR block had no '---' separator
        /// or where another '### TABLE :' marker appeared immediately on the same or next line.
        /// </summary>
        public static Dictionary<string, DataTable> LoadAllTablesFromContent(string fileContent)
        {
            var tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fileContent)) return tables;

            using var reader = new StringReader(fileContent);
            string? pendingLine = null;

            while (true)
            {
                string? line = pendingLine ?? reader.ReadLine();
                pendingLine = null;
                if (line == null)
                {
                    break;
                }

                int markerIdx = line.IndexOf("### TABLE : ", StringComparison.Ordinal);
                if (markerIdx < 0)
                {
                    continue;
                }

                string headerLine = markerIdx == 0 ? line : line.Substring(markerIdx);
                if (!TryExtractTableHeader(headerLine, out string tableName, out bool isExtractionError, out string? trailingTableHeader))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(trailingTableHeader))
                {
                    tables[tableName] = new DataTable(tableName);
                    pendingLine = trailingTableHeader;
                    continue;
                }

                if (isExtractionError)
                {
                    tables[tableName] = new DataTable(tableName);
                    pendingLine = SkipUntilNextTableMarker(reader);
                    continue;
                }

                var dt = new DataTable(tableName);
                pendingLine = ParseCurrentTableLines(reader, dt);
                tables[tableName] = dt;
            }

            return tables;
        }

        public static DataTable LoadTableFromContent(string fileContent, string tableName)
        {
            if (string.IsNullOrEmpty(fileContent)) return new DataTable(tableName);
            using (var reader = new StringReader(fileContent))
            {
                return LoadTableFromReader(reader, tableName);
            }
        }

        public static DataTable LoadTableFromCsv(string filePath, string tableName)
        {
            if (!File.Exists(filePath)) return new DataTable(tableName);

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                return LoadTableFromReader(reader, tableName);
            }
        }

        private static DataTable LoadTableFromReader(TextReader reader, string tableName)
        {
            var dt = new DataTable(tableName);
            string? pendingLine = null;

            while (true)
            {
                string? line = pendingLine ?? reader.ReadLine();
                pendingLine = null;
                if (line == null)
                {
                    return dt;
                }

                int markerIdx = line.IndexOf("### TABLE : ", StringComparison.OrdinalIgnoreCase);
                if (markerIdx < 0)
                {
                    continue;
                }

                string headerLine = markerIdx == 0 ? line : line.Substring(markerIdx);
                if (!TryExtractTableHeader(headerLine, out string parsedName, out bool isExtractionError, out string? trailingHeader))
                {
                    continue;
                }

                if (!string.Equals(parsedName, tableName, StringComparison.OrdinalIgnoreCase))
                {
                    pendingLine = trailingHeader;
                    continue;
                }

                if (isExtractionError || !string.IsNullOrEmpty(trailingHeader))
                {
                    return dt;
                }

                ParseCurrentTableLines(reader, dt);
                return dt;
            }
        }

        private static bool TryExtractTableHeader(
            string headerLine,
            out string tableName,
            out bool isExtractionError,
            out string? trailingTableHeader)
        {
            tableName = string.Empty;
            isExtractionError = false;
            trailingTableHeader = null;

            const string prefix = "### TABLE : ";
            if (!headerLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int secondMarker = headerLine.IndexOf("### TABLE : ", prefix.Length, StringComparison.OrdinalIgnoreCase);
            string currentHeader = secondMarker > 0 ? headerLine.Substring(0, secondMarker).TrimEnd() : headerLine;
            if (secondMarker > 0)
            {
                trailingTableHeader = headerLine.Substring(secondMarker);
            }

            int pipeIndex = currentHeader.IndexOf('|', prefix.Length);
            if (pipeIndex <= prefix.Length)
            {
                return false;
            }

            tableName = currentHeader.Substring(prefix.Length, pipeIndex - prefix.Length).Trim();
            isExtractionError = currentHeader.IndexOf("EXTRACTION ERROR", pipeIndex, StringComparison.OrdinalIgnoreCase) >= 0;
            return tableName.Length > 0;
        }

        private static string? SkipUntilNextTableMarker(TextReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                int markerIdx = line.IndexOf("### TABLE : ", StringComparison.Ordinal);
                if (markerIdx >= 0)
                {
                    return markerIdx == 0 ? line : line.Substring(markerIdx);
                }
            }
            return null;
        }

        private static string? ParseCurrentTableLines(TextReader reader, DataTable dt)
        {
            string? firstLine = reader.ReadLine();
            if (firstLine == null)
            {
                return null;
            }

            int immediateMarker = firstLine.IndexOf("### TABLE : ", StringComparison.Ordinal);
            if (immediateMarker >= 0)
            {
                return immediateMarker == 0 ? firstLine : firstLine.Substring(immediateMarker);
            }

            // Standard separator "---..." right under "### TABLE : ..."
            bool consumedSeparator = firstLine.TrimStart().StartsWith("---", StringComparison.Ordinal);
            string? bufferedFirstDataLine = consumedSeparator ? null : firstLine;

            bool isHeader = true;
            var logicalRecord = new StringBuilder();

            while (true)
            {
                string? rawLine = bufferedFirstDataLine ?? reader.ReadLine();
                bufferedFirstDataLine = null;

                if (rawLine == null)
                {
                    break;
                }

                if (logicalRecord.Length == 0)
                {
                    int embeddedMarker = rawLine.IndexOf("### TABLE : ", StringComparison.Ordinal);
                    if (embeddedMarker >= 0)
                    {
                        return embeddedMarker == 0 ? rawLine : rawLine.Substring(embeddedMarker);
                    }

                    string trimmed = rawLine.Trim();
                    if (trimmed.Length == 0 ||
                        trimmed.StartsWith("---", StringComparison.Ordinal) ||
                        trimmed.StartsWith("===", StringComparison.Ordinal) ||
                        string.Equals(trimmed, "NO DATA FOUND", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("SQL Error:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Details:", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }

                if (logicalRecord.Length > 0)
                {
                    logicalRecord.Append('\n');
                }
                logicalRecord.Append(rawLine);

                if (!HasBalancedQuotes(logicalRecord))
                {
                    continue;
                }

                List<string> fields = ParseCsvLine(logicalRecord.ToString(), ';');
                logicalRecord.Clear();

                if (isHeader)
                {
                    foreach (string field in fields)
                    {
                        string baseColName = field.Trim();
                        if (string.IsNullOrEmpty(baseColName))
                        {
                            baseColName = "COL";
                        }
                        string colName = baseColName;
                        int suffix = 1;
                        while (dt.Columns.Contains(colName))
                        {
                            colName = $"{baseColName}_{suffix++}";
                        }
                        dt.Columns.Add(colName);
                    }
                    isHeader = false;
                }
                else if (fields.Any(x => !string.IsNullOrWhiteSpace(x)))
                {
                    DataRow row = dt.NewRow();
                    for (int j = 0; j < fields.Count && j < dt.Columns.Count; j++)
                    {
                        row[j] = fields[j];
                    }
                    dt.Rows.Add(row);
                }
            }

            return null;
        }

        private static bool HasBalancedQuotes(StringBuilder text)
        {
            int quoteCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    quoteCount++;
                }
            }
            return (quoteCount & 1) == 0;
        }
    }
}