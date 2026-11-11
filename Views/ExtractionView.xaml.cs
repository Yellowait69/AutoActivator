using System;
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
        private ICollectionView? _historyCollectionView;

        public ObservableCollection<ExtractionItem> ExtractionHistory { get; set; } = new ObservableCollection<ExtractionItem>();

        private int _koExtractionCount = 0;
        private int _okExtractionCount = 0;
        private int _branch21Count = 0;
        private int _branch23Count = 0;
        private int _totalExtractionCount = 0;
        private int _batchPlannedCount = 0;
        private int _batchProcessedCount = 0;

        public ExtractionView()
        {
            InitializeComponent();

            _extractionService = new ExtractionService();
            _historyCollectionView = CollectionViewSource.GetDefaultView(ExtractionHistory);
            if (_historyCollectionView != null)
            {
                _historyCollectionView.Filter = FilterExtractionPredicate;
            }

            ListHistory.ItemsSource = _historyCollectionView ?? (System.Collections.IEnumerable)ExtractionHistory;
            UpdateBatchExtCsvPath();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
        }

        /// <summary>
        /// Pre-fills the Single Contract Extraction input from an external module (such as ActivationView).
        /// </summary>
        public void LoadContractFromExternal(string contractOrDemand, bool isDemandId, string env)
        {
            if (TxtExtContract != null)
            {
                TxtExtContract.Text = (contractOrDemand ?? string.Empty).Trim();
            }

            string envLetter = (env ?? string.Empty).Trim().ToUpperInvariant().StartsWith("Q") ? "Q" : "D";
            if (CmbExtEnv != null)
            {
                foreach (object entry in CmbExtEnv.Items)
                {
                    if (entry is ComboBoxItem item &&
                        string.Equals(item.Tag?.ToString(), envLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbExtEnv.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
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

        // =============================================================
        // LIVE FILTERING & CSV EXPORT
        // =============================================================

        private void FilterExtHistory_Changed(object sender, EventArgs e)
        {
            _historyCollectionView?.Refresh();
        }

        private void BadgeExtAll_Click(object sender, MouseButtonEventArgs e)
        {
            SetStatusFilterTag("ALL");
        }

        private void BadgeExtGreen_Click(object sender, MouseButtonEventArgs e)
        {
            SetStatusFilterTag("OK");
        }

        private void BadgeExtFailed_Click(object sender, MouseButtonEventArgs e)
        {
            SetStatusFilterTag("KO_OR_ELIA");
        }

        private void SetStatusFilterTag(string targetTag)
        {
            if (CmbFilterExtStatus == null)
            {
                return;
            }

            foreach (object entry in CmbFilterExtStatus.Items)
            {
                if (entry is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), targetTag, StringComparison.OrdinalIgnoreCase))
                {
                    CmbFilterExtStatus.SelectedItem = item;
                    break;
                }
            }
        }

        private bool FilterExtractionPredicate(object obj)
        {
            if (obj is not ExtractionItem item)
            {
                return false;
            }

            if (CmbFilterExtBranch?.SelectedItem is ComboBoxItem branchItem)
            {
                string branchTag = branchItem.Tag?.ToString() ?? "ALL";
                if (!string.Equals(branchTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.BranchDisplay, branchTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (CmbFilterExtStatus?.SelectedItem is ComboBoxItem statusItem)
            {
                string statusTag = statusItem.Tag?.ToString() ?? "ALL";
                if (string.Equals(statusTag, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    if (!item.IsGreen)
                    {
                        return false;
                    }
                }
                else if (string.Equals(statusTag, "KO_OR_ELIA", StringComparison.OrdinalIgnoreCase))
                {
                    if (!item.IsFailed)
                    {
                        return false;
                    }
                }
                else if (!string.Equals(statusTag, "ALL", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(item.Status, statusTag, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            string query = TxtFilterExtHistory?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (item.RowNum?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Test?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ContractId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.JclContract?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.InternalId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Ucon?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Hdmd?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProductCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.ProcessCode?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BusinessChannel?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Bucp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.RunDate?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.RoutingRule?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.FundsSummary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.BranchDisplay?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (item.Status?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void BtnExportExtHistoryCsv_Click(object sender, RoutedEventArgs e)
        {
            if (ExtractionHistory.Count == 0)
            {
                MessageBox.Show(
                    "There are no extraction items in the history table to export.",
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
                    $"ExtractionHistory_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("ExcelRow;Time;Env;DemandId;ContractId;JclId;LisaId_NO_CNT;UconId;Branche;Product;Process;ChannelCode;BusinessChannel;BUCP;CMDPMT;RoutingRule;GrossPremium;RunDate;ConsolidationDate;FundsAndMovement;TestId;Status;FilePath");

                foreach (ExtractionItem row in ExtractionHistory)
                {
                    sb.AppendLine(string.Join(";",
                        EscapeCsv(row.RowNum),
                        EscapeCsv(row.Time),
                        EscapeCsv(row.Env),
                        EscapeCsv(row.Hdmd),
                        EscapeCsv(row.ContractId),
                        EscapeCsv(row.JclContract),
                        EscapeCsv(row.InternalId),
                        EscapeCsv(row.Ucon),
                        EscapeCsv(row.BranchDisplay),
                        EscapeCsv(row.ProductCode),
                        EscapeCsv(row.ProcessCode),
                        EscapeCsv(row.ChannelCode),
                        EscapeCsv(row.BusinessChannel),
                        EscapeCsv(row.Bucp),
                        EscapeCsv(row.Cmdpmt),
                        EscapeCsv(row.RoutingRule),
                        EscapeCsv(row.Premium),
                        EscapeCsv(row.RunDate),
                        EscapeCsv(row.ConsolidationDate),
                        EscapeCsv(row.FundsSummary),
                        EscapeCsv(row.Test),
                        EscapeCsv(row.Status),
                        EscapeCsv(row.FilePath)));
                }

                File.WriteAllText(exportPath, sb.ToString(), new UTF8Encoding(true));

                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    mainWindow.LastGeneratedPath = exportPath;
                    mainWindow.TxtStatus.Text = $"Extraction history exported to CSV: {Path.GetFileName(exportPath)}";
                    mainWindow.TxtStatus.Foreground = Brushes.ForestGreen;
                }

                MessageBox.Show(
                    $"Extraction history exported successfully ({ExtractionHistory.Count} rows):\n\n{exportPath}",
                    "CSV Export Completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to export extraction history: {ex.Message}",
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

            string clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (clean.Contains(';') || clean.Contains('"'))
            {
                return $"\"{clean.Replace("\"", "\"\"")}\"";
            }

            return clean;
        }

        // =============================================================
        // HISTORY MANAGEMENT & KPI COUNTERS
        // =============================================================

        private static int GetRowNumValue(string rowNumStr)
        {
            return int.TryParse(rowNumStr, out int val) ? val : int.MaxValue;
        }

        private void AddExtractionItemToHistory(ExtractionItem item)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddExtractionItemToHistory(item));
                return;
            }

            int newItemRow = GetRowNumValue(item.RowNum);
            bool isKoOrEliaOnly = !string.Equals(item.Status, "OK", StringComparison.OrdinalIgnoreCase);

            _totalExtractionCount++;
            if (item.IsBranch23)
            {
                _branch23Count++;
            }
            else
            {
                _branch21Count++;
            }

            if (isKoOrEliaOnly)
            {
                // Échecs / Non actifs insérés tout en haut du tableau (index 0)
                ExtractionHistory.Insert(0, item);
                ListHistory.SelectedItem = item;
                _koExtractionCount++;
            }
            else
            {
                int insertIndex = _koExtractionCount;
                while (insertIndex < ExtractionHistory.Count)
                {
                    if (GetRowNumValue(ExtractionHistory[insertIndex].RowNum) > newItemRow)
                    {
                        break;
                    }
                    insertIndex++;
                }
                ExtractionHistory.Insert(insertIndex, item);
                if (ListHistory.SelectedItem == null)
                {
                    ListHistory.SelectedItem = item;
                }
                _okExtractionCount++;
            }

            UpdateExtractionResultBanner(item);
            UpdateKpiCounters();
        }

        private void UpdateExtractionResultBanner(ExtractionItem latestItem)
        {
            if (BannerExtractionResult == null)
            {
                return;
            }

            BannerExtractionResult.Visibility = Visibility.Visible;

            if (latestItem.IsFailed)
            {
                // ROUGE : Contrat non actif dans LISA (ELIA ONLY) ou introuvable (KO)
                BannerExtractionResult.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                BannerExtractionResult.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                TxtBannerExtIcon.Text = "🔴";
                TxtBannerExtTitle.Text = $"NON ACTIF LISA / ÉCHEC ({latestItem.Status}) — Contrat : {latestItem.ContractId} (Demand : {latestItem.Hdmd})";
                TxtBannerExtTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                TxtBannerExtDetail.Text = latestItem.SummaryDisplay;
                TxtBannerExtDetail.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
            }
            else if (_koExtractionCount == 0)
            {
                // VERT : Contrat actif dans LISA + ELIA (et aucun échec dans la session)
                BannerExtractionResult.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                BannerExtractionResult.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
                TxtBannerExtIcon.Text = "🟢";
                TxtBannerExtTitle.Text = $"ACTIF LISA + ELIA — Contrat : {latestItem.ContractId} (LISA ID : {latestItem.InternalId})";
                TxtBannerExtTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D"));
                TxtBannerExtDetail.Text = $"{latestItem.BranchDisplay} · {latestItem.ProductProcessDisplay} · {latestItem.ChannelPaymentDisplay} · {latestItem.SummaryDisplay}";
                TxtBannerExtDetail.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
            }
        }

        private void UpdateKpiCounters()
        {
            if (TxtTotalCount != null) TxtTotalCount.Text = _totalExtractionCount.ToString(CultureInfo.InvariantCulture);
            if (TxtOkCount != null) TxtOkCount.Text = _okExtractionCount.ToString(CultureInfo.InvariantCulture);
            if (TxtKoCount != null) TxtKoCount.Text = _koExtractionCount.ToString(CultureInfo.InvariantCulture);
        }

        private readonly Stopwatch _extProgressStopwatch = new Stopwatch();

        private void UpdateBatchProgressDisplay(
            int processed,
            int total,
            string? currentContract,
            string? status = null,
            string? modeLabel = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => UpdateBatchProgressDisplay(processed, total, currentContract, status, modeLabel));
                return;
            }

            _batchProcessedCount = Math.Max(0, processed);
            _batchPlannedCount = Math.Max(0, total);
            if (_batchProcessedCount > _batchPlannedCount)
            {
                _batchProcessedCount = _batchPlannedCount;
            }

            int remaining = Math.Max(0, _batchPlannedCount - _batchProcessedCount);
            double percentage = _batchPlannedCount > 0
                ? (double)_batchProcessedCount / _batchPlannedCount * 100.0
                : 0.0;

            string normalizedStatus = string.IsNullOrWhiteSpace(status) ? "RUNNING" : status.Trim().ToUpperInvariant();
            if (normalizedStatus == "STARTING")
            {
                _extProgressStopwatch.Restart();
            }
            else if (normalizedStatus is "COMPLETED" or "CANCELLED" or "FAILED" or "READY" or "ELIA_ONLY")
            {
                _extProgressStopwatch.Stop();
            }

            if (TxtExtBatchProcessedCount != null) TxtExtBatchProcessedCount.Text = _batchProcessedCount.ToString(CultureInfo.InvariantCulture);
            if (TxtExtBatchPlannedCount != null) TxtExtBatchPlannedCount.Text = _batchPlannedCount.ToString(CultureInfo.InvariantCulture);
            if (TxtExtBatchCurrentContract != null) TxtExtBatchCurrentContract.Text = string.IsNullOrWhiteSpace(currentContract) ? "Prêt" : currentContract;
            if (TxtExtBatchProgressPercentage != null) TxtExtBatchProgressPercentage.Text = $"{percentage:F0}%";

            if (TxtExtElapsedTime != null)
            {
                double elapsedSec = _extProgressStopwatch.Elapsed.TotalSeconds;
                if (normalizedStatus == "READY" && elapsedSec <= 0.01)
                {
                    TxtExtElapsedTime.Text = "   ⏱️ 0.0s";
                }
                else if (remaining > 0 && _batchProcessedCount > 0 && elapsedSec > 0.3 && (modeLabel == null || modeLabel.Contains("BATCH", StringComparison.OrdinalIgnoreCase)))
                {
                    double avgPerItem = elapsedSec / _batchProcessedCount;
                    double etaSec = avgPerItem * remaining;
                    TxtExtElapsedTime.Text = $"   ⏱️ {elapsedSec:F1}s (ETA ~{etaSec:F0}s)";
                }
                else
                {
                    TxtExtElapsedTime.Text = $"   ⏱️ {elapsedSec:F1}s";
                }
            }

            if (ProgressBatchExtraction != null)
            {
                ProgressBatchExtraction.Minimum = 0;
                ProgressBatchExtraction.Maximum = Math.Max(1, _batchPlannedCount);
                ProgressBatchExtraction.Value = Math.Min(_batchProcessedCount, _batchPlannedCount);
                ProgressBatchExtraction.Foreground = normalizedStatus switch
                {
                    "COMPLETED" => Brushes.MediumSeaGreen,
                    "ELIA_ONLY" or "CANCELLED" or "FAILED" => Brushes.Crimson,
                    _ => Brushes.RoyalBlue
                };
            }
        }

        private static string FormatContractForDisplay(string contract)
        {
            if (string.IsNullOrWhiteSpace(contract)) return "-";

            string clean = contract.Replace("-", "").Replace(" ", "");

            return clean.Length == 12
                ? $"{clean.Substring(0, 3)}-{clean.Substring(3, 7)}-{clean.Substring(10, 2)}"
                : contract;
        }

        // =============================================================
        // DOUBLE-CLICK & CONTEXT MENU HANDLERS
        // =============================================================

        private void ListHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InspectSelectedExtractionItem();
        }

        private void MenuExtInspect_Click(object sender, RoutedEventArgs e)
        {
            InspectSelectedExtractionItem();
        }

        private void MenuExtLoadSingle_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem is not ExtractionItem selected)
            {
                return;
            }

            bool hasContract = !string.IsNullOrWhiteSpace(selected.ContractId) &&
                               selected.ContractId != "-" &&
                               selected.ContractId != "N/A" &&
                               !selected.ContractId.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
            string target = hasContract ? selected.ContractId : selected.Hdmd;
            LoadContractFromExternal(target, !hasContract, selected.Env);
            TxtExtContract?.Focus();
        }

        private void BtnInspectSelectedExt_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem == null && ExtractionHistory.Count > 0)
            {
                ListHistory.SelectedIndex = 0;
            }
            InspectSelectedExtractionItem();
        }

        private void BtnSendSelectedToAct_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem == null && ExtractionHistory.Count > 0)
            {
                ListHistory.SelectedIndex = 0;
            }
            MenuExtSendToActivation_Click(sender, e);
        }

        private void BtnCompareLatestExt_Click(object sender, RoutedEventArgs e)
        {
            string? latestFile = null;
            if (Directory.Exists(Settings.OutputDir))
            {
                latestFile = Directory.GetFiles(Settings.OutputDir, "*.csv")
                    .Where(f => !Path.GetFileName(f).StartsWith("Converted_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ExtractionHistory_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ActivationHistory_", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("ComparisonGrid_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }

            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToComparisonWithLatest(latestFile);
            }
        }

        private void InspectSelectedExtractionItem()
        {
            if (ListHistory.SelectedItem is not ExtractionItem selected)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("====================================================================================");
            sb.AppendLine("                    SQL DATA EXTRACTION DIAGNOSTIC REPORT");
            sb.AppendLine("====================================================================================");
            sb.AppendLine("[0. SYNTHÈSE AUTOCOMPLETE SQL & ROUTAGE MÉTIER]");
            sb.AppendLine($"  • Contrat (3-7-2)      : {selected.ContractId}  |  JCL (9 chiffres / NO_CNT) : {selected.JclContract}");
            sb.AppendLine($"  • Demand ID / ELIA ID  : {selected.Hdmd}  /  UCON {selected.Ucon} (Statut Demand : {selected.DemandStatus})");
            sb.AppendLine($"  • État LISA (NO_CNT)   : {selected.InternalId}");
            sb.AppendLine($"  • Produit / Process    : {selected.ProductCode}  /  {selected.ProcessCode}  (ChannelCode ELIA : {selected.ChannelCode})");
            sb.AppendLine($"  • Canal & Paiement     : {selected.BusinessChannel}  ->  BUCP = {selected.Bucp} | CMDPMT = {selected.Cmdpmt} ({selected.RoutingRule})");
            sb.AppendLine($"  • Prime Brute / JCL    : {selected.Premium} €  ->  AMOUNT = {selected.AmountJcl}");
            sb.AppendLine($"  • Date N (Run Date)    : {selected.RunDate} ({selected.RunDateSource})");
            sb.AppendLine($"  • Date N+2 (Consolid.) : {selected.ConsolidationDate} (LVDQFG01 / VNI LV000GD0.LV.GFCRT0)");
            sb.AppendLine($"  • Branche & Fonds      : {selected.BranchDisplay} ({selected.BranchDetectionReason})  |  Fonds : {selected.FundsSummary}");
            sb.AppendLine("------------------------------------------------------------------------------------");
            sb.AppendLine("[1. DÉTAILS D'EXTRACTION]");
            sb.AppendLine($"  Excel Row #          : {selected.RowNum}");
            sb.AppendLine($"  Test ID              : {selected.Test}");
            sb.AppendLine($"  Extraction Status    : {selected.Status}");
            sb.AppendLine($"  Environment          : {selected.Env}");
            sb.AppendLine($"  Tables Extracted     : {selected.TablesExtractedCount}");
            sb.AppendLine($"  Output CSV File      : {(string.IsNullOrWhiteSpace(selected.FilePath) ? "(Merged into Batch CSV in Output folder)" : selected.FilePath)}");
            sb.AppendLine("====================================================================================");

            if (!string.IsNullOrWhiteSpace(selected.FilePath) && File.Exists(selected.FilePath))
            {
                sb.AppendLine();
                sb.AppendLine("EXTRACTED SQL TABLES PREVIEW:");
                sb.AppendLine("------------------------------------------------------------------------------------");
                try
                {
                    var previewLines = ExtractContractSectionLines(selected, maxLines: 160);
                    foreach (string line in previewLines.Lines)
                    {
                        sb.AppendLine(line);
                    }

                    if (previewLines.Truncated)
                    {
                        sb.AppendLine("... (Preview truncated at 160 lines. Open the CSV file to inspect all rows.)");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[Unable to preview CSV file: {ex.Message}]");
                }
            }

            var reportWindow = new DiagnosticReportWindow(
                title: $"Extraction Inspector — {selected.ContractId} ({selected.BranchDisplay})",
                subtitle: $"Excel Row: {selected.RowNum} | Demand: {selected.Hdmd} | LISA ID: {selected.InternalId} | Product: {selected.ProductCode} | Branche: {selected.BranchDisplay}",
                status: selected.Status == "OK" ? "SUCCESS" : "FAILED",
                reportContent: sb.ToString(),
                contractReference: selected.ContractId)
            {
                Owner = Window.GetWindow(this)
            };
            reportWindow.ShowDialog();
        }

        private static (System.Collections.Generic.List<string> Lines, bool Truncated) ExtractContractSectionLines(
            ExtractionItem selected,
            int maxLines)
        {
            var collected = new System.Collections.Generic.List<string>();
            bool truncated = false;
            string cleanSelectedDigits = new string((selected.ContractId ?? string.Empty).Where(char.IsDigit).ToArray());
            string rowMarker = !string.IsNullOrWhiteSpace(selected.RowNum) && selected.RowNum != "-" && selected.RowNum != "1"
                ? $"EXCEL ROW: #{selected.RowNum}"
                : string.Empty;

            bool hasGlobalHeaders = false;
            bool insideMatchingSection = false;

            foreach (string line in File.ReadLines(selected.FilePath))
            {
                if (line.StartsWith("### GLOBAL CONTRACT REPORT:", StringComparison.OrdinalIgnoreCase))
                {
                    hasGlobalHeaders = true;
                    if (insideMatchingSection)
                    {
                        break; // Reached the next contract section
                    }

                    string lineDigits = new string(line.Take(95).Where(char.IsDigit).ToArray());
                    bool matchesContract = !string.IsNullOrWhiteSpace(selected.ContractId) &&
                                           line.Contains(selected.ContractId, StringComparison.OrdinalIgnoreCase);
                    bool matchesDigits = cleanSelectedDigits.Length >= 9 &&
                                         lineDigits.Contains(cleanSelectedDigits, StringComparison.Ordinal);
                    bool matchesRow = !string.IsNullOrEmpty(rowMarker) &&
                                      line.Contains(rowMarker, StringComparison.OrdinalIgnoreCase);

                    if (matchesContract || matchesDigits || matchesRow)
                    {
                        insideMatchingSection = true;
                        collected.Add(new string('=', 80));
                        collected.Add(line);
                    }
                    continue;
                }

                if (insideMatchingSection)
                {
                    if (collected.Count >= maxLines)
                    {
                        truncated = true;
                        break;
                    }
                    collected.Add(line);
                }
            }

            if (!hasGlobalHeaders || collected.Count == 0)
            {
                collected.Clear();
                foreach (string line in File.ReadLines(selected.FilePath))
                {
                    if (collected.Count >= maxLines)
                    {
                        truncated = true;
                        break;
                    }
                    collected.Add(line);
                }
            }

            return (collected, truncated);
        }

        private void MenuExtSendToActivation_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem is not ExtractionItem selected)
            {
                return;
            }

            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                bool hasContract = !string.IsNullOrWhiteSpace(selected.ContractId) &&
                                   selected.ContractId != "-" &&
                                   selected.ContractId != "N/A";
                string target = hasContract ? selected.ContractId : selected.Hdmd;
                mainWindow.NavigateToActivationWithContract(target, !hasContract, selected.Env);
            }
        }

        private void MenuExtCopyContract_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem is ExtractionItem selected &&
                !string.IsNullOrWhiteSpace(selected.ContractId))
            {
                Clipboard.SetText(selected.ContractId);
            }
        }

        private void MenuExtCopyDemand_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem is ExtractionItem selected &&
                !string.IsNullOrWhiteSpace(selected.Hdmd))
            {
                Clipboard.SetText(selected.Hdmd);
            }
        }

        private void MenuExtOpenCsv_Click(object sender, RoutedEventArgs e)
        {
            if (ListHistory.SelectedItem is ExtractionItem selected &&
                !string.IsNullOrWhiteSpace(selected.FilePath) &&
                File.Exists(selected.FilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(selected.FilePath)}\"")
                {
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(Settings.OutputDir))
            {
                Process.Start(new ProcessStartInfo(Path.GetFullPath(Settings.OutputDir))
                {
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }

        // =============================================================
        // FILE & BASELINE CONTROLS
        // =============================================================

        private void TxtExtContract_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtExtDetectedTypeBadge != null)
            {
                TxtExtDetectedTypeBadge.Text = ExcelHelper.DescribeIdentifierType(TxtExtContract?.Text);
            }
        }

        private void TxtExtContract_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnRunSingle?.IsEnabled == true)
            {
                e.Handled = true;
                BtnRunSingle_Click(sender, e);
            }
        }

        private void UpdateBatchExtCsvPath()
        {
            if (TxtBatchExtCsv == null) return;

            string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";
            string channelValue = (CmbExtChannel?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "C01";

            TxtBatchExtCsv.Text = Settings.GetDefaultTestingExcelPath(channelValue, envValue);
        }

        private void BtnUseDefaultQcExtFile_Click(object sender, RoutedEventArgs e) => UpdateBatchExtCsvPath();
        private void CmbExtEnv_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBatchExtCsvPath();
        private void CmbExtChannel_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBatchExtCsvPath();

        private void BtnPreviewExtFile_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtBatchExtCsv?.Text?.Trim() ?? string.Empty;
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
                                                                !Path.GetFileName(f).StartsWith("ActivationHistory_", StringComparison.OrdinalIgnoreCase))
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
                Process.Start("explorer.exe", Settings.BaselineDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Baseline folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =============================================================
        // SINGLE & BATCH EXTRACTION EXECUTION
        // =============================================================

        private async void BtnRunSingle_Click(object sender, RoutedEventArgs e)
        {
            if (!(Window.GetWindow(this) is MainWindow mainWindow)) return;

            string? rawInput = TxtExtContract?.Text.Trim();
            bool isDemandId = ExcelHelper.IsDemandIdInput(rawInput);
            string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";

            if (string.IsNullOrEmpty(rawInput))
            {
                UpdateMainWindowStatus(mainWindow, "Please enter a value.", Brushes.Orange);
                return;
            }

            IProgress<ExtractionItem> progress = new Progress<ExtractionItem>(AddExtractionItemToHistory);

            SetExtractionButtons(running: true);
            UpdateBatchProgressDisplay(0, 5, $"{rawInput} — Initialisation de l'extraction SQL ({envValue}000)...", "STARTING", "EXTRACTION UNITAIRE (ÉTAPES SQL)");

            await mainWindow.RunProcessAsync(async () =>
            {
                try
                {
                    CancellationToken token = mainWindow.GetCancellationToken();

                    UpdateMainWindowStatus(mainWindow, $"Extracting Environment {envValue}000...");
                    bool succeeded = await PerformSingleExtractionAsync(rawInput, $"{envValue}000", progress, isDemandId, mainWindow, token);

                    if (token.IsCancellationRequested)
                    {
                        UpdateBatchProgressDisplay(_batchProcessedCount, 5, $"{rawInput} — Annulé par l'utilisateur.", "CANCELLED", "EXTRACTION UNITAIRE (ÉTAPES SQL)");
                        UpdateMainWindowStatus(mainWindow, "Single extraction cancelled by user.", Brushes.Orange);
                    }
                    else if (succeeded)
                    {
                        UpdateBatchProgressDisplay(5, 5, $"{rawInput} — Extraction complète LISA + ELIA terminée (OK).", "COMPLETED", "EXTRACTION UNITAIRE (ÉTAPES SQL)");
                        UpdateMainWindowStatus(mainWindow, "Single extraction completed successfully.", Brushes.Green);
                    }
                    else
                    {
                        UpdateBatchProgressDisplay(5, 5, $"{rawInput} — Extraction terminée (ELIA ONLY ou contrat absent de LISA).", "ELIA_ONLY", "EXTRACTION UNITAIRE (ÉTAPES SQL)");
                        UpdateMainWindowStatus(mainWindow, "Single extraction finished (ELIA ONLY or contract not active in LISA).", Brushes.DarkOrange);
                    }
                }
                catch (OperationCanceledException)
                {
                    UpdateBatchProgressDisplay(_batchProcessedCount, 5, $"{rawInput} — Annulé par l'utilisateur.", "CANCELLED", "EXTRACTION UNITAIRE (ÉTAPES SQL)");
                    UpdateMainWindowStatus(mainWindow, "Single extraction cancelled by user.", Brushes.Orange);
                }
                finally
                {
                    Dispatcher.Invoke(() => SetExtractionButtons(running: false));
                }
            });
        }

        private async Task<bool> PerformSingleExtractionAsync(string targetValue, string env, IProgress<ExtractionItem> progress, bool isDemandId, MainWindow mainWindow, CancellationToken token)
        {
            try
            {
                ExtractionResult result = await _extractionService.PerformExtractionAsync(
                    targetValue,
                    env,
                    true,
                    isDemandId,
                    token,
                    (step, totalSteps, stepMsg) =>
                    {
                        UpdateBatchProgressDisplay(step, totalSteps, stepMsg, "RUNNING", "EXTRACTION UNITAIRE (ÉTAPES SQL)");
                        UpdateMainWindowStatus(mainWindow, stepMsg, Brushes.RoyalBlue);
                    }).ConfigureAwait(false);
                mainWindow.LastGeneratedPath = !string.IsNullOrEmpty(result.FilePath) ? result.FilePath : Settings.OutputDir;

                string resolvedRef = !string.IsNullOrWhiteSpace(result.ContractReference) && result.ContractReference != "Not found"
                    ? result.ContractReference
                    : targetValue;
                string displayContract = FormatContractForDisplay(resolvedRef);

                bool hasLisa = result.InternalId != "Not found" && result.InternalId != "Error" && !string.IsNullOrWhiteSpace(result.InternalId);
                bool hasElia = result.UconId != "N/A" && result.UconId != "Error" && !string.IsNullOrWhiteSpace(result.UconId);
                string finalStatus = hasLisa ? "OK" : (hasElia ? "ELIA ONLY" : "KO");

                progress.Report(new ExtractionItem
                {
                    RowNum = "Unit",
                    Env = env,
                    ContractId = displayContract,
                    JclContract = string.IsNullOrWhiteSpace(result.JclContract) ? "-" : result.JclContract,
                    InternalId = result.InternalId,
                    Product = string.IsNullOrWhiteSpace(result.ProductCode) ? env : result.ProductCode,
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
                    Premium = string.IsNullOrWhiteSpace(result.Premium) ? "0" : result.Premium,
                    Ucon = result.UconId,
                    Hdmd = result.DemandId,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = "UNIT",
                    Status = finalStatus,
                    TablesExtractedCount = result.TablesExtractedCount,
                    FilePath = result.FilePath
                });

                return hasLisa;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                progress.Report(new ExtractionItem
                {
                    RowNum = "Unit",
                    Env = env,
                    ContractId = targetValue,
                    JclContract = "-",
                    InternalId = "Error",
                    Product = env,
                    ProductCode = "-",
                    ProcessCode = "-",
                    ChannelCode = "-",
                    BusinessChannel = "-",
                    Bucp = "-",
                    Cmdpmt = "-",
                    FundsSummary = "-",
                    IsBranch23 = false,
                    Premium = "0",
                    Ucon = "N/A",
                    Hdmd = "N/A",
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = "UNIT",
                    Status = "KO",
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
            bool isDemandId = true;
            string envValue = (CmbExtEnv?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "D";

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                UpdateMainWindowStatus(mainWindow, "Please select a valid file or verify the network path.", Brushes.Orange);
                return;
            }

            var batchService = new BatchExtractionService(_extractionService);
            UpdateBatchProgressDisplay(0, 0, null, "STARTING");

            IProgress<BatchProgressInfo> progress = new Progress<BatchProgressInfo>(info =>
            {
                UpdateBatchProgressDisplay(info.CurrentItem, info.TotalItems, info.ContractId, "RUNNING");
                mainWindow.TxtStatus.Text =
                    $"Batch extraction: {info.CurrentItem}/{info.TotalItems} | Excel Row {info.RowNum} | {info.ContractId} ({info.BranchDisplay})";

                bool hasLisa = info.InternalId != "Not found" && info.InternalId != "Error" && !string.IsNullOrWhiteSpace(info.InternalId);
                bool hasElia = info.UconId != "N/A" && info.UconId != "Error" && !string.IsNullOrWhiteSpace(info.UconId);
                string status = hasLisa ? "OK" : (hasElia ? "ELIA ONLY" : "KO");

                AddExtractionItemToHistory(new ExtractionItem
                {
                    RowNum = info.RowNum.ToString(CultureInfo.InvariantCulture),
                    Env = string.IsNullOrWhiteSpace(info.Environment) ? $"{envValue}000" : info.Environment,
                    ContractId = FormatContractForDisplay(info.ContractId),
                    JclContract = string.IsNullOrWhiteSpace(info.JclContract) ? "-" : info.JclContract,
                    InternalId = info.InternalId,
                    Product = string.IsNullOrWhiteSpace(info.ProductCode) ? info.Product : info.ProductCode,
                    ProductCode = string.IsNullOrWhiteSpace(info.ProductCode) ? "-" : info.ProductCode,
                    ProcessCode = string.IsNullOrWhiteSpace(info.ProcessCode) ? "-" : info.ProcessCode,
                    ChannelCode = string.IsNullOrWhiteSpace(info.ChannelCode) ? "-" : info.ChannelCode,
                    BusinessChannel = string.IsNullOrWhiteSpace(info.BusinessChannel) ? "-" : info.BusinessChannel,
                    Bucp = string.IsNullOrWhiteSpace(info.Bucp) ? "-" : info.Bucp,
                    Cmdpmt = string.IsNullOrWhiteSpace(info.Cmdpmt) ? "-" : info.Cmdpmt,
                    RoutingRule = string.IsNullOrWhiteSpace(info.RoutingRule) ? "-" : info.RoutingRule,
                    RunDate = string.IsNullOrWhiteSpace(info.RunDate) ? "-" : info.RunDate,
                    RunDateSource = string.IsNullOrWhiteSpace(info.RunDateSource) ? "-" : info.RunDateSource,
                    ConsolidationDate = string.IsNullOrWhiteSpace(info.ConsolidationDate) ? "-" : info.ConsolidationDate,
                    AmountJcl = string.IsNullOrWhiteSpace(info.AmountJcl) ? "-" : info.AmountJcl,
                    BranchDetectionReason = string.IsNullOrWhiteSpace(info.BranchDetectionReason) ? "-" : info.BranchDetectionReason,
                    DemandStatus = string.IsNullOrWhiteSpace(info.DemandStatus) ? "-" : info.DemandStatus,
                    FundsSummary = string.IsNullOrWhiteSpace(info.FundsSummary) ? "-" : info.FundsSummary,
                    IsBranch23 = info.IsBranch23,
                    Premium = info.Premium,
                    Ucon = info.UconId,
                    Hdmd = info.DemandId,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    Test = string.IsNullOrWhiteSpace(info.TestId) ? $"Test{info.CurrentItem}" : info.TestId,
                    Status = status,
                    TablesExtractedCount = info.TablesExtractedCount,
                    FilePath = string.Empty
                });
            });

            SetExtractionButtons(running: true);

            await mainWindow.RunProcessAsync(async () =>
            {
                try
                {
                    CancellationToken token = mainWindow.GetCancellationToken();

                    string actualFile = filePath;
                    UpdateMainWindowStatus(mainWindow, $"Preparing Environment {envValue}000...");

                    if (filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateMainWindowStatus(mainWindow, $"Converting Excel to CSV (preserving 1:1 Excel row numbering) for {envValue}000...");
                        actualFile = await Task.Run(() => PrepareCsvFromExcel(filePath), token).ConfigureAwait(false);
                    }

                    token.ThrowIfCancellationRequested();

                    UpdateMainWindowStatus(mainWindow, $"Launching batch extraction ({envValue}000)...");

                    string mergedFilePath = await batchService.PerformBatchExtractionAsync(actualFile, $"{envValue}000", progress.Report, isDemandId, token).ConfigureAwait(false);

                    mainWindow.LastGeneratedPath = !string.IsNullOrWhiteSpace(mergedFilePath) && File.Exists(mergedFilePath)
                        ? mergedFilePath
                        : Settings.OutputDir;

                    if (token.IsCancellationRequested)
                    {
                        UpdateBatchProgressDisplay(_batchProcessedCount, _batchPlannedCount, null, "CANCELLED");
                        UpdateMainWindowStatus(mainWindow, "Batch extraction cancelled. Partial files saved in Output folder.", Brushes.Orange);
                    }
                    else
                    {
                        UpdateBatchProgressDisplay(_batchPlannedCount, _batchPlannedCount, null, "COMPLETED");
                        UpdateMainWindowStatus(mainWindow, $"Batch extraction completed! Merged snapshot: {Path.GetFileName(mergedFilePath)}", Brushes.Green);
                    }
                }
                catch (OperationCanceledException)
                {
                    mainWindow.LastGeneratedPath = Settings.OutputDir;
                    UpdateBatchProgressDisplay(_batchProcessedCount, _batchPlannedCount, null, "CANCELLED");
                    UpdateMainWindowStatus(mainWindow, "Batch extraction cancelled by user.", Brushes.Orange);
                }
                catch (Exception ex)
                {
                    UpdateBatchProgressDisplay(_batchProcessedCount, _batchPlannedCount, null, "FAILED");
                    UpdateMainWindowStatus(mainWindow, $"Batch Error: {ex.Message}", Brushes.Red);
                    throw;
                }
                finally
                {
                    Dispatcher.Invoke(() => SetExtractionButtons(running: false));
                }
            });
        }

        private void SetExtractionButtons(bool running)
        {
            BtnCancelSingleExt.IsEnabled = running;
            BtnRunSingle.IsEnabled = !running;
            BtnRunBatch.IsEnabled = !running;
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            ExtractionHistory?.Clear();
            _koExtractionCount = 0;
            _okExtractionCount = 0;
            _branch21Count = 0;
            _branch23Count = 0;
            _totalExtractionCount = 0;
            _batchPlannedCount = 0;
            _batchProcessedCount = 0;
            if (BannerExtractionResult != null)
            {
                BannerExtractionResult.Visibility = Visibility.Collapsed;
            }
            UpdateKpiCounters();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
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
