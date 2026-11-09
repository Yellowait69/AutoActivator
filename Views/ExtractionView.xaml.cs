using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;
using AutoActivator.Utils;

namespace AutoActivator.Gui.Views
{
    public partial class ExtractionView : UserControl
    {
        private readonly ExtractionService _extractionService;

        public ObservableCollection<ExtractionItem> ExtractionHistory { get; set; } = new ObservableCollection<ExtractionItem>();

        private int _koExtractionCount = 0;
        private int _totalExtractionCount = 0;

        public ExtractionView()
        {
            InitializeComponent();
            ListHistory.ItemsSource = ExtractionHistory;
            _extractionService = new ExtractionService();
        }

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                // Tab 1 in HelpView corresponds to Extraction
                mainWindow.OpenHelpTargetingTab(1);
            }
        }

        private void BtnCancelExtraction_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.CancelCurrentProcess();
            }
        }

        private static int GetRowNumValue(string rowNumStr)
        {
            return int.TryParse(rowNumStr, out int val) ? val : 0;
        }

        private void AddExtractionItemToHistory(ExtractionItem item)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddExtractionItemToHistory(item));
                return;
            }

            int newItemRow = GetRowNumValue(item.RowNum);
            bool isKo = item.Test == "KO";

            _totalExtractionCount++;
            TxtTotalCount.Text = _totalExtractionCount.ToString();

            int insertIndex = isKo ? 0 : _koExtractionCount;
            int limit = isKo ? _koExtractionCount : ExtractionHistory.Count;

            while (insertIndex < limit)
            {
                if (GetRowNumValue(ExtractionHistory[insertIndex].RowNum) > newItemRow)
                {
                    break;
                }
                insertIndex++;
            }

            ExtractionHistory.Insert(insertIndex, item);

            if (isKo)
            {
                _koExtractionCount++;
                TxtKoCount.Text = _koExtractionCount.ToString();
            }
        }

        private static string FormatContractForDisplay(string contract)
        {
            if (string.IsNullOrWhiteSpace(contract)) return contract;

            string clean = contract.Replace("-", "").Replace(" ", "");

            return clean.Length == 12
                ? $"{clean.Substring(0, 3)}-{clean.Substring(3, 7)}-{clean.Substring(10, 2)}"
                : contract;
        }

        private void InputType_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtExtContract != null) TxtExtContract.Text = string.Empty;
        }

        private void UpdateBatchExtCsvPath()
        {
            if (TxtBatchExtCsv == null) return;

            if (RbBatchSearchContract?.IsChecked == true)
            {
                TxtBatchExtCsv.Text = string.Empty;
            }
            else if (RbBatchSearchDemand?.IsChecked == true)
            {
                string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";
                string channelValue = (CmbExtChannel?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "C01";

                TxtBatchExtCsv.Text = Settings.GetDefaultTestingExcelPath(channelValue, envValue);
            }
        }

        private void BatchInputType_Checked(object sender, RoutedEventArgs e) => UpdateBatchExtCsvPath();
        private void CmbExtEnv_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBatchExtCsvPath();
        private void CmbExtChannel_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBatchExtCsvPath();

        private void BtnAddBaseline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(Settings.OutputDir))
                {
                    MessageBox.Show("The extraction folder does not exist yet. Please run an extraction first.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var latestExtractionFile = Directory.GetFiles(Settings.OutputDir, "*.csv")
                                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                                    .FirstOrDefault();

                if (latestExtractionFile != null)
                {
                    Directory.CreateDirectory(Settings.BaselineDir);

                    string fileName = Path.GetFileName(latestExtractionFile);
                    string destPath = Path.Combine(Settings.BaselineDir, fileName);

                    File.Copy(latestExtractionFile, destPath, true);

                    MessageBox.Show($"The latest extraction ({fileName}) was automatically added to the baselines!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("No extraction was found in the output folder.", "No file", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying the file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenBaselineFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.BaselineDir);
                System.Diagnostics.Process.Start("explorer.exe", Settings.BaselineDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Baseline folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRunSingle_Click(object sender, RoutedEventArgs e)
        {
            if (!(Window.GetWindow(this) is MainWindow mainWindow)) return;

            string? rawInput = TxtExtContract?.Text.Trim();
            bool isDemandId = RbSearchDemand?.IsChecked == true;
            string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";

            if (string.IsNullOrEmpty(rawInput))
            {
                UpdateMainWindowStatus(mainWindow, "Please enter a value.", Brushes.Orange);
                return;
            }

            IProgress<ExtractionItem> progress = new Progress<ExtractionItem>(AddExtractionItemToHistory);

            BtnCancelSingleExt.IsEnabled = true;
            BtnRunSingle.IsEnabled = false;

            await mainWindow.RunProcessAsync(async () =>
            {
                try
                {
                    CancellationToken token = mainWindow.GetCancellationToken();

                    UpdateMainWindowStatus(mainWindow, $"Extracting Environment {envValue}000...");
                    bool succeeded = await PerformSingleExtractionAsync(rawInput, $"{envValue}000", progress, isDemandId, mainWindow, token);

                    if (token.IsCancellationRequested)
                    {
                        UpdateMainWindowStatus(mainWindow, "Single extraction cancelled by user.", Brushes.Orange);
                    }
                    else if (succeeded)
                    {
                        UpdateMainWindowStatus(mainWindow, "Single extraction completed successfully.", Brushes.Green);
                    }
                    else
                    {
                        UpdateMainWindowStatus(mainWindow, "Single extraction finished with errors or contract not found.", Brushes.OrangeRed);
                    }
                }
                catch (OperationCanceledException)
                {
                    UpdateMainWindowStatus(mainWindow, "Single extraction cancelled by user.", Brushes.Orange);
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        BtnCancelSingleExt.IsEnabled = false;
                        BtnRunSingle.IsEnabled = true;
                    });
                }
            });
        }

        private async Task<bool> PerformSingleExtractionAsync(string targetValue, string env, IProgress<ExtractionItem> progress, bool isDemandId, MainWindow mainWindow, CancellationToken token)
        {
            try
            {
                ExtractionResult result = await _extractionService.PerformExtractionAsync(targetValue, env, true, isDemandId, token).ConfigureAwait(false);
                mainWindow.LastGeneratedPath = !string.IsNullOrEmpty(result.FilePath) ? result.FilePath : Settings.OutputDir;

                string displayContract = isDemandId ? FormatContractForDisplay(result.ContractReference) : FormatContractForDisplay(targetValue);
                bool isOk = result.InternalId != "Not found" && result.InternalId != "Error";
                string finalTest = isOk ? "OK" : "KO";

                progress.Report(new ExtractionItem
                {
                    RowNum = "-",
                    ContractId = displayContract,
                    InternalId = result.InternalId,
                    Product = env,
                    Premium = string.IsNullOrWhiteSpace(result.Premium) ? "0" : result.Premium,
                    Ucon = result.UconId,
                    Hdmd = result.DemandId,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = finalTest,
                    FilePath = result.FilePath
                });

                return isOk;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                progress.Report(new ExtractionItem
                {
                    RowNum = "-",
                    ContractId = targetValue,
                    InternalId = "Error",
                    Product = env,
                    Premium = "0",
                    Ucon = "N/A",
                    Hdmd = "N/A",
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = "KO",
                    FilePath = string.Empty
                });
                return false;
            }
        }

        private void BtnBrowseExtCsv_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Data Files (*.csv;*.xls;*.xlsx)|*.csv;*.xls;*.xlsx",
                Title = "Select a file containing contracts",
                InitialDirectory = Path.GetFullPath(Settings.InputDir)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtBatchExtCsv.Text = openFileDialog.FileName;
            }
        }

        public string PrepareCsvFromExcel(string excelFilePath)
        {
            return ExcelHelper.ConvertExcelToCsv(excelFilePath, Settings.OutputDir, "Converted_Ext");
        }

        private async void BtnRunBatch_Click(object sender, RoutedEventArgs e)
        {
            if (!(Window.GetWindow(this) is MainWindow mainWindow)) return;

            string? filePath = TxtBatchExtCsv?.Text.Trim();
            bool isDemandId = RbBatchSearchDemand?.IsChecked == true;
            string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                UpdateMainWindowStatus(mainWindow, "Please select a valid file or verify the network path.", Brushes.Orange);
                return;
            }

            var batchService = new BatchExtractionService(_extractionService);

            IProgress<BatchProgressInfo> progress = new Progress<BatchProgressInfo>(info =>
            {
                mainWindow.TxtStatus.Text = $"Batch extraction in progress: {info.CurrentItem} / {info.TotalItems} contracts processed...";

                string status = (info.InternalId == "Not found" || info.InternalId == "Error" || info.Status.Contains("Error") || info.Status.Contains("Not found"))
                                ? "KO" : info.Status;

                AddExtractionItemToHistory(new ExtractionItem
                {
                    RowNum = info.RowNum.ToString(),
                    ContractId = FormatContractForDisplay(info.ContractId),
                    InternalId = info.InternalId,
                    Product = info.Product,
                    Premium = info.Premium,
                    Ucon = info.UconId,
                    Hdmd = info.DemandId,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = status,
                    FilePath = string.Empty
                });
            });

            BtnCancelBatchExt.IsEnabled = true;
            BtnRunBatch.IsEnabled = false;

            await mainWindow.RunProcessAsync(async () =>
            {
                try
                {
                    CancellationToken token = mainWindow.GetCancellationToken();

                    string actualFile = filePath;
                    UpdateMainWindowStatus(mainWindow, $"Preparing Environment {envValue}000...");

                    if (filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateMainWindowStatus(mainWindow, $"Converting Network Excel to CSV for {envValue}000...");
                        actualFile = await Task.Run(() => PrepareCsvFromExcel(filePath), token).ConfigureAwait(false);
                    }

                    token.ThrowIfCancellationRequested();

                    UpdateMainWindowStatus(mainWindow, $"Launching batch extraction ({envValue}000)...");

                    await batchService.PerformBatchExtractionAsync(actualFile, $"{envValue}000", progress.Report, isDemandId, token).ConfigureAwait(false);

                    mainWindow.LastGeneratedPath = Settings.OutputDir;

                    if (token.IsCancellationRequested)
                    {
                        UpdateMainWindowStatus(mainWindow, "Batch extraction cancelled. Partial files saved in Output folder.", Brushes.Orange);
                    }
                    else
                    {
                        UpdateMainWindowStatus(mainWindow, "Batch extraction completed! Files saved in Output folder.", Brushes.Green);
                    }
                }
                catch (OperationCanceledException)
                {
                    mainWindow.LastGeneratedPath = Settings.OutputDir;
                    UpdateMainWindowStatus(mainWindow, "Batch extraction cancelled by user.", Brushes.Orange);
                }
                catch (Exception ex)
                {
                    UpdateMainWindowStatus(mainWindow, $"Batch Error: {ex.Message}", Brushes.Red);
                    throw;
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        BtnCancelBatchExt.IsEnabled = false;
                        BtnRunBatch.IsEnabled = true;
                    });
                }
            });
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            ExtractionHistory?.Clear();
            _koExtractionCount = 0;
            _totalExtractionCount = 0;
            if (TxtKoCount != null) TxtKoCount.Text = "0";
            if (TxtTotalCount != null) TxtTotalCount.Text = "0";
        }

        private void UpdateMainWindowStatus(MainWindow mainWindow, string text, Brush? color = null)
        {
            Dispatcher.Invoke(() =>
            {
                mainWindow.TxtStatus.Text = text;
                mainWindow.TxtStatus.Foreground = color ?? Brushes.Black;
            });
        }
    }
}