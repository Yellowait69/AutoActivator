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
    /// Service responsible for processing bulk contract extractions directly from an Excel (.xls/.xlsx) or CSV file
    /// without intermediate CSV conversion files on disk.
    /// Preserves the exact 1-based Excel row number and resolves Branche 21 / Branche 23.
    /// </summary>
    public class BatchExtractionService
    {
        private readonly ExtractionService _extractionService;

        public BatchExtractionService(ExtractionService extractionService)
        {
            _extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        }

        public async Task<string> PerformBatchExtractionAsync(
            string filePath,
            string env,
            Action<BatchProgressInfo> onProgressUpdate,
            bool isDemandId = false,
            CancellationToken token = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The specified batch file could not be found.", filePath);
            }

            env = Settings.NormalizeEnvironmentSuffix(env);
            string tempDirPath = Path.Combine(Path.GetTempPath(), $"AutoActivator_Batch_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirPath);

            string finalMergedPath = Settings.OutputDir;

            try
            {
                IReadOnlyList<BatchFileRow> batchRows =
                    await ExcelHelper.ReadBatchRowsAsync(filePath, token).ConfigureAwait(false);

                string origFileName = Path.GetFileNameWithoutExtension(filePath);
                if (origFileName.StartsWith("Converted_Ext_", StringComparison.OrdinalIgnoreCase))
                {
                    origFileName = origFileName.Substring("Converted_Ext_".Length);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                Directory.CreateDirectory(Settings.OutputDir);

                char envLetter = !string.IsNullOrEmpty(env) ? char.ToUpperInvariant(env[0]) : 'U';
                string combinedPath = Path.Combine(Settings.OutputDir, $"Extraction_{origFileName}_{envLetter}_{timestamp}.csv");
                finalMergedPath = combinedPath;

                int totalItems = batchRows.Count;
                int processedItems = 0;

                onProgressUpdate?.Invoke(new BatchProgressInfo
                {
                    Environment = env,
                    Status = "STARTING",
                    CurrentItem = 0,
                    TotalItems = totalItems,
                    FilePath = combinedPath
                });

                if (totalItems == 0)
                {
                    await WriteMergedBatchOutputAsync(combinedPath, tempDirPath, 0, token.IsCancellationRequested)
                        .ConfigureAwait(false);
                    return finalMergedPath;
                }

                // Capped at 12 concurrent contracts (each running up to 10 table queries) to prevent SQL connection pool exhaustion
                using (var semaphore = new SemaphoreSlim(12))
                {
                    async Task ProcessContractItemAsync(BatchFileRow item, int index)
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();

                            await semaphore.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                token.ThrowIfCancellationRequested();

                                bool itemIsDemandId = ExcelHelper.IsDemandIdInput(item.RawInput);

                                ExtractionResult result = await _extractionService
                                    .PerformExtractionAsync(item.RawInput, env, false, itemIsDemandId, token)
                                    .ConfigureAwait(false);

                                token.ThrowIfCancellationRequested();

                                string displayContract = !string.IsNullOrWhiteSpace(result.ContractReference) &&
                                                         !string.Equals(result.ContractReference, "Not found", StringComparison.OrdinalIgnoreCase)
                                    ? result.ContractReference
                                    : item.RawInput;

                                if (!string.IsNullOrWhiteSpace(result.LisaContent) || !string.IsNullOrWhiteSpace(result.EliaContent))
                                {
                                    var localReport = new StringBuilder(65536);
                                    localReport.AppendLine(new string('=', 80));
                                    localReport.AppendLine($"### GLOBAL CONTRACT REPORT: {displayContract} | JCL: {result.JclContract} | EXCEL ROW: #{item.RowNumber} | TEST ID: {item.TestId} | ENV: {env} | BRANCH: {result.BranchDisplay} | BUCP/CMD: {result.Bucp}/{result.Cmdpmt} | FONDS: {result.FundsSummary} ###");
                                    localReport.AppendLine($"Contrat (3-7-2)      : {displayContract} | JCL (9 chiffres / NO_CNT) : {result.JclContract} | LISA ID : {result.InternalId}");
                                    localReport.AppendLine($"Demand ID / ELIA ID  : {result.DemandId} / UCON {result.UconId} (Statut Demand : {result.DemandStatus})");
                                    localReport.AppendLine($"Produit / Process    : {result.ProductCode} / {result.ProcessCode} (ChannelCode ELIA : {result.ChannelCode})");
                                    localReport.AppendLine($"Canal & Paiement     : {result.BusinessChannel} -> BUCP = {result.Bucp} | CMDPMT = {result.Cmdpmt} ({result.RoutingRule})");
                                    localReport.AppendLine($"Prime Brute / JCL    : {result.Premium} € -> AMOUNT = {result.AmountJcl}");
                                    localReport.AppendLine($"Date N (Run Date)    : {result.RunDate} ({result.RunDateSource}) | Date N+2 (Consolidation BR23) : {result.ConsolidationDate}");
                                    localReport.AppendLine($"Branche & Fonds      : {result.BranchDisplay} ({result.BranchDetectionReason}) | Fonds : {result.FundsSummary}");
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
                                string itemStatus = result.InternalId != "Not found"
                                    ? "OK"
                                    : (result.UconId != "Not found" ? "ELIA ONLY" : "KO");

                                onProgressUpdate?.Invoke(new BatchProgressInfo
                                {
                                    RowNum = item.RowNumber,
                                    Environment = env,
                                    TestId = item.TestId,
                                    ContractId = displayContract,
                                    JclContract = string.IsNullOrWhiteSpace(result.JclContract) ? "-" : result.JclContract,
                                    InternalId = result.InternalId,
                                    Product = string.IsNullOrWhiteSpace(result.ProductCode) ? "-" : result.ProductCode,
                                    ProductCode = string.IsNullOrWhiteSpace(result.ProductCode) ? "-" : result.ProductCode,
                                    ProcessCode = string.IsNullOrWhiteSpace(result.ProcessCode) ? "-" : result.ProcessCode,
                                    ChannelCode = string.IsNullOrWhiteSpace(result.ChannelCode) ? "-" : result.ChannelCode,
                                    BusinessChannel = string.IsNullOrWhiteSpace(result.BusinessChannel) ? "-" : result.BusinessChannel,
                                    Bucp = string.IsNullOrWhiteSpace(result.Bucp) ? "-" : result.Bucp,
                                    Cmdpmt = string.IsNullOrWhiteSpace(result.Cmdpmt) ? "-" : result.Cmdpmt,
                                    RoutingRule = string.IsNullOrWhiteSpace(result.RoutingRule) ? "-" : result.RoutingRule,
                                    RunDate = string.IsNullOrWhiteSpace(result.RunDate) ? "-" : result.RunDate,
                                    RunDateSource = string.IsNullOrWhiteSpace(result.RunDateSource) ? "-" : result.RunDateSource,
                                    ConsolidationDate = string.IsNullOrWhiteSpace(result.ConsolidationDate) ? "-" : result.ConsolidationDate,
                                    AmountJcl = string.IsNullOrWhiteSpace(result.AmountJcl) ? "-" : result.AmountJcl,
                                    BranchDetectionReason = string.IsNullOrWhiteSpace(result.BranchDetectionReason) ? "-" : result.BranchDetectionReason,
                                    DemandStatus = string.IsNullOrWhiteSpace(result.DemandStatus) ? "-" : result.DemandStatus,
                                    FundsSummary = string.IsNullOrWhiteSpace(result.FundsSummary) ? "-" : result.FundsSummary,
                                    IsBranch23 = result.IsBranch23,
                                    BranchDisplay = result.BranchDisplay,
                                    Premium = string.IsNullOrWhiteSpace(result.Premium) ? "0" : result.Premium,
                                    UconId = result.UconId,
                                    DemandId = result.DemandId,
                                    FilePath = combinedPath,
                                    Status = itemStatus,
                                    TablesExtractedCount = result.TablesExtractedCount,
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
                                RowNum = item.RowNumber,
                                Environment = env,
                                TestId = item.TestId,
                                ContractId = $"{item.RawInput} (FAILED)",
                                InternalId = "Error",
                                Product = "-",
                                ProductCode = "-",
                                ProcessCode = "-",
                                ChannelCode = "-",
                                BusinessChannel = "-",
                                IsBranch23 = false,
                                BranchDisplay = "N/A",
                                Premium = "0",
                                UconId = "Error",
                                DemandId = "Error",
                                FilePath = combinedPath,
                                Status = ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ? "Not found in DB" : "SQL Error",
                                CurrentItem = current,
                                TotalItems = totalItems
                            });
                        }
                    }

                    var tasks = batchRows
                        .Select((item, index) => ProcessContractItemAsync(item, index))
                        .ToList();

                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Allow partial snapshot generation below when cancelled by the user
                    }
                }

                string partialSuffix = token.IsCancellationRequested ? "_PARTIAL_CANCELLED" : string.Empty;
                if (!string.IsNullOrEmpty(partialSuffix))
                {
                    combinedPath = Path.Combine(Settings.OutputDir, $"Extraction_{origFileName}_{envLetter}{partialSuffix}_{timestamp}.csv");
                    finalMergedPath = combinedPath;
                }

                try
                {
                    await WriteMergedBatchOutputAsync(combinedPath, tempDirPath, totalItems, token.IsCancellationRequested).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    string alternativePath = Path.Combine(Settings.OutputDir, $"Extraction_{origFileName}_{envLetter}{partialSuffix}_{timestamp}_{Guid.NewGuid().ToString("N").Substring(0, 4)}.csv");
                    await WriteMergedBatchOutputAsync(alternativePath, tempDirPath, totalItems, token.IsCancellationRequested).ConfigureAwait(false);
                    finalMergedPath = alternativePath;
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
                    catch
                    {
                        // Ignore temporary directory lock by antivirus
                    }
                }
            }

            return finalMergedPath;
        }

        private static async Task WriteMergedBatchOutputAsync(string outputPath, string tempDirPath, int totalItems, bool isCancelled)
        {
            bool anyDataWritten = false;

            using (var finalWriter = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                char[] buffer = new char[16384];

                for (int i = 0; i < totalItems; i++)
                {
                    string tempFile = Path.Combine(tempDirPath, $"{i}.tmp");

                    if (File.Exists(tempFile))
                    {
                        anyDataWritten = true;

                        using (var tempReader = new StreamReader(tempFile, Encoding.UTF8))
                        {
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
