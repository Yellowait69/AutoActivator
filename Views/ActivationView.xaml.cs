using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;
using AutoActivator.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

namespace AutoActivator.Gui.Views
{
    public partial class ActivationView : UserControl
    {
        private readonly ActivationDataService _activationDataService;
        private readonly IActivationRoutingService _activationRoutingService;
        private CancellationTokenSource? _cts;
        private ICollectionView? _historyCollectionView;

        public ObservableCollection<ActivationHistoryItem> ActivationHistory { get; } =
            new ObservableCollection<ActivationHistoryItem>();

        private readonly HashSet<string> _historyTraceKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _totalCount;
        private int _successCount;
        private int _alreadyActiveCount;
        private int _failCount;
        private int _branch21Count;
        private int _branch23Count;
        private int _batchPlannedCount;
        private int _batchProcessedCount;

        public ActivationView()
        {
            InitializeComponent();

            _activationDataService = new ActivationDataService();
            _activationRoutingService = new ActivationRoutingService();

            _historyCollectionView = CollectionViewSource.GetDefaultView(ActivationHistory);
            if (_historyCollectionView != null)
            {
                _historyCollectionView.Filter = FilterHistoryPredicate;
            }

            ListActivationHistory.ItemsSource = _historyCollectionView ?? (System.Collections.IEnumerable)ActivationHistory;
            ApplyActivationInputModeState();
            UpdateBatchActCsvPath();
            UpdateSingleActivationControls();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
        }

