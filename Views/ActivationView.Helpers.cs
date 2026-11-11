using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services;
using AutoActivator.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoActivator.Gui.Views
{
    /// <summary>
    /// Helper, trace mapping, progress bar, and UI reading partial class for ActivationView.
    /// </summary>
    public partial class ActivationView
    {
        private static ActivationHistoryItem CreateHistoryItem(ContractActivationTrace trace)
        {
            string statusDisplay = trace.FinalStatus switch
            {
                "SUCCESS" => "SUCCESS",
                "ALREADY_ACTIVE" => "ALREADY ACTIVE",
                "CANCELLED" => "CANCELLED",
                "PENDING_ASYNC_LISA_CREATION" => "PENDING ASYNC LISA CREATION",
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

            return new ActivationHistoryItem
            {
                RowNum = trace.RowNum > 0
                    ? trace.RowNum.ToString(CultureInfo.InvariantCulture)
                    : "Unit",
                Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Env = environment + "000",
                InputId = trace.RawInput,
                DemandId = !IsMissingValue(trace.DemandId) ? trace.DemandId : Display(trace.RawInput),
                UconId = Display(trace.EliaUconId),
                LisaId = Display(trace.LisaInternalId),
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
                FundsSummary = FormatFundsSummary(trace),
                IsBranch23 = trace.IsBranch23,
                StepsInfo = string.IsNullOrWhiteSpace(trace.StepsSummary) ? "-" : trace.StepsSummary,
                Duration = $"{trace.TotalDurationMs / 1000.0:F1}s",
                Status = statusDisplay,
                DiagnosticSummary = diagnosticSummary,
                FullTrace = trace
            };
        }

        private static string FormatFundsSummary(ContractActivationTrace trace)
        {
            string funds = trace.Branch23Funds != null && trace.Branch23Funds.Count > 0
                ? string.Join(",", trace.Branch23Funds)
                : trace.FundFeatureCount > 0
                    ? $"BR23 ({trace.FundFeatureCount} fonds)"
                    : string.Empty;

            string mvtState = !IsMissingValue(trace.Branch23MovementState)
                ? $"[Mvt:{trace.Branch23MovementState}]"
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(funds) && !string.IsNullOrWhiteSpace(mvtState))
            {
                return $"{funds} {mvtState}";
            }

            if (!string.IsNullOrWhiteSpace(funds))
            {
                return funds;
            }

            if (!string.IsNullOrWhiteSpace(mvtState))
            {
                return mvtState;
            }

            return trace.IsBranch23 ? "BR23" : "-";
        }

        private static string BuildTraceKey(ContractActivationTrace trace)
        {
            string rowOrTimestamp = trace.RowNum > 0
                ? trace.RowNum.ToString(CultureInfo.InvariantCulture)
                : trace.StartTime.Ticks.ToString(CultureInfo.InvariantCulture);

            return string.Join(
                "|",
                rowOrTimestamp,
                trace.RawInput ?? string.Empty,
                trace.DemandId ?? string.Empty,
                trace.ResolvedContract ?? string.Empty,
                trace.FinalStatus ?? string.Empty);
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
            AddOverride(overrides, "DRUN", input.RunDateOverride);
            AddOverride(overrides, "AMOUNT", input.AmountOverride);
            return overrides;
        }

        private static IReadOnlyDictionary<string, string> BuildBatchUiOverrides(
            BatchActivationInput input)
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
            AddOverride(overrides, "DRUN", input.RunDateOverride);
            AddOverride(overrides, "AMOUNT", input.AmountOverride);
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
            if (!string.IsNullOrWhiteSpace(plan.LisaInternalId))
            {
                trace.LisaInternalId = plan.LisaInternalId;
            }
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
            trace.FundFeatureCount = plan.FundFeatureCount;
            if (plan.ResolvedFunds != null && plan.ResolvedFunds.Count > 0)
            {
                trace.Branch23Funds = new List<string>(plan.ResolvedFunds);
                trace.Branch23FundsSource = plan.FundsSource;
            }
            trace.IsBranch23 = plan.IsBranch23;
            trace.BranchClassification = plan.BranchClassification != ContractBranchClassification.Unknown
                ? plan.BranchClassification
                : (plan.IsBranch23
                    ? ContractBranchClassification.Branch23
                    : ContractBranchClassification.Standard);
            trace.Branch23Source = plan.ForceBranch23
                ? "Manual UI override"
                : plan.BranchDetectionSource != BranchDetectionSource.Unknown
                    ? $"{plan.BranchDetectionSource}: {plan.BranchDetectionReason}"
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
                LisaInternalId = plan.LisaInternalId,
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
                FundFeatureCount = plan.FundFeatureCount,
                BranchClassification = plan.BranchClassification != ContractBranchClassification.Unknown
                    ? plan.BranchClassification
                    : (plan.IsBranch23
                        ? ContractBranchClassification.Branch23
                        : ContractBranchClassification.Standard),
                BranchDetectionSource = plan.BranchDetectionSource,
                BranchDetectionReason = plan.BranchDetectionReason,
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

        private static string BuildRoutingStatusMessage(
            ActivationLookupResult lookup,
            string process,
            bool isBranch23)
        {
            string branchLabel = isBranch23 ? "Branche 23" : "Branche 21";
            return
                $"ELIA routing resolved. " +
                $"Contract={Display(lookup.ContractNumber)}, " +
                $"Product={Display(lookup.ProductCode)}, " +
                $"Process={process}, " +
                $"Channel={Display(lookup.ChannelCode)}, " +
                $"Branche={branchLabel}.";
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
        // PROGRESSION UNIFIÉE (UNITAIRE & BATCH)
        // =============================================================

        private readonly Stopwatch _batchStopwatch = new Stopwatch();

        private void UpdateBatchProgressDisplay(
            int processed,
            int total,
            string? currentContract,
            string? status = null,
            string? modeLabel = null,
            double? elapsedSecondsOverride = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => UpdateBatchProgressDisplay(
                    processed,
                    total,
                    currentContract,
                    status,
                    modeLabel,
                    elapsedSecondsOverride));
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

            string normalizedStatus = string.IsNullOrWhiteSpace(status)
                ? "RUNNING"
                : status.Trim().ToUpperInvariant();

            if (normalizedStatus == "STARTING")
            {
                _batchStopwatch.Restart();
            }
            else if (normalizedStatus is "COMPLETED" or "CANCELLED" or "FAILED" or "READY" or "ALREADY_ACTIVE")
            {
                _batchStopwatch.Stop();
            }

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

            if (TxtBatchElapsedTime != null)
            {
                double elapsedSec = elapsedSecondsOverride ?? _batchStopwatch.Elapsed.TotalSeconds;
                if (normalizedStatus == "READY" && elapsedSec <= 0.01)
                {
                    TxtBatchElapsedTime.Text = "   ⏱️ 0.0s";
                }
                else if (remaining > 0 && _batchProcessedCount > 0 && elapsedSec > 0.5 && (modeLabel == null || modeLabel.Contains("BATCH", StringComparison.OrdinalIgnoreCase)))
                {
                    double avgPerItem = elapsedSec / _batchProcessedCount;
                    double etaSec = avgPerItem * remaining;
                    TxtBatchElapsedTime.Text = $"   ⏱️ {elapsedSec:F1}s (ETA ~{etaSec:F0}s)";
                }
                else
                {
                    TxtBatchElapsedTime.Text = $"   ⏱️ {elapsedSec:F1}s";
                }
            }

            if (TxtBatchCurrentContract != null)
            {
                TxtBatchCurrentContract.Text = string.IsNullOrWhiteSpace(currentContract)
                    ? "Prêt"
                    : currentContract;
            }

            if (TxtBatchProgressPercentage != null)
            {
                TxtBatchProgressPercentage.Text = $"{percentage:F0}%";
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
                    "COMPLETED" or "ALREADY_ACTIVE" => Brushes.MediumSeaGreen,
                    "CANCELLED" or "FAILED" => Brushes.Crimson,
                    _ => Brushes.RoyalBlue
                };
            }
        }

        private void UpdateSingleActivationStepProgress(
            ContractActivationTrace trace,
            Stopwatch stopwatch,
            string liveMessage,
            string status = "RUNNING")
        {
            int totalSteps = trace.Steps != null && trace.Steps.Count > 0
                ? trace.Steps.Count + 2
                : (trace.IsBranch23 ? 10 : 7);

            int completedJobs = 0;
            if (trace.Steps != null && trace.Steps.Count > 0)
            {
                foreach (var s in trace.Steps)
                {
                    if (s.IsSuccess || s.IsSkipped || s.Status == "CompleteRc8")
                    {
                        completedJobs++;
                    }
                }
            }

            int currentStage = Math.Min(totalSteps - 1, 1 + completedJobs);
            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                currentStage = totalSteps;
            }

            string contractLabel = !IsMissingValue(trace.ResolvedContract)
                ? trace.ResolvedContract
                : !IsMissingValue(trace.RawInput)
                    ? trace.RawInput
                    : "-";

            string detail = $"{contractLabel} ({trace.BranchDisplay}) — {liveMessage}";
            UpdateBatchProgressDisplay(
                currentStage,
                totalSteps,
                detail,
                status,
                "ACTIVATION UNITAIRE (ÉTAPES JCL/SQL)",
                stopwatch.Elapsed.TotalSeconds);
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

            int remaining = Math.Max(
                0,
                progress.TotalItems - progress.CurrentItem);

            string product = string.IsNullOrWhiteSpace(progress.Product)
                ? "-"
                : progress.Product;
            string process = string.IsNullOrWhiteSpace(progress.ProcessCode)
                ? "-"
                : progress.ProcessCode;

            string liveDetail = $"Row #{progress.RowNum} : {currentIdentifier} ({progress.BranchDisplay} | {product}/{process}) — {progress.Status}";

            UpdateBatchProgressDisplay(
                progress.CurrentItem,
                progress.TotalItems,
                liveDetail,
                "RUNNING",
                "ACTIVATION BATCH EXCEL");

            mainWindow.TxtStatus.Text =
                $"Batch progress: {progress.CurrentItem}/" +
                $"{progress.TotalItems} completed, {remaining} remaining. " +
                $"Excel Row={progress.RowNum}, Current={currentIdentifier}, Product={product}, " +
                $"Process={process}, Branche={progress.BranchDisplay}.";
            mainWindow.TxtStatus.Foreground = Brushes.RoyalBlue;
        }

        // =============================================================
        // LECTURE DE L'INTERFACE & HELPERS
        // =============================================================

        private Task<SingleActivationInput> ReadSingleActivationInputAsync()
        {
            return Dispatcher.InvokeAsync(() =>
            {
                string rawInput = TxtActContract.Text.Trim();
                return new SingleActivationInput
                {
                    RawInput = rawInput,
                    IsDemandId = ExcelHelper.IsDemandIdInput(rawInput),
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
                    RunDateOverride = SelectedActivationInputMode.AllowsManualOverrides()
                        ? (TxtActOverrideRunDate?.Text?.Trim() ?? string.Empty)
                        : string.Empty,
                    AmountOverride = SelectedActivationInputMode.AllowsManualOverrides()
                        ? (TxtActOverrideAmount?.Text?.Trim() ?? string.Empty)
                        : string.Empty,
                    ChannelCodeOverride = string.Empty,
                    Process = SelectedActivationInputMode == ActivationInputMode.Manual
                        ? GetSelectedProcess()
                        : GetSelectedProcessOverride(),
                    JobSelection = ReadJobSelectionFromUi(),
                    Username = Settings.DbConfig.Uid,
                    Password = Settings.DbConfig.Pwd
                };
            }).Task;
        }

        private Task<BatchActivationInput> ReadBatchActivationInputAsync()
        {
            return Dispatcher.InvokeAsync(() => new BatchActivationInput
            {
                FilePath = TxtBatchActCsv.Text.Trim(),
                IsDemandId = true,
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
                RunDateOverride = SelectedActivationInputMode.AllowsManualOverrides()
                    ? (TxtActOverrideRunDate?.Text?.Trim() ?? string.Empty)
                    : string.Empty,
                AmountOverride = SelectedActivationInputMode.AllowsManualOverrides()
                    ? (TxtActOverrideAmount?.Text?.Trim() ?? string.Empty)
                    : string.Empty,
                ChannelCodeOverride = string.Empty,
                Process = SelectedActivationInputMode == ActivationInputMode.Manual
                    ? GetSelectedProcess()
                    : GetSelectedProcessOverride(),
                JobSelection = ReadJobSelectionFromUi(),
                Username = Settings.DbConfig.Uid,
                Password = Settings.DbConfig.Pwd
            }).Task;
        }

        private ActivationJobSelection ReadJobSelectionFromUi()
        {
            return new ActivationJobSelection
            {
                RunAddPrct = ChkJobAddPrct?.IsChecked != false,
                UseSqlPrctInsert = ChkJobSqlPrct?.IsChecked == true,
                RunLvpp06u = ChkJobLvpp06u?.IsChecked != false,
                RunLvpg22u = ChkJobLvpg22u?.IsChecked != false,
                RunLi1j04d0 = ChkJobLi1j04d0?.IsChecked != false,
                RunLi1j04d2 = ChkJobLi1j04d2?.IsChecked != false,
                RunLvdqFg03 = ChkJobLvdqFg03?.IsChecked != false,
                RunBranch23SqlVni = ChkJobSqlVni?.IsChecked != false,
                RunLvdqFg01 = ChkJobLvdqFg01?.IsChecked != false,
                RunLvd1Pe01 = ChkJobLvd1Pe01?.IsChecked == true
            };
        }

        private void ApplyJobSelectionToUi(ActivationJobSelection selection)
        {
            if (ChkJobAddPrct != null) ChkJobAddPrct.IsChecked = selection.RunAddPrct;
            if (ChkJobSqlPrct != null) ChkJobSqlPrct.IsChecked = selection.UseSqlPrctInsert;
            if (ChkJobLvpp06u != null) ChkJobLvpp06u.IsChecked = selection.RunLvpp06u;
            if (ChkJobLvpg22u != null) ChkJobLvpg22u.IsChecked = selection.RunLvpg22u;
            if (ChkJobLi1j04d0 != null) ChkJobLi1j04d0.IsChecked = selection.RunLi1j04d0;
            if (ChkJobLi1j04d2 != null) ChkJobLi1j04d2.IsChecked = selection.RunLi1j04d2;
            if (ChkJobLvdqFg03 != null) ChkJobLvdqFg03.IsChecked = selection.RunLvdqFg03;
            if (ChkJobSqlVni != null) ChkJobSqlVni.IsChecked = selection.RunBranch23SqlVni;
            if (ChkJobLvdqFg01 != null) ChkJobLvdqFg01.IsChecked = selection.RunLvdqFg01;
            if (ChkJobLvd1Pe01 != null) ChkJobLvd1Pe01.IsChecked = selection.RunLvd1Pe01;
        }

        private string GetSelectedEnvironment()
        {
            if (CmbActEnv?.SelectedItem is ComboBoxItem item)
            {
                return NormalizeShortEnvironment(item.Tag?.ToString());
            }

            return "Q";
        }

        private string GetSelectedProcessOverride()
        {
            if (CmbActChannel?.SelectedItem is ComboBoxItem item)
            {
                string? value = item.Tag?.ToString();
                return string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : value.Trim().ToUpperInvariant();
            }

            return string.Empty;
        }

        private string GetSelectedProcess()
        {
            string overrideValue = GetSelectedProcessOverride();
            return string.IsNullOrWhiteSpace(overrideValue)
                ? "C01"
                : overrideValue;
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
            BtnRunBatchAct.IsEnabled = !running;
            BtnSkipPrimeBatchAct.IsEnabled = !running;
        }

        private void SetBatchActivationButtons(bool running)
        {
            BtnCancelSingleAct.IsEnabled = running;
            BtnRunSingleAct.IsEnabled = !running;
            BtnSkipPrimeSingleAct.IsEnabled = !running;
            BtnRunBatchAct.IsEnabled = !running;
            BtnSkipPrimeBatchAct.IsEnabled = !running;
        }

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
            public string RunDateOverride { get; init; } = string.Empty;
            public string AmountOverride { get; init; } = string.Empty;
            public string Process { get; init; } = "C01";
            public ActivationJobSelection JobSelection { get; init; } = ActivationJobSelection.FullChain();
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
            public string RunDateOverride { get; init; } = string.Empty;
            public string AmountOverride { get; init; } = string.Empty;
            public string Process { get; init; } = "C01";
            public ActivationJobSelection JobSelection { get; init; } = ActivationJobSelection.FullChain();
            public string Username { get; init; } = string.Empty;
            public string Password { get; init; } = string.Empty;
        }
    }
}
