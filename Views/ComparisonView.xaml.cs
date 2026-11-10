using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;

namespace AutoActivator.Gui.Views
{
    public class UIComparisonResult
    {
        internal static readonly SolidColorBrush MatchBrush = CreateFrozenBrush("#2ECC71");
        internal static readonly SolidColorBrush DiffBrush = CreateFrozenBrush("#E74C3C");

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public string TestId { get; set; } = string.Empty;
        public string ExcelRowDisplay { get; set; } = "-";
        public string ContractNumber { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string CleanTableName { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string ProcessCode { get; set; } = string.Empty;
        public string ChannelCode { get; set; } = "-";
        public string BusinessChannel { get; set; } = "-";
        public string BucpCmdpmtDisplay { get; set; } = "-";
        public string FundsSummary { get; set; } = "-";
        public bool IsBranch23 { get; set; }
        public string BranchDisplay => IsBranch23 ? "Branche 23" : "Branche 21";
        public string RowsSummary { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ErrorDetails { get; set; } = string.Empty;
        public int DifferingColumnsCount { get; set; }
        public string DifferingColumnsSummary { get; set; } = "-";
        public bool IsMatch { get; set; }
        public string StatusIcon => IsMatch ? "✔ OK" : "✖ KO";
        public SolidColorBrush StatusColor => IsMatch ? MatchBrush : DiffBrush;
    }

    public partial class ComparisonView : UserControl
    {
        private static readonly SolidColorBrush YellowStrokeBrush = CreateFrozenBrush("#F1C40F");
        private static readonly SolidColorBrush OrangeTitleBrush = CreateFrozenBrush("#F39C12");

        private List<UIComparisonResult> _currentResults = new List<UIComparisonResult>();
        private ICollectionView? _resultsCollectionView;

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public ComparisonView()
        {
            InitializeComponent();
        }

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.OpenHelpTargetingTab(2);
            }
        }

        private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                LoadBaselines();
            }
        }

