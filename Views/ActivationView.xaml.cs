using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;
using AutoActivator.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoActivator.Gui.Views
{
    public partial class ActivationView : UserControl
    {
        private readonly ActivationDataService _activationDataService;
        private readonly IActivationRoutingService _activationRoutingService;
        private CancellationTokenSource? _cts;
        private AutomaticActivationPlan? _lastAnalyzedPlan;

        public ObservableCollection<ActivationHistoryItem> ActivationHistory { get; } =
            new ObservableCollection<ActivationHistoryItem>();

        private readonly HashSet<string> _historyTraceKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _totalCount;
        private int _successCount;
        private int _alreadyActiveCount;
        private int _failCount;
        private int _batchPlannedCount;
        private int _batchProcessedCount;

        public ActivationView()
        {
            InitializeComponent();

            _activationDataService = new ActivationDataService();
            _activationRoutingService = new ActivationRoutingService();
            ListActivationHistory.ItemsSource = ActivationHistory;
            ApplyActivationInputModeState();

            if (RbBatchActSearchDemand.IsChecked == true)
            {
                UpdateBatchActCsvPath();
            }

            UpdateSingleActivationControls();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
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
        // HISTORIQUE
        // =============================================================

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

            string statusDisplay = trace.FinalStatus switch
            {
                "SUCCESS" => "SUCCESS",
                "ALREADY_ACTIVE" => "ALREADY ACTIVE",
                "CANCELLED" => "CANCELLED",
                "PENDING_ASYNC_LISA_CREATION" =>
                    "PENDING ASYNC LISA CREATION",
                _ => "FAILED"
            };

            string diagnosticSummary = statusDisplay == "SUCCESS"
                ? trace.StepsSummary
                : !string.IsNullOrWhiteSpace(trace.FailureReason)
                    ? $"{trace.FailureStage}: {trace.FailureReason}"
                    : trace.StepsSummary;

            string environment = NormalizeShortEnvironment(trace.Environment);
            string productCode = string.IsNullOrWhiteSpace(trace.ProductCode)
                ? "-"
                : trace.ProductCode.Trim();
            string processCode = !string.IsNullOrWhiteSpace(trace.ProcessCode)
                ? trace.ProcessCode.Trim()
                : !string.IsNullOrWhiteSpace(trace.Channel)
                    ? trace.Channel.Trim()
                    : "-";
            string contractId = !IsMissingValue(trace.ResolvedContract)
                ? trace.ResolvedContract
                : trace.RawInput;

            var item = new ActivationHistoryItem
            {
                RowNum = trace.RowNum > 0
                    ? trace.RowNum.ToString(CultureInfo.InvariantCulture)
                    : "-",
                Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Env = environment + "000",
                InputId = trace.RawInput,
                ContractId = contractId,
                JclContract = IsMissingValue(trace.JclContract) ? "-" : trace.JclContract,
                ProductCode = productCode,
                ProcessCode = processCode,
                ChannelCode = Display(trace.EliaChannelCode),
                BusinessChannel = ResolveBusinessChannelDisplay(trace),
                Bucp = Display(trace.Bucp),
                Cmdpmt = Display(trace.Cmdpmt),
                RunDate = Display(trace.RunDate),
                RoutingRule = Display(trace.RoutingRule),
                Premium = NormalizePremiumDisplay(trace.RawPremium),
                IsBranch23 = trace.IsBranch23,
                StepsInfo = trace.StepsSummary,
                Duration = $"{trace.TotalDurationMs / 1000.0:F1}s",
                Status = statusDisplay,
                DiagnosticSummary = diagnosticSummary,
                FullTrace = trace
            };

            _totalCount++;

            switch (statusDisplay)
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

            if (statusDisplay is "FAILED" or "CANCELLED")
            {
                ActivationHistory.Insert(0, item);
            }
            else
            {
                ActivationHistory.Add(item);
            }
        }

        private static string BuildTraceKey(ContractActivationTrace trace)
        {
            return string.Join(
                "|",
                trace.RowNum.ToString(CultureInfo.InvariantCulture),
                trace.RawInput ?? string.Empty,
                trace.DemandId ?? string.Empty,
                trace.ResolvedContract ?? string.Empty,
                trace.FinalStatus ?? string.Empty);
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
            _batchPlannedCount = 0;
            _batchProcessedCount = 0;

            UpdateHistoryCounters();
            UpdateBatchProgressDisplay(0, 0, null, "READY");
        }

        private void ListActivationHistory_MouseDoubleClick(
            object sender,
            MouseButtonEventArgs e)
        {
            if (ListActivationHistory.SelectedItem is not ActivationHistoryItem selected ||
                selected.FullTrace == null)
            {
                return;
            }

            string fullDetails =
                _activationDataService.FormatSingleActivationReport(selected.FullTrace);

            var reportWindow = new DiagnosticReportWindow(
                title: $"Activation Diagnostic — {selected.ContractId}",
                subtitle: $"Demand: {selected.FullTrace.DemandId} | Product: {selected.FullTrace.ProductCode} | Process: {selected.FullTrace.ProcessCode} | Routing: {selected.RoutingRule}",
                status: selected.Status,
                reportContent: fullDetails,
                contractReference: selected.ContractId)
            {
                Owner = Window.GetWindow(this)
            };
            reportWindow.ShowDialog();
        }

        // =============================================================
        // FICHIER BATCH
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

        private void UpdateBatchActCsvPath()
        {
            if (TxtBatchActCsv == null || RbBatchActSearchDemand == null)
            {
                return;
            }

            if (RbBatchActSearchDemand.IsChecked == true)
            {
                string environment = GetSelectedEnvironment();
                string process = GetSelectedProcess();
                TxtBatchActCsv.Text =
                    Settings.GetDefaultTestingExcelPath(process, environment);
            }
            else if (RbBatchActSearchContract?.IsChecked == true)
            {
                TxtBatchActCsv.Text = string.Empty;
            }
        }

        private void BatchActInputType_Checked(object sender, RoutedEventArgs e)
        {
            UpdateBatchActCsvPath();
        }

        // =============================================================
        // CONTROLES D'ENTREE
        // =============================================================

        private void ActInputType_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtActContract != null)
            {
                TxtActContract.Text = string.Empty;
            }

            UpdateSingleActivationControls();
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

        // =============================================================
        // MODE DE RESOLUTION ET PREANALYSE SQL
        // =============================================================

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
            if (ManualConfigurationPanel == null || AutomaticRoutingPanel == null)
            {
                return;
            }

            ActivationInputMode mode = SelectedActivationInputMode;
            ManualConfigurationPanel.Visibility = mode.AllowsManualOverrides()
                ? Visibility.Visible
                : Visibility.Collapsed;
            AutomaticRoutingPanel.Visibility = mode.UsesSqlAutocomplete()
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (BtnAnalyzeActivation != null)
            {
                BtnAnalyzeActivation.IsEnabled = mode.UsesSqlAutocomplete();
            }

            _lastAnalyzedPlan = null;
            if (TxtAutomaticRoutingPreview != null && mode.UsesSqlAutocomplete())
            {
                TxtAutomaticRoutingPreview.Text =
                    "Optional preflight. You can run the activation directly.";
            }
        }

        private async void BtnAnalyzeActivation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SingleActivationInput input = await ReadSingleActivationInputAsync();
                ValidateSingleInput(input.RawInput);
                BtnAnalyzeActivation.IsEnabled = false;
                TxtAutomaticRoutingPreview.Text = "Resolving SQL activation data...";

                AutomaticActivationPlan plan =
                    await _activationDataService.ResolveAutomaticActivationPlanAsync(
                        input.RawInput,
                        input.IsDemandId,
                        input.Environment + "000",
                        input.Cus,
                        input.ForceBranch23,
                        input.InputMode,
                        BuildUiOverrides(input),
                        CancellationToken.None);

                _lastAnalyzedPlan = plan;
                TxtAutomaticRoutingPreview.Text = plan.ToPreviewText();
            }
            catch (Exception exception)
            {
                TxtAutomaticRoutingPreview.Text = "BLOCKED: " + exception.Message;
                MessageBox.Show(
                    exception.Message,
                    "SQL Activation Analysis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                BtnAnalyzeActivation.IsEnabled =
                    SelectedActivationInputMode.UsesSqlAutocomplete();
            }
        }

        private static IReadOnlyDictionary<string, string> BuildUiOverrides(
            SingleActivationInput input)
        {
            var overrides = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            if (!input.InputMode.AllowsManualOverrides())
            {
                return overrides;
            }

            AddOverride(overrides, "BUCP", input.Bucp);
            AddOverride(overrides, "CMDPMT", input.Cmdpmt);
            AddOverride(overrides, "PROCESS", input.Process);
            AddOverride(overrides, "CHANNEL", input.ChannelCodeOverride);
            return overrides;
        }

        private static void AddOverride(
            IDictionary<string, string> overrides,
            string key,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                overrides[key] = value.Trim();
            }
        }

        private ActivationRoutingResult ResolveInputRouting(
            SingleActivationInput input,
            string? sqlChannelCode)
        {
            return _activationRoutingService.Resolve(
                input.InputMode == ActivationInputMode.Manual
                    ? input.ChannelCodeOverride
                    : sqlChannelCode,
                input.Bucp,
                input.Cmdpmt,
                input.InputMode);
        }

        // =============================================================
        // ANNULATION
        // =============================================================

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

        // =============================================================
        // PRESETS DE PAIEMENT
        // =============================================================

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
            else if (sender == RbPresetBrokerNew)
            {
                SelectComboBoxItemByContent(CmbActBucp, "80819");
                SelectComboBoxItemByContent(CmbActCmdpmt, "H");
            }
        }

        private void BtnClearPreset_Click(object sender, RoutedEventArgs e)
        {
            if (RbPresetBank != null)
            {
                RbPresetBank.IsChecked = false;
            }

            if (RbPresetBrokerOld != null)
            {
                RbPresetBrokerOld.IsChecked = false;
            }

            if (RbPresetBrokerNew != null)
            {
                RbPresetBrokerNew.IsChecked = false;
            }
        }

        private static void SelectComboBoxItemByContent(
            ComboBox comboBox,
            string content)
        {
            foreach (object entry in comboBox.Items)
            {
                if (entry is not ComboBoxItem item)
                {
                    continue;
                }

                if (!string.Equals(
                        item.Content?.ToString(),
                        content,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                comboBox.SelectedItem = item;
                break;
            }
        }

        // =============================================================
        // ACTIVATION UNITAIRE
        // =============================================================

        private void BtnRunSingleActivation_Click(object sender, RoutedEventArgs e)
        {
            RunSingleActivation(skipPrime: false);
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
                    RowNum = _totalCount + 1,
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

                    await SetStatusAsync(
                            mainWindow,
                            $"Resolving ELIA routing, contract and premium in " +
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

                    IReadOnlyList<ActivationPlanIssue> blockingIssues = plan
                        .Validate()
                        .Where(issue => issue.IsBlocking)
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

                    if (plan.ResumeStrategy == ActivationResumeStrategy.AlreadyActive ||
                        plan.IsAlreadyActive)
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
                        trace.Branch23Source = "Standard or unresolved branch";
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
                            message => SetStatusWithoutAwait(mainWindow, message),
                            _cts.Token,
                            trace,
                            isBranch23,
                            branch23FundSource,
                            branch23ActivationOnly:
                                Settings.Branch23Activation.Branch23ActivationOnly)
                        .ConfigureAwait(false);

                    string successfulStatus = string.Equals(
                            trace.LisaInternalId,
                            "PENDING_ASYNC_LISA_CREATION",
                            StringComparison.OrdinalIgnoreCase)
                        ? "PENDING_ASYNC_LISA_CREATION"
                        : "SUCCESS";
                    CompleteTrace(trace, stopwatch, successfulStatus);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        mainWindow.TxtStatus.Text =
                            $"Activation completed in " +
                            $"{trace.TotalDurationMs / 1000.0:F1}s for " +
                            $"{resolvedContract}. {trace.StepsSummary}";
                        mainWindow.TxtStatus.Foreground = Brushes.DarkGreen;

                        MessageBox.Show(
                            $"Contract {resolvedContract} " +
                            $"(JCL: {formattedContract}) was activated successfully.\n\n" +
                            $"Product: {Display(lookup.ProductCode)}\n" +
                            $"Process: {effectiveProcess}\n" +
                            $"Branch: {lookup.BranchClassification}\n" +
                            $"BR23 workflow: {(isBranch23 ? "YES" : "NO")}\n" +
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
        // ROUTING ET TRACE
        // =============================================================

        private static void PopulateTraceFromPlan(
            ContractActivationTrace trace,
            AutomaticActivationPlan plan)
        {
            trace.RawInput = plan.InputValue;
            trace.IsDemandId = plan.InputIsDemandId;
            trace.Environment = NormalizeShortEnvironment(plan.Environment);
            trace.ResolvedContract = plan.ContractNumber;
            trace.JclContract = plan.MainframeContractNumber;
            trace.EliaUconId = plan.EliaId;
            trace.DemandId = plan.DemandId;
            trace.ProductCode = plan.ProductCode;
            trace.ProcessCode = plan.ProcessCode;
            trace.Channel = plan.ProcessCode;
            trace.EliaChannelCode = plan.ChannelCode;
            trace.ApplicationChannelCode = plan.ApplicationChannel;
            trace.DemandStatus = plan.DemandStatus;
            trace.RawPremium = plan.GrossPremium.ToString(
                "0.00",
                CultureInfo.InvariantCulture);
            trace.NetPremium = plan.NetPremium?.ToString(
                "0.00",
                CultureInfo.InvariantCulture) ?? string.Empty;
            trace.PaymentMethodCode = plan.EliaPaymentCode;
            trace.PaymentChannel = plan.PaymentChannel;
            trace.BusinessChannel = plan.ResolvedChannel;
            trace.Bucp = plan.Bucp;
            trace.Cmdpmt = plan.PaymentCommand;
            trace.RoutingRule = plan.RoutingRule;
            trace.RoutingStatus = PreActivationRoutingStatus.Ready;
            trace.RoutingDiagnostic = plan.Diagnostic;
            trace.RunDate = plan.Drun;
            trace.RunDateSource = plan.RunDateSource;
            trace.IsBranch23 = plan.IsBranch23;
            trace.BranchClassification = plan.IsBranch23
                ? ContractBranchClassification.Branch23
                : ContractBranchClassification.Standard;
            trace.Branch23Source = plan.ForceBranch23
                ? "Manual UI override"
                : "Automatic SQL activation plan";
        }

        private static ActivationLookupResult CreateLookupFromPlan(
            AutomaticActivationPlan plan)
        {
            return new ActivationLookupResult
            {
                ContractNumber = plan.ContractNumber,
                JclContract = plan.MainframeContractNumber,
                EliaUconId = plan.EliaId,
                DemandId = plan.DemandId,
                ResolvedEnvironment = plan.Environment,
                ProductCode = plan.ProductCode,
                ProcessCode = plan.ProcessCode,
                ChannelCode = plan.ChannelCode,
                ApplicationChannelCode = plan.ApplicationChannel,
                DemandStatus = plan.DemandStatus,
                RawPremium = plan.GrossPremium.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture),
                NetPremium = plan.NetPremium?.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture) ?? string.Empty,
                PaymentMethodCode = plan.EliaPaymentCode,
                PaymentChannel = plan.PaymentChannel,
                BusinessChannel = plan.ResolvedChannel,
                Bucp = plan.Bucp,
                Cmdpmt = plan.PaymentCommand,
                RoutingRule = plan.RoutingRule,
                RunDate = plan.Drun,
                RunDateSource = plan.RunDateSource,
                BranchClassification = plan.IsBranch23
                    ? ContractBranchClassification.Branch23
                    : ContractBranchClassification.Standard,
                RoutingStatus = PreActivationRoutingStatus.Ready,
                RoutingDiagnostic = plan.Diagnostic
            };
        }

        private static string ResolveBusinessChannelDisplay(
            ContractActivationTrace trace)
        {
            if (!string.IsNullOrWhiteSpace(trace.BusinessChannel))
            {
                return trace.BusinessChannel.Trim().ToUpperInvariant();
            }

            return PreActivationRoutingInfo.ResolveBusinessChannel(
                trace.EliaChannelCode) switch
            {
                PreActivationBusinessChannel.Bank => "BANK",
                PreActivationBusinessChannel.BrokerNewGeneration =>
                    "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }

        private static void PopulateTraceFromLookup(
            ContractActivationTrace trace,
            ActivationLookupResult lookup)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(lookup);

            trace.DbLookupDurationMs = lookup.LookupDurationMs;
            trace.DbDiagnosticStatus = lookup.DbDiagnosticStatus;
            trace.DbError = lookup.DbError;
            trace.EliaUconId = lookup.EliaUconId;
            trace.DemandId = lookup.DemandId;
            trace.LisaInternalId = lookup.LisaInternalId;
            trace.LisaContractNumber = lookup.LisaContractNumber;
            trace.ResolvedContract = lookup.ContractNumber;
            trace.JclContract = lookup.JclContract;

            if (!string.IsNullOrWhiteSpace(lookup.ResolvedEnvironment))
            {
                string environment =
                    NormalizeShortEnvironment(lookup.ResolvedEnvironment);

                trace.Environment = environment;
                trace.EnvIms = environment == "D" ? "T" : "C";
                trace.Q2Region = environment == "D" ? "Q2T" : "Q2C";
                trace.FastCtrlDsn = environment == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL";
            }

            trace.ProductCode = lookup.ProductCode;
            trace.ProcessCode = lookup.ProcessCode;
            trace.EliaChannelCode = lookup.ChannelCode;
            trace.ApplicationChannelCode = lookup.ApplicationChannelCode;
            trace.DemandStatus = lookup.DemandStatus;

            if (PreActivationRoutingService.IsSupportedActivationProcess(
                    lookup.ProcessCode))
            {
                trace.Channel = lookup.ProcessCode.Trim().ToUpperInvariant();
            }

            trace.PremiumSequence = lookup.PremiumSequence;
            trace.RawPremium = lookup.RawPremium;
            trace.NetPremium = lookup.NetPremium;
            trace.PaymentMethodCode = lookup.PaymentMethodCode;
            trace.PremiumGroupType = lookup.PremiumGroupType;
            trace.FundFeatureCount = lookup.FundFeatureCount;
            trace.BranchClassification = lookup.BranchClassification;
            trace.BranchDetectionSource = lookup.BranchDetectionSource;
            trace.BranchDetectionReason = lookup.BranchDetectionReason;

            if (lookup.BranchClassification ==
                ContractBranchClassification.Branch23)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source =
                    $"{lookup.BranchDetectionSource}: " +
                    lookup.BranchDetectionReason;
            }
            else if (lookup.BranchClassification ==
                     ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source =
                    $"{lookup.BranchDetectionSource}: " +
                    lookup.BranchDetectionReason;
            }

            trace.PaymentChannel = lookup.PaymentChannel;
            trace.BusinessChannel = lookup.ResolvedBusinessChannel;
            trace.Bucp = lookup.Bucp;
            trace.Cmdpmt = lookup.Cmdpmt;
            trace.RoutingRule = lookup.RoutingRule;
            trace.RoutingSource = lookup.RoutingSource;
            trace.RunDate = lookup.RunDate;
            trace.RunDateSource = lookup.RunDateSource;
            trace.RoutingStatus = lookup.RoutingStatus;
            trace.RoutingDiagnostic = lookup.RoutingDiagnostic;
        }

        private static void PopulateInitialTrace(
            ContractActivationTrace trace,
            SingleActivationInput input,
            bool skipPrime)
        {
            trace.RawInput = input.RawInput;
            trace.IsDemandId = input.IsDemandId;
            trace.Environment = input.Environment;
            trace.EnvIms = input.Environment == "D" ? "T" : "C";
            trace.Q2Region = input.Environment == "D" ? "Q2T" : "Q2C";
            trace.FastCtrlDsn = input.Environment == "D"
                ? "I0T.DB.CA.FIB.FASTCTRL"
                : "I10.DB.CA.FIB.FASTCTRL";
            trace.Channel = input.Process;
            trace.ProcessCode = input.Process;
            trace.Cus = input.Cus;
            trace.Bucp = string.IsNullOrWhiteSpace(input.Bucp)
                ? string.Empty
                : input.Bucp.Trim().PadLeft(5, '0');
            trace.Cmdpmt = input.Cmdpmt;
            trace.SkipPrime = skipPrime;
            trace.Username = input.Username;
            trace.IsBranch23 = input.ForceBranch23;
            trace.Branch23Source = input.ForceBranch23
                ? "Manual UI override"
                : "Pending ELIA routing";
        }

        private static void ValidateLookupResult(
            ActivationLookupResult lookup,
            SingleActivationInput input,
            ContractActivationTrace trace)
        {
            if (input.IsDemandId &&
                string.IsNullOrWhiteSpace(lookup.ContractNumber))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage =
                    lookup.RoutingStatus ==
                    PreActivationRoutingStatus.EliaContractNotLinked
                        ? "ELIA_CONTRACT_NOT_LINKED"
                        : "DB_DEMAND_LOOKUP";
                trace.FailureReason =
                    $"No contract is associated with Demand ID " +
                    $"'{input.RawInput}' in {input.Environment}000.";
                trace.RootCauseAnalysis =
                    "The Demand ID was not found, or ELIA has not yet " +
                    "linked the Demand to a UCON contract.";
                trace.RecommendedAction =
                    $"Verify Demand ID {input.RawInput} in " +
                    $"{input.Environment}000 and confirm that proposal " +
                    "creation has completed.";

                throw new InvalidOperationException(trace.FailureReason);
            }

            if (string.IsNullOrWhiteSpace(lookup.ContractNumber))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = "DB_CONTRACT_NOT_FOUND";
                trace.FailureReason =
                    $"Contract '{input.RawInput}' was not found in " +
                    $"{input.Environment}000.";
                trace.RootCauseAnalysis =
                    "No matching ELIA contract reference was found.";
                trace.RecommendedAction =
                    "Verify the contract format and selected environment.";

                throw new InvalidOperationException(trace.FailureReason);
            }

            if (string.IsNullOrWhiteSpace(lookup.JclContract))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = "JCL_CONTRACT_NOT_RESOLVED";
                trace.FailureReason =
                    "The JCL contract identifier could not be generated.";
                trace.RootCauseAnalysis =
                    "The resolved contract number does not match the " +
                    "expected format.";
                trace.RecommendedAction =
                    "Verify the ELIA contract number before activation.";

                throw new InvalidOperationException(trace.FailureReason);
            }

            if (input.IsDemandId &&
                lookup.RoutingStatus ==
                PreActivationRoutingStatus.EnvironmentAmbiguous)
            {
                throw new InvalidOperationException(
                    "The Demand ID exists in multiple environments. " +
                    "Select and validate the environment explicitly.");
            }
        }

        private static string ResolveEffectiveProcess(
            string manuallySelectedProcess,
            string? routedProcess)
        {
            if (PreActivationRoutingService.IsSupportedActivationProcess(
                    routedProcess))
            {
                return routedProcess!.Trim().ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(manuallySelectedProcess)
                ? "C01"
                : manuallySelectedProcess.Trim().ToUpperInvariant();
        }

        private static string ResolveEffectiveEnvironment(
            string selectedEnvironment,
            string? routedEnvironment)
        {
            return !string.IsNullOrWhiteSpace(routedEnvironment)
                ? NormalizeShortEnvironment(routedEnvironment)
                : NormalizeShortEnvironment(selectedEnvironment);
        }

        private static string BuildRoutingStatusMessage(
            ActivationLookupResult lookup,
            string process,
            bool isBranch23)
        {
            return
                $"ELIA routing resolved. " +
                $"Contract={Display(lookup.ContractNumber)}, " +
                $"Product={Display(lookup.ProductCode)}, " +
                $"Process={process}, " +
                $"Channel={Display(lookup.ChannelCode)}, " +
                $"Branch={lookup.BranchClassification}, " +
                $"BR23={(isBranch23 ? "YES" : "NO")}.";
        }

        // =============================================================
        // RAPPORT UNITAIRE
        // =============================================================

        private string SaveSingleActivationReport(
            ContractActivationTrace trace,
            bool skipPrime)
        {
            Directory.CreateDirectory(Settings.OutputDir);

            string skipSuffix = skipPrime ? "_SKIP_PRIME" : string.Empty;
            string safeContract = SanitizeFileNamePart(
                IsMissingValue(trace.JclContract)
                    ? "Unknown"
                    : trace.JclContract);

            string path = Path.Combine(
                Settings.OutputDir,
                $"Single_Activation_{safeContract}{skipSuffix}_" +
                $"{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            string detailedReport =
                _activationDataService.FormatSingleActivationReport(trace);

            File.WriteAllText(
                path,
                detailedReport,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return path;
        }

        // =============================================================
        // PROGRESSION BATCH
        // =============================================================

        private void UpdateBatchProgressDisplay(
            int processed,
            int total,
            string? currentContract,
            string? status = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => UpdateBatchProgressDisplay(
                    processed,
                    total,
                    currentContract,
                    status));
                return;
            }

            _batchProcessedCount = Math.Max(0, processed);
            _batchPlannedCount = Math.Max(0, total);

            if (_batchProcessedCount > _batchPlannedCount)
            {
                _batchProcessedCount = _batchPlannedCount;
            }

            int remaining = Math.Max(
                0,
                _batchPlannedCount - _batchProcessedCount);

            double percentage = _batchPlannedCount > 0
                ? (double)_batchProcessedCount / _batchPlannedCount * 100.0
                : 0.0;

            if (TxtBatchProcessedCount != null)
            {
                TxtBatchProcessedCount.Text =
                    _batchProcessedCount.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtBatchPlannedCount != null)
            {
                TxtBatchPlannedCount.Text =
                    _batchPlannedCount.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtBatchRemainingCount != null)
            {
                TxtBatchRemainingCount.Text = remaining.ToString(CultureInfo.InvariantCulture);
            }

            if (TxtBatchCurrentContract != null)
            {
                TxtBatchCurrentContract.Text = string.IsNullOrWhiteSpace(currentContract)
                    ? "-"
                    : currentContract;
            }

            if (TxtBatchProgressPercentage != null)
            {
                TxtBatchProgressPercentage.Text = $"{percentage:F0}%";
            }

            string normalizedStatus = string.IsNullOrWhiteSpace(status)
                ? "RUNNING"
                : status.Trim().ToUpperInvariant();

            if (TxtBatchProgressStatus != null)
            {
                TxtBatchProgressStatus.Text = normalizedStatus;
                TxtBatchProgressStatus.Foreground = normalizedStatus switch
                {
                    "COMPLETED" => Brushes.DarkGreen,
                    "CANCELLED" => Brushes.DarkOrange,
                    "FAILED" => Brushes.DarkRed,
                    "READY" => Brushes.SlateGray,
                    _ => Brushes.RoyalBlue
                };
            }

            if (ProgressBatchActivation != null)
            {
                ProgressBatchActivation.Minimum = 0;
                ProgressBatchActivation.Maximum = Math.Max(1, _batchPlannedCount);
                ProgressBatchActivation.Value = Math.Min(
                    _batchProcessedCount,
                    _batchPlannedCount);
                ProgressBatchActivation.Foreground = normalizedStatus switch
                {
                    "COMPLETED" => Brushes.MediumSeaGreen,
                    "CANCELLED" => Brushes.DarkOrange,
                    "FAILED" => Brushes.IndianRed,
                    _ => Brushes.RoyalBlue
                };
            }
        }

        private void HandleBatchProgress(
            MainWindow mainWindow,
            BatchProgressInfo progress)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(
                    () => HandleBatchProgress(mainWindow, progress));
                return;
            }

            string currentIdentifier = !IsMissingValue(progress.ContractId)
                ? progress.ContractId
                : !IsMissingValue(progress.DemandId)
                    ? progress.DemandId
                    : "-";

            UpdateBatchProgressDisplay(
                progress.CurrentItem,
                progress.TotalItems,
                currentIdentifier,
                progress.Status);

            int remaining = Math.Max(
                0,
                progress.TotalItems - progress.CurrentItem);

            string product = string.IsNullOrWhiteSpace(progress.Product)
                ? "-"
                : progress.Product;
            string process = string.IsNullOrWhiteSpace(progress.ProcessCode)
                ? "-"
                : progress.ProcessCode;

            mainWindow.TxtStatus.Text =
                $"Batch progress: {progress.CurrentItem}/" +
                $"{progress.TotalItems} completed, {remaining} remaining. " +
                $"Current={currentIdentifier}, Product={product}, " +
                $"Process={process}, " +
                $"BR23={(progress.IsBranch23 ? "YES" : "NO")}.";
            mainWindow.TxtStatus.Foreground = Brushes.RoyalBlue;
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
                                "Converting Excel file to local CSV.")
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
                            input.InputMode)
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

        // =============================================================
        // LECTURE DE L'INTERFACE
        // =============================================================

        private Task<SingleActivationInput> ReadSingleActivationInputAsync()
        {
            return Dispatcher.InvokeAsync(() => new SingleActivationInput
            {
                RawInput = TxtActContract.Text.Trim(),
                IsDemandId = RbActSearchDemand?.IsChecked == true,
                ForceBranch23 = ChkActBranch23?.IsChecked == true,
                Branch23Funds = TxtActBranch23Funds?.Text ?? string.Empty,
                Environment = GetSelectedEnvironment(),
                Cus = TxtActCus.Text.Trim(),
                InputMode = SelectedActivationInputMode,
                Bucp = SelectedActivationInputMode.AllowsManualOverrides()
                    ? GetSelectedComboBoxContent(CmbActBucp, string.Empty)
                    : string.Empty,
                Cmdpmt = SelectedActivationInputMode.AllowsManualOverrides()
                    ? GetSelectedComboBoxContent(CmbActCmdpmt, string.Empty)
                    : string.Empty,
                ChannelCodeOverride = string.Empty,
                Process = GetSelectedProcess(),
                Username = Settings.DbConfig.Uid,
                Password = Settings.DbConfig.Pwd
            }).Task;
        }

        private Task<BatchActivationInput> ReadBatchActivationInputAsync()
        {
            return Dispatcher.InvokeAsync(() => new BatchActivationInput
            {
                FilePath = TxtBatchActCsv.Text.Trim(),
                IsDemandId = RbBatchActSearchDemand?.IsChecked == true,
                MarkAllAsBranch23 = ChkActBranch23?.IsChecked == true,
                DefaultBranch23Funds =
                    TxtActBranch23Funds?.Text ?? string.Empty,
                Environment = GetSelectedEnvironment(),
                Cus = TxtActCus.Text.Trim(),
                InputMode = SelectedActivationInputMode,
                Bucp = SelectedActivationInputMode.AllowsManualOverrides()
                    ? GetSelectedComboBoxContent(CmbActBucp, string.Empty)
                    : string.Empty,
                Cmdpmt = SelectedActivationInputMode.AllowsManualOverrides()
                    ? GetSelectedComboBoxContent(CmbActCmdpmt, string.Empty)
                    : string.Empty,
                ChannelCodeOverride = string.Empty,
                Process = GetSelectedProcess(),
                Username = Settings.DbConfig.Uid,
                Password = Settings.DbConfig.Pwd
            }).Task;
        }

        private string GetSelectedEnvironment()
        {
            if (CmbActEnv?.SelectedItem is ComboBoxItem item)
            {
                return NormalizeShortEnvironment(item.Tag?.ToString());
            }

            return "Q";
        }

        private string GetSelectedProcess()
        {
            if (CmbActChannel?.SelectedItem is ComboBoxItem item)
            {
                string? value = item.Tag?.ToString();
                return string.IsNullOrWhiteSpace(value)
                    ? "C01"
                    : value.Trim().ToUpperInvariant();
            }

            return "C01";
        }

        private static string GetSelectedComboBoxContent(
            ComboBox comboBox,
            string defaultValue)
        {
            if (comboBox.SelectedItem is ComboBoxItem item)
            {
                string? value = item.Content?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            if (comboBox.IsEditable && !string.IsNullOrWhiteSpace(comboBox.Text))
            {
                return comboBox.Text.Trim();
            }

            return defaultValue;
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private void ResetCancellationToken()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }

        private static void ValidateCredentials(
            string username,
            string password)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "Credentials are not configured. Please log in again.");
            }
        }

        private static void ValidateSingleInput(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                throw new InvalidOperationException(
                    "Please enter a contract number or Demand ID.");
            }
        }

        private static IBranch23FundSource? BuildBranch23FundSource(
            bool isBranch23,
            string? rawFunds)
        {
            if (!isBranch23)
            {
                return null;
            }

            IReadOnlyList<string> funds =
                ConfiguredBranch23FundSource.Parse(rawFunds);

            return new ConfiguredBranch23FundSource(funds);
        }

        private static void CompleteTrace(
            ContractActivationTrace trace,
            Stopwatch stopwatch,
            string finalStatus)
        {
            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }

            trace.EndTime = DateTime.Now;
            trace.TotalDurationMs = stopwatch.ElapsedMilliseconds;
            trace.FinalStatus = finalStatus;
        }

        private static string NormalizePremium(string? value)
        {
            string normalized = (value ?? "0")
                .Replace(",", ".")
                .Trim();

            if (!decimal.TryParse(
                    normalized,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out decimal amount))
            {
                return "0";
            }

            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string NormalizePremiumDisplay(string? value)
        {
            return NormalizePremium(value) + " €";
        }

        private static string NormalizeShortEnvironment(string? environment)
        {
            string value = (environment ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (value.StartsWith("D", StringComparison.Ordinal))
            {
                return "D";
            }

            if (value.StartsWith("Q", StringComparison.Ordinal))
            {
                return "Q";
            }

            return "D";
        }

        private static bool IsMissingValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(
                       value.Trim(),
                       "N/A",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       value.Trim(),
                       "Not found",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       value.Trim(),
                       "Not resolved",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string Display(string? value)
        {
            return IsMissingValue(value) ? "-" : value!.Trim();
        }

        private static string SanitizeFileNamePart(string value)
        {
            string result = value;

            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
        }

        private static Task SetStatusAsync(
            MainWindow mainWindow,
            string message)
        {
            return Application.Current.Dispatcher.InvokeAsync(() =>
            {
                mainWindow.TxtStatus.Text = message;
            }).Task;
        }

        private static void SetStatusWithoutAwait(
            MainWindow mainWindow,
            string message)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                mainWindow.TxtStatus.Text = message;
            });
        }

        private void SetSingleActivationButtons(bool running)
        {
            BtnCancelSingleAct.IsEnabled = running;
            BtnRunSingleAct.IsEnabled = !running;
            BtnSkipPrimeSingleAct.IsEnabled = !running;
        }

        private void SetBatchActivationButtons(bool running)
        {
            BtnCancelBatchAct.IsEnabled = running;
            BtnRunBatchAct.IsEnabled = !running;
            BtnSkipPrimeBatchAct.IsEnabled = !running;
        }

        // =============================================================
        // MODELES INTERNES DE L'INTERFACE
        // =============================================================

        private sealed class SingleActivationInput
        {
            public string RawInput { get; init; } = string.Empty;
            public bool IsDemandId { get; init; }
            public bool ForceBranch23 { get; init; }
            public string Branch23Funds { get; init; } = string.Empty;
            public ActivationInputMode InputMode { get; init; } = ActivationInputMode.SqlAutocomplete;
            public string ChannelCodeOverride { get; init; } = string.Empty;
            public string Environment { get; init; } = "Q";
            public string Cus { get; init; } = "MDEEM";
            public string Bucp { get; init; } = string.Empty;
            public string Cmdpmt { get; init; } = string.Empty;
            public string Process { get; init; } = "C01";
            public string Username { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
        }

        private sealed class BatchActivationInput
        {
            public string FilePath { get; init; } = string.Empty;
            public bool IsDemandId { get; init; }
            public bool MarkAllAsBranch23 { get; init; }
            public string DefaultBranch23Funds { get; init; } = string.Empty;
            public ActivationInputMode InputMode { get; init; } = ActivationInputMode.SqlAutocomplete;
            public string ChannelCodeOverride { get; init; } = string.Empty;
            public string Environment { get; init; } = "Q";
            public string Cus { get; init; } = "MDEEM";
            public string Bucp { get; init; } = string.Empty;
            public string Cmdpmt { get; init; } = string.Empty;
            public string Process { get; init; } = "C01";
            public string Username { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
        }
    }
}