        /// <summary>
        /// Pre-fills the Single Contract Activation input from an external module (such as ExtractionView).
        /// </summary>
        public void LoadContractFromExternal(string contractOrDemand, bool isDemandId, string env)
        {
            if (TxtActContract != null)
            {
                TxtActContract.Text = (contractOrDemand ?? string.Empty).Trim();
            }

            string normalizedEnv = NormalizeShortEnvironment(env);
            if (CmbActEnv != null)
            {
                foreach (object entry in CmbActEnv.Items)
                {
                    if (entry is ComboBoxItem item &&
                        string.Equals(item.Tag?.ToString(), normalizedEnv, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbActEnv.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        // =============================================================
        // AIDE
        // =============================================================

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.OpenHelpTargetingTab(0);
            }
        }

        // =============================================================
        // HISTORIQUE, FILTRAGE LIVE & EXPORT CSV
        // =============================================================

        private void FilterActHistory_Changed(object sender, EventArgs e)
        {
            _historyCollectionView?.Refresh();
        }

        private bool FilterHistoryPredicate(object obj)
        {
            if (obj is not ActivationHistoryItem item)
            {
                return false;
            }

            if (CmbFilterActBranch?.SelectedItem is ComboBoxItem branchItem)
            {
                string branchTag = branchItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(branchTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.BranchDisplay, branchTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (CmbFilterActStatus?.SelectedItem is ComboBoxItem statusItem)
            {
                string statusTag = statusItem.Tag?.ToString() ?? "ALL";
                if (string.Equals(statusTag, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(statusTag, "ALREADY", StringComparison.OrdinalIgnoreCase) &&
                    !item.Status.Contains("ALREADY", StringComparison.OrdinalIgnoreCase) &&
                    !item.Status.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(statusTag, "FAILED", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "FAILED", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string query = TxtFilterActHistory?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (item.RowNum?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.InputId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.DemandId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.UconId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.JclContract?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.LisaId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProductCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProcessCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BusinessChannel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.FundsSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BranchDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Status?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void BtnExportActHistoryCsv_Click(object sender, RoutedEventArgs e)
        {
            if (ActivationHistory.Count == 0)
            {
                MessageBox.Show(
                    "There are no activation items in the history table to export.",
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
                    $"ActivationHistory_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("ExcelRow;Time;Env;DemandId;ContractId;JclId;LisaId;UconId;Branche;Product;Process;ChannelCode;BusinessChannel;BUCP;CMDPMT;Premium;FundsAndMovement;RunDate;Steps;Duration;Status;RoutingRule;Diagnostic");

                foreach (ActivationHistoryItem row in ActivationHistory)
                {
                    sb.AppendLine(string.Join(";",
                        EscapeCsv(row.RowNum),
                        EscapeCsv(row.Time),
                        EscapeCsv(row.Env),
                        EscapeCsv(row.DemandId),
                        EscapeCsv(row.ContractId),
                        EscapeCsv(row.JclContract),
                        EscapeCsv(row.LisaId),
                        EscapeCsv(row.UconId),
                        EscapeCsv(row.BranchDisplay),
                        EscapeCsv(row.ProductCode),
                        EscapeCsv(row.ProcessCode),
                        EscapeCsv(row.ChannelCode),
                        EscapeCsv(row.BusinessChannel),
                        EscapeCsv(row.Bucp),
                        EscapeCsv(row.Cmdpmt),
                        EscapeCsv(row.Premium),
                        EscapeCsv(row.FundsSummary),
                        EscapeCsv(row.RunDate),
                        EscapeCsv(row.StepsInfo),
                        EscapeCsv(row.Duration),
                        EscapeCsv(row.Status),
                        EscapeCsv(row.RoutingRule),
                        EscapeCsv(row.DiagnosticSummary)));
                }

                File.WriteAllText(exportPath, sb.ToString(), new UTF8Encoding(true));

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.LastGeneratedPath = exportPath;
                    mainWindow.TxtStatus.Text = $"Activation history exported to CSV: {Path.GetFileName(exportPath)}";
                    mainWindow.TxtStatus.Foreground = Brushes.ForestGreen;
                }

                MessageBox.Show(
                    $"Activation history exported successfully ({ActivationHistory.Count} rows):\n\n{exportPath}",
                    "CSV Export Completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to export activation history: {ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void AddTraceToHistory(ContractActivationTrace? trace)
        {
            if (trace == null)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => AddTraceToHistory(trace));
                return;
            }

            string traceKey = BuildTraceKey(trace);
            if (!_historyTraceKeys.Add(traceKey))
            {
                return;
            }

            ActivationHistoryItem item = CreateHistoryItem(trace);

            _totalCount++;
            if (item.IsBranch23)
            {
                _branch23Count++;
            }
            else
            {
                _branch21Count++;
            }

            switch (item.Status)
            {
                case "SUCCESS":
                    _successCount++;
                    break;
                case "ALREADY ACTIVE":
                case "PENDING ASYNC LISA CREATION":
                    _alreadyActiveCount++;
                    break;
                default:
                    _failCount++;
                    break;
            }

            UpdateHistoryCounters();

            if (item.Status is "FAILED" or "CANCELLED")
            {
                ActivationHistory.Insert(0, item);
            }
            else
            {
                ActivationHistory.Add(item);
            }
        }

        private void UpdateHistoryCounters()
        {
            if (TxtActTotalCount != null)
            {
                TxtActTotalCount.Text = _totalCount.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtActSuccessCount != null)
            {
                TxtActSuccessCount.Text = _successCount.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtActBranch21Count != null)
            {
                TxtActBranch21Count.Text = _branch21Count.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtActBranch23Count != null)
            {
                TxtActBranch23Count.Text = _branch23Count.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtActAlreadyCount != null)
            {
                TxtActAlreadyCount.Text = _alreadyActiveCount.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtActFailCount != null)
            {
                TxtActFailCount.Text = _failCount.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void BtnClearActHistory_Click(object sender, RoutedEventArgs e)
        {
            ActivationHistory.Clear();
            _historyTraceKeys.Clear();

            _totalCount = 0;
            _successCount = 0;
            _alreadyActiveCount = 0;
            _failCount = 0;
            _branch21Count = 0;
            _branch23Count = 0;
            _batchPlannedCount = 0;
            _batchProcessedCount = 0;

            UpdateHistoryCounters();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
        }

        private void ListActivationHistory_MouseDoubleClick(
            object sender,
            MouseButtonEventArgs e)
        {
            OpenSelectedHistoryReport();
        }

        private void MenuActOpenReport_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedHistoryReport();
        }

        private void BtnInspectSelectedAct_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem == null && ActivationHistory.Count > 0)
            {
                ListActivationHistory.SelectedIndex = 0;
            }
            OpenSelectedHistoryReport();
        }

        private void BtnRetrySelectedAct_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem is not ActivationHistoryItem selected &&
                ActivationHistory.Count > 0)
            {
                ListActivationHistory.SelectedIndex = 0;
            }

            if (ListActivationHistory.SelectedItem is not ActivationHistoryItem item)
            {
                MessageBox.Show(
                    "Please select a row in the Live Activation Monitor first.",
                    "Load Contract",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            bool hasContract = !IsMissingValue(item.ContractId) && item.ContractId != "-";
            string target = hasContract ? item.ContractId : item.InputId;
            LoadContractFromExternal(target, !hasContract, item.Env);
        }

        private void BtnSendSelectedToExt_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem == null && ActivationHistory.Count > 0)
            {
                ListActivationHistory.SelectedIndex = 0;
            }
            MenuActSendToExtraction_Click(sender, e);
        }

        private void OpenSelectedHistoryReport()
        {
            if (ListActivationHistory.SelectedItem is not ActivationHistoryItem selected ||
                selected.FullTrace == null)
            {
                return;
            }

            string fullDetails =
                _activationDataService.FormatSingleActivationReport(selected.FullTrace);

            var reportWindow = new DiagnosticReportWindow(
                title: $"Activation Diagnostic — {selected.ContractId} ({selected.BranchDisplay})",
                subtitle: $"Excel Row: {selected.RowNum} | Demand: {selected.FullTrace.DemandId} | Product: {selected.FullTrace.ProductCode} | Process: {selected.FullTrace.ProcessCode} | Branche: {selected.BranchDisplay} | Routing: {selected.RoutingRule}",
                status: selected.Status,
                reportContent: fullDetails,
                contractReference: selected.ContractId)
            {
                Owner = Window.GetWindow(this)
            };
            reportWindow.ShowDialog();
        }

        private void MenuActCopyContract_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem is ActivationHistoryItem selected &&
                !IsMissingValue(selected.ContractId))
            {
                Clipboard.SetText(selected.ContractId);
            }
        }

        private void MenuActCopyDemand_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem is ActivationHistoryItem selected)
            {
                string valueToCopy = !IsMissingValue(selected.DemandId)
                    ? selected.DemandId
                    : selected.InputId;
                if (!IsMissingValue(valueToCopy))
                {
                    Clipboard.SetText(valueToCopy);
                }
            }
        }

        private void MenuActSendToExtraction_Click(object sender, RoutedEventArgs e)
        {
            if (ListActivationHistory.SelectedItem is not ActivationHistoryItem selected)
            {
                return;
            }

            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                bool useContract = !IsMissingValue(selected.ContractId) && selected.ContractId != "-";
                string target = useContract ? selected.ContractId : selected.InputId;
                mainWindow.NavigateToExtractionWithContract(target, !useContract, selected.Env);
            }
        }

        // =============================================================
        // FICHIER BATCH & PREVIEW EXCEL ROWS
        // =============================================================

        private void BtnBrowseActCsv_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter =
                    "Data Files (*.csv;*.xls;*.xlsx)|*.csv;*.xls;*.xlsx|" +
                    "CSV Files (*.csv)|*.csv|" +
                    "Excel Files (*.xls;*.xlsx)|*.xls;*.xlsx",
                Title = "Select file for batch activation",
                InitialDirectory = Path.GetFullPath(Settings.InputDir)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                TxtBatchActCsv.Text = openFileDialog.FileName;
            }
        }

        private void BtnPreviewBatchFile_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtBatchActCsv?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show(
                    "Please select a valid Excel or CSV batch file first.",
                    "File Preview",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                var rows = ExcelHelper.InspectBatchFileRows(filePath);
                var sb = new StringBuilder();
                sb.AppendLine("====================================================================================");
                sb.AppendLine("                      EXCEL / CSV BATCH FILE PREVIEW");
                sb.AppendLine("====================================================================================");
                sb.AppendLine($"  File Name        : {Path.GetFileName(filePath)}");
                sb.AppendLine($"  Full Path        : {filePath}");
                sb.AppendLine($"  Valid Contracts  : {rows.Count}");
                sb.AppendLine("------------------------------------------------------------------------------------");
                sb.AppendLine("  Excel Row #   | Identifier (Contract / Demand ID)   | Auto-Type      | Extra Info / Test Label");
                sb.AppendLine("------------------------------------------------------------------------------------");

                foreach (var r in rows)
                {
                    string autoType = ExcelHelper.IsDemandIdInput(r.Identifier) ? "Demand ID" : "Contrat";
                    sb.AppendLine($"  Row {r.RowNumber,-9} | {r.Identifier,-35} | {autoType,-14} | {r.SecondaryInfo}");
                }

                if (rows.Count == 0)
                {
                    sb.AppendLine("  [WARNING] No valid 11/12-digit Contract or Demand ID rows detected in this file.");
                }

                sb.AppendLine("====================================================================================");

                var previewWindow = new DiagnosticReportWindow(
                    title: $"Batch File Inspector — {Path.GetFileName(filePath)}",
                    subtitle: $"{rows.Count} valid contract/demand rows detected with exact 1-based Excel row numbering",
                    status: rows.Count > 0 ? "SUCCESS" : "FAILED",
                    reportContent: sb.ToString(),
                    contractReference: Path.GetFileNameWithoutExtension(filePath))
                {
                    Owner = Window.GetWindow(this)
                };
                previewWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to inspect batch file: {ex.Message}",
                    "Preview Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void UpdateBatchActCsvPath()
        {
            if (TxtBatchActCsv == null)
            {
                return;
            }

            string environment = GetSelectedEnvironment();
            string process = GetSelectedProcess();
            TxtBatchActCsv.Text =
                Settings.GetDefaultTestingExcelPath(process, environment);
        }

        private void BtnUseDefaultQcActFile_Click(object sender, RoutedEventArgs e)
        {
            UpdateBatchActCsvPath();
        }

        // =============================================================
        // CONTROLES D'ENTREE & PRESETS
        // =============================================================

        private void TxtActContract_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtActDetectedTypeBadge != null)
            {
                TxtActDetectedTypeBadge.Text = ExcelHelper.DescribeIdentifierType(TxtActContract?.Text);
            }
        }

        private void CmbActEnv_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            UpdateBatchActCsvPath();
        }