        public void LoadLatestExtractionForComparison(string? targetCsvPath)
        {
            LoadBaselines();

            if (!string.IsNullOrWhiteSpace(targetCsvPath) && File.Exists(targetCsvPath))
            {
                TxtTargetFile.Text = targetCsvPath;
                string fileName = Path.GetFileName(targetCsvPath);
                string[] channels = { "C01", "C02", "C03", "C05" };
                string? foundChannel = channels.FirstOrDefault(
                    c => fileName.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0);

                if (foundChannel != null && Directory.Exists(Settings.BaselineDir))
                {
                    string? matchingBaseline = Directory.GetFiles(Settings.BaselineDir, $"*{foundChannel}*.csv")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(matchingBaseline))
                    {
                        TxtBaseFile.Text = matchingBaseline;
                    }
                }

                CheckEnableRunButton();
            }
        }

        private void LoadBaselines()
        {
            if (Directory.Exists(Settings.BaselineDir))
            {
                var files = Directory.GetFiles(Settings.BaselineDir, "*.csv").Select(f => Path.GetFileName(f)).ToList();
                files.Insert(0, "-- Select a Baseline --");

                CmbBaselineSelector.SelectionChanged -= SmartMatcher_Changed;
                CmbBaselineSelector.ItemsSource = files;
                CmbBaselineSelector.SelectedIndex = 0;
                CmbBaselineSelector.SelectionChanged += SmartMatcher_Changed;
            }
        }

        private void SmartMatcher_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBaselineSelector == null || CmbSmartEnv == null || TxtBaseFile == null || TxtTargetFile == null) return;

            if (CmbBaselineSelector.SelectedIndex > 0 && CmbBaselineSelector.SelectedItem is string selectedFileName)
            {
                string basePath = Path.Combine(Settings.BaselineDir, selectedFileName);
                TxtBaseFile.Text = basePath;

                string envLetter = CmbSmartEnv.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "D" : "D";

                string? targetPath = FindLatestMatchingExtraction(selectedFileName, envLetter);

                if (!string.IsNullOrEmpty(targetPath))
                {
                    TxtTargetFile.Text = targetPath;
                }
                else
                {
                    TxtTargetFile.Text = "";
                    MessageBox.Show($"No recent extraction found for {selectedFileName} in environment {envLetter}000.\nPlease ensure you have run an extraction recently.", "No Match Found", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                CheckEnableRunButton();
            }
            else if (CmbBaselineSelector.SelectedIndex == 0)
            {
                TxtBaseFile.Text = "";
                TxtTargetFile.Text = "";
                CheckEnableRunButton();
            }
        }

        private string? FindLatestMatchingExtraction(string baselineName, string envLetter)
        {
            string[] channels = { "C01", "C02", "C03", "C05" };
            string? foundChannel = channels.FirstOrDefault(c => baselineName.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0);

            if (foundChannel != null && Directory.Exists(Settings.OutputDir))
            {
                string searchPattern = $"*{foundChannel}*_{envLetter}_*.csv";

                var files = Directory.GetFiles(Settings.OutputDir, searchPattern)
                                     .Where(f => !Path.GetFileName(f).StartsWith("Converted_", StringComparison.OrdinalIgnoreCase) &&
                                                 !Path.GetFileName(f).StartsWith("ComparisonGrid_", StringComparison.OrdinalIgnoreCase))
                                     .ToList();
                if (files.Any())
                {
                    return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                }
            }
            return null;
        }

        private void BtnSwapFiles_Click(object sender, RoutedEventArgs e)
        {
            if (TxtBaseFile == null || TxtTargetFile == null) return;

            string temp = TxtBaseFile.Text;
            TxtBaseFile.Text = TxtTargetFile.Text;
            TxtTargetFile.Text = temp;
            CheckEnableRunButton();
        }

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
                                                    .Where(f => !Path.GetFileName(f).StartsWith("Converted_", StringComparison.OrdinalIgnoreCase) &&
                                                                !Path.GetFileName(f).StartsWith("ExtractionHistory_", StringComparison.OrdinalIgnoreCase) &&
                                                                !Path.GetFileName(f).StartsWith("ActivationHistory_", StringComparison.OrdinalIgnoreCase) &&
                                                                !Path.GetFileName(f).StartsWith("ComparisonGrid_", StringComparison.OrdinalIgnoreCase))
                                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                                    .FirstOrDefault();

                if (latestExtractionFile != null)
                {
                    Directory.CreateDirectory(Settings.BaselineDir);

                    string fileName = Path.GetFileName(latestExtractionFile);
                    string destPath = Path.Combine(Settings.BaselineDir, fileName);

                    File.Copy(latestExtractionFile, destPath, true);

                    MessageBox.Show($"The latest extraction ({fileName}) was automatically added to the baselines!", "Baseline Added", MessageBoxButton.OK, MessageBoxImage.Information);

                    LoadBaselines();
                }
                else
                {
                    MessageBox.Show("No extraction was found in the output folder.", "No File", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error while copying the file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenBaselineFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.BaselineDir);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = Settings.BaselineDir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Baseline folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnBrowseBase_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "CSV Files|*.csv",
                Title = "Select the base extraction CSV file",
                InitialDirectory = Directory.Exists(Settings.BaselineDir) ? Path.GetFullPath(Settings.BaselineDir) : Path.GetFullPath(Settings.OutputDir)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                CmbBaselineSelector.SelectionChanged -= SmartMatcher_Changed;
                CmbBaselineSelector.SelectedIndex = 0;
                CmbBaselineSelector.SelectionChanged += SmartMatcher_Changed;

                TxtBaseFile.Text = openFileDialog.FileName;
                CheckEnableRunButton();
            }
        }

        private void BtnBrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "CSV Files|*.csv",
                Title = "Select the target extraction CSV file",
                InitialDirectory = Path.GetFullPath(Settings.OutputDir)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                CmbBaselineSelector.SelectionChanged -= SmartMatcher_Changed;
                CmbBaselineSelector.SelectedIndex = 0;
                CmbBaselineSelector.SelectionChanged += SmartMatcher_Changed;

                TxtTargetFile.Text = openFileDialog.FileName;
                CheckEnableRunButton();
            }
        }

        private void CheckEnableRunButton()
        {
            BtnRunComparison.IsEnabled = !string.IsNullOrWhiteSpace(TxtBaseFile.Text) &&
                                         !string.IsNullOrWhiteSpace(TxtTargetFile.Text);
        }

        // =============================================================
        // LIVE FILTERING & CSV EXPORT
        // =============================================================

        private void FilterComparison_Changed(object sender, EventArgs e)
        {
            _resultsCollectionView?.Refresh();
        }

        private bool FilterComparisonPredicate(object obj)
        {
            if (obj is not UIComparisonResult item)
            {
                return false;
            }

            if (CmbFilterCompContract?.SelectedItem is ComboBoxItem contractItem)
            {
                string contractTag = contractItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(contractTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.TestId, contractTag, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.ContractNumber, contractTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (CmbFilterCompStatus?.SelectedItem is ComboBoxItem statusItem)
            {
                string statusTag = statusItem.Tag?.ToString() ?? "ALL";
                if (string.Equals(statusTag, "KO", StringComparison.OrdinalIgnoreCase) && item.IsMatch)
                {
                    return false;
                }
                if (string.Equals(statusTag, "OK", StringComparison.OrdinalIgnoreCase) && !item.IsMatch)
                {
                    return false;
                }
            }

            if (CmbFilterCompSystem?.SelectedItem is ComboBoxItem sysItem)
            {
                string sysTag = sysItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(sysTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.FileType, sysTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (CmbFilterCompBranch?.SelectedItem is ComboBoxItem branchItem)
            {
                string branchTag = branchItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(branchTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.BranchDisplay, branchTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string query = TxtFilterComparison?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (item.ExcelRowDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TestId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.CleanTableName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TableName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProductCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProcessCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BusinessChannel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BucpCmdpmtDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.FundsSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BranchDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.DifferingColumnsSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ErrorDetails?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void BtnExportComparisonCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count == 0)
            {
                MessageBox.Show(
                    "Please run a comparison first before exporting the results.",
                    "Export CSV",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(Settings.OutputDir);
                string exportPath = Path.Combine(
                    Settings.OutputDir,
                    $"ComparisonGrid_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("Status;ExcelRow;TestId;ContractNumber;System;Branche;Product;Process;BusinessChannel;BucpCmdpmt;Funds;SqlTable;RowsOkTotal;DiscrepancyDetails");

                foreach (UIComparisonResult row in _currentResults)
                {
                    sb.AppendLine(string.Join(";",
                        EscapeCsv(row.StatusIcon),
                        EscapeCsv(row.ExcelRowDisplay),
                        EscapeCsv(row.TestId),
                        EscapeCsv(row.ContractNumber),
                        EscapeCsv(row.FileType),
                        EscapeCsv(row.BranchDisplay),
                        EscapeCsv(row.ProductCode),
                        EscapeCsv(row.ProcessCode),
                        EscapeCsv(row.BusinessChannel),
                        EscapeCsv(row.BucpCmdpmtDisplay),
                        EscapeCsv(row.FundsSummary),
                        EscapeCsv(row.CleanTableName),
                        EscapeCsv(row.RowsSummary),
                        EscapeCsv(row.ErrorDetails)));
                }

                File.WriteAllText(exportPath, sb.ToString(), new UTF8Encoding(true));

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.LastGeneratedPath = exportPath;
                    mainWindow.TxtStatus.Text = $"Comparison table exported to CSV: {Path.GetFileName(exportPath)}";
                    mainWindow.TxtStatus.Foreground = Brushes.ForestGreen;
                }

                MessageBox.Show(
                    $"Comparison results exported successfully ({_currentResults.Count} rows):\n\n{exportPath}",
                    "CSV Export Completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to export comparison results: {ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string clean = value.Replace("\r", " ").Replace("\n", " | ").Trim();
            if (clean.Contains(';') || clean.Contains('"'))
            {
                return $"\"{clean.Replace("\"", "\"\"")}\"";
            }

            return clean;
        }

        // =============================================================
        // EXECUTION & DASHBOARD RENDERING
        // =============================================================

        private void ChkIgnoreTechColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count > 0 && BtnRunComparison.IsEnabled)
            {
                BtnRunComparisonAction_Click(sender, e);
            }
        }

        private async void BtnRunComparisonAction_Click(object sender, RoutedEventArgs e)
        {
            if (!(Window.GetWindow(this) is MainWindow mainWindow)) return;

            string baseFile = TxtBaseFile.Text;
            string targetFile = TxtTargetFile.Text;
            bool ignoreTechCols = ChkIgnoreTechColumns?.IsChecked == true;

            if (string.IsNullOrEmpty(baseFile) || string.IsNullOrEmpty(targetFile)) return;

            BtnRunComparison.IsEnabled = false;

            PanelDashboard.Visibility = Visibility.Collapsed;
            PanelFilterBar.Visibility = Visibility.Collapsed;
            GridResults.Visibility = Visibility.Collapsed;
            if (PanelInlineDiffInspector != null) PanelInlineDiffInspector.Visibility = Visibility.Collapsed;
            TxtComparisonWaiting.Visibility = Visibility.Visible;
            TxtComparisonWaiting.Foreground = Brushes.Gray;
            TxtComparisonWaiting.Text = "Deep analysis in progress...\n(Filtering the latest contract by Test ID and detecting Branche 21 / Branche 23). Please wait.";
            mainWindow.TxtStatus.Text = "Running table-by-table comparison...";

            await mainWindow.RunProcessAsync(async () =>
            {
                await Task.Run(async () =>
                {
                    var orchestrator = new ComparisonOrchestrator();
                    try
                    {
                        var report = orchestrator.RunFullComparison(baseFile, targetFile, ignoreTechCols);

                        string reportContent = GenerateReportText(report);

                        string[] fileIds = orchestrator.GetFileIds(baseFile);
                        string uniqueId = fileIds.Length >= 3 ? fileIds[2] : "UnknownID";
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                        Directory.CreateDirectory(Settings.OutputDir);
                        string reportFilePath = Path.Combine(Settings.OutputDir, $"ComparisonReport_{uniqueId}_{timestamp}.txt");

                        using (StreamWriter writer = new StreamWriter(reportFilePath, false, Encoding.UTF8))
                        {
                            await writer.WriteAsync(reportContent).ConfigureAwait(false);
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            mainWindow.LastGeneratedPath = reportFilePath;
                            mainWindow.TxtStatus.Text = $"Comparison completed — Global score: {report.GlobalSuccessPercentage}% ({report.TotalDifferencesFound:N0} diff(s) across {report.TotalRowsCompared:N0} rows).";
                            UpdateDashboardUI(report);
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            TxtComparisonWaiting.Text = $"CRITICAL ERROR:\n{ex.Message}\n\n{ex.StackTrace}";
                            TxtComparisonWaiting.Foreground = Brushes.Red;
                        });
                        throw;
                    }
                });
            });

            CheckEnableRunButton();
        }

        private void UpdateDashboardUI(ComparisonReport report)
        {
            TxtComparisonWaiting.Visibility = Visibility.Collapsed;
            PanelDashboard.Visibility = Visibility.Visible;
            PanelFilterBar.Visibility = Visibility.Visible;
            GridResults.Visibility = Visibility.Visible;
            if (PanelInlineDiffInspector != null) PanelInlineDiffInspector.Visibility = Visibility.Visible;

            int matchedTables = report.FileResults.Count(r => r.IsMatch);
            int totalTables = report.FileResults.Count;

            int lisaTotal = report.FileResults.Count(r => string.Equals(r.FileType, "LISA", StringComparison.OrdinalIgnoreCase));
            int lisaOk = report.FileResults.Count(r => string.Equals(r.FileType, "LISA", StringComparison.OrdinalIgnoreCase) && r.IsMatch);
            int eliaTotal = report.FileResults.Count(r => string.Equals(r.FileType, "ELIA", StringComparison.OrdinalIgnoreCase));
            int eliaOk = report.FileResults.Count(r => string.Equals(r.FileType, "ELIA", StringComparison.OrdinalIgnoreCase) && r.IsMatch);

            int br21Total = report.FileResults.Count(r => !r.IsBranch23);
            int br21Ok = report.FileResults.Count(r => !r.IsBranch23 && r.IsMatch);
            int br23Total = report.FileResults.Count(r => r.IsBranch23);
            int br23Ok = report.FileResults.Count(r => r.IsBranch23 && r.IsMatch);

            int contractsOk = report.TestMetrics.Values.Count(m => m.ErrorRows == 0);
            int contractsKo = report.TestMetrics.Values.Count(m => m.ErrorRows > 0);
            int totalDiffCols = report.FileResults.Sum(r => r.DifferingColumnsCount);

            if (TxtComparedTests != null) TxtComparedTests.Text = report.ComparedTestsCount.ToString("N0", CultureInfo.InvariantCulture);
            if (TxtContractsSummary != null) TxtContractsSummary.Text = $"{contractsOk} OK / {contractsKo} avec écart(s)";
            if (TxtTablesMatched != null) TxtTablesMatched.Text = $"{matchedTables} / {totalTables}";
            if (TxtSystemBreakdown != null) TxtSystemBreakdown.Text = $"LISA: {lisaOk}/{lisaTotal} | ELIA: {eliaOk}/{eliaTotal}";
            if (TxtBranchBreakdown != null) TxtBranchBreakdown.Text = $"B21: {br21Ok}/{br21Total} | B23: {br23Ok}/{br23Total}";
            TxtTotalRows.Text = report.TotalRowsCompared.ToString("N0", CultureInfo.InvariantCulture);
            TxtTotalErrors.Text = report.TotalDifferencesFound.ToString("N0", CultureInfo.InvariantCulture);
            if (TxtDiffColsTotal != null) TxtDiffColsTotal.Text = $"{totalDiffCols} col. modifiée(s)";
            TxtScorePercentage.Text = $"{report.GlobalSuccessPercentage}%";

            string testSummary = string.Join(" | ", report.TestMetrics.Select(t => $"{t.Key}: {t.Value.SuccessPercentage}%"));
            string cleanTestScores = $"\n🎯 Test Scores: {testSummary}";

            string productSummary = string.Join(" | ", report.ProductMetrics.Select(p => $"{p.Key}: {p.Value.SuccessPercentage}%"));
            string cleanProductScores = report.ProductMetrics.Any() ? $"\n📦 Product Scores: {productSummary}" : "";

            string combinedScores = cleanTestScores + cleanProductScores;

            double radius = 64.0;
            double circumference = 2 * Math.PI * radius;
            double dashLength = (report.GlobalSuccessPercentage / 100.0) * circumference;

            CircleProgress.StrokeDashArray = new DoubleCollection { dashLength / 12.0, circumference };

            if (report.GlobalSuccessPercentage == 100)
            {
                CircleProgress.Stroke = UIComparisonResult.MatchBrush;
                TxtDashboardTitle.Text = "🎉 Perfect Match!" + combinedScores;
                TxtDashboardTitle.Foreground = UIComparisonResult.MatchBrush;
            }
            else if (report.GlobalSuccessPercentage >= 95)
            {
                CircleProgress.Stroke = YellowStrokeBrush;
                TxtDashboardTitle.Text = "⚠️ Minor Discrepancies" + combinedScores;
                TxtDashboardTitle.Foreground = OrangeTitleBrush;
            }
            else
            {
                CircleProgress.Stroke = UIComparisonResult.DiffBrush;
                TxtDashboardTitle.Text = "❌ Differences Detected" + combinedScores;
                TxtDashboardTitle.Foreground = UIComparisonResult.DiffBrush;
            }

            var uiResults = new List<UIComparisonResult>(report.FileResults.Count);
            foreach (var r in report.FileResults)
            {
                uiResults.Add(new UIComparisonResult
                {
                    TestId = string.IsNullOrWhiteSpace(r.TestId) ? "-" : r.TestId,
                    ExcelRowDisplay = string.IsNullOrWhiteSpace(r.ExcelRowDisplay) ? "-" : r.ExcelRowDisplay,
                    ContractNumber = string.IsNullOrWhiteSpace(r.ContractNumber) ? "-" : r.ContractNumber,
                    FileType = string.IsNullOrWhiteSpace(r.FileType) ? "-" : r.FileType,
                    TableName = r.TableName,
                    CleanTableName = r.CleanTableName,
                    ProductCode = string.IsNullOrWhiteSpace(r.ProductCode) ? "-" : r.ProductCode,
                    ProcessCode = string.IsNullOrWhiteSpace(r.ProcessCode) ? "-" : r.ProcessCode,
                    ChannelCode = string.IsNullOrWhiteSpace(r.ChannelCode) ? "-" : r.ChannelCode,
                    BusinessChannel = string.IsNullOrWhiteSpace(r.BusinessChannel) ? "-" : r.BusinessChannel,
                    BucpCmdpmtDisplay = string.IsNullOrWhiteSpace(r.BucpCmdpmtDisplay) ? "-" : r.BucpCmdpmtDisplay,
                    FundsSummary = string.IsNullOrWhiteSpace(r.FundsSummary) ? "-" : r.FundsSummary,
                    IsBranch23 = r.IsBranch23,
                    RowsSummary = r.RowsSummary,
                    Status = r.Status,
                    IsMatch = r.IsMatch,
                    DifferingColumnsCount = r.DifferingColumnsCount,
                    DifferingColumnsSummary = string.IsNullOrWhiteSpace(r.DifferingColumnsSummary)
                        ? (r.IsMatch ? "✓ Identique" : "-")
                        : r.DifferingColumnsSummary,
                    ErrorDetails = string.IsNullOrWhiteSpace(r.ErrorDetails) ? "✓ Aucune différence détectée entre Baseline et Target." : r.ErrorDetails.Trim()
                });
            }

            _currentResults = uiResults.OrderBy(x => x.IsMatch).ThenBy(x => x.TestId).ThenBy(x => x.CleanTableName).ToList();

            if (CmbFilterCompContract != null)
            {
                CmbFilterCompContract.SelectionChanged -= FilterComparison_Changed;
                CmbFilterCompContract.Items.Clear();
                CmbFilterCompContract.Items.Add(new ComboBoxItem
                {
                    Content = $"Tous les Contrats ({report.ComparedTestsCount})",
                    Tag = "ALL"
                });

                var byTest = _currentResults
                    .GroupBy(x => x.TestId, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var grp in byTest)
                {
                    string contractNum = grp.First().ContractNumber;
                    int koTables = grp.Count(x => !x.IsMatch);
                    string badge = koTables == 0 ? "✔ 100% OK" : $"✖ {koTables} KO";
                    CmbFilterCompContract.Items.Add(new ComboBoxItem
                    {
                        Content = $"{contractNum} [{grp.Key}] ({badge})",
                        Tag = grp.Key
                    });
                }

                CmbFilterCompContract.SelectedIndex = 0;
                CmbFilterCompContract.SelectionChanged += FilterComparison_Changed;
            }

            _resultsCollectionView = CollectionViewSource.GetDefaultView(_currentResults);
            if (_resultsCollectionView != null)
            {
                _resultsCollectionView.Filter = FilterComparisonPredicate;
            }

            GridResults.ItemsSource = _resultsCollectionView ?? (System.Collections.IEnumerable)_currentResults;
            if (_currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }
        }

        private string GenerateReportText(ComparisonReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=====================================================");
            sb.AppendLine("                 COMPARISON REPORT                   ");
            sb.AppendLine("=====================================================");

            sb.AppendLine($"[TESTS METRICS]");
            sb.AppendLine($"Tests in Base File   : {report.TotalBaseTests}");
            sb.AppendLine($"Tests in Target File : {report.TotalTargetTests}");
            sb.AppendLine($"Tests Compared       : {report.ComparedTestsCount}");

            if (report.MissingInTarget.Any() || report.MissingInBase.Any())
            {
                sb.AppendLine("\n[DISCREPANCIES - NON BLOCKING INFO]");
                if (report.MissingInTarget.Any())
                {
                    sb.AppendLine($"- Tests found ONLY in Base (ignored): {string.Join(", ", report.MissingInTarget)}");
                }
                if (report.MissingInBase.Any())
                {
                    sb.AppendLine($"- Tests found ONLY in Target (ignored): {string.Join(", ", report.MissingInBase)}");
                }
            }

            sb.AppendLine("\n[PRODUCT SCORES]");
            if (report.ProductMetrics.Any())
            {
                foreach (var kvp in report.ProductMetrics.OrderByDescending(x => x.Value.SuccessPercentage))
                {
                    sb.AppendLine($"- Product {kvp.Key,-10} : {kvp.Value.SuccessPercentage,6}%  ({kvp.Value.TotalRows - kvp.Value.ErrorRows}/{kvp.Value.TotalRows} rows OK)");
                }
            }
            else
            {
                sb.AppendLine("- No products detected.");
            }

            sb.AppendLine("\n[TEST ID SCORES]");
            foreach (var kvp in report.TestMetrics.OrderBy(x => x.Key))
            {
                sb.AppendLine($"- Test {kvp.Key,-13} : {kvp.Value.SuccessPercentage,6}%  ({kvp.Value.TotalRows - kvp.Value.ErrorRows}/{kvp.Value.TotalRows} rows OK)");
            }

            sb.AppendLine("-----------------------------------------------------");

            sb.AppendLine($"Global Success Rate : {report.GlobalSuccessPercentage} %");
            sb.AppendLine($"Total Rows Analyzed : {report.TotalRowsCompared}");
            sb.AppendLine($"Total Errors Found  : {report.TotalDifferencesFound}");
            sb.AppendLine("=====================================================\n");

            if (report.GlobalSuccessPercentage == 100)
            {
                sb.AppendLine("✅ PERFECT MATCH!");
                sb.AppendLine("All tables for the common tested contracts are perfectly identical.");
            }
            else
            {
                foreach (var result in report.FileResults)
                {
                    sb.AppendLine($"--- TABLE : {result.TableName} ({result.FileType} | Contract: {result.ContractNumber} | {result.BranchDisplay} | Product: {result.ProductCode} | Rows: {result.RowsSummary}) ---");

                    if (result.IsMatch)
                    {
                        sb.AppendLine("Status : ✅ OK (No differences)\n");
                    }
                    else
                    {
                        sb.AppendLine($"Status : ❌ {result.Status}");
                        sb.AppendLine("Details:");
                        sb.AppendLine(result.ErrorDetails);
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }

        private void GridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridResults.SelectedItem is not UIComparisonResult selected)
            {
                return;
            }

            if (TxtInlineDiffHeader != null)
            {
                TxtInlineDiffHeader.Text =
                    $"🔎 Inspecteur d'Écarts — {selected.CleanTableName} ({selected.FileType}) | Contrat : {selected.ContractNumber} [{selected.TestId}] | {selected.StatusIcon}";
                TxtInlineDiffHeader.Foreground = selected.IsMatch
                    ? Brushes.MediumSeaGreen
                    : Brushes.Salmon;
            }

            if (TxtInlineDiffSubHeader != null)
            {
                TxtInlineDiffSubHeader.Text =
                    $"Excel Row: {selected.ExcelRowDisplay}  •  Branche: {selected.BranchDisplay} (Fonds: {selected.FundsSummary})  •  " +
                    $"Produit/Process: {selected.ProductCode}/{selected.ProcessCode}  •  Canal: {selected.BusinessChannel} (BUCP/CMD: {selected.BucpCmdpmtDisplay})  •  " +
                    $"Lignes OK/Total: {selected.RowsSummary}  •  Colonnes en écart: {selected.DifferingColumnsSummary}";
            }

            if (TxtInlineDiffContent != null)
            {
                TxtInlineDiffContent.Text = selected.ErrorDetails;
            }
        }

        private void BtnCopyInlineDiff_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem is UIComparisonResult selected)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Table: {selected.CleanTableName} ({selected.FileType}) | Contract: {selected.ContractNumber} [{selected.TestId}] | Status: {selected.Status}");
                sb.AppendLine($"Excel Row: {selected.ExcelRowDisplay} | Branche: {selected.BranchDisplay} | Canal: {selected.BusinessChannel} ({selected.BucpCmdpmtDisplay}) | Rows: {selected.RowsSummary}");
                sb.AppendLine(selected.ErrorDetails);
                Clipboard.SetText(sb.ToString());
            }
        }

        private void GridResults_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            InspectSelectedComparisonRow();
        }

        private void MenuCompInspect_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem == null && _currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }
            InspectSelectedComparisonRow();
        }

        private void InspectSelectedComparisonRow()
        {
            if (GridResults.SelectedItem is not UIComparisonResult selected)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("====================================================================================");
            sb.AppendLine("                   SQL TABLE COMPARISON DIAGNOSTIC REPORT");
            sb.AppendLine("====================================================================================");
            sb.AppendLine("[0. SYNTHÈSE DU CONTRAT & ROUTAGE MÉTIER]");
            sb.AppendLine($"  • Contrat (3-7-2)      : {selected.ContractNumber}  |  Excel Row : {selected.ExcelRowDisplay}  |  Test ID : {selected.TestId}");
            sb.AppendLine($"  • Produit / Process    : {selected.ProductCode}  /  {selected.ProcessCode}  (ChannelCode ELIA : {selected.ChannelCode})");
            sb.AppendLine($"  • Canal & Paiement     : {selected.BusinessChannel}  ->  BUCP / CMDPMT = {selected.BucpCmdpmtDisplay}");
            sb.AppendLine($"  • Branche & Fonds      : {selected.BranchDisplay}  |  Fonds : {selected.FundsSummary}");
            sb.AppendLine($"  • Table SQL Comparée   : {selected.CleanTableName} ({selected.FileType})  |  Lignes OK/Total : {selected.RowsSummary}  |  Statut : {selected.Status}");
            sb.AppendLine("------------------------------------------------------------------------------------");
            sb.AppendLine("[1. DÉTAIL DES ÉCARTS CELLULE PAR CELLULE (BASELINE vs TARGET)]");
            sb.AppendLine(selected.ErrorDetails);

            var reportWindow = new DiagnosticReportWindow(
                title: $"Table Comparison — {selected.CleanTableName} ({selected.BranchDisplay})",
                subtitle: $"Excel Row: {selected.ExcelRowDisplay} | Test: {selected.TestId} | Contract: {selected.ContractNumber} | Canal: {selected.BusinessChannel} ({selected.BucpCmdpmtDisplay}) | Rows: {selected.RowsSummary}",
                status: selected.IsMatch ? "SUCCESS" : "FAILED",
                reportContent: sb.ToString(),
                contractReference: selected.ContractNumber != "-" ? selected.ContractNumber : selected.TableName)
            {
                Owner = Window.GetWindow(this)
            };
            reportWindow.ShowDialog();
        }

        private void MenuCompSendToExtraction_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem == null && _currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }
            if (GridResults.SelectedItem is not UIComparisonResult selected ||
                string.IsNullOrWhiteSpace(selected.ContractNumber) ||
                selected.ContractNumber == "-")
            {
                MessageBox.Show("Please select a comparison row with a valid Contract Number.", "Re-Extract Contract", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string envLetter = CmbSmartEnv?.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "Q" : "Q";
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToExtractionWithContract(selected.ContractNumber, false, envLetter);
            }
        }

        private void MenuCompSendToActivation_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem == null && _currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }
            if (GridResults.SelectedItem is not UIComparisonResult selected ||
                string.IsNullOrWhiteSpace(selected.ContractNumber) ||
                selected.ContractNumber == "-")
            {
                MessageBox.Show("Please select a comparison row with a valid Contract Number.", "Activate Contract", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string envLetter = CmbSmartEnv?.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "Q" : "Q";
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToActivationWithContract(selected.ContractNumber, false, envLetter);
            }
        }

        private void MenuCompCopyContract_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem is UIComparisonResult selected &&
                !string.IsNullOrWhiteSpace(selected.ContractNumber) &&
                selected.ContractNumber != "-")
            {
                Clipboard.SetText(selected.ContractNumber);
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                string pathToOpen = mainWindow.LastGeneratedPath;
                if (!string.IsNullOrEmpty(pathToOpen))
                {
                    if (File.Exists(pathToOpen))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(pathToOpen)}\"")
                        {
                            UseShellExecute = true
                        });
                        return;
                    }
                    if (Directory.Exists(pathToOpen))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(pathToOpen))
                        {
                            UseShellExecute = true,
                            Verb = "open"
                        });
                        return;
                    }
                }

                if (Directory.Exists(Settings.OutputDir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(Settings.OutputDir))
                    {
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    MessageBox.Show("No output folder is available at the moment.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}
