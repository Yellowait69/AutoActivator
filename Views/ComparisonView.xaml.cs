using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;
using AutoActivator.Utils;

namespace AutoActivator.Gui.Views
{
    public sealed class HotTableSummaryPill
    {
        public string CleanTableName { get; init; } = string.Empty;

        public string BadgeSummary { get; init; } = string.Empty;

        public string TooltipText { get; init; } = string.Empty;
    }

    public sealed class DiffColumnFilterChip
    {
        public string ColumnFilterKey { get; init; } = string.Empty;

        public string DisplayLabel { get; init; } = string.Empty;

        public SolidColorBrush ChipBackground { get; init; } = Brushes.White;

        public SolidColorBrush ChipForeground { get; init; } = Brushes.Black;

        public SolidColorBrush ChipBorder { get; init; } = Brushes.Gray;
    }

    public sealed class UIComparisonResult
    {
        internal static readonly SolidColorBrush MatchBrush = CreateFrozenBrush("#10B981");
        internal static readonly SolidColorBrush VolumeWarnBrush = CreateFrozenBrush("#D97706");
        internal static readonly SolidColorBrush DiffBrush = CreateFrozenBrush("#DC2626");

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
        public string VolumeDisplay { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ErrorDetails { get; set; } = string.Empty;
        public int DifferingColumnsCount { get; set; }
        public string DifferingColumnsSummary { get; set; } = "-";
        public List<string> DifferingColumns { get; set; } = new List<string>();
        public List<CellDifferenceItem> ActiveDifferences { get; set; } = new List<CellDifferenceItem>();
        public int ActiveCellDifferencesCount { get; set; }
        public int IgnoredDifferencesCount { get; set; }
        public bool IsMatch { get; set; }

        public string StatusIcon => IsMatch ? "✔ OK" : "✖ KO";

        public string StatusBadgeText => Status switch
        {
            "OK" => "✓ IDENTIQUE",
            "OK_EMPTY" => "✓ VIDE (0/0)",
            "KO_ROW_COUNT" => "⚠️ ÉCART VOLUME",
            "KO_MISSING_DATA" => "🔴 MANQUANTE",
            _ => "🔴 ÉCART MÉTIER"
        };

        public SolidColorBrush StatusColor =>
            IsMatch
                ? MatchBrush
                : string.Equals(Status, "KO_ROW_COUNT", StringComparison.OrdinalIgnoreCase)
                    ? VolumeWarnBrush
                    : DiffBrush;

        public string DiffColsBadge =>
            IsMatch
                ? "0"
                : DifferingColumnsCount > 0
                    ? $"{DifferingColumnsCount} col."
                    : "Vol.";

        public string IgnoredBadgeText =>
            IgnoredDifferencesCount > 0 ? $"🛡️ {IgnoredDifferencesCount}" : "-";
    }

    public partial class ComparisonView : UserControl
    {
        private static readonly SolidColorBrush YellowStrokeBrush = CreateFrozenBrush("#F59E0B");
        private static readonly SolidColorBrush OrangeTitleBrush = CreateFrozenBrush("#D97706");
        private static readonly SolidColorBrush ActiveKoBtnBg = CreateFrozenBrush("#DC2626");
        private static readonly SolidColorBrush ActiveAllBtnBg = CreateFrozenBrush("#0F172A");
        private static readonly SolidColorBrush ActiveOkBtnBg = CreateFrozenBrush("#059669");
        private static readonly SolidColorBrush ActiveSpecificBtnBg = CreateFrozenBrush("#D97706");
        private static readonly SolidColorBrush ActiveGlobalBtnBg = CreateFrozenBrush("#2563EB");
        private static readonly SolidColorBrush ActiveAuditBtnBg = CreateFrozenBrush("#475569");
        private static readonly SolidColorBrush InactiveBtnBg = CreateFrozenBrush("#FFFFFF");
        private static readonly SolidColorBrush InactiveBorderBrush = CreateFrozenBrush("#CBD5E1");
        private static readonly SolidColorBrush DarkTextBrush = CreateFrozenBrush("#334155");
        private static readonly SolidColorBrush ChipSelectedBg = CreateFrozenBrush("#1E3A8A");
        private static readonly SolidColorBrush ChipUnselectedBg = CreateFrozenBrush("#EFF6FF");
        private static readonly SolidColorBrush ChipUnselectedFg = CreateFrozenBrush("#1D4ED8");
        private static readonly SolidColorBrush ChipUnselectedBorder = CreateFrozenBrush("#BFDBFE");

        private readonly OperationProgressController _progressController;

        private List<UIComparisonResult> _currentResults = new List<UIComparisonResult>();
        private List<IgnoredDifferenceItem> _currentIgnoredDiffs = new List<IgnoredDifferenceItem>();
        private ICollectionView? _resultsCollectionView;
        private ICollectionView? _ignoredCollectionView;

        private string _quickExactTableFilter = string.Empty;
        private string _selectedInspectorColumnFilter = "ALL";
        private string _ignoredCategoryFilter = "ALL";
        private bool _suppressStatusComboSync;

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public ComparisonView()
        {
            InitializeComponent();

            _progressController = new OperationProgressController(
                Dispatcher,
                ProgressComparison,
                TxtCompProgressDetail,
                TxtCompProcessedCount,
                TxtCompPlannedCount,
                TxtCompElapsedTime,
                TxtCompProgressPercentage);

            _progressController.Reset("Prêt — sélectionnez SOURCE et TARGET");
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
                var files = Directory.GetFiles(Settings.BaselineDir, "*.csv")
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Cast<string>()
                    .ToList();
                files.Insert(0, "-- Sélectionner une Baseline --");

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
                    TxtTargetFile.Text = string.Empty;
                    MessageBox.Show(
                        $"Aucune extraction récente trouvée pour {selectedFileName} dans l'environnement {envLetter}000.\nVeuillez lancer une extraction ou sélectionner manuellement le fichier TARGET.",
                        "Aucune extraction correspondante",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                CheckEnableRunButton();
            }
            else if (CmbBaselineSelector.SelectedIndex == 0)
            {
                TxtBaseFile.Text = string.Empty;
                TxtTargetFile.Text = string.Empty;
                CheckEnableRunButton();
            }
        }

