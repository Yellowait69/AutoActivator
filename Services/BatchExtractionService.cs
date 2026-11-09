using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Utils;

namespace AutoActivator.Services
{
    /// <summary>
    /// Service responsible for processing bulk contract extractions from a CSV file.
    /// It uses parallel processing and streams results to temporary files
    /// to avoid OutOfMemory (RAM) issues on large datasets.
    /// </summary>
    public class BatchExtractionService
    {
        private readonly ExtractionService _extractionService;

        public BatchExtractionService(ExtractionService extractionService)
        {
            _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        }

        public async Task PerformBatchExtractionAsync(
            string filePath,
            string env,
            Action<BatchProgressInfo> onProgressUpdate,
            bool isDemandId = false,
            CancellationToken token = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The specified CSV file could not be found.", filePath);

            env = Settings.NormalizeEnvironmentSuffix(env);
            string tempDirPath = Path.Combine(Path.GetTempPath(), $"AutoActivator_Batch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirPath);

            var contractsToProcess = new List<(int rowNum, string contractNumber, string rawTestId)>();

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string? line;
                    char delimiter = ';';
                    int contractIndex = -1, testIdIndex = -1;
                    bool headerFound = false;
                    int lineNumber = 0;

                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;

                        if (line.StartsWith("\uFEFF", StringComparison.Ordinal)) line = line.Substring(1);
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        if (line.Replace("\"", "").TrimStart().StartsWith("Contract in", StringComparison.OrdinalIgnoreCase))
                            continue;

                        delimiter = line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
                        var cols = CsvFormatter.ParseCsvLine(line, delimiter);

                        for (int i = 0; i < cols.Count; i++)
                        {
                            string h = cols[i].Trim().ToLowerInvariant();
                            if (h.Contains("contract") || h.Contains("contrat") || h.Contains("lisa") || h.Contains("value") || h.Contains("demand"))
                                contractIndex = i;
                            if (h.Contains("test") || h.Contains("id test") || h.Contains("idtest") || h == "key")
                                testIdIndex = i;
                        }

                        if (contractIndex != -1)
                        {
                            headerFound = true;
                            break;
                        }
                    }

                    if (!headerFound)
                        throw new Exception("Unable to find 'Value', 'Demand', or 'Contract' column in the CSV file.");

                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;

                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var columns = CsvFormatter.ParseCsvLine(line, delimiter);

                        if (columns.Count > contractIndex)
                        {
                            string contractNumber = columns[contractIndex]
                                .Replace("=", "")
                                .Replace("\"", "")
                                .Replace("\u00A0", "")
                                .Replace("\uFEFF", "")
                                .Trim();

                            if (string.IsNullOrEmpty(contractNumber) ||
                                contractNumber.Equals("End of File", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string rawTestId = (testIdIndex != -1 && columns.Count > testIdIndex)
                                ? columns[testIdIndex].Replace("=", "").Replace("\"", "").Trim()
                                : contractNumber;

                            contractsToProcess.Add((lineNumber, contractNumber, rawTestId));
                        }
                    }
                }

                int totalItems = contractsToProcess.Count;
                int processedItems = 0;

                // Capped at 12 concurrent contracts (each running up to 10 table queries) to prevent SQL connection pool exhaustion
                using (var semaphore = new SemaphoreSlim(12))
                {
                    async Task ProcessContractItemAsync((int rowNum, string contractNumber, string rawTestId) item, int index)
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            await semaphore.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                token.ThrowIfCancellationRequested();

                                ExtractionResult result = await _extractionService
                                    .PerformExtractionAsync(item.contractNumber, env, false, isDemandId, token)
                                    .ConfigureAwait(false);

                                token.ThrowIfCancellationRequested();

                                string displayContract = isDemandId && !string.IsNullOrEmpty(result.ContractReference)
                                    ? result.ContractReference
                                    : item.contractNumber;

                                if (!string.IsNullOrWhiteSpace(result.LisaContent) || !string.IsNullOrWhiteSpace(result.EliaContent))
                                {
                                    var localReport = new StringBuilder();
                                    localReport.AppendLine(new string('=', 80));
                                    localReport.AppendLine($"### GLOBAL CONTRACT REPORT: {displayContract} | TEST ID: {item.rawTestId} | ENV: {env} ###");
                                    localReport.AppendLine(new string('=', 80));

                                    if (!string.IsNullOrWhiteSpace(result.LisaContent))
                                    {
                                        localReport.AppendLine("--- LISA SECTION ---");
                                        localReport.Append(result.LisaContent).AppendLine();
                                    }

                                    if (!string.IsNullOrWhiteSpace(result.EliaContent))
                                    {
                                        localReport.AppendLine($"--- ELIA SECTION (UCON: {result.UconId}) ---");
                                        localReport.Append(result.EliaContent).AppendLine();
                                    }

                                    string tempFile = Path.Combine(tempDirPath, $"{index}.tmp");
                                    using (var writer = new StreamWriter(tempFile, false, Encoding.UTF8))
                                    {
                                        await writer.WriteAsync(localReport.ToString()).ConfigureAwait(false);
                                    }
                                }

                                int current = Interlocked.Increment(ref processedItems);

                                onProgressUpdate?.Invoke(new BatchProgressInfo
                                {
                                    RowNum = item.rowNum,
                                    ContractId = displayContract,
                                    InternalId = result.InternalId,
                                    Product = env,
                                    Premium = string.IsNullOrWhiteSpace(result.Premium) ? "0" : result.Premium,
                                    UconId = result.UconId,
                                    DemandId = result.DemandId,
                                    Status = "OK",
                                    CurrentItem = current,
                                    TotalItems = totalItems
                                });
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            int current = Interlocked.Increment(ref processedItems);
                            onProgressUpdate?.Invoke(new BatchProgressInfo
                            {
                                RowNum = item.rowNum,
                                ContractId = $"{item.contractNumber} (FAILED)",
                                InternalId = "Error",
                                Product = env,
                                Premium = "0",
                                UconId = "Error",
                                DemandId = "Error",
                                Status = ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ? "Not found in DB" : "SQL Error",
                                CurrentItem = current,
                                TotalItems = totalItems
                            });
                        }
                    }

