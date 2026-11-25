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
    public sealed class BatchContractSummaryItem
    {
        public string TestKey { get; init; } = "ALL";
        public string DisplayName { get; init; } = string.Empty;
        public string ShortStatus { get; init; } = string.Empty;
        public string TooltipText { get; init; } = string.Empty;
        public bool HasErrors { get; init; }
        public int KoTablesCount { get; init; }
    }

    public sealed class UIComparisonResult
    {
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
        public int BaseRowCount { get; set; }
        public int TargetRowCount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ErrorDetails { get; set; } = string.Empty;
        public int DifferingColumnsCount { get; set; }
        public string DifferingColumnsSummary { get; set; } = "-";
        public List<string> DifferingColumns { get; set; } = new List<string>();
        public List<CellDifferenceItem> ActiveDifferences { get; set; } = new List<CellDifferenceItem>();
        public int ActiveCellDifferencesCount { get; set; }
        public int IgnoredDifferencesCount { get; set; }
        public bool IsMatch { get; set; }

        public string StatusIcon => IsMatch ? "OK" : "KO";

        public string StatusBadgeText => Status switch
        {
            "OK" => "Identique (OK)",
            "OK_EMPTY" => "Vide des deux côtés (0/0)",
            "KO_ROW_COUNT" => $"Écart de nombre de lignes ({BaseRowCount} -> {TargetRowCount})",
            "KO_MISSING_DATA" => BaseRowCount == 0
                ? $"Table absente dans SOURCE (0 -> {TargetRowCount})"
                : $"Table absente dans TARGET ({BaseRowCount} -> 0)",
            _ => $"{DifferingColumnsCount} colonne(s) en écart"
        };

        public string ShortTableStatus
        {
            get
            {
                if (IsMatch)
                {
                    return string.Equals(Status, "OK_EMPTY", StringComparison.OrdinalIgnoreCase)
                        ? "Vide"
                        : "OK";
                }

                if (string.Equals(Status, "KO_MISSING_DATA", StringComparison.OrdinalIgnoreCase))
                {
                    return BaseRowCount == 0 ? "Absent Src" : "Absent Tgt";
                }

                if (string.Equals(Status, "KO_ROW_COUNT", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Lignes {BaseRowCount}->{TargetRowCount}";
                }

                return DifferingColumnsCount > 0
                    ? $"{DifferingColumnsCount} col. KO"
                    : "Écart";
            }
        }

        public string LeftRowTooltip =>
            IsMatch
                ? $"{CleanTableName} ({FileType}) — Identique ({VolumeDisplay} lignes)"
                : $"{CleanTableName} ({FileType}) — Lignes : {VolumeDisplay}\n{DifferingColumnsSummary}";
    }

    public partial class ComparisonView : UserControl
    {
        private static readonly SolidColorBrush SlateDarkBrush = CreateFrozenBrush("#0F172A");
        private static readonly SolidColorBrush SlateMutedBrush = CreateFrozenBrush("#64748B");
        private static readonly SolidColorBrush DiffRedBrush = CreateFrozenBrush("#DC2626");

        private readonly OperationProgressController _progressController;
        private readonly ExtractionService _extractionService = new ExtractionService();

        private List<UIComparisonResult> _currentResults = new List<UIComparisonResult>();
        private List<IgnoredDifferenceItem> _currentIgnoredDiffs = new List<IgnoredDifferenceItem>();
        private ICollectionView? _resultsCollectionView;
        private ICollectionView? _ignoredCollectionView;

        private string _selectedBatchTestKey = "ALL";
        private string _ignoredCategoryFilter = "ALL";
        private bool _suppressStatusComboSync;
        private bool _suppressIgnoredCategorySync;
        private bool _isLeftPaneCollapsed;
        private bool _isMultiContractReport;
        private GridLength _savedLeftPaneWidth = new GridLength(225);
        private GridLength _savedContractsPaneWidth = new GridLength(220);

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

            UpdateModeVisibilityAndRunButton();
            _progressController.Reset("Prêt — saisissez un Contrat / Demand ID ou choisissez deux CSV");
        }

        private bool IsDirectSqlMode => RbModeDirectSql?.IsChecked == true;

        private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                LoadBaselines();
                CheckEnableRunButton();
            }
        }

        private void ComparisonMode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateModeVisibilityAndRunButton();
        }

        private void UpdateModeVisibilityAndRunButton()
        {
            if (PanelDirectSqlMode == null || PanelCsvFilesMode == null || BtnRunComparison == null)
            {
                return;
            }

            bool directSql = IsDirectSqlMode;
            PanelDirectSqlMode.Visibility = directSql ? Visibility.Visible : Visibility.Collapsed;
            PanelCsvFilesMode.Visibility = directSql ? Visibility.Collapsed : Visibility.Visible;
            BtnRunComparison.Content = directSql ? "Extraire & Comparer" : "Comparer";
            CheckEnableRunButton();
        }

        public void LoadLatestExtractionForComparison(string? targetCsvPath)
        {
            LoadBaselines();

            if (!string.IsNullOrWhiteSpace(targetCsvPath) && File.Exists(targetCsvPath))
            {
                if (RbModeCsvFiles != null)
                {
                    RbModeCsvFiles.IsChecked = true;
                }
                UpdateModeVisibilityAndRunButton();

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
            if (CmbBaselineSelector == null) return;

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

        // =============================================================
        // CONTRÔLES D'ENTRÉE (SQL DIRECT & FICHIERS CSV)
        // =============================================================

        private void DirectInput_TextChanged(object sender, TextChangedEventArgs e) => CheckEnableRunButton();

        private void DirectInput_SelectionChanged(object sender, SelectionChangedEventArgs e) => CheckEnableRunButton();

        private void DirectInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnRunComparison?.IsEnabled == true)
            {
                e.Handled = true;
                BtnRunComparisonAction_Click(sender, e);
            }
        }

        private void BtnSwapDirectInputs_Click(object sender, RoutedEventArgs e)
        {
            if (TxtDirectSourceInput == null || TxtDirectTargetInput == null ||
                CmbDirectSourceEnv == null || CmbDirectTargetEnv == null)
            {
                return;
            }

            string tmpText = TxtDirectSourceInput.Text;
            TxtDirectSourceInput.Text = TxtDirectTargetInput.Text;
            TxtDirectTargetInput.Text = tmpText;

            int tmpEnvIdx = CmbDirectSourceEnv.SelectedIndex;
            CmbDirectSourceEnv.SelectedIndex = CmbDirectTargetEnv.SelectedIndex;
            CmbDirectTargetEnv.SelectedIndex = tmpEnvIdx;

            CheckEnableRunButton();
        }

        private void BtnOpenBaseFile_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.TryOpenFileDirect(TxtBaseFile?.Text, "Snapshot CSV SOURCE");
        }

        private void BtnOpenTargetFile_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.TryOpenFileDirect(TxtTargetFile?.Text, "Snapshot CSV TARGET");
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
                    MessageBox.Show($"La dernière extraction ({fileName}) a été copiée dans les Baselines.", "Baseline ajoutée", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private bool CanRunCurrentMode()
        {
            if (IsDirectSqlMode)
            {
                string srcId = TxtDirectSourceInput?.Text?.Trim() ?? string.Empty;
                string tgtId = TxtDirectTargetInput?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(srcId) && string.IsNullOrWhiteSpace(tgtId))
                {
                    return false;
                }

                string srcEnv = (CmbDirectSourceEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q";
                string tgtEnv = (CmbDirectTargetEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q";

                if (!string.IsNullOrWhiteSpace(srcId) && !string.IsNullOrWhiteSpace(tgtId))
                {
                    return true;
                }

                return !string.Equals(srcEnv, tgtEnv, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(TxtBaseFile?.Text) &&
                   !string.IsNullOrWhiteSpace(TxtTargetFile?.Text);
        }

        private void CheckEnableRunButton()
        {
            if (BtnRunComparison == null) return;
            bool running = Window.GetWindow(this) is MainWindow mw && mw.IsProcessRunning;
            BtnRunComparison.IsEnabled = !running && CanRunCurrentMode();
        }

        private void SetComparisonButtons(bool running)
        {
            if (BtnCancelComparison != null)
            {
                BtnCancelComparison.IsEnabled = running;
            }
            if (BtnRunComparison != null)
            {
                BtnRunComparison.IsEnabled = !running && CanRunCurrentMode();
            }
        }

        private void BtnCancelComparison_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.CancelCurrentProcess();
            }
        }

        private void BtnToggleTablesPane_Click(object sender, RoutedEventArgs e)
        {
            if (ColLeftTablesPane == null || ColSplitterTablesPane == null || PanelLeftTablesList == null || BtnToggleTablesPane == null)
            {
                return;
            }

            if (!_isLeftPaneCollapsed)
            {
                if (ColLeftTablesPane.ActualWidth > 120)
                {
                    _savedLeftPaneWidth = new GridLength(ColLeftTablesPane.ActualWidth);
                }
                if (_isMultiContractReport && ColBatchContractsPane.ActualWidth > 120)
                {
                    _savedContractsPaneWidth = new GridLength(ColBatchContractsPane.ActualWidth);
                }

                _isLeftPaneCollapsed = true;
                PanelLeftTablesList.Visibility = Visibility.Collapsed;
                SplitterTablesPane.Visibility = Visibility.Collapsed;
                ColLeftTablesPane.MinWidth = 0;
                ColLeftTablesPane.Width = new GridLength(0);
                ColSplitterTablesPane.Width = new GridLength(0);

                if (_isMultiContractReport)
                {
                    PanelBatchContractsList.Visibility = Visibility.Collapsed;
                    SplitterContractsPane.Visibility = Visibility.Collapsed;
                    ColBatchContractsPane.MinWidth = 0;
                    ColBatchContractsPane.Width = new GridLength(0);
                    ColSplitterContractsPane.Width = new GridLength(0);
                }

                BtnToggleTablesPane.Content = "Afficher listes";
            }
            else
            {
                _isLeftPaneCollapsed = false;
                ApplyLeftPanesLayout(_isMultiContractReport);
                BtnToggleTablesPane.Content = "Pleine largeur";
            }
        }

        private void ApplyLeftPanesLayout(bool multiContracts)
        {
            PanelLeftTablesList.Visibility = Visibility.Visible;
            SplitterTablesPane.Visibility = Visibility.Visible;
            ColLeftTablesPane.MinWidth = 175;
            ColLeftTablesPane.Width = _savedLeftPaneWidth.Value >= 175 ? _savedLeftPaneWidth : new GridLength(225);
            ColSplitterTablesPane.Width = new GridLength(8);

            if (multiContracts)
            {
                PanelBatchContractsList.Visibility = Visibility.Visible;
                SplitterContractsPane.Visibility = Visibility.Visible;
                ColBatchContractsPane.MinWidth = 175;
                ColBatchContractsPane.Width = _savedContractsPaneWidth.Value >= 175 ? _savedContractsPaneWidth : new GridLength(220);
                ColSplitterContractsPane.Width = new GridLength(8);
            }
            else
            {
                PanelBatchContractsList.Visibility = Visibility.Collapsed;
                SplitterContractsPane.Visibility = Visibility.Collapsed;
                ColBatchContractsPane.MinWidth = 0;
                ColBatchContractsPane.Width = new GridLength(0);
                ColSplitterContractsPane.Width = new GridLength(0);
            }
        }

        // =============================================================
        // EXÉCUTION ASYNCHRONE : EXTRACTION SQL DIRECTE + COMPARAISON
        // =============================================================

        private void ChkIgnoreTechColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count > 0 &&
                !string.IsNullOrWhiteSpace(TxtBaseFile?.Text) &&
                !string.IsNullOrWhiteSpace(TxtTargetFile?.Text) &&
                File.Exists(TxtBaseFile.Text) &&
                File.Exists(TxtTargetFile.Text) &&
                !(Window.GetWindow(this) is MainWindow mw && mw.IsProcessRunning))
            {
                _ = ExecuteComparisonAsync(forceUseExistingCsvFiles: true);
            }
        }

        private async void BtnRunComparisonAction_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteComparisonAsync(forceUseExistingCsvFiles: false);
        }

        private async Task ExecuteComparisonAsync(bool forceUseExistingCsvFiles)
        {
            if (Window.GetWindow(this) is not MainWindow mainWindow) return;

            bool ignoreTechCols = ChkIgnoreTechColumns?.IsChecked == true;
            bool runDirectSql = IsDirectSqlMode && !forceUseExistingCsvFiles;

            string directSrcInput = TxtDirectSourceInput?.Text?.Trim() ?? string.Empty;
            string directTgtInput = TxtDirectTargetInput?.Text?.Trim() ?? string.Empty;
            string srcEnvLetter = (CmbDirectSourceEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q";
            string tgtEnvLetter = (CmbDirectTargetEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q";

            if (runDirectSql)
            {
                if (string.IsNullOrWhiteSpace(directSrcInput) && !string.IsNullOrWhiteSpace(directTgtInput) &&
                    !string.Equals(srcEnvLetter, tgtEnvLetter, StringComparison.OrdinalIgnoreCase))
                {
                    directSrcInput = directTgtInput;
                }
                else if (string.IsNullOrWhiteSpace(directTgtInput) && !string.IsNullOrWhiteSpace(directSrcInput) &&
                         !string.Equals(srcEnvLetter, tgtEnvLetter, StringComparison.OrdinalIgnoreCase))
                {
                    directTgtInput = directSrcInput;
                }

                if (string.IsNullOrWhiteSpace(directSrcInput) || string.IsNullOrWhiteSpace(directTgtInput))
                {
                    MessageBox.Show(
                        "Veuillez saisir le Contrat ou Demand ID SOURCE et le Contrat ou Demand ID TARGET (ou choisir deux environnements différents Q000 vs D000 pour comparer un même contrat).",
                        "Comparaison par Contrat / Demand ID",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }
            else
            {
                string b = TxtBaseFile?.Text?.Trim() ?? string.Empty;
                string t = TxtTargetFile?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(b) || string.IsNullOrEmpty(t))
                {
                    return;
                }
            }

            SetComparisonButtons(running: true);

            PanelDashboard.Visibility = Visibility.Collapsed;
            TabComparisonMain.Visibility = Visibility.Collapsed;
            PanelWaitingCard.Visibility = Visibility.Visible;
            TxtComparisonWaiting.Foreground = SlateMutedBrush;
            TxtComparisonWaiting.Text = runDirectSql
                ? $"Extraction SQL complète LISA + ELIA en cours pour SOURCE ({directSrcInput} en {srcEnvLetter}000) et TARGET ({directTgtInput} en {tgtEnvLetter}000)..."
                : "Comparaison SOURCE vs TARGET en cours...";

            _progressController.Start(
                runDirectSql
                    ? $"Extraction parallèle SOURCE ({srcEnvLetter}000) & TARGET ({tgtEnvLetter}000)..."
                    : "Initialisation de la comparaison SOURCE vs TARGET...",
                totalUnits: runDirectSql ? 12 : 0,
                showEta: true);

            mainWindow.TxtStatus.Text = runDirectSql
                ? $"Extraction SQL complète et comparaison : {directSrcInput} ({srcEnvLetter}000) vs {directTgtInput} ({tgtEnvLetter}000)..."
                : "Comparaison table par table en cours...";

            await mainWindow.RunProcessAsync(async token =>
            {
                try
                {
                    string baseFile;
                    string targetFile;

                    if (runDirectSql)
                    {
                        bool srcIsDemand = ExcelHelper.IsDemandIdInput(directSrcInput);
                        bool tgtIsDemand = ExcelHelper.IsDemandIdInput(directTgtInput);
                        int srcStep = 0;
                        int tgtStep = 0;

                        void ReportDualExtractionProgress(string latestDetail)
                        {
                            double completed = Math.Min(10.0, srcStep + tgtStep);
                            _progressController.Report(completed, 12.0, latestDetail);
                        }

                        Task<ExtractionResult> srcTask = _extractionService.PerformExtractionAsync(
                            directSrcInput,
                            $"{srcEnvLetter}000",
                            saveIndividualFile: true,
                            isDemandId: srcIsDemand,
                            token: token,
                            onStepProgress: (s, _, msg) =>
                            {
                                Interlocked.Exchange(ref srcStep, Math.Clamp(s, 0, 5));
                                ReportDualExtractionProgress($"[SOURCE {srcEnvLetter}000] {msg}");
                            },
                            fileTag: "SRC");

                        Task<ExtractionResult> tgtTask = _extractionService.PerformExtractionAsync(
                            directTgtInput,
                            $"{tgtEnvLetter}000",
                            saveIndividualFile: true,
                            isDemandId: tgtIsDemand,
                            token: token,
                            onStepProgress: (s, _, msg) =>
                            {
                                Interlocked.Exchange(ref tgtStep, Math.Clamp(s, 0, 5));
                                ReportDualExtractionProgress($"[TARGET {tgtEnvLetter}000] {msg}");
                            },
                            fileTag: "TGT");

                        ExtractionResult[] extracted = await Task.WhenAll(srcTask, tgtTask).ConfigureAwait(false);
                        ExtractionResult srcResult = extracted[0];
                        ExtractionResult tgtResult = extracted[1];

                        bool srcEmpty = srcResult.InternalId == "Not found" && srcResult.UconId == "Not found";
                        bool tgtEmpty = tgtResult.InternalId == "Not found" && tgtResult.UconId == "Not found";
                        if (srcEmpty || tgtEmpty)
                        {
                            string missingMsg = srcEmpty && tgtEmpty
                                ? $"SOURCE ({directSrcInput} en {srcEnvLetter}000) et TARGET ({directTgtInput} en {tgtEnvLetter}000) sont tous deux introuvables dans LISA et ELIA."
                                : srcEmpty
                                    ? $"SOURCE ({directSrcInput} en {srcEnvLetter}000) est introuvable dans LISA et ELIA."
                                    : $"TARGET ({directTgtInput} en {tgtEnvLetter}000) est introuvable dans LISA et ELIA.";
                            throw new InvalidOperationException(missingMsg);
                        }

                        baseFile = srcResult.FilePath;
                        targetFile = tgtResult.FilePath;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (TxtBaseFile != null) TxtBaseFile.Text = baseFile;
                            if (TxtTargetFile != null) TxtTargetFile.Text = targetFile;
                        });
                    }
                    else
                    {
                        baseFile = await Dispatcher.InvokeAsync(() => TxtBaseFile?.Text?.Trim() ?? string.Empty);
                        targetFile = await Dispatcher.InvokeAsync(() => TxtTargetFile?.Text?.Trim() ?? string.Empty);
                    }

                    token.ThrowIfCancellationRequested();

                    var orchestrator = new ComparisonOrchestrator();
                    ComparisonReport report = await orchestrator.RunFullComparisonAsync(
                            baseFile,
                            targetFile,
                            ignoreTechCols,
                            progress =>
                            {
                                if (runDirectSql)
                                {
                                    double mapped = 10.0 + (progress.TotalSteps > 0
                                        ? (double)progress.CurrentStep / progress.TotalSteps * 2.0
                                        : 1.0);
                                    _progressController.Report(mapped, 12.0, progress.Message);
                                }
                                else
                                {
                                    _progressController.Report(progress.CurrentStep, progress.TotalSteps, progress.Message);
                                }
                            },
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
                            $"Terminé — Conformité : {report.GlobalSuccessPercentage:F2}% ({report.TotalTablesWithDifferences} table(s) en écart)",
                            warning: report.TotalTablesWithDifferences > 0);
                        UpdateDashboardUI(report);
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _progressController.Cancel("Opération annulée par l'utilisateur.");
                        TxtComparisonWaiting.Text = "Comparaison arrêtée à la demande de l'utilisateur.";
                        TxtComparisonWaiting.Foreground = SlateMutedBrush;
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _progressController.Fail($"Échec : {ex.Message}");
                        TxtComparisonWaiting.Text = $"Erreur de comparaison :\n{ex.Message}";
                        TxtComparisonWaiting.Foreground = DiffRedBrush;
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
        // RENDU DE LA BARRE DE SYNTHÈSE SOBRE & DES DEUX ONGLETS
        // =============================================================

        private void UpdateDashboardUI(ComparisonReport report)
        {
            PanelWaitingCard.Visibility = Visibility.Collapsed;
            PanelDashboard.Visibility = Visibility.Visible;
            TabComparisonMain.Visibility = Visibility.Visible;

            int matchedTables = report.FileResults.Count(r => r.IsMatch);
            int totalTables = report.FileResults.Count;
            int koTables = totalTables - matchedTables;
            bool hasActiveDiffs = koTables > 0 || report.TotalDifferencesFound > 0;

            TxtScorePercentage.Text = $"{report.GlobalSuccessPercentage:F2}%";
            TxtScorePercentage.Foreground = hasActiveDiffs ? DiffRedBrush : SlateDarkBrush;

            TxtComparedTests.Text = report.ComparedTestsCount.ToString("N0", CultureInfo.InvariantCulture);
            TxtTablesMatched.Text = $"{matchedTables} / {totalTables}";

            TxtKoTablesCount.Text = koTables.ToString("N0", CultureInfo.InvariantCulture);
            TxtKoTablesCount.Foreground = koTables > 0 ? DiffRedBrush : SlateDarkBrush;

            TxtIgnoredSummary.Text = $"{report.IgnoredDifferences.Count:N0} (0% impact)";

            TxtTabActiveHeader.Text = koTables > 0
                ? $"Écarts métier ({koTables} KO / {totalTables})"
                : $"Écarts métier ({totalTables}/{totalTables} OK)";
            TxtTabIgnoredHeader.Text = $"Différences ignorées ({report.IgnoredDifferences.Count:N0})";

            _isMultiContractReport = report.ComparedTestsCount > 1;
            _isLeftPaneCollapsed = false;
            BtnToggleTablesPane.Content = "Pleine largeur";
            ApplyLeftPanesLayout(_isMultiContractReport);

            CmbFilterIgnoredContract.Visibility = _isMultiContractReport ? Visibility.Visible : Visibility.Collapsed;
            ColIgnoredContract.Visibility = _isMultiContractReport ? Visibility.Visible : Visibility.Collapsed;

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
                    BaseRowCount = r.BaseRowCount,
                    TargetRowCount = r.TargetRowCount,
                    Status = r.Status,
                    IsMatch = r.IsMatch,
                    DifferingColumns = r.DifferingColumns,
                    DifferingColumnsCount = r.DifferingColumnsCount,
                    DifferingColumnsSummary = string.IsNullOrWhiteSpace(r.DifferingColumnsSummary)
                        ? (r.IsMatch ? "Identique" : "-")
                        : r.DifferingColumnsSummary,
                    ActiveDifferences = r.ActiveDifferences,
                    ActiveCellDifferencesCount = r.ActiveCellDifferencesCount,
                    IgnoredDifferencesCount = r.IgnoredDifferencesCount,
                    ErrorDetails = string.IsNullOrWhiteSpace(r.ErrorDetails)
                        ? "Aucune différence métier active détectée entre SOURCE et TARGET."
                        : r.ErrorDetails.Trim()
                });
            }

            // Tri logique pour le testeur : vraies erreurs de colonnes métier en premier, puis écarts de volume, puis tables absentes, puis OK
            _currentResults = uiResults
                .OrderBy(x => x.IsMatch)
                .ThenBy(x => string.Equals(x.Status, "KO_MISSING_DATA", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(x => x.DifferingColumnsCount)
                .ThenBy(x => x.CleanTableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TestId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PopulateBatchContractsListAndCombo(report.ComparedTestsCount);
            UpdateIgnoredCategoryComboLabels(report);

            _resultsCollectionView = CollectionViewSource.GetDefaultView(_currentResults);
            if (_resultsCollectionView != null)
            {
                _resultsCollectionView.Filter = FilterComparisonPredicate;
            }
            GridResults.ItemsSource = _resultsCollectionView ?? (System.Collections.IEnumerable)_currentResults;

            ApplyQuickStatusFilter(koTables > 0 ? "KO" : "ALL");

            _currentIgnoredDiffs = report.IgnoredDifferences
                .OrderBy(d => d.Category == ExclusionRuleCategory.SpecificTable ? 0 : d.Category == ExclusionRuleCategory.Global ? 1 : 2)
                .ThenBy(d => d.CleanTableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ColumnName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.TestId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.RowNumber)
                .ToList();

            _ignoredCollectionView = CollectionViewSource.GetDefaultView(_currentIgnoredDiffs);
            if (_ignoredCollectionView != null)
            {
                _ignoredCollectionView.Filter = FilterIgnoredPredicate;
            }
            GridIgnoredDiffs.ItemsSource = _ignoredCollectionView ?? (System.Collections.IEnumerable)_currentIgnoredDiffs;
            ApplyIgnoredCategoryFilter("ALL");

            TabComparisonMain.SelectedIndex = 0;
        }

        private void PopulateBatchContractsListAndCombo(int comparedTestsCount)
        {
            CmbFilterIgnoredContract.SelectionChanged -= FilterIgnored_Changed;
            CmbFilterIgnoredContract.Items.Clear();
            CmbFilterIgnoredContract.Items.Add(new ComboBoxItem { Content = $"Tous les contrats ({comparedTestsCount})", Tag = "ALL" });

            var contractItems = new List<BatchContractSummaryItem>();
            var grouped = _currentResults
                .GroupBy(x => x.TestId, StringComparer.OrdinalIgnoreCase)
                .Select(grp =>
                {
                    var first = grp.First();
                    int koCnt = grp.Count(x => !x.IsMatch);
                    string rowPrefix = first.ExcelRowDisplay != "-" ? $"{first.ExcelRowDisplay} " : string.Empty;
                    string label = !string.IsNullOrWhiteSpace(first.ContractNumber) && first.ContractNumber != "-" &&
                                   !string.Equals(first.ContractNumber, grp.Key, StringComparison.OrdinalIgnoreCase)
                        ? $"{rowPrefix}{grp.Key} ({first.ContractNumber})"
                        : $"{rowPrefix}{grp.Key}";

                    return new BatchContractSummaryItem
                    {
                        TestKey = grp.Key,
                        DisplayName = label,
                        ShortStatus = koCnt > 0 ? $"{koCnt} KO" : "OK",
                        HasErrors = koCnt > 0,
                        KoTablesCount = koCnt,
                        TooltipText = $"Test : {grp.Key}\nContrat : {first.ContractNumber}\nLigne Excel : {first.ExcelRowDisplay}\nBranche : {first.BranchDisplay} ({first.ProductCode}/{first.ProcessCode})\nTables KO : {koCnt} / {grp.Count()}"
                    };
                })
                .OrderByDescending(c => c.HasErrors)
                .ThenByDescending(c => c.KoTablesCount)
                .ThenBy(c => c.TestKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (BatchContractSummaryItem item in grouped)
            {
                contractItems.Add(item);
                CmbFilterIgnoredContract.Items.Add(new ComboBoxItem
                {
                    Content = $"{item.DisplayName} ({item.ShortStatus})",
                    Tag = item.TestKey
                });
            }

            CmbFilterIgnoredContract.SelectedIndex = 0;
            CmbFilterIgnoredContract.SelectionChanged += FilterIgnored_Changed;

            if (_isMultiContractReport)
            {
                int koContracts = grouped.Count(c => c.HasErrors);
                TxtBatchContractsTitle.Text = koContracts > 0
                    ? $"1. Contrats ({koContracts} KO / {grouped.Count})"
                    : $"1. Contrats ({grouped.Count} OK)";

                GridBatchContracts.SelectionChanged -= GridBatchContracts_SelectionChanged;
                GridBatchContracts.ItemsSource = contractItems;
                if (contractItems.Count > 0)
                {
                    _selectedBatchTestKey = contractItems[0].TestKey;
                    GridBatchContracts.SelectedIndex = 0;
                }
                else
                {
                    _selectedBatchTestKey = "ALL";
                }
                GridBatchContracts.SelectionChanged += GridBatchContracts_SelectionChanged;
            }
            else
            {
                _selectedBatchTestKey = "ALL";
                GridBatchContracts.ItemsSource = null;
            }
        }

        private void GridBatchContracts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridBatchContracts.SelectedItem is BatchContractSummaryItem selectedContract)
            {
                _selectedBatchTestKey = selectedContract.TestKey;
                RefreshActiveGridAndCounters();
            }
        }

        private void UpdateIgnoredCategoryComboLabels(ComparisonReport report)
        {
            if (CmbFilterIgnoredCategory == null) return;

            _suppressIgnoredCategorySync = true;
            try
            {
                foreach (object entry in CmbFilterIgnoredCategory.Items)
                {
                    if (entry is not ComboBoxItem item) continue;
                    string tag = item.Tag?.ToString() ?? "ALL";
                    item.Content = tag switch
                    {
                        "SPECIFIC" => $"Spécifiques table ({report.IgnoredSpecificTableCount:N0})",
                        "GLOBAL" => $"Exclusions globales ({report.IgnoredGlobalCount:N0})",
                        "AUDIT" => $"Audit / Timestamps ({report.IgnoredTechnicalAuditCount:N0})",
                        _ => $"Toutes les règles ({report.IgnoredDifferences.Count:N0})"
                    };
                }
            }
            finally
            {
                _suppressIgnoredCategorySync = false;
            }
        }

        // =============================================================
        // FILTRAGE ONGLET 1 & EXPLICATION CLAIRE POUR LE TESTEUR
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

            RefreshActiveGridAndCounters();
        }

        private void CmbFilterCompStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressStatusComboSync) return;
            RefreshActiveGridAndCounters();
        }

        private void FilterComparison_Changed(object sender, EventArgs e)
        {
            RefreshActiveGridAndCounters();
        }

        private void RefreshActiveGridAndCounters()
        {
            if (_resultsCollectionView == null) return;
            _resultsCollectionView.Refresh();

            var visibleItems = _resultsCollectionView.Cast<UIComparisonResult>().ToList();
            int visibleCount = visibleItems.Count;

            int scopeTotal = _isMultiContractReport && !string.Equals(_selectedBatchTestKey, "ALL", StringComparison.OrdinalIgnoreCase)
                ? _currentResults.Count(x => string.Equals(x.TestId, _selectedBatchTestKey, StringComparison.OrdinalIgnoreCase))
                : _currentResults.Count;

            if (TxtFilteredTablesCount != null)
            {
                string stepPrefix = _isMultiContractReport ? "2. Tables : " : "Tables affichées : ";
                TxtFilteredTablesCount.Text = $"{stepPrefix}{visibleCount} / {scopeTotal}";
            }

            UpdateContractQuickDiagnosisBanner();

            if (visibleCount == 0)
            {
                GridCellDiffs.ItemsSource = null;
                GridCellDiffs.Visibility = Visibility.Collapsed;
                PanelSelectedTableMatchBanner.Visibility = Visibility.Visible;
                TxtInlineDiffHeader.Text = "Aucune table en écart sur cette sélection";
                TxtInlineDiffHeader.Foreground = SlateDarkBrush;
                TxtInlineDiffSubHeader.Text = "-";
                TxtSelectedTableMatchMessage.Text = "Ce contrat est 100% conforme ou aucune table ne correspond au filtre choisi.";
                BtnJumpToTableIgnored.Visibility = Visibility.Collapsed;
                return;
            }

            if (GridResults.SelectedItem is not UIComparisonResult currentSelected ||
                !visibleItems.Contains(currentSelected))
            {
                GridResults.SelectedItem = visibleItems[0];
            }
            else
            {
                RenderSelectedTableSideBySideInspector(currentSelected);
            }
        }

        private void UpdateContractQuickDiagnosisBanner()
        {
            if (PanelContractQuickDiagnosis == null || TxtContractQuickDiagnosis == null) return;

            List<UIComparisonResult> contractTables = _isMultiContractReport && !string.Equals(_selectedBatchTestKey, "ALL", StringComparison.OrdinalIgnoreCase)
                ? _currentResults.Where(r => string.Equals(r.TestId, _selectedBatchTestKey, StringComparison.OrdinalIgnoreCase)).ToList()
                : _currentResults;

            var lisaTables = contractTables
                .Where(r => string.Equals(r.FileType, "LISA", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lisaTables.Count >= 5)
            {
                int missingInTarget = lisaTables.Count(r => r.BaseRowCount > 0 && r.TargetRowCount == 0);
                int missingInSource = lisaTables.Count(r => r.BaseRowCount == 0 && r.TargetRowCount > 0);
                int anyNonEmptyTarget = lisaTables.Count(r => r.TargetRowCount > 0);
                int anyNonEmptySource = lisaTables.Count(r => r.BaseRowCount > 0);

                if (missingInTarget >= 5 && anyNonEmptyTarget == 0)
                {
                    TxtContractQuickDiagnosis.Text =
                        $"Diagnostic rapide : le contrat est actif dans LISA côté SOURCE, mais absent de LISA côté TARGET ({missingInTarget} tables LISA à 0 ligne côté TARGET — contrat non activé sur TARGET).";
                    PanelContractQuickDiagnosis.Visibility = Visibility.Visible;
                    return;
                }

                if (missingInSource >= 5 && anyNonEmptySource == 0)
                {
                    TxtContractQuickDiagnosis.Text =
                        $"Diagnostic rapide : le contrat n'est pas activé dans LISA côté SOURCE (0 ligne sur {missingInSource} tables LISA), alors qu'il est actif côté TARGET.";
                    PanelContractQuickDiagnosis.Visibility = Visibility.Visible;
                    return;
                }
            }

            PanelContractQuickDiagnosis.Visibility = Visibility.Collapsed;
        }

        private bool FilterComparisonPredicate(object obj)
        {
            if (obj is not UIComparisonResult item) return false;

            if (_isMultiContractReport &&
                !string.Equals(_selectedBatchTestKey, "ALL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.TestId, _selectedBatchTestKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (CmbFilterCompStatus?.SelectedItem is ComboBoxItem statusItem)
            {
                string statusTag = statusItem.Tag?.ToString() ?? "ALL";
                if (string.Equals(statusTag, "KO", StringComparison.OrdinalIgnoreCase) && item.IsMatch)
                    return false;
                if (string.Equals(statusTag, "OK", StringComparison.OrdinalIgnoreCase) && !item.IsMatch)
                    return false;
            }

            string query = TxtFilterComparison?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query)) return true;

            return (item.CleanTableName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TableName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.FileType?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TestId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.DifferingColumnsSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void GridResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridResults.SelectedItem is not UIComparisonResult selected)
            {
                return;
            }

            RenderSelectedTableSideBySideInspector(selected);
        }

        private void RenderSelectedTableSideBySideInspector(UIComparisonResult selected)
        {
            TxtInlineDiffHeader.Text = $"{selected.CleanTableName} ({selected.FileType}) — {selected.StatusBadgeText}";
            TxtInlineDiffHeader.Foreground = selected.IsMatch ? SlateDarkBrush : DiffRedBrush;

            string contractLabel = string.Equals(selected.TestId, "SINGLE_CONTRACT", StringComparison.OrdinalIgnoreCase)
                ? $"Contrat : {selected.ContractNumber}"
                : $"Test : {selected.TestId} ({selected.ContractNumber})";

            string subHeader =
                $"{contractLabel}  |  {selected.BranchDisplay} ({selected.ProductCode}/{selected.ProcessCode})  |  " +
                $"Lignes (Source -> Target) : {selected.VolumeDisplay}";
            if (selected.IgnoredDifferencesCount > 0)
            {
                subHeader += $"  |  Colonnes exclues différentes : {selected.IgnoredDifferencesCount}";
            }
            TxtInlineDiffSubHeader.Text = subHeader;

            if (selected.IgnoredDifferencesCount > 0)
            {
                BtnJumpToTableIgnored.Content = $"Ignorées ({selected.IgnoredDifferencesCount})";
                BtnJumpToTableIgnored.Visibility = Visibility.Visible;
            }
            else
            {
                BtnJumpToTableIgnored.Visibility = Visibility.Collapsed;
            }

            // Cas 1 : Table absente d'un des deux côtés (0 -> N ou N -> 0) -> Explication claire en français sans mélanger de colonnes dans un tableau !
            if (string.Equals(selected.Status, "KO_MISSING_DATA", StringComparison.OrdinalIgnoreCase))
            {
                GridCellDiffs.ItemsSource = null;
                GridCellDiffs.Visibility = Visibility.Collapsed;
                PanelSelectedTableMatchBanner.Visibility = Visibility.Visible;

                TxtSelectedTableMatchMessage.Text = selected.BaseRowCount == 0
                    ? $"Cette table ({selected.CleanTableName}) est vide côté SOURCE (0 ligne), mais contient {selected.TargetRowCount} ligne(s) côté TARGET.\n\n" +
                      "Cause habituelle : le contrat de référence (SOURCE) n'était pas activé sur cette table, alors que le contrat TARGET l'a alimentée.\n" +
                      "(Cliquez sur 'Rapport brut' en haut à droite si vous souhaitez consulter les valeurs détaillées de la ligne.)"
                    : $"Cette table ({selected.CleanTableName}) contient {selected.BaseRowCount} ligne(s) côté SOURCE, mais est vide (0 ligne) côté TARGET.\n\n" +
                      "Cause habituelle : l'activation ou l'alimentation de cette table n'a pas eu lieu sur le contrat TARGET.\n" +
                      "(Cliquez sur 'Rapport brut' en haut à droite si vous souhaitez consulter les valeurs attendues de SOURCE.)";
                return;
            }

            // Cas 2 : Table identique
            if (selected.IsMatch || selected.ActiveDifferences.Count == 0)
            {
                GridCellDiffs.ItemsSource = null;
                GridCellDiffs.Visibility = Visibility.Collapsed;
                PanelSelectedTableMatchBanner.Visibility = Visibility.Visible;
                TxtSelectedTableMatchMessage.Text = selected.IsMatch
                    ? $"Toutes les colonnes métier de {selected.CleanTableName} sont strictement identiques entre SOURCE et TARGET ({selected.VolumeDisplay} lignes)."
                    : selected.ErrorDetails;
                return;
            }

            // Cas 3 : Écarts colonne par colonne -> 1 ligne = 1 colonne SQL exacte (Attendu vs Obtenu)
            List<CellDifferenceItem> columnOnlyDiffs = selected.ActiveDifferences
                .Where(d => d.DiffKind == "VALUE_DIFF")
                .ToList();

            if (columnOnlyDiffs.Count == 0)
            {
                // Écart pur de nombre de lignes (ex: 2 lignes vs 3 lignes avec les 2 premières identiques)
                GridCellDiffs.ItemsSource = null;
                GridCellDiffs.Visibility = Visibility.Collapsed;
                PanelSelectedTableMatchBanner.Visibility = Visibility.Visible;
                TxtSelectedTableMatchMessage.Text =
                    $"Les lignes communes de {selected.CleanTableName} sont identiques, mais le nombre de lignes diffère : " +
                    $"{selected.BaseRowCount} ligne(s) dans SOURCE contre {selected.TargetRowCount} ligne(s) dans TARGET.\n\n" +
                    "(Cliquez sur 'Rapport brut' pour voir le détail des lignes supplémentaires ou manquantes.)";
                return;
            }

            PanelSelectedTableMatchBanner.Visibility = Visibility.Collapsed;
            GridCellDiffs.Visibility = Visibility.Visible;
            GridCellDiffs.ItemsSource = columnOnlyDiffs;
        }

        private void GridCellDiffs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InspectSelectedComparisonRow();
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

        private void CmbFilterIgnoredCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressIgnoredCategorySync) return;
            string categoryKey = (CmbFilterIgnoredCategory?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
            _ignoredCategoryFilter = categoryKey;
            RefreshIgnoredGridAndCounters();
        }

        private void ApplyIgnoredCategoryFilter(string categoryKey)
        {
            _ignoredCategoryFilter = categoryKey;
            if (CmbFilterIgnoredCategory != null)
            {
                _suppressIgnoredCategorySync = true;
                try
                {
                    foreach (object entry in CmbFilterIgnoredCategory.Items)
                    {
                        if (entry is ComboBoxItem item &&
                            string.Equals(item.Tag?.ToString(), categoryKey, StringComparison.OrdinalIgnoreCase))
                        {
                            CmbFilterIgnoredCategory.SelectedItem = item;
                            break;
                        }
                    }
                }
                finally
                {
                    _suppressIgnoredCategorySync = false;
                }
            }

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
                TxtFilteredIgnoredCount.Text = $"{visible:N0} / {_currentIgnoredDiffs.Count:N0} affichée(s) (0% impact)";
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

            string q = TxtFilterIgnored?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(q)) return true;

            return (item.CleanTableName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ColumnName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractNumber?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.TestId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.SystemType?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
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
                sb.AppendLine("[OK] PERFECT MATCH");
                sb.AppendLine("All active business columns for the common tested contracts are strictly identical.");
            }
            else
            {
                foreach (FileComparisonResult result in report.FileResults.Where(r => !r.IsMatch))
                {
                    sb.AppendLine($"--- TABLE : {result.TableName} ({result.FileType} | Contract: {result.ContractNumber} | {result.BranchDisplay} | Product: {result.ProductCode} | Rows: {result.RowsSummary}) ---");
                    sb.AppendLine($"Status  : [KO] {result.Status}");
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
                        $"  - [{ign.CategoryBadge}] {ign.ContractAndTestDisplay} | {ign.CleanTableName}.{ign.ColumnName} (Row #{ign.RowNumber}, {ign.RowContextKey}) : SOURCE='{ign.SourceDisplay}' -> TARGET='{ign.TargetDisplay}'");
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
            sb.AppendLine($"  - Contrat(s)           : {selected.ContractNumber}  |  Excel Row : {selected.ExcelRowDisplay}  |  Test ID : {selected.TestId}");
            sb.AppendLine($"  - Produit / Process    : {selected.ProductCode}  /  {selected.ProcessCode}  (ChannelCode ELIA : {selected.ChannelCode})");
            sb.AppendLine($"  - Canal & Paiement     : {selected.BusinessChannel}  ->  BUCP / CMDPMT = {selected.BucpCmdpmtDisplay}");
            sb.AppendLine($"  - Branche & Fonds      : {selected.BranchDisplay}  |  Fonds : {selected.FundsSummary}");
            sb.AppendLine($"  - Table SQL Comparée   : {selected.CleanTableName} ({selected.FileType})  |  Volume (Src -> Tgt) : {selected.VolumeDisplay}  |  Lignes OK/Total : {selected.RowsSummary}  |  Statut : {selected.Status}");
            sb.AppendLine($"  - Différences ignorées : {selected.IgnoredDifferencesCount} (0% impact sur le score)");
            sb.AppendLine("------------------------------------------------------------------------------------");
            sb.AppendLine("[1. DÉTAIL DES ÉCARTS MÉTIER CELLULE PAR CELLULE (SOURCE vs TARGET)]");
            sb.AppendLine(selected.ErrorDetails);

            var reportWindow = new DiagnosticReportWindow(
                title: $"Table Comparison — {selected.CleanTableName} ({selected.BranchDisplay})",
                subtitle: $"Contract: {selected.ContractNumber} | Volume: {selected.VolumeDisplay} | Rows: {selected.RowsSummary}",
                status: selected.IsMatch ? "SUCCESS" : "FAILED",
                reportContent: sb.ToString(),
                contractReference: selected.ContractNumber != "-" ? selected.ContractNumber : selected.TableName)
            {
                Owner = Window.GetWindow(this)
            };
            reportWindow.ShowDialog();
        }

        private static string ExtractFirstSingleContract(string? rawContractDisplay)
        {
            if (string.IsNullOrWhiteSpace(rawContractDisplay) || rawContractDisplay == "-")
            {
                return string.Empty;
            }

            int arrowIdx = rawContractDisplay.IndexOf("->", StringComparison.Ordinal);
            string candidate = arrowIdx >= 0
                ? rawContractDisplay.Substring(arrowIdx + 2).Trim()
                : rawContractDisplay.Trim();

            if (candidate.StartsWith("Demand ", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(7).Trim();
            }

            return candidate;
        }

        private void MenuCompSendToExtraction_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem == null && _currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }

            string targetContract = ExtractFirstSingleContract((GridResults.SelectedItem as UIComparisonResult)?.ContractNumber);
            if (string.IsNullOrWhiteSpace(targetContract))
            {
                MessageBox.Show("Veuillez sélectionner une ligne possédant un numéro de contrat ou Demand ID valide.", "Re-Extraire Contrat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string envLetter = IsDirectSqlMode
                ? ((CmbDirectTargetEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q")
                : ((CmbSmartEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q");
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToExtractionWithContract(targetContract, ExcelHelper.IsDemandIdInput(targetContract), envLetter);
            }
        }

        private void MenuCompSendToActivation_Click(object sender, RoutedEventArgs e)
        {
            if (GridResults.SelectedItem == null && _currentResults.Count > 0)
            {
                GridResults.SelectedIndex = 0;
            }

            string targetContract = ExtractFirstSingleContract((GridResults.SelectedItem as UIComparisonResult)?.ContractNumber);
            if (string.IsNullOrWhiteSpace(targetContract))
            {
                MessageBox.Show("Veuillez sélectionner une ligne possédant un numéro de contrat ou Demand ID valide.", "Activer Contrat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string envLetter = IsDirectSqlMode
                ? ((CmbDirectTargetEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q")
                : ((CmbSmartEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Q");
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToActivationWithContract(targetContract, ExcelHelper.IsDemandIdInput(targetContract), envLetter);
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
    }
}
