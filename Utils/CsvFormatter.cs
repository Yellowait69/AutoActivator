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
        /// </summary>
        public static Dictionary<string, DataTable> LoadAllTablesFromContent(string fileContent)
        {
            var tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fileContent)) return tables;

            using (var reader = new StringReader(fileContent))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("### TABLE : ", StringComparison.Ordinal))
                    {
                        int startIndex = 12;
                        int endIndex = line.IndexOf(" |", startIndex, StringComparison.Ordinal);
                        if (endIndex > startIndex)
                        {
                            string tableName = line.Substring(startIndex, endIndex - startIndex).Trim();
                            // Consume the separator line "---..." immediately following the header
                            reader.ReadLine();

                            var dt = new DataTable(tableName);
                            ParseCurrentTableFromReader(reader, dt);
                            tables[tableName] = dt;
                        }
                    }
                }
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

        /// <summary>
        /// Extracts a table using a text stream (file or memory), which prevents
        /// RAM saturation when parsing very large files.
        /// </summary>
        private static DataTable LoadTableFromReader(TextReader reader, string tableName)
        {
            var dt = new DataTable(tableName);
            string searchString = $"### TABLE : {tableName} |";
            string? line;
            bool tableFound = false;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith(searchString, StringComparison.OrdinalIgnoreCase))
                {
                    tableFound = true;
                    reader.ReadLine();
                    break;
                }
            }

            if (!tableFound) return dt;

            ParseCurrentTableFromReader(reader, dt);
            return dt;
        }

        private static void ParseCurrentTableFromReader(TextReader reader, DataTable dt)
        {
            bool isHeader = true;
            bool inQuotes = false;
            StringBuilder currentField = new StringBuilder();
            List<string> currentLine = new List<string>();

            int intChar;
            while ((intChar = reader.Read()) != -1)
            {
                char c = (char)intChar;
                int peek = reader.Peek();
                char nextC = peek != -1 ? (char)peek : '\0';

                if (c == '"')
                {
                    if (inQuotes && nextC == '"')
                    {
                        currentField.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ';' && !inQuotes)
                {
                    currentLine.Add(currentField.ToString());
                    currentField.Clear();
                }
                else if ((c == '\r' || c == '\n') && !inQuotes)
                {
                    if (c == '\r' && nextC == '\n') reader.Read();

                    currentLine.Add(currentField.ToString());
                    currentField.Clear();

                    if (currentLine.Count == 1 && (string.IsNullOrWhiteSpace(currentLine[0]) || currentLine[0].StartsWith("---", StringComparison.Ordinal) || currentLine[0] == "NO DATA FOUND"))
                    {
                        break;
                    }

                    if (isHeader)
                    {
                        foreach (var field in currentLine)
                        {
                            string colName = field.Trim();
                            int suffix = 1;
                            while (dt.Columns.Contains(colName)) colName = $"{field.Trim()}_{suffix++}";
                            dt.Columns.Add(colName);
                        }
                        isHeader = false;
                    }
                    else if (currentLine.Any(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        var row = dt.NewRow();
                        for (int j = 0; j < currentLine.Count && j < dt.Columns.Count; j++)
                        {
                            row[j] = currentLine[j];
                        }
                        dt.Rows.Add(row);
                    }

                    currentLine.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            if (currentField.Length > 0 || currentLine.Count > 0)
            {
                currentLine.Add(currentField.ToString());
                if (!isHeader && currentLine.Any(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var row = dt.NewRow();
                    for (int j = 0; j < currentLine.Count && j < dt.Columns.Count; j++)
                    {
                        row[j] = currentLine[j];
                    }
                    dt.Rows.Add(row);
                }
            }
        }
    }
}