                    var tasks = contractsToProcess
                        .Select((item, index) => ProcessContractItemAsync(item, index))
                        .ToList();

                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Allow partial report generation below when cancelled by the user
                    }
                }

                string origFileName = Path.GetFileNameWithoutExtension(filePath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                Directory.CreateDirectory(Settings.OutputDir);

                char envLetter = !string.IsNullOrEmpty(env) ? char.ToUpperInvariant(env[0]) : 'U';
                string partialSuffix = token.IsCancellationRequested ? "_PARTIAL_CANCELLED" : string.Empty;
                string combinedPath = Path.Combine(Settings.OutputDir, $"Extraction_{origFileName}_{envLetter}{partialSuffix}_{timestamp}.csv");

                try
                {
                    await WriteMergedBatchOutputAsync(combinedPath, tempDirPath, totalItems, token.IsCancellationRequested).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    string alternativePath = Path.Combine(Settings.OutputDir, $"Extraction_{origFileName}_{envLetter}{partialSuffix}_{timestamp}_{Guid.NewGuid().ToString("N").Substring(0, 4)}.csv");
                    await WriteMergedBatchOutputAsync(alternativePath, tempDirPath, totalItems, token.IsCancellationRequested).ConfigureAwait(false);
                }
            }
            finally
            {
                if (Directory.Exists(tempDirPath))
                {
                    try
                    {
                        Directory.Delete(tempDirPath, true);
                    }
                    catch { /* Ignore temporary directory lock by antivirus */ }
                }
            }
        }

        private static async Task WriteMergedBatchOutputAsync(string outputPath, string tempDirPath, int totalItems, bool isCancelled)
        {
            bool anyDataWritten = false;

            using (var finalWriter = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                for (int i = 0; i < totalItems; i++)
                {
                    string tempFile = Path.Combine(tempDirPath, $"{i}.tmp");

                    if (File.Exists(tempFile))
                    {
                        anyDataWritten = true;

                        using (var tempReader = new StreamReader(tempFile, Encoding.UTF8))
                        {
                            char[] buffer = new char[8192];
                            int charsRead;
                            while ((charsRead = await tempReader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await finalWriter.WriteAsync(buffer, 0, charsRead).ConfigureAwait(false);
                            }
                        }
                    }
                }

                if (!anyDataWritten)
                {
                    if (isCancelled)
                        await finalWriter.WriteAsync("EXTRACTION CANCELLED BEFORE ANY CONTRACT WAS FOUND.").ConfigureAwait(false);
                    else
                        await finalWriter.WriteAsync("NO CONTRACT FOUND.").ConfigureAwait(false);
                }
            }
        }
    }
}