        private static string? FindLatestMatchingExtraction(string baselineName, string envLetter)
        {
            string[] channels = { "C01", "C02", "C03", "C05" };
            string? foundChannel = channels.FirstOrDefault(c => baselineName.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0);

            if (foundChannel != null && Directory.Exists(Settings.OutputDir))
            {
                string searchPattern = $"*{foundChannel}*_{envLetter}_*.csv";

                var files = Directory.GetFiles(Settings.OutputDir, searchPattern)
                    .Where(f => !Path.GetFileName(f).StartsWith("Converted_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ComparisonGrid_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("IgnoredDiffs_", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (files.Count > 0)
                {
                    return files.OrderByDescending(File.GetLastWriteTime).First();
                }
            }
            return null;
        }

        private void BtnOpenBaseFile_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.TryOpenFileDirect(TxtBaseFile?.Text, "Ouverture SOURCE (Baseline)");
        }

        private void BtnOpenTargetFile_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.TryOpenFileDirect(TxtTargetFile?.Text, "Ouverture TARGET (Extrait)");
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
                    MessageBox.Show("Le dossier Output n'existe pas encore. Lancez d'abord une extraction.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string? latestExtractionFile = Directory.GetFiles(Settings.OutputDir, "*.csv")
                    .Where(f => !Path.GetFileName(f).StartsWith("Converted_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ExtractionHistory_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ActivationHistory_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ComparisonGrid_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("IgnoredDiffs_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (latestExtractionFile != null)
                {
                    Directory.CreateDirectory(Settings.BaselineDir);

                    string fileName = Path.GetFileName(latestExtractionFile);
                    string destPath = Path.Combine(Settings.BaselineDir, fileName);

                    File.Copy(latestExtractionFile, destPath, true);
                    MessageBox.Show($"La dernière extraction ({fileName}) a été copiée dans les Baselines !", "Baseline ajoutée", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadBaselines();
                }
                else
                {
                    MessageBox.Show("Aucun fichier d'extraction CSV trouvé dans le dossier Output.", "Aucun fichier", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la copie de la Baseline : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenBaselineFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.BaselineDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetFullPath(Settings.BaselineDir),
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossible d'ouvrir le dossier Baselines : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnBrowseBase_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "CSV Files|*.csv",
                Title = "Sélectionner le fichier SOURCE (Baseline CSV)",
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
                Title = "Sélectionner le fichier TARGET (Extrait CSV)",
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
            if (BtnRunComparison == null) return;
            bool hasFiles = !string.IsNullOrWhiteSpace(TxtBaseFile?.Text) &&
                            !string.IsNullOrWhiteSpace(TxtTargetFile?.Text);
            bool running = Window.GetWindow(this) is MainWindow mw && mw.IsProcessRunning;
            BtnRunComparison.IsEnabled = hasFiles && !running;
        }

        private void SetComparisonButtons(bool running)
        {
            if (BtnCancelComparison != null)
            {
                BtnCancelComparison.IsEnabled = running;
            }
            if (BtnRunComparison != null)
            {
                bool hasFiles = !string.IsNullOrWhiteSpace(TxtBaseFile?.Text) &&
                                !string.IsNullOrWhiteSpace(TxtTargetFile?.Text);
                BtnRunComparison.IsEnabled = !running && hasFiles;
            }
        }

        private void BtnCancelComparison_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.CancelCurrentProcess();
            }
        }

        // =============================================================
        // EXECUTION ASYNCHRONE AVEC PROGRESSION DYNAMIQUE & STOP
        // =============================================================

        private void ChkIgnoreTechColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count > 0 && BtnRunComparison?.IsEnabled == true)
            {
                BtnRunComparisonAction_Click(sender, e);
            }
        }

        private async void BtnRunComparisonAction_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is not MainWindow mainWindow) return;

            string baseFile = TxtBaseFile.Text.Trim();
            string targetFile = TxtTargetFile.Text.Trim();
            bool ignoreTechCols = ChkIgnoreTechColumns?.IsChecked == true;

            if (string.IsNullOrEmpty(baseFile) || string.IsNullOrEmpty(targetFile)) return;

            SetComparisonButtons(running: true);

            PanelDashboard.Visibility = Visibility.Collapsed;
            TabComparisonMain.Visibility = Visibility.Collapsed;
            PanelWaitingCard.Visibility = Visibility.Visible;
            TxtComparisonWaiting.Foreground = Brushes.SlateGray;
            TxtComparisonWaiting.Text = "Analyse matricielle SOURCE vs TARGET en cours...\nComparaison cellule par cellule et audit des règles d'exclusion.";

            _progressController.Start("Initialisation de la comparaison SOURCE vs TARGET...", totalUnits: 0, showEta: true);
            mainWindow.TxtStatus.Text = "Comparaison table par table en cours...";

            await mainWindow.RunProcessAsync(async token =>
            {
                try
                {
                    var orchestrator = new ComparisonOrchestrator();
                    ComparisonReport report = await orchestrator.RunFullComparisonAsync(
                            baseFile,
                            targetFile,
                            ignoreTechCols,
                            progress => _progressController.Report(progress.CurrentStep, progress.TotalSteps, progress.Message),
                            token)
                        .ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    string reportContent = GenerateReportText(report);
                    string[] fileIds = orchestrator.GetFileIds(baseFile);
                    string uniqueId = fileIds.Length >= 3 ? fileIds[2] : "Snapshot";
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

                    Directory.CreateDirectory(Settings.OutputDir);
                    string reportFilePath = Path.Combine(Settings.OutputDir, $"ComparisonReport_{uniqueId}_{timestamp}.txt");

                    await File.WriteAllTextAsync(reportFilePath, reportContent, new UTF8Encoding(true), token)
                        .ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.LastGeneratedPath = reportFilePath;
                        mainWindow.TxtStatus.Text =
                            $"Comparaison terminée — Conformité : {report.GlobalSuccessPercentage:F2}% " +
                            $"({report.TotalTablesWithDifferences} table(s) KO, {report.IgnoredDifferences.Count} diff. ignorée(s) à 0% d'impact).";
                        _progressController.Complete(
                            $"Terminé — Conformité : {report.GlobalSuccessPercentage:F2}% ({report.ComparedTestsCount} contrat(s) analysé(s))",
                            warning: report.TotalTablesWithDifferences > 0);
                        UpdateDashboardUI(report);
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _progressController.Cancel("Comparaison annulée par l'utilisateur.");
                        TxtComparisonWaiting.Text = "Comparaison arrêtée à la demande de l'utilisateur.";
                        TxtComparisonWaiting.Foreground = Brushes.DarkOrange;
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _progressController.Fail($"Échec : {ex.Message}");
                        TxtComparisonWaiting.Text = $"ERREUR DE COMPARAISON :\n{ex.Message}";
                        TxtComparisonWaiting.Foreground = Brushes.Crimson;
                    });
                    throw;
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() => SetComparisonButtons(running: false));
                }
            });
        }

        // =============================================================
        // RENDU DU TABLEAU DE BORD & DES DEUX ONGLETS
        // =============================================================

        private void UpdateDashboardUI(ComparisonReport report)
        {
            PanelWaitingCard.Visibility = Visibility.Collapsed;
            PanelDashboard.Visibility = Visibility.Visible;
            TabComparisonMain.Visibility = Visibility.Visible;

            int matchedTables = report.FileResults.Count(r => r.IsMatch);
            int totalTables = report.FileResults.Count;
            int koTables = totalTables - matchedTables;

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

            TxtComparedTests.Text = report.ComparedTestsCount.ToString("N0", CultureInfo.InvariantCulture);
            TxtContractsSummary.Text = $"{contractsOk} OK • {contractsKo} avec écart(s)";
            TxtTablesMatched.Text = $"{matchedTables} / {totalTables}";
            TxtSystemBreakdown.Text = $"LISA: {lisaOk}/{lisaTotal} | ELIA: {eliaOk}/{eliaTotal}";
            TxtKoTablesCount.Text = $"{koTables} table(s)";
            TxtDiffColsTotal.Text = $"{report.TotalDifferingColumnsDistinct} col. • {report.TotalActiveCellDifferences:N0} cell.";
            TxtBranchBreakdown.Text = $"B21: {br21Ok}/{br21Total} | B23: {br23Ok}/{br23Total}";
            TxtTotalRows.Text = report.TotalRowsCompared.ToString("N0", CultureInfo.InvariantCulture);
            TxtTotalErrors.Text = $"{report.TotalDifferencesFound:N0} ligne(s) KO";
            TxtIgnoredTotalCount.Text = report.IgnoredDifferences.Count.ToString("N0", CultureInfo.InvariantCulture);
            TxtIgnoredBreakdown.Text = $"{report.IgnoredSpecificTableCount} spécifique(s) table → Voir";
            TxtScorePercentage.Text = $"{report.GlobalSuccessPercentage:F2}%";

            TxtTabActiveHeader.Text = koTables > 0
                ? $"📊 Écarts Métier & Tables SQL ({koTables} KO / {totalTables})"
                : $"📊 Écarts Métier & Tables SQL (✔ {totalTables}/{totalTables} OK)";
            TxtTabIgnoredHeader.Text = $"🛡️ Différences ignorées ({report.IgnoredDifferences.Count:N0})";

            string testSummary = string.Join(" | ", report.TestMetrics.Take(8).Select(t => $"{t.Key}: {t.Value.SuccessPercentage}%"));
            if (report.TestMetrics.Count > 8)
            {
                testSummary += $" (+{report.TestMetrics.Count - 8})";
            }

            string productSummary = string.Join(" | ", report.ProductMetrics.Select(p => $"{p.Key}: {p.Value.SuccessPercentage}%"));
            string scoresLine = $" • Tests : {testSummary}" + (report.ProductMetrics.Count > 0 ? $"  |  Produits : {productSummary}" : string.Empty);

            double radius = 57.5;
            double circumference = 2 * Math.PI * radius;
            double dashLength = (report.GlobalSuccessPercentage / 100.0) * circumference;
            CircleProgress.StrokeDashArray = new DoubleCollection { dashLength / 11.0, circumference };

            if (report.GlobalSuccessPercentage >= 100.0 && koTables == 0)
            {
                CircleProgress.Stroke = UIComparisonResult.MatchBrush;
                TxtDashboardTitle.Text = "🎉 Conformité Métier Parfaite (100%)" + scoresLine;
                TxtDashboardTitle.Foreground = UIComparisonResult.MatchBrush;
            }
            else if (report.GlobalSuccessPercentage >= 95.0)
            {
                CircleProgress.Stroke = YellowStrokeBrush;
                TxtDashboardTitle.Text = $"⚠️ Écarts ciblés détectés ({koTables} table(s) en écart)" + scoresLine;
                TxtDashboardTitle.Foreground = OrangeTitleBrush;
            }
            else
            {
                CircleProgress.Stroke = UIComparisonResult.DiffBrush;
                TxtDashboardTitle.Text = $"🔴 Écarts significatifs détectés ({koTables} table(s) en écart)" + scoresLine;
                TxtDashboardTitle.Foreground = UIComparisonResult.DiffBrush;
            }

            // Construction des résultats de l'onglet 1
            var uiResults = new List<UIComparisonResult>(report.FileResults.Count);
            foreach (FileComparisonResult r in report.FileResults)
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
                    VolumeDisplay = r.VolumeDisplay,
                    Status = r.Status,
                    IsMatch = r.IsMatch,
                    DifferingColumns = r.DifferingColumns,
                    DifferingColumnsCount = r.DifferingColumnsCount,
                    DifferingColumnsSummary = string.IsNullOrWhiteSpace(r.DifferingColumnsSummary)
                        ? (r.IsMatch ? "✓ Identique" : "-")
                        : r.DifferingColumnsSummary,
                    ActiveDifferences = r.ActiveDifferences,
                    ActiveCellDifferencesCount = r.ActiveCellDifferencesCount,
                    IgnoredDifferencesCount = r.IgnoredDifferencesCount,
                    ErrorDetails = string.IsNullOrWhiteSpace(r.ErrorDetails)
                        ? "✓ Aucune différence métier active détectée entre SOURCE et TARGET."
                        : r.ErrorDetails.Trim()
                });
            }

            _currentResults = uiResults
                .OrderBy(x => x.IsMatch)
                .ThenByDescending(x => x.DifferingColumnsCount)
                .ThenBy(x => x.CleanTableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TestId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            BuildHotTablePills(_currentResults);
            PopulateContractFilterComboBoxes(report.ComparedTestsCount);

            _quickExactTableFilter = string.Empty;
            _resultsCollectionView = CollectionViewSource.GetDefaultView(_currentResults);
            if (_resultsCollectionView != null)
            {
                _resultsCollectionView.Filter = FilterComparisonPredicate;
            }
            GridResults.ItemsSource = _resultsCollectionView ?? (System.Collections.IEnumerable)_currentResults;

            // Sélection automatique intelligente : KO uniquement s'il existe des écarts, sinon Toutes
            ApplyQuickStatusFilter(koTables > 0 ? "KO" : "ALL");

            // Construction de l'onglet 2 : Différences ignorées (Spécifiques Table affichées en premier !)
            _currentIgnoredDiffs = report.IgnoredDifferences
                .OrderBy(d => d.Category == ExclusionRuleCategory.SpecificTable ? 0 : d.Category == ExclusionRuleCategory.Global ? 1 : 2)
                .ThenBy(d => d.CleanTableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ColumnName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.TestId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.RowNumber)
                .ToList();

            BtnIgnoredCatAll.Content = $"📋 Toutes ({report.IgnoredDifferences.Count:N0})";
            BtnIgnoredCatSpecific.Content = $"⚠️ Spécifiques Table ({report.IgnoredSpecificTableCount:N0})";
            BtnIgnoredCatGlobal.Content = $"🌐 Globales ({report.IgnoredGlobalCount:N0})";
            BtnIgnoredCatAudit.Content = $"⏱️ Audit / Timestamps ({report.IgnoredTechnicalAuditCount:N0})";

            _ignoredCollectionView = CollectionViewSource.GetDefaultView(_currentIgnoredDiffs);
            if (_ignoredCollectionView != null)
            {
                _ignoredCollectionView.Filter = FilterIgnoredPredicate;
            }
            GridIgnoredDiffs.ItemsSource = _ignoredCollectionView ?? (System.Collections.IEnumerable)_currentIgnoredDiffs;
            ApplyIgnoredCategoryFilter("ALL");

            TabComparisonMain.SelectedIndex = 0;
        }

        private void BuildHotTablePills(IReadOnlyList<UIComparisonResult> results)
        {
            var koGroups = results
                .Where(r => !r.IsMatch)
                .GroupBy(r => r.CleanTableName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (koGroups.Count == 0)
            {
                PanelHotTablesStrip.Visibility = Visibility.Collapsed;
                ItemsHotTablePills.ItemsSource = null;
                return;
            }

            var pills = new List<HotTableSummaryPill>(koGroups.Count);
            foreach (var grp in koGroups)
            {
                var distinctCols = grp
                    .SelectMany(x => x.DifferingColumns)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string colsPreview = distinctCols.Count == 0
                    ? "écart volume/présence"
                    : distinctCols.Count <= 3
                        ? string.Join(", ", distinctCols)
                        : $"{string.Join(", ", distinctCols.Take(3))} +{distinctCols.Count - 3}";

                pills.Add(new HotTableSummaryPill
                {
                    CleanTableName = grp.Key,
                    BadgeSummary = $"{grp.Count()}× • {colsPreview}",
                    TooltipText = $"Cliquer pour filtrer sur {grp.Key}\nContrats en écart : {grp.Count()}\nColonnes impactées : {(distinctCols.Count > 0 ? string.Join(", ", distinctCols) : "Volume de lignes")}"
                });
            }

            ItemsHotTablePills.ItemsSource = pills;
            PanelHotTablesStrip.Visibility = Visibility.Visible;
        }

        private void PopulateContractFilterComboBoxes(int comparedTestsCount)
        {
            CmbFilterCompContract.SelectionChanged -= FilterComparison_Changed;
            CmbFilterIgnoredContract.SelectionChanged -= FilterIgnored_Changed;

            CmbFilterCompContract.Items.Clear();
            CmbFilterIgnoredContract.Items.Clear();

            CmbFilterCompContract.Items.Add(new ComboBoxItem { Content = $"Tous les Contrats ({comparedTestsCount})", Tag = "ALL" });
            CmbFilterIgnoredContract.Items.Add(new ComboBoxItem { Content = $"Tous les Contrats ({comparedTestsCount})", Tag = "ALL" });

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
                CmbFilterIgnoredContract.Items.Add(new ComboBoxItem
                {
                    Content = $"{contractNum} [{grp.Key}]",
                    Tag = grp.Key
                });
            }

            CmbFilterCompContract.SelectedIndex = 0;
            CmbFilterIgnoredContract.SelectedIndex = 0;

            CmbFilterCompContract.SelectionChanged += FilterComparison_Changed;
            CmbFilterIgnoredContract.SelectionChanged += FilterIgnored_Changed;
        }

        // =============================================================
        // FILTRAGE ONGLET 1 (TABLES & INSPECTEUR CÔTE À CÔTE)
        // =============================================================

        private void CardActiveDiffs_Click(object sender, MouseButtonEventArgs e)
        {
            TabComparisonMain.SelectedIndex = 0;
            ApplyQuickStatusFilter("KO");
        }

        private void CardIgnoredDiffs_Click(object sender, MouseButtonEventArgs e)
        {
            TabComparisonMain.SelectedIndex = 1;
        }

        private void BtnHotTablePill_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tableName)
            {
                _quickExactTableFilter = tableName;
                ApplyQuickStatusFilter("KO");
            }
        }

        private void BtnResetTableQuickFilter_Click(object sender, RoutedEventArgs e)
        {
            _quickExactTableFilter = string.Empty;
            RefreshActiveGridAndCounters();
        }

        private void BtnQuickFilterKo_Click(object sender, RoutedEventArgs e) => ApplyQuickStatusFilter("KO");
        private void BtnQuickFilterAll_Click(object sender, RoutedEventArgs e) => ApplyQuickStatusFilter("ALL");
        private void BtnQuickFilterOk_Click(object sender, RoutedEventArgs e) => ApplyQuickStatusFilter("OK");

        private void ApplyQuickStatusFilter(string statusTag)
        {
            _suppressStatusComboSync = true;
            try
            {
                foreach (object entry in CmbFilterCompStatus.Items)
                {
                    if (entry is ComboBoxItem item &&
                        string.Equals(item.Tag?.ToString(), statusTag, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbFilterCompStatus.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _suppressStatusComboSync = false;
            }

            UpdateQuickStatusButtonVisuals(statusTag);
            RefreshActiveGridAndCounters();
        }

        private void CmbFilterCompStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressStatusComboSync) return;
            string tag = (CmbFilterCompStatus?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
            UpdateQuickStatusButtonVisuals(tag);
            RefreshActiveGridAndCounters();
        }

        private void UpdateQuickStatusButtonVisuals(string activeTag)
        {
            if (BtnQuickFilterKo == null || BtnQuickFilterAll == null || BtnQuickFilterOk == null) return;

            bool isKo = string.Equals(activeTag, "KO", StringComparison.OrdinalIgnoreCase);
            bool isAll = string.Equals(activeTag, "ALL", StringComparison.OrdinalIgnoreCase);
            bool isOk = string.Equals(activeTag, "OK", StringComparison.OrdinalIgnoreCase);

            BtnQuickFilterKo.Background = isKo ? ActiveKoBtnBg : InactiveBtnBg;
            BtnQuickFilterKo.Foreground = isKo ? Brushes.White : UIComparisonResult.DiffBrush;
            BtnQuickFilterKo.BorderBrush = isKo ? ActiveKoBtnBg : InactiveBorderBrush;

            BtnQuickFilterAll.Background = isAll ? ActiveAllBtnBg : InactiveBtnBg;
            BtnQuickFilterAll.Foreground = isAll ? Brushes.White : DarkTextBrush;
            BtnQuickFilterAll.BorderBrush = isAll ? ActiveAllBtnBg : InactiveBorderBrush;

            BtnQuickFilterOk.Background = isOk ? ActiveOkBtnBg : InactiveBtnBg;
            BtnQuickFilterOk.Foreground = isOk ? Brushes.White : ActiveOkBtnBg;
            BtnQuickFilterOk.BorderBrush = isOk ? ActiveOkBtnBg : InactiveBorderBrush;
        }

        private void FilterComparison_Changed(object sender, EventArgs e)
        {
            RefreshActiveGridAndCounters();
        }

        private void RefreshActiveGridAndCounters()
        {
            if (_resultsCollectionView == null) return;
            _resultsCollectionView.Refresh();

            int visibleCount = _resultsCollectionView.Cast<object>().Count();
            string tableNote = string.IsNullOrWhiteSpace(_quickExactTableFilter)
                ? string.Empty
                : $" [Filtre table : {_quickExactTableFilter}]";
            if (TxtFilteredTablesCount != null)
            {
                TxtFilteredTablesCount.Text = $"{visibleCount} / {_currentResults.Count} table(s) affichée(s){tableNote}";
            }

            if (GridResults.SelectedItem == null && visibleCount > 0)
            {
                GridResults.SelectedIndex = 0;
            }
        }

        private bool FilterComparisonPredicate(object obj)
        {
            if (obj is not UIComparisonResult item) return false;

            if (!string.IsNullOrWhiteSpace(_quickExactTableFilter) &&
                !string.Equals(item.CleanTableName, _quickExactTableFilter, StringComparison.OrdinalIgnoreCase))
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
                    return false;
                if (string.Equals(statusTag, "OK", StringComparison.OrdinalIgnoreCase) && !item.IsMatch)
                    return false;
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
            if (string.IsNullOrWhiteSpace(query)) return true;

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

        private void GridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridResults.SelectedItem is not UIComparisonResult selected)
            {
                return;
            }

            _selectedInspectorColumnFilter = "ALL";
            RenderSelectedTableSideBySideInspector(selected);
        }

        private void RenderSelectedTableSideBySideInspector(UIComparisonResult selected)
        {
            TxtInlineDiffHeader.Text =
                $"🔎 Comparaison Côte à Côte — {selected.CleanTableName} ({selected.FileType})  |  Contrat : {selected.ContractNumber} [{selected.TestId}]  |  {selected.StatusBadgeText}";
            TxtInlineDiffHeader.Foreground = selected.IsMatch
                ? ActiveOkBtnBg
                : UIComparisonResult.DiffBrush;

            TxtInlineDiffSubHeader.Text =
                $"Excel Row: {selected.ExcelRowDisplay}  •  Branche: {selected.BranchDisplay} (Fonds: {selected.FundsSummary})  •  " +
                $"Produit/Process: {selected.ProductCode}/{selected.ProcessCode}  •  Canal: {selected.BusinessChannel} ({selected.BucpCmdpmtDisplay})  •  " +
                $"Volume (Src➔Tgt): {selected.VolumeDisplay}  •  Lignes OK/Total: {selected.RowsSummary}  •  Diff. ignorées: {selected.IgnoredDifferencesCount}";

            if (selected.IgnoredDifferencesCount > 0)
            {
                BtnJumpToTableIgnored.Content = $"🛡️ Voir les {selected.IgnoredDifferencesCount} diff. ignorée(s) de {selected.CleanTableName}";
                BtnJumpToTableIgnored.Visibility = Visibility.Visible;
            }
            else
            {
                BtnJumpToTableIgnored.Visibility = Visibility.Collapsed;
            }

            if (selected.IsMatch || selected.ActiveDifferences.Count == 0)
            {
                ItemsDiffColumnChips.Visibility = Visibility.Collapsed;
                GridCellDiffs.Visibility = Visibility.Collapsed;
                PanelSelectedTableMatchBanner.Visibility = Visibility.Visible;
                TxtSelectedTableMatchMessage.Text = selected.IsMatch
                    ? $"✓ Toutes les colonnes métier actives de {selected.CleanTableName} sont strictement identiques entre SOURCE (Baseline) et TARGET ({selected.VolumeDisplay} lignes)."
                    : selected.ErrorDetails;
                return;
            }

            PanelSelectedTableMatchBanner.Visibility = Visibility.Collapsed;
            GridCellDiffs.Visibility = Visibility.Visible;

            // Puces de filtrage par colonne SQL
            var colGroups = selected.ActiveDifferences
                .GroupBy(d => d.ColumnName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chips = new List<DiffColumnFilterChip>(colGroups.Count + 1)
            {
                BuildColumnChip("ALL", $"Toutes les colonnes ({selected.ActiveDifferences.Count})", _selectedInspectorColumnFilter == "ALL")
            };

            foreach (var grp in colGroups)
            {
                bool isSel = string.Equals(_selectedInspectorColumnFilter, grp.Key, StringComparison.OrdinalIgnoreCase);
                chips.Add(BuildColumnChip(grp.Key, $"{grp.Key} ({grp.Count()})", isSel));
            }

            ItemsDiffColumnChips.ItemsSource = chips;
            ItemsDiffColumnChips.Visibility = Visibility.Visible;

            IEnumerable<CellDifferenceItem> filteredCells = string.Equals(_selectedInspectorColumnFilter, "ALL", StringComparison.OrdinalIgnoreCase)
                ? selected.ActiveDifferences
                : selected.ActiveDifferences.Where(d => string.Equals(d.ColumnName, _selectedInspectorColumnFilter, StringComparison.OrdinalIgnoreCase));

            GridCellDiffs.ItemsSource = filteredCells.ToList();
        }

        private static DiffColumnFilterChip BuildColumnChip(string key, string label, bool selected)
        {
            return new DiffColumnFilterChip
            {
                ColumnFilterKey = key,
                DisplayLabel = label,
                ChipBackground = selected ? ChipSelectedBg : ChipUnselectedBg,
                ChipForeground = selected ? Brushes.White : ChipUnselectedFg,
                ChipBorder = selected ? ChipSelectedBg : ChipUnselectedBorder
            };
        }

        private void BtnDiffColumnChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string colKey && GridResults.SelectedItem is UIComparisonResult selected)
            {
                _selectedInspectorColumnFilter = colKey;
                RenderSelectedTableSideBySideInspector(selected);
            }
        }

        private void BtnJumpToTableIgnored_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem is not UIComparisonResult selected) return;

            TabComparisonMain.SelectedIndex = 1;
            ApplyIgnoredCategoryFilter("ALL");
            TxtFilterIgnored.Text = selected.CleanTableName;

            foreach (object entry in CmbFilterIgnoredContract.Items)
            {
                if (entry is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), selected.TestId, StringComparison.OrdinalIgnoreCase))
                {
                    CmbFilterIgnoredContract.SelectedItem = item;
                    break;
                }
            }
        }

        // =============================================================
        // FILTRAGE & EXPORT ONGLET 2 (DIFFÉRENCES IGNORÉES)
        // =============================================================

        private void BtnIgnoredCatAll_Click(object sender, RoutedEventArgs e) => ApplyIgnoredCategoryFilter("ALL");
        private void BtnIgnoredCatSpecific_Click(object sender, RoutedEventArgs e) => ApplyIgnoredCategoryFilter("SPECIFIC");
        private void BtnIgnoredCatGlobal_Click(object sender, RoutedEventArgs e) => ApplyIgnoredCategoryFilter("GLOBAL");
        private void BtnIgnoredCatAudit_Click(object sender, RoutedEventArgs e) => ApplyIgnoredCategoryFilter("AUDIT");

        private void ApplyIgnoredCategoryFilter(string categoryKey)
        {
            _ignoredCategoryFilter = categoryKey;

            bool isAll = categoryKey == "ALL";
            bool isSpec = categoryKey == "SPECIFIC";
            bool isGlob = categoryKey == "GLOBAL";
            bool isAud = categoryKey == "AUDIT";

            BtnIgnoredCatAll.Background = isAll ? ActiveAllBtnBg : InactiveBtnBg;
            BtnIgnoredCatAll.Foreground = isAll ? Brushes.White : DarkTextBrush;
            BtnIgnoredCatAll.BorderBrush = isAll ? ActiveAllBtnBg : InactiveBorderBrush;

            BtnIgnoredCatSpecific.Background = isSpec ? ActiveSpecificBtnBg : InactiveBtnBg;
            BtnIgnoredCatSpecific.Foreground = isSpec ? Brushes.White : ActiveSpecificBtnBg;
            BtnIgnoredCatSpecific.BorderBrush = isSpec ? ActiveSpecificBtnBg : InactiveBorderBrush;

            BtnIgnoredCatGlobal.Background = isGlob ? ActiveGlobalBtnBg : InactiveBtnBg;
            BtnIgnoredCatGlobal.Foreground = isGlob ? Brushes.White : ActiveGlobalBtnBg;
            BtnIgnoredCatGlobal.BorderBrush = isGlob ? ActiveGlobalBtnBg : InactiveBorderBrush;

            BtnIgnoredCatAudit.Background = isAud ? ActiveAuditBtnBg : InactiveBtnBg;
            BtnIgnoredCatAudit.Foreground = isAud ? Brushes.White : ActiveAuditBtnBg;
            BtnIgnoredCatAudit.BorderBrush = isAud ? ActiveAuditBtnBg : InactiveBorderBrush;

            RefreshIgnoredGridAndCounters();
        }

        private void FilterIgnored_Changed(object sender, EventArgs e)
        {
            RefreshIgnoredGridAndCounters();
        }

        private void RefreshIgnoredGridAndCounters()
        {
            if (_ignoredCollectionView == null) return;
            _ignoredCollectionView.Refresh();

            int visible = _ignoredCollectionView.Cast<object>().Count();
            if (TxtFilteredIgnoredCount != null)
            {
                TxtFilteredIgnoredCount.Text = $"{visible:N0} / {_currentIgnoredDiffs.Count:N0} différence(s) ignorée(s) affichée(s) (0% impact score)";
            }
        }

        private bool FilterIgnoredPredicate(object obj)
        {
            if (obj is not IgnoredDifferenceItem item) return false;

            switch (_ignoredCategoryFilter)
            {
                case "SPECIFIC":
                    if (item.Category != ExclusionRuleCategory.SpecificTable) return false;
                    break;
                case "GLOBAL":
                    if (item.Category != ExclusionRuleCategory.Global) return false;
                    break;
                case "AUDIT":
                    if (item.Category != ExclusionRuleCategory.TechnicalAudit) return false;
                    break;
            }

            if (CmbFilterIgnoredContract?.SelectedItem is ComboBoxItem contractItem)
            {
                string tag = contractItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(tag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.TestId, tag, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.ContractNumber, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (CmbFilterIgnoredSystem?.SelectedItem is ComboBoxItem sysItem)
            {
                string sysTag = sysItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(sysTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.SystemType, sysTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string q = TxtFilterIgnored?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(q)) return true;

            return (item.CleanTableName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ColumnName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TestId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.RowContextKey?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.SourceDisplay?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TargetDisplay?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.CategoryBadge?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void BtnExportIgnoredCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIgnoredDiffs.Count == 0)
            {
                MessageBox.Show("Aucune différence ignorée à exporter.", "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(Settings.OutputDir);
                string exportPath = Path.Combine(
                    Settings.OutputDir,
                    $"IgnoredDiffs_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("ExclusionCategory;TestId;ExcelRow;ContractNumber;System;Branche;Product;SqlTable;IgnoredColumn;RowNumber;RowContextKey;SourceValue;TargetValue");

                IEnumerable<IgnoredDifferenceItem> rowsToExport = _ignoredCollectionView != null
                    ? _ignoredCollectionView.Cast<IgnoredDifferenceItem>()
                    : _currentIgnoredDiffs;

                int exportedCount = 0;
                foreach (IgnoredDifferenceItem row in rowsToExport)
                {
                    exportedCount++;
                    sb.AppendLine(string.Join(";",
                        EscapeCsv(row.CategoryBadge),
                        EscapeCsv(row.TestId),
                        EscapeCsv(row.ExcelRowDisplay),
                        EscapeCsv(row.ContractNumber),
                        EscapeCsv(row.SystemType),
                        EscapeCsv(row.BranchDisplay),
                        EscapeCsv(row.ProductCode),
                        EscapeCsv(row.CleanTableName),
                        EscapeCsv(row.ColumnName),
                        row.RowNumber.ToString(CultureInfo.InvariantCulture),
                        EscapeCsv(row.RowContextKey),
                        EscapeCsv(row.SourceValue),
                        EscapeCsv(row.TargetValue)));
                }

                File.WriteAllText(exportPath, sb.ToString(), new UTF8Encoding(true));

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.LastGeneratedPath = exportPath;
                    mainWindow.TxtStatus.Text = $"Différences ignorées exportées en CSV ({exportedCount} lignes) : {Path.GetFileName(exportPath)}";
                    mainWindow.TxtStatus.Foreground = Brushes.ForestGreen;
                }

                MessageBox.Show(
                    $"Différences ignorées exportées avec succès ({exportedCount} lignes) :\n\n{exportPath}",
                    "Export CSV Terminé",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de l'export des différences ignorées : {ex.Message}", "Erreur d'export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =============================================================
        // EXPORT CSV ONGLET 1, RAPPORT TEXTE & NAVIGATION
        // =============================================================

        private void BtnExportComparisonCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count == 0)
            {
                MessageBox.Show("Veuillez d'abord lancer une comparaison avant d'exporter.", "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(Settings.OutputDir);
                string exportPath = Path.Combine(
                    Settings.OutputDir,
                    $"ComparisonGrid_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("Status;ExcelRow;TestId;ContractNumber;System;Branche;Product;Process;BusinessChannel;BucpCmdpmt;Funds;SqlTable;VolumeSrcTgt;RowsOkTotal;DifferingColumns;IgnoredDiffsCount;DiscrepancyDetails");

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
                        EscapeCsv(row.VolumeDisplay),
                        EscapeCsv(row.RowsSummary),
                        EscapeCsv(row.DifferingColumnsSummary),
                        row.IgnoredDifferencesCount.ToString(CultureInfo.InvariantCulture),
                        EscapeCsv(row.ErrorDetails)));
                }

                File.WriteAllText(exportPath, sb.ToString(), new UTF8Encoding(true));

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.LastGeneratedPath = exportPath;
                    mainWindow.TxtStatus.Text = $"Table de comparaison exportée en CSV : {Path.GetFileName(exportPath)}";
                    mainWindow.TxtStatus.Foreground = Brushes.ForestGreen;
                }

                MessageBox.Show(
                    $"Résultats de comparaison exportés avec succès ({_currentResults.Count} lignes) :\n\n{exportPath}",
                    "Export CSV Terminé",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Échec de l'export CSV : {ex.Message}", "Erreur d'export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string clean = value.Replace("\r", " ").Replace("\n", " | ").Trim();
            if (clean.Contains(';') || clean.Contains('"'))
            {
                return $"\"{clean.Replace("\"", "\"\"")}\"";
            }

            return clean;
        }

        private static string GenerateReportText(ComparisonReport report)
        {
            var sb = new StringBuilder(16384);

            sb.AppendLine("====================================================================================");
            sb.AppendLine("                 AUTO-ACTIVATOR — DEEP NON-REGRESSION COMPARISON REPORT              ");
            sb.AppendLine("====================================================================================");
            sb.AppendLine($"Tests in SOURCE (Baseline) : {report.TotalBaseTests}");
            sb.AppendLine($"Tests in TARGET (Extract)  : {report.TotalTargetTests}");
            sb.AppendLine($"Tests Compared             : {report.ComparedTestsCount}");
            sb.AppendLine($"Tables Compared            : {report.TotalTablesCompared} (KO Tables: {report.TotalTablesWithDifferences})");
            sb.AppendLine($"Ignored Differences        : {report.IgnoredDifferences.Count} (Specific Table Rules: {report.IgnoredSpecificTableCount}) [0% SCORE IMPACT]");

            if (report.MissingInTarget.Count > 0 || report.MissingInBase.Count > 0)
            {
                sb.AppendLine("\n[DISCREPANCIES - NON BLOCKING INFO]");
                if (report.MissingInTarget.Count > 0)
                    sb.AppendLine($"- Tests found ONLY in SOURCE (ignored): {string.Join(", ", report.MissingInTarget)}");
                if (report.MissingInBase.Count > 0)
                    sb.AppendLine($"- Tests found ONLY in TARGET (ignored): {string.Join(", ", report.MissingInBase)}");
            }

            sb.AppendLine("\n[PRODUCT SCORES]");
            if (report.ProductMetrics.Count > 0)
            {
                foreach (var kvp in report.ProductMetrics.OrderByDescending(x => x.Value.SuccessPercentage))
                {
                    sb.AppendLine($"- Product {kvp.Key,-18} : {kvp.Value.SuccessPercentage,6}%  ({kvp.Value.SuccessfulRows}/{kvp.Value.TotalRows} rows OK)");
                }
            }
            else
            {
                sb.AppendLine("- No products detected.");
            }

            sb.AppendLine("\n[TEST ID SCORES]");
            foreach (var kvp in report.TestMetrics.OrderBy(x => x.Key))
            {
                sb.AppendLine($"- Test {kvp.Key,-15} : {kvp.Value.SuccessPercentage,6}%  ({kvp.Value.SuccessfulRows}/{kvp.Value.TotalRows} rows OK)");
            }

            sb.AppendLine("------------------------------------------------------------------------------------");
            sb.AppendLine($"Global Compliance Rate : {report.GlobalSuccessPercentage:F2} %");
            sb.AppendLine($"Total Rows Analyzed    : {report.TotalRowsCompared}");
            sb.AppendLine($"Total Differing Rows   : {report.TotalDifferencesFound}");
            sb.AppendLine("====================================================================================\n");

            if (report.GlobalSuccessPercentage >= 100.0 && report.TotalTablesWithDifferences == 0)
            {
                sb.AppendLine("✅ PERFECT MATCH!");
                sb.AppendLine("All active business columns for the common tested contracts are strictly identical.");
            }
            else
            {
                foreach (FileComparisonResult result in report.FileResults.Where(r => !r.IsMatch))
                {
                    sb.AppendLine($"--- TABLE : {result.TableName} ({result.FileType} | Contract: {result.ContractNumber} | {result.BranchDisplay} | Product: {result.ProductCode} | Rows: {result.RowsSummary}) ---");
                    sb.AppendLine($"Status  : ❌ {result.Status}");
                    sb.AppendLine($"Columns : {result.DifferingColumnsSummary}");
                    sb.AppendLine("Details :");
                    sb.AppendLine(result.ErrorDetails);
                    sb.AppendLine();
                }
            }

            if (report.IgnoredDifferences.Count > 0)
            {
                sb.AppendLine("====================================================================================");
                sb.AppendLine($"[IGNORED DIFFERENCES AUDIT SUMMARY — {report.IgnoredDifferences.Count} TOTAL (0% IMPACT ON SCORE)]");
                sb.AppendLine("====================================================================================");
                foreach (IgnoredDifferenceItem ign in report.IgnoredDifferences.Take(250))
                {
                    sb.AppendLine(
                        $"  • [{ign.CategoryBadge}] {ign.ContractAndTestDisplay} | {ign.CleanTableName}.{ign.ColumnName} (Row #{ign.RowNumber}, {ign.RowContextKey}) : SOURCE='{ign.SourceDisplay}' -> TARGET='{ign.TargetDisplay}'");
                }
                if (report.IgnoredDifferences.Count > 250)
                {
                    sb.AppendLine($"  ... (+{report.IgnoredDifferences.Count - 250} additional ignored differences available in UI Tab 'Différences ignorées')");
                }
            }

            return sb.ToString();
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

        private void GridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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
            if (GridResults.SelectedItem is not UIComparisonResult selected) return;

            var sb = new StringBuilder();
            sb.AppendLine("====================================================================================");
            sb.AppendLine("                   SQL TABLE COMPARISON DIAGNOSTIC REPORT");
            sb.AppendLine("====================================================================================");
            sb.AppendLine("[0. SYNTHÈSE DU CONTRAT & ROUTAGE MÉTIER]");
            sb.AppendLine($"  • Contrat (3-7-2)      : {selected.ContractNumber}  |  Excel Row : {selected.ExcelRowDisplay}  |  Test ID : {selected.TestId}");
            sb.AppendLine($"  • Produit / Process    : {selected.ProductCode}  /  {selected.ProcessCode}  (ChannelCode ELIA : {selected.ChannelCode})");
            sb.AppendLine($"  • Canal & Paiement     : {selected.BusinessChannel}  ->  BUCP / CMDPMT = {selected.BucpCmdpmtDisplay}");
            sb.AppendLine($"  • Branche & Fonds      : {selected.BranchDisplay}  |  Fonds : {selected.FundsSummary}");
            sb.AppendLine($"  • Table SQL Comparée   : {selected.CleanTableName} ({selected.FileType})  |  Volume (Src➔Tgt) : {selected.VolumeDisplay}  |  Lignes OK/Total : {selected.RowsSummary}  |  Statut : {selected.Status}");
            sb.AppendLine($"  • Différences ignorées : {selected.IgnoredDifferencesCount} (0% impact sur le score)");
            sb.AppendLine("------------------------------------------------------------------------------------");
            sb.AppendLine("[1. DÉTAIL DES ÉCARTS MÉTIER CELLULE PAR CELLULE (SOURCE vs TARGET)]");
            sb.AppendLine(selected.ErrorDetails);

            var reportWindow = new DiagnosticReportWindow(
                title: $"Table Comparison — {selected.CleanTableName} ({selected.BranchDisplay})",
                subtitle: $"Excel Row: {selected.ExcelRowDisplay} | Test: {selected.TestId} | Contract: {selected.ContractNumber} | Volume: {selected.VolumeDisplay} | Rows: {selected.RowsSummary}",
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
                MessageBox.Show("Veuillez sélectionner une ligne possédant un numéro de contrat valide.", "Re-Extraire Contrat", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show("Veuillez sélectionner une ligne possédant un numéro de contrat valide.", "Activer Contrat", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    MessageBox.Show("Aucun dossier Output disponible pour le moment.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}