        private void CmbActChannel_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            UpdateBatchActCsvPath();
            UpdateSingleActivationControls();
        }

        private void UpdateSingleActivationControls()
        {
            string process = GetSelectedProcess();

            bool allowSkipPrime =
                string.Equals(process, "C01", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(process, "C03", StringComparison.OrdinalIgnoreCase);

            Visibility visibility = allowSkipPrime
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (BtnSkipPrimeSingleAct != null)
            {
                BtnSkipPrimeSingleAct.Visibility = visibility;
            }

            if (BtnSkipPrimeBatchAct != null)
            {
                BtnSkipPrimeBatchAct.Visibility = visibility;
            }
        }

        private ActivationInputMode SelectedActivationInputMode
        {
            get
            {
                if (CmbActivationInputMode?.SelectedItem is ComboBoxItem item &&
                    ActivationInputModeExtensions.TryParse(item.Tag?.ToString(), out ActivationInputMode mode))
                {
                    return mode;
                }

                return ActivationInputMode.SqlAutocomplete;
            }
        }

        private void ActivationInputMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            ApplyActivationInputModeState();
        }

        private void ApplyActivationInputModeState()
        {
            if (ManualConfigurationPanel == null)
            {
                return;
            }

            ActivationInputMode mode = SelectedActivationInputMode;
            ManualConfigurationPanel.Visibility = mode.AllowsManualOverrides()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void BtnCancelActivation_Click(object sender, RoutedEventArgs e)
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();

            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.CancelCurrentProcess();
                mainWindow.TxtStatus.Text =
                    "Canceling. The current sequence will stop shortly.";
                mainWindow.TxtStatus.Foreground = Brushes.DarkOrange;
            }
        }

        private void Preset_Checked(object sender, RoutedEventArgs e)
        {
            if (CmbActBucp == null || CmbActCmdpmt == null)
            {
                return;
            }

            if (sender == RbPresetBank)
            {
                SelectComboBoxItemByContent(CmbActBucp, "00382");
                SelectComboBoxItemByContent(CmbActCmdpmt, "8");
            }
            else if (sender == RbPresetBankLegacy)
            {
                SelectComboBoxItemByContent(CmbActBucp, "00182");
                SelectComboBoxItemByContent(CmbActCmdpmt, "8");
            }
            else if (sender == RbPresetBrokerNew)
            {
                SelectComboBoxItemByContent(CmbActBucp, "80819");
                SelectComboBoxItemByContent(CmbActCmdpmt, "H");
            }
            else if (sender == RbPresetBrokerOld)
            {
                SelectComboBoxItemByContent(CmbActBucp, "49797");
                SelectComboBoxItemByContent(CmbActCmdpmt, "X");
            }
            else if (sender == RbPresetBrokerInvest)
            {
                SelectComboBoxItemByContent(CmbActBucp, "49797");
                SelectComboBoxItemByContent(CmbActCmdpmt, "H");
            }
        }

        private void BtnClearPreset_Click(object sender, RoutedEventArgs e)
        {
            if (RbPresetBank != null)
            {
                RbPresetBank.IsChecked = false;
            }

            if (RbPresetBankLegacy != null)
            {
                RbPresetBankLegacy.IsChecked = false;
            }

            if (RbPresetBrokerOld != null)
            {
                RbPresetBrokerOld.IsChecked = false;
            }

            if (RbPresetBrokerInvest != null)
            {
                RbPresetBrokerInvest.IsChecked = false;
            }

            if (RbPresetBrokerNew != null)
            {
                RbPresetBrokerNew.IsChecked = false;
            }

            if (CmbActBucp != null)
            {
                CmbActBucp.SelectedIndex = -1;
                CmbActBucp.Text = string.Empty;
            }

            if (CmbActCmdpmt != null)
            {
                CmbActCmdpmt.SelectedIndex = -1;
                CmbActCmdpmt.Text = string.Empty;
            }

            if (CmbActChannel != null)
            {
                CmbActChannel.SelectedIndex = 0;
            }

            if (TxtActOverrideRunDate != null)
            {
                TxtActOverrideRunDate.Text = string.Empty;
            }

            if (TxtActOverrideAmount != null)
            {
                TxtActOverrideAmount.Text = string.Empty;
            }
        }

        private void BtnJobPresetFull_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.FullChain());

        private void BtnJobPresetPremeliaOnly_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.PremeliaOnly());

        private void BtnJobPresetNoAddPrct_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.SkipAddPrct());

        private void BtnJobPresetImsOnly_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.ImsOnly());

        private void BtnJobPresetBr23LvdqOnly_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.Branch23FundsAndConsolidationOnly());

        private void BtnJobPresetBr23ConsolidateOnly_Click(object sender, RoutedEventArgs e) =>
            ApplyJobSelectionToUi(ActivationJobSelection.Branch23VniAndConsolidationOnly());

        // =============================================================
        // ACTIVATION UNITAIRE
        // =============================================================

        private void BtnRunSingleActivation_Click(object sender, RoutedEventArgs e)
        {
            RunSingleActivation(skipPrime: false);
        }

        private void TxtActContract_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnRunSingleAct?.IsEnabled == true)
            {
                e.Handled = true;
                RunSingleActivation(skipPrime: false);
            }
        }

        private void BtnSkipPrimeSingleAct_Click(object sender, RoutedEventArgs e)
        {
            RunSingleActivation(skipPrime: true);
        }

        private async void RunSingleActivation(bool skipPrime)
        {
            if (Window.GetWindow(this) is not MainWindow mainWindow)
            {
                return;
            }

            ResetCancellationToken();
            SetSingleActivationButtons(running: true);

            await mainWindow.RunProcessAsync(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var trace = new ContractActivationTrace
                {
                    RowNum = 0, // Single activation (not from Excel row)
                    StartTime = DateTime.Now,
                    FinalStatus = "RUNNING"
                };

                bool historyAdded = false;

                try
                {
                    SingleActivationInput input =
                        await ReadSingleActivationInputAsync().ConfigureAwait(false);

                    ValidateCredentials(input.Username, input.Password);
                    ValidateSingleInput(input.RawInput);
                    PopulateInitialTrace(trace, input, skipPrime);

                    UpdateBatchProgressDisplay(
                        1,
                        input.ForceBranch23 ? 10 : 7,
                        $"{input.RawInput} — Résolution SQL Autocomplete & Préchargement JCL ({input.Environment}000)...",
                        "STARTING",
                        "ACTIVATION UNITAIRE (ÉTAPES JCL/SQL)",
                        stopwatch.Elapsed.TotalSeconds);

                    await SetStatusAsync(
                            mainWindow,
                            $"Resolving ELIA routing, Branche 21/23, contract and premium in " +
                            $"{input.Environment}000.")
                        .ConfigureAwait(false);

                    var jclProcessor =
                        new JclProcessorService(Settings.JclNetworkFolder);

                    Task<AutomaticActivationPlan> planTask =
                        _activationDataService.ResolveAutomaticActivationPlanAsync(
                            input.RawInput,
                            input.IsDemandId,
                            input.Environment + "000",
                            input.Cus,
                            input.ForceBranch23,
                            input.InputMode,
                            BuildUiOverrides(input),
                            _cts!.Token);

                    Task preloadTask = jclProcessor.PreloadTemplatesAsync(
                        JclProcessorService.StandardActivationJobs
                            .Concat(ActivationOrchestrator.Branch23JclNames)
                            .Distinct(StringComparer.OrdinalIgnoreCase));

                    await Task.WhenAll(planTask, preloadTask).ConfigureAwait(false);
                    AutomaticActivationPlan plan = await planTask.ConfigureAwait(false);
                    PopulateTraceFromPlan(trace, plan);

                    bool onlyRunningPostActivationBranch23Steps =
                        (plan.IsBranch23 || input.ForceBranch23) &&
                        input.JobSelection != null &&
                        !input.JobSelection.RequiresPremelia &&
                        input.JobSelection.HasAnyBranch23Step;

                    IReadOnlyList<ActivationPlanIssue> blockingIssues = plan
                        .Validate()
                        .Where(issue => issue.IsBlocking &&
                                        !(onlyRunningPostActivationBranch23Steps && issue.Code == "ALREADY_ACTIVE"))
                        .ToList();
                    if (blockingIssues.Count > 0)
                    {
                        trace.FailureStage = "AUTOMATIC_PLAN_VALIDATION";
                        trace.FailureReason = string.Join(
                            " | ",
                            blockingIssues.Select(issue =>
                                $"{issue.Code}: {issue.Message}"));
                        throw new InvalidOperationException(trace.FailureReason);
                    }

                    if ((plan.ResumeStrategy == ActivationResumeStrategy.AlreadyActive ||
                         plan.IsAlreadyActive) &&
                        !onlyRunningPostActivationBranch23Steps)
                    {
                        throw new InvalidOperationException("ALREADY_ACTIVE");
                    }

                    string effectiveEnvironment =
                        NormalizeShortEnvironment(plan.Environment);
                    string effectiveProcess = plan.ProcessCode;
                    ActivationLookupResult lookup = CreateLookupFromPlan(plan);
                    ActivationRoutingResult paymentRouting = ResolveInputRouting(
                        input,
                        plan.ChannelCode);
                    if (!paymentRouting.IsResolved)
                    {
                        trace.FailureStage = "PAYMENT_ROUTING";
                        trace.FailureReason = paymentRouting.DiagnosticMessage;
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(paymentRouting.DiagnosticMessage)
                                ? "BUCP/CMDPMT routing could not be resolved."
                                : paymentRouting.DiagnosticMessage);
                    }

                    trace.PaymentChannel = plan.PaymentChannel;
                    trace.BusinessChannel = plan.ResolvedChannel;
                    trace.Bucp = plan.Bucp;
                    trace.Cmdpmt = plan.PaymentCommand;
                    trace.RoutingRule = plan.RoutingRule;
                    trace.RoutingSource = plan.ValueSources.TryGetValue(
                        "BUCP",
                        out string? routingSource)
                            ? routingSource
                            : "SQL ChannelCode";
                    trace.RunDate = plan.Drun;
                    trace.RunDateSource = plan.RunDateSource;
                    trace.RoutingStatus = PreActivationRoutingStatus.Ready;
                    trace.RoutingDiagnostic =
                        plan.RoutingRule +
                        " | BusinessChannel=" + plan.ResolvedChannel +
                        " | BUCP/CMDPMT=" + plan.Bucp + "/" +
                        plan.PaymentCommand +
                        " | RunDate=" + plan.Drun +
                        " | RunDateSource=" + plan.RunDateSource;

                    bool automaticBranch23 =
                        lookup.BranchClassification ==
                        ContractBranchClassification.Branch23;

                    bool isBranch23 = input.ForceBranch23 || automaticBranch23;

                    trace.Environment = effectiveEnvironment;
                    trace.Channel = effectiveProcess;
                    trace.ProcessCode = effectiveProcess;
                    trace.IsBranch23 = isBranch23;

                    if (input.ForceBranch23)
                    {
                        trace.Branch23Source = "Manual UI override";
                    }
                    else if (automaticBranch23)
                    {
                        trace.Branch23Source =
                            $"{lookup.BranchDetectionSource}: " +
                            lookup.BranchDetectionReason;
                    }
                    else if (lookup.BranchClassification ==
                             ContractBranchClassification.CompositeWithFunds)
                    {
                        trace.Branch23Source =
                            "ELIA fund component detected, but automatic BR23 " +
                            "execution was not enabled.";
                    }
                    else
                    {
                        trace.Branch23Source = "Branche 21 (Standard)";
                    }

                    string resolvedContract = plan.ContractNumber;
                    string formattedContract = plan.MainframeContractNumber;
                    string amount = plan.GrossPremium.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture);

                    IBranch23FundSource? branch23FundSource =
                        BuildBranch23FundSource(isBranch23, input.Branch23Funds);

                    await SetStatusAsync(
                            mainWindow,
                            BuildRoutingStatusMessage(
                                lookup,
                                effectiveProcess,
                                isBranch23))
                        .ConfigureAwait(false);

                    UpdateSingleActivationStepProgress(
                        trace,
                        stopwatch,
                        $"Routage SQL prêt ({plan.ResolvedChannel} | BUCP={plan.Bucp}/CMD={plan.PaymentCommand} | Date N={plan.Drun}) — Connexion ESCWA...");

                    string effectiveBucp = !string.IsNullOrWhiteSpace(plan.Bucp)
                        ? plan.Bucp
                        : paymentRouting.Bucp;
                    string effectiveCmdpmt = !string.IsNullOrWhiteSpace(plan.PaymentCommand)
                        ? plan.PaymentCommand
                        : paymentRouting.PaymentCommand;

                    await _activationDataService.ExecuteActivationSequenceAsync(
                            formattedContract,
                            amount,
                            effectiveEnvironment,
                            input.Cus,
                            effectiveBucp,
                            effectiveCmdpmt,
                            effectiveProcess,
                            skipPrime,
                            skipLogon: false,
                            input.Username,
                            input.Password,
                            message =>
                            {
                                SetStatusWithoutAwait(mainWindow, message);
                                UpdateSingleActivationStepProgress(trace, stopwatch, message, "RUNNING");
                            },
                            _cts.Token,
                            trace,
                            isBranch23,
                            branch23FundSource,
                            branch23ActivationOnly:
                                Settings.Branch23Activation.Branch23ActivationOnly,
                            jobSelection: input.JobSelection,
                            manualPaymentOverride: input.InputMode.AllowsManualOverrides())
                        .ConfigureAwait(false);

                    string successfulStatus = string.Equals(
                            trace.LisaInternalId,
                            "PENDING_ASYNC_LISA_CREATION",
                            StringComparison.OrdinalIgnoreCase)
                        ? "PENDING_ASYNC_LISA_CREATION"
                        : "SUCCESS";
                    CompleteTrace(trace, stopwatch, successfulStatus);

                    UpdateSingleActivationStepProgress(
                        trace,
                        stopwatch,
                        $"Terminé avec succès ({trace.StepsSummary})",
                        "COMPLETED");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.TxtStatus.Text =
                            $"Activation completed in " +
                            $"{trace.TotalDurationMs / 1000.0:F1}s for " +
                            $"{resolvedContract} ({trace.BranchDisplay}). {trace.StepsSummary}";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkGreen;

                        MessageBox.Show(
                            $"Contract {resolvedContract} " +
                            $"(JCL: {formattedContract}) was activated successfully.\n\n" +
                            $"Product: {Display(lookup.ProductCode)}\n" +
                            $"Process: {effectiveProcess}\n" +
                            $"Branche: {trace.BranchDisplay}\n" +
                            $"Duration: {trace.TotalDurationMs / 1000.0:F2}s\n" +
                            $"Steps: {trace.StepsSummary}",
                            "Activation Successful",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                }
                catch (OperationCanceledException)
                {
                    CompleteTrace(trace, stopwatch, "CANCELLED");
                    trace.FailureStage = "USER_CANCELLED";
                    trace.FailureReason = "Activation was cancelled by the user.";
                    UpdateSingleActivationStepProgress(trace, stopwatch, "Activation annulée par l'utilisateur.", "CANCELLED");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.TxtStatus.Text = "Activation cancelled.";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkOrange;
                    });
                }
                catch (Exception exception)
                    when (string.Equals(
                        exception.Message,
                        "ALREADY_ACTIVE",
                        StringComparison.Ordinal))
                {
                    CompleteTrace(trace, stopwatch, "ALREADY_ACTIVE");
                    UpdateSingleActivationStepProgress(trace, stopwatch, "Contrat déjà actif (ADDPRCT RC=0008).", "ALREADY_ACTIVE");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.TxtStatus.Text =
                            $"Contract {trace.ResolvedContract} is already active. " +
                            "ADDPRCT returned RC=0008.";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkOrange;

                        MessageBox.Show(
                            $"Contract {trace.ResolvedContract} is already active.\n\n" +
                            $"Job: {trace.FailedJobName} {trace.FailedJobNum}\n" +
                            $"Return code: {trace.FailedReturnCode}\n\n" +
                            "Use Skip Prime only when the remaining IMS " +
                            "activation jobs must be replayed.",
                            "Contract Already Active",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                }
                catch (Exception exception)
                {
                    CompleteTrace(trace, stopwatch, "FAILED");

                    if (string.IsNullOrWhiteSpace(trace.FailureReason))
                    {
                        trace.FailureReason = exception.Message;
                    }

                    if (string.IsNullOrWhiteSpace(trace.FailureStage) ||
                        string.Equals(
                            trace.FailureStage,
                            "NONE",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        trace.FailureStage = "SINGLE_ACTIVATION";
                    }

                    UpdateSingleActivationStepProgress(trace, stopwatch, $"Échec : {trace.FailureReason}", "FAILED");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.TxtStatus.Text =
                            $"Activation failed: {trace.FailureReason}";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkRed;
                    });

                    throw;
                }
                finally
                {
                    if (trace.TotalDurationMs == 0)
                    {
                        stopwatch.Stop();
                        trace.EndTime = DateTime.Now;
                        trace.TotalDurationMs = stopwatch.ElapsedMilliseconds;
                    }

                    if (!historyAdded)
                    {
                        AddTraceToHistory(trace);
                        historyAdded = true;
                    }

                    string reportPath = SaveSingleActivationReport(trace, skipPrime);
                    mainWindow.LastGeneratedPath = reportPath;

                    await Dispatcher.InvokeAsync(
                        () => SetSingleActivationButtons(running: false));
                }
            });
        }

        // =============================================================
        // ACTIVATION BATCH
        // =============================================================

        private void BtnRunBatchActivation_Click(object sender, RoutedEventArgs e)
        {
            RunBatchActivation(skipPrime: false);
        }

        private void BtnSkipPrimeBatchAct_Click(object sender, RoutedEventArgs e)
        {
            RunBatchActivation(skipPrime: true);
        }

        public string PrepareCsvFromExcel(string excelFilePath)
        {
            return ExcelHelper.ConvertExcelToCsv(
                excelFilePath,
                Settings.OutputDir,
                "Converted_Act");
        }

        private async void RunBatchActivation(bool skipPrime)
        {
            if (Window.GetWindow(this) is not MainWindow mainWindow)
            {
                return;
            }

            ResetCancellationToken();
            SetBatchActivationButtons(running: true);

            await Dispatcher.InvokeAsync(() =>
                UpdateBatchProgressDisplay(0, 0, null, "STARTING"));

            await mainWindow.RunProcessAsync(async () =>
            {
                try
                {
                    BatchActivationInput input =
                        await ReadBatchActivationInputAsync().ConfigureAwait(false);

                    ValidateCredentials(input.Username, input.Password);

                    if (string.IsNullOrWhiteSpace(input.FilePath) ||
                        !File.Exists(input.FilePath))
                    {
                        throw new FileNotFoundException(
                            "Please select a valid CSV or Excel file.",
                            input.FilePath);
                    }

                    string inputPath = input.FilePath;

                    if (inputPath.EndsWith(
                            ".xls",
                            StringComparison.OrdinalIgnoreCase) ||
                        inputPath.EndsWith(
                            ".xlsx",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await SetStatusAsync(
                                mainWindow,
                                "Converting Excel file to local CSV (preserving 1:1 Excel row numbering).")
                            .ConfigureAwait(false);

                        inputPath = await Task.Run(
                                () => PrepareCsvFromExcel(inputPath),
                                _cts!.Token)
                            .ConfigureAwait(false);
                    }

                    _cts!.Token.ThrowIfCancellationRequested();

                    var batchService =
                        new BatchActivationService(_activationDataService);

                    var result = await batchService.RunBatchAsync(
                            inputPath,
                            input.IsDemandId,
                            input.Environment,
                            input.Cus,
                            input.Bucp,
                            input.Cmdpmt,
                            input.Process,
                            skipPrime,
                            input.Username,
                            input.Password,
                            Settings.OutputDir,
                            message => SetStatusWithoutAwait(mainWindow, message),
                            _cts.Token,
                            AddTraceToHistory,
                            input.MarkAllAsBranch23,
                            input.DefaultBranch23Funds,
                            progress => Dispatcher.InvokeAsync(
                                () => HandleBatchProgress(mainWindow, progress)),
                            input.InputMode,
                            input.JobSelection,
                            manualPaymentOverride: input.InputMode.AllowsManualOverrides(),
                            uiOverrides: BuildBatchUiOverrides(input))
                        .ConfigureAwait(false);

                    mainWindow.LastGeneratedPath = result.reportPath;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        int finalProcessed =
                            result.successCount +
                            result.alreadyActiveCount +
                            result.errorCount;

                        int finalTotal = Math.Max(
                            finalProcessed,
                            _batchPlannedCount);

                        UpdateBatchProgressDisplay(
                            finalProcessed,
                            finalTotal,
                            null,
                            "COMPLETED");

                        mainWindow.TxtStatus.Text =
                            $"Batch completed. {finalProcessed}/{finalTotal} " +
                            $"processed. Activated: {result.successCount} | " +
                            $"Already active: {result.alreadyActiveCount} | " +
                            $"Failed: {result.errorCount}";
                        mainWindow.TxtStatus.Foreground = result.errorCount > 0
                            ? Brushes.DarkOrange
                            : Brushes.DarkGreen;

                        MessageBox.Show(
                            $"Batch activation completed.\n\n" +
                            $"Total processed: {finalProcessed}\n" +
                            $"Successfully activated: {result.successCount}\n" +
                            $"Already active: {result.alreadyActiveCount}\n" +
                            $"Failed: {result.errorCount}\n\n" +
                            $"Report:\n{result.reportPath}",
                            "Batch Activation Summary",
                            MessageBoxButton.OK,
                            result.errorCount > 0
                                ? MessageBoxImage.Warning
                                : MessageBoxImage.Information);
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int remaining = Math.Max(
                            0,
                            _batchPlannedCount - _batchProcessedCount);

                        UpdateBatchProgressDisplay(
                            _batchProcessedCount,
                            _batchPlannedCount,
                            null,
                            "CANCELLED");

                        mainWindow.TxtStatus.Text =
                            $"Batch activation cancelled. " +
                            $"{_batchProcessedCount}/{_batchPlannedCount} " +
                            $"completed, {remaining} remaining. " +
                            "A partial report may have been generated.";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkOrange;
                    });
                }
                catch (Exception exception)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateBatchProgressDisplay(
                            _batchProcessedCount,
                            _batchPlannedCount,
                            null,
                            "FAILED");

                        mainWindow.TxtStatus.Text =
                            $"Batch activation failed: {exception.Message}";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkRed;
                    });

                    throw;
                }
                finally
                {
                    await Dispatcher.InvokeAsync(
                        () => SetBatchActivationButtons(running: false));
                }
            });
        }
    }
}
