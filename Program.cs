using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;
using AutoActivator.Services;
using AutoActivator.Utils;

namespace AutoActivator
{
    public static class Program
    {
        public static async Task RunCliAsync(string[] args)
        {
            try
            {
                Settings.EnsureDirectoriesExist();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Unable to create directories: {ex.Message}");
            }

            while (true)
            {
                Console.WriteLine("\n==================================================");
                Console.WriteLine("       AUTO-ACTIVATOR L.I.S.A (.NET VERSION)      ");
                Console.WriteLine("==================================================");
                Console.WriteLine("1. Run Single Extraction (LISA & ELIA)");
                Console.WriteLine("2. Run Batch Extraction (CSV / Excel)");
                Console.WriteLine("3. Run Massive Batch Activation");
                Console.WriteLine("4. Run Comparison");
                Console.WriteLine("0. Exit");
                Console.WriteLine("==================================================");
                Console.Write("Your choice: ");

                var choice = Console.ReadLine()?.Trim();

                switch (choice)
                {
                    case "1":
                        await RunTestExtractionAsync();
                        break;
                    case "2":
                        await RunBatchExtractionAsync();
                        break;
                    case "3":
                        await RunActivationAsync();
                        break;
                    case "4":
                        RunComparison();
                        break;
                    case "0":
                        return;
                    default:
                        Console.WriteLine("Invalid choice.");
                        break;
                }
            }
        }

        // -----------------------------------------------------------
        // 1. SINGLE EXTRACTION
        // -----------------------------------------------------------
        private static async Task RunTestExtractionAsync()
        {
            Console.WriteLine("\n--- Starting Single Extraction ---");
            Console.Write("Search by (1) Contract Number or (2) Demand ID? [1]: ");
            bool isDemandId = Console.ReadLine()?.Trim() == "2";

            Console.Write(isDemandId ? "Enter Demand ID: " : "Enter the contract number (e.g., 182-2728195-31): ");
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input)) return;

            Console.Write("Environment suffix (e.g., D000 or Q000) [D000]: ");
            string envSuffix = Settings.NormalizeEnvironmentSuffix(Console.ReadLine());

            var extractionService = new ExtractionService();

            try
            {
                var result = await extractionService.PerformExtractionAsync(input, envSuffix, true, isDemandId);
                Console.WriteLine($"\nExtraction completed! Status: {(result.InternalId == "Not found" || result.InternalId == "Error" ? "KO" : "OK")}");
                Console.WriteLine($"Contract: {result.ContractReference} | Internal ID: {result.InternalId} | Premium: {result.Premium}");
                if (!string.IsNullOrEmpty(result.FilePath))
                {
                    Console.WriteLine($"File generated in:\n--> {result.FilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Extraction failed:\n{ex}");
            }
        }

        // -----------------------------------------------------------
        // 2. BATCH EXTRACTION
        // -----------------------------------------------------------
        private static async Task RunBatchExtractionAsync()
        {
            Console.WriteLine("\n--- Starting Batch Extraction ---");
            Console.Write("Path to CSV/Excel file: ");
            string? filePath = Console.ReadLine()?.Trim('\"', ' ');

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Console.WriteLine("[ERROR] File not found.");
                return;
            }

            Console.Write("Search by (1) Contract Number or (2) Demand ID? [1]: ");
            bool isDemandId = Console.ReadLine()?.Trim() == "2";

            Console.Write("Environment suffix (e.g., D000 or Q000) [D000]: ");
            string envSuffix = Settings.NormalizeEnvironmentSuffix(Console.ReadLine());

            try
            {
                if (filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[INFO] Converting Excel file to CSV...");
                    filePath = ExcelHelper.ConvertExcelToCsv(filePath, Settings.OutputDir, "Converted_CLI");
                }

                var extractionService = new ExtractionService();
                var batchService = new BatchExtractionService(extractionService);

                await batchService.PerformBatchExtractionAsync(
                    filePath,
                    envSuffix,
                    info => Console.WriteLine($"[{info.CurrentItem}/{info.TotalItems}] Row {info.RowNum} | {info.ContractId} | {info.Status}"),
                    isDemandId,
                    CancellationToken.None);

                Console.WriteLine($"\nBatch extraction completed! Output directory: {Settings.OutputDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Batch extraction failed:\n{ex}");
            }
        }

        // -----------------------------------------------------------
        // 3. ACTIVATION (Mode MASSIVE BATCH)
        // -----------------------------------------------------------
        private static async Task RunActivationAsync()
        {
            Console.WriteLine("\n--- Starting Massive Batch Activation ---");
            Console.Write("Environment suffix (e.g., D000 or Q000) [D000]: ");
            string envSuffix = Settings.NormalizeEnvironmentSuffix(Console.ReadLine());
            string envLetter = envSuffix.Substring(0, 1);

            Console.Write("Username Mainframe (e.g., XA3894): ");
            string? usernameInput = Console.ReadLine()?.Trim();
            string username = string.IsNullOrWhiteSpace(usernameInput) ? Settings.DbConfig.Uid : usernameInput;

            Console.Write("Password Mainframe: ");
            string password = Console.ReadLine()?.Trim() ?? string.Empty;

            Console.WriteLine("\nHow do you want to load the contracts?");
            Console.WriteLine("1. Enter a single contract manually");
            Console.WriteLine($"2. Load a list from a text file (located in '{Settings.InputDir}')");
            Console.Write("Choice: ");
            string? inputChoice = Console.ReadLine()?.Trim();

            var contractsToProcess = new List<string>();

            if (inputChoice == "1")
            {
                Console.Write("Enter contract number (e.g., 182-2728195-31): ");
                string? contract = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(contract))
                {
                    contractsToProcess.Add(contract);
                }
            }
            else if (inputChoice == "2")
            {
                Console.Write("Enter the file name (e.g., contrats.txt): ");
                string fileName = Console.ReadLine()?.Trim() ?? "";
                string filePath = Path.Combine(Settings.InputDir, fileName);

                if (File.Exists(filePath))
                {
                    contractsToProcess = File.ReadAllLines(filePath)
                                             .Where(line => !string.IsNullOrWhiteSpace(line))
                                             .Select(line => line.Trim())
                                             .ToList();
                    Console.WriteLine($"\n[INFO] Successfully loaded {contractsToProcess.Count} contracts from {fileName}.");
                }
                else
                {
                    Console.WriteLine($"[ERROR] File not found: {filePath}");
                    return;
                }
            }
            else
            {
                Console.WriteLine("[ERROR] Invalid choice. Returning to main menu.");
                return;
            }

            if (contractsToProcess.Count == 0)
            {
                Console.WriteLine("[WARNING] No contracts to process. Aborting activation.");
                return;
            }

            Console.Write("Enter CUS [Default: MDEEM]: ");
            string? cusInput = Console.ReadLine()?.Trim();
            string cus = string.IsNullOrWhiteSpace(cusInput) ? "MDEEM" : cusInput.ToUpperInvariant();

            string tempCsvPath = Path.Combine(Settings.InputDir, $"temp_batch_{Guid.NewGuid():N}.csv");

            try
            {
                File.WriteAllText(tempCsvPath, "Contract\n" + string.Join("\n", contractsToProcess), Encoding.UTF8);

                var dataService = new ActivationDataService();
                var batchActivationService = new BatchActivationService(dataService);

                Console.WriteLine($"\n[INFO] Launching Bulk Activation for {contractsToProcess.Count} item(s)...");

                var result = await batchActivationService.RunBatchAsync(
                    filePath: tempCsvPath,
                    isDemandId: false,
                    envValue: envLetter,
                    cus: cus,
                    bucp: "80819",
                    cmdpmt: "H",
                    channel: "C01",
                    skipPrime: false,
                    username: username,
                    password: password,
                    outputDir: Settings.OutputDir,
                    onProgress: msg => Console.WriteLine(msg),
                    token: CancellationToken.None
                );

                Console.WriteLine("\n==================================================");
                Console.WriteLine("BATCH ACTIVATION PROCESS FINISHED!");
                Console.WriteLine($"Success: {result.successCount} | Already Active: {result.alreadyActiveCount} | Errors: {result.errorCount}");
                Console.WriteLine($"Report saved at: {result.reportPath}");
                Console.WriteLine("==================================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL ERROR] Bulk Activation failed:\n{ex}");
            }
            finally
            {
                if (File.Exists(tempCsvPath))
                {
                    File.Delete(tempCsvPath);
                }
            }
        }

        // -----------------------------------------------------------
        // 4. COMPARISON
        // -----------------------------------------------------------
        private static void RunComparison()
        {
            Console.WriteLine("\n--- Starting Comparator ---");
            Console.Write("Path to Base File (e.g., ExtractionLISA_...): ");
            string? baseFile = Console.ReadLine()?.Trim('\"', ' ');

            Console.Write("Path to Target File (e.g., snapshot/ExtractionLISA_...): ");
            string? targetFile = Console.ReadLine()?.Trim('\"', ' ');

            if (string.IsNullOrWhiteSpace(baseFile) || string.IsNullOrWhiteSpace(targetFile) || !File.Exists(baseFile) || !File.Exists(targetFile))
            {
                Console.WriteLine("[ERROR] One or both files do not exist.");
                return;
            }

            var orchestrator = new ComparisonOrchestrator();
            try
            {
                var report = orchestrator.RunFullComparison(baseFile, targetFile);

                Console.WriteLine($"\n=== COMPARISON REPORT ===");
                Console.WriteLine($"Global Success: {report.GlobalSuccessPercentage}%");
                Console.WriteLine($"Total Rows Compared: {report.TotalRowsCompared}");
                Console.WriteLine($"Total Differences: {report.TotalDifferencesFound}\n");

                foreach (var fileResult in report.FileResults)
                {
                    Console.WriteLine($"[{fileResult.Status}] {fileResult.FileType} - {fileResult.TableName}");
                    if (!fileResult.IsMatch && !string.IsNullOrEmpty(fileResult.ErrorDetails))
                    {
                        Console.WriteLine(fileResult.ErrorDetails);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Comparison failed:\n{ex}");
            }
        }
    }
}