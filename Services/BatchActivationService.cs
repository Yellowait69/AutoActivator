using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Traite les activations en masse depuis un fichier CSV/Excel.
    /// Découpé en classes partielles :
    /// - BatchActivationService.cs : Orchestration du batch et traitement par contrat ;
    /// - BatchActivationService.Helpers.cs : Lecture CSV (avec numéro de ligne Excel exact) et helpers ;
    /// - BatchActivationService.Reporting.cs : Génération du rapport détaillé de batch.
    /// </summary>
    public sealed partial class BatchActivationService
    {
        private const int MaximumConcurrentDatabaseLookups =
            Settings.ActivationAutomation.BatchSqlConcurrency;
        private const int MaximumConcurrentMainframeExecutions = 1;

        private readonly ActivationDataService _dataService;

        public BatchActivationService(ActivationDataService dataService)
        {
            _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        }

        public async Task<(
            int successCount,
            int alreadyActiveCount,
            int errorCount,
            string reportPath)> RunBatchAsync(
                string filePath,
                bool isDemandId,
                string envValue,
                string cus,
                string bucp,
                string cmdpmt,
                string channel,
                bool skipPrime,
                string username,
                string password,
                string outputDir,
                Action<string>? onProgress,
                CancellationToken token,
                Action<ContractActivationTrace>? onContractCompleted = null,
                bool markAllAsBranch23 = false,
                string? defaultBranch23Funds = null,
                Action<BatchProgressInfo>? onBatchProgress = null,
                ActivationInputMode inputMode = ActivationInputMode.SqlAutocomplete,
                ActivationJobSelection? jobSelection = null,
                bool manualPaymentOverride = false,
                IReadOnlyDictionary<string, string>? uiOverrides = null)
        {
            ValidateArguments(filePath, envValue, username, outputDir);

            ServicePointManager.DefaultConnectionLimit = 500;
            ServicePointManager.Expect100Continue = false;

            var batchStopwatch = Stopwatch.StartNew();
            DateTime batchStartTime = DateTime.Now;

            int successCount = 0;
            int alreadyActiveCount = 0;
            int errorCount = 0;
            int processedItems = 0;

            var traces = new ConcurrentBag<ContractActivationTrace>();

            IReadOnlyList<BatchInputItem> items =
                await ReadInputFileAsync(filePath, token).ConfigureAwait(false);

            int totalItems = items.Count;
            if (totalItems == 0)
            {
                throw new InvalidOperationException(
                    "The input file does not contain any usable contract or Demand ID.");
            }

            onBatchProgress?.Invoke(
                new BatchProgressInfo
                {
                    CurrentItem = 0,
                    TotalItems = totalItems,
                    Status = "STARTING"
                });

            string normalizedEnvironment = NormalizeShortEnvironment(envValue);
            string environmentSuffix = Settings.NormalizeEnvironmentSuffix(normalizedEnvironment);

            bool fileContainsBranch23Overrides = items.Any(item => item.Branch23Override == true);
            bool shouldPreloadBranch23Templates =
                markAllAsBranch23 || fileContainsBranch23Overrides || inputMode.UsesSqlAutocomplete() || isDemandId;

            onProgress?.Invoke("Authenticating with Mainframe and preloading JCL templates.");

            await AuthenticateAndPreloadAsync(
                    normalizedEnvironment,
                    username,
                    password,
                    shouldPreloadBranch23Templates,
                    onProgress,
                    token)
                .ConfigureAwait(false);

            using var databaseSemaphore = new SemaphoreSlim(
                MaximumConcurrentDatabaseLookups,
                MaximumConcurrentDatabaseLookups);

            using var mainframeSemaphore = new SemaphoreSlim(
                MaximumConcurrentMainframeExecutions,
                MaximumConcurrentMainframeExecutions);

            onProgress?.Invoke($"Starting parallel ELIA resolution for {totalItems} item(s).");

            List<Task> tasks = items.Select(
                item => ProcessBatchItemAsync(
                    item,
                    isDemandId,
                    normalizedEnvironment,
                    environmentSuffix,
                    cus,
                    bucp,
                    cmdpmt,
                    channel,
                    skipPrime,
                    username,
                    password,
                    markAllAsBranch23,
                    defaultBranch23Funds,
                    inputMode,
                    jobSelection,
                    manualPaymentOverride,
                    uiOverrides,
                    totalItems,
                    databaseSemaphore,
                    mainframeSemaphore,
                    traces,
                    () => Interlocked.Increment(ref processedItems),
                    () => Volatile.Read(ref processedItems),
                    () => Interlocked.Increment(ref successCount),
                    () => Interlocked.Increment(ref alreadyActiveCount),
                    () => Interlocked.Increment(ref errorCount),
                    onProgress,
                    onContractCompleted,
                    onBatchProgress,
                    token))
                .ToList();

            bool batchCancelled = false;

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
                onProgress?.Invoke("All batch items were processed. Generating the diagnostic report.");
            }
            catch (OperationCanceledException)
            {
                batchCancelled = true;
                onProgress?.Invoke("Batch cancelled. Generating a partial diagnostic report.");
            }

            batchStopwatch.Stop();

            List<ContractActivationTrace> orderedTraces =
                traces.OrderBy(trace => trace.RowNum).ToList();

            string reportPath = BuildReportPath(
                outputDir,
                skipPrime,
                batchCancelled || token.IsCancellationRequested);

            Directory.CreateDirectory(outputDir);

            try
            {
                await WriteBatchReportAsync(
                        reportPath,
                        batchStartTime,
                        batchStopwatch.ElapsedMilliseconds,
                        skipPrime,
                        normalizedEnvironment,
                        channel,
                        cus,
                        bucp,
                        cmdpmt,
                        orderedTraces,
                        batchCancelled || token.IsCancellationRequested,
                        successCount,
                        alreadyActiveCount,
                        errorCount,
                        inputMode)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                reportPath = BuildReportPath(
                    outputDir,
                    skipPrime,
                    batchCancelled || token.IsCancellationRequested,
                    includeUniqueSuffix: true);

                await WriteBatchReportAsync(
                        reportPath,
                        batchStartTime,
                        batchStopwatch.ElapsedMilliseconds,
                        skipPrime,
                        normalizedEnvironment,
                        channel,
                        cus,
                        bucp,
                        cmdpmt,
                        orderedTraces,
                        batchCancelled || token.IsCancellationRequested,
                        successCount,
                        alreadyActiveCount,
                        errorCount,
                        inputMode)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                onProgress?.Invoke($"CRITICAL ERROR: Unable to save the batch report. {exception.Message}");
                throw;
            }

            onProgress?.Invoke($"Batch report generated: {reportPath}");

            if (batchCancelled || token.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"Batch execution was cancelled. A partial report was generated at '{reportPath}'.",
                    token);
            }

            return (successCount, alreadyActiveCount, errorCount, reportPath);
        }

        private async Task ProcessBatchItemAsync(
            BatchInputItem item,
            bool isDemandId,
            string environment,
            string environmentSuffix,
            string cus,
            string bucp,
            string cmdpmt,
            string defaultProcess,
            bool skipPrime,
            string username,
            string password,
            bool markAllAsBranch23,
            string? defaultBranch23Funds,
            ActivationInputMode inputMode,
            ActivationJobSelection? jobSelection,
            bool manualPaymentOverride,
            IReadOnlyDictionary<string, string>? uiOverrides,
            int totalItems,
            SemaphoreSlim databaseSemaphore,
            SemaphoreSlim mainframeSemaphore,
            ConcurrentBag<ContractActivationTrace> traces,
            Func<int> incrementProcessed,
            Func<int> getProcessed,
            Func<int> incrementSuccess,
            Func<int> incrementAlreadyActive,
            Func<int> incrementError,
            Action<string>? onProgress,
            Action<ContractActivationTrace>? onContractCompleted,
            Action<BatchProgressInfo>? onBatchProgress,
            CancellationToken token)
        {
            var contractStopwatch = Stopwatch.StartNew();
            bool itemIsDemandId = ExcelHelper.IsDemandIdInput(item.RawInput);

            ContractActivationTrace trace = CreateInitialTrace(
                item,
                itemIsDemandId,
                environment,
                cus,
                bucp,
                cmdpmt,
                defaultProcess,
                skipPrime,
                username);

            bool traceAdded = false;

            try
            {
                token.ThrowIfCancellationRequested();

                ActivationLookupResult lookup;
                PreActivationRoutingInfo? routing = null;
                AutomaticActivationPlan? automaticPlan = null;

                await databaseSemaphore.WaitAsync(token).ConfigureAwait(false);

                try
                {
                    lookup = await _dataService
                        .ResolveContractForActivationAsync(
                            item.RawInput,
                            itemIsDemandId,
                            environmentSuffix,
                            token)
                        .ConfigureAwait(false);

                    string demandCandidate = itemIsDemandId ? item.RawInput : lookup.DemandId;
                    if (!string.IsNullOrWhiteSpace(demandCandidate) &&
                        long.TryParse(demandCandidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        routing = await TryResolveRoutingAsync(
                                demandCandidate,
                                environmentSuffix,
                                onProgress,
                                token)
                            .ConfigureAwait(false);
                    }

                    var mergedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (inputMode.AllowsManualOverrides())
                    {
                        if (!string.IsNullOrWhiteSpace(bucp))
                            mergedOverrides["BUCP"] = bucp.Trim();
                        if (!string.IsNullOrWhiteSpace(cmdpmt))
                            mergedOverrides["CMDPMT"] = cmdpmt.Trim();
                        if (!string.IsNullOrWhiteSpace(defaultProcess))
                            mergedOverrides["PROCESS"] = defaultProcess.Trim();

                        if (uiOverrides != null)
                        {
                            foreach (var kvp in uiOverrides)
                            {
                                if (!string.IsNullOrWhiteSpace(kvp.Value))
                                {
                                    mergedOverrides[kvp.Key] = kvp.Value.Trim();
                                }
                            }
                        }

                        foreach (var kvp in item.Overrides)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Value))
                            {
                                mergedOverrides[kvp.Key] = kvp.Value.Trim();
                            }
                        }
                    }

                    automaticPlan = await _dataService.ResolveAutomaticActivationPlanAsync(
                            item.RawInput,
                            itemIsDemandId,
                            environmentSuffix,
                            cus,
                            item.Branch23Override ?? markAllAsBranch23,
                            inputMode,
                            mergedOverrides,
                            token,
                            lookup,
                            routing)
                        .ConfigureAwait(false);
                }
                finally
                {
                    databaseSemaphore.Release();
                }

                bool effectiveBranch23 = automaticPlan != null
                    ? automaticPlan.IsBranch23
                    : ResolveEffectiveBranch23(item, markAllAsBranch23, routing, trace);

                bool onlyRunningPostActivationBranch23Steps =
                    effectiveBranch23 &&
                    jobSelection != null &&
                    !jobSelection.RequiresPremelia &&
                    jobSelection.HasAnyBranch23Step;

                if (automaticPlan != null)
                {
                    if (automaticPlan.ResumeStrategy == ActivationResumeStrategy.AlreadyActive &&
                        !onlyRunningPostActivationBranch23Steps)
                    {
                        PopulateTraceFromLookup(trace, lookup);
                        throw new InvalidOperationException("ALREADY_ACTIVE");
                    }

                    bool bypassPrimeValidation =
                        skipPrime || onlyRunningPostActivationBranch23Steps;

                    IReadOnlyList<ActivationPlanIssue> blockingIssues =
                        automaticPlan.Validate()
                            .Where(issue => issue.IsBlocking &&
                                            !(onlyRunningPostActivationBranch23Steps && issue.Code == "ALREADY_ACTIVE") &&
                                            !(bypassPrimeValidation &&
                                              (issue.Code == "PREMIUM_NOT_POSITIVE" ||
                                               issue.Code == "PREMIUM_CANDIDATE_COUNT_INVALID")))
                            .ToList();
                    if (blockingIssues.Count > 0)
                    {
                        PopulateTraceFromLookup(trace, lookup);
                        trace.FailureStage = "AUTOMATIC_PLAN_VALIDATION";
                        trace.FailureReason = string.Join(
                            " | ",
                            blockingIssues.Select(issue => $"{issue.Code}: {issue.Message}"));
                        throw new InvalidOperationException(trace.FailureReason);
                    }
                }

                PopulateTraceFromLookup(trace, lookup);

                if (automaticPlan != null)
                {
                    trace.ResolvedContract = !string.IsNullOrWhiteSpace(automaticPlan.ContractNumber)
                        ? automaticPlan.ContractNumber
                        : trace.ResolvedContract;
                    trace.JclContract = !string.IsNullOrWhiteSpace(automaticPlan.MainframeContractNumber)
                        ? automaticPlan.MainframeContractNumber
                        : trace.JclContract;
                    trace.EliaUconId = !string.IsNullOrWhiteSpace(automaticPlan.EliaId)
                        ? automaticPlan.EliaId
                        : trace.EliaUconId;
                    trace.DemandId = !string.IsNullOrWhiteSpace(automaticPlan.DemandId)
                        ? automaticPlan.DemandId
                        : trace.DemandId;
                    if (!string.IsNullOrWhiteSpace(automaticPlan.LisaInternalId))
                    {
                        trace.LisaInternalId = automaticPlan.LisaInternalId;
                    }
                    trace.Bucp = manualPaymentOverride && Settings.ActivationAutomation.IsSupportedManualPair(bucp, cmdpmt)
                        ? AutomaticActivationPlan.NormalizeBucp(bucp)
                        : automaticPlan.Bucp;
                    trace.Cmdpmt = manualPaymentOverride && Settings.ActivationAutomation.IsSupportedManualPair(bucp, cmdpmt)
                        ? cmdpmt.Trim().ToUpperInvariant()
                        : automaticPlan.PaymentCommand;
                    trace.PaymentChannel = automaticPlan.PaymentChannel;
                    trace.EliaChannelCode = automaticPlan.ChannelCode;
                    trace.BusinessChannel = !string.IsNullOrWhiteSpace(automaticPlan.ResolvedChannel) &&
                                            automaticPlan.ResolvedChannel != "UNKNOWN"
                        ? automaticPlan.ResolvedChannel
                        : ResolveBusinessChannelName(automaticPlan.ChannelCode);
                    trace.ProductCode = automaticPlan.ProductCode;
                    trace.ProcessCode = automaticPlan.ProcessCode;
                    trace.RoutingRule = automaticPlan.RoutingRule;
                    trace.RunDate = automaticPlan.Drun;
                    trace.RunDateSource = automaticPlan.RunDateSource;
                    trace.RawPremium = automaticPlan.GrossPremium.ToString("0.00", CultureInfo.InvariantCulture);
                    trace.FundFeatureCount = Math.Max(trace.FundFeatureCount, automaticPlan.FundFeatureCount);
                    if (automaticPlan.ResolvedFunds != null && automaticPlan.ResolvedFunds.Count > 0)
                    {
                        trace.Branch23Funds = automaticPlan.ResolvedFunds.ToList();
                        trace.Branch23FundsSource = automaticPlan.FundsSource;
                    }
                    trace.RoutingDiagnostic =
                        automaticPlan.RoutingRule +
                        " | BusinessChannel=" + trace.BusinessChannel +
                        " | BUCP/CMDPMT=" + trace.Bucp + "/" +
                        trace.Cmdpmt +
                        " | AutomaticRoutingStatus=" +
                        automaticPlan.RoutingStatus +
                        " | RunDate=" + automaticPlan.Drun +
                        " | RunDateSource=" + automaticPlan.RunDateSource;
                }

                string effectiveProcess = automaticPlan != null
                    ? automaticPlan.ProcessCode
                    : ResolveEffectiveProcess(defaultProcess, routing);

                trace.Channel = effectiveProcess;
                trace.IsBranch23 = effectiveBranch23;

                if (routing != null)
                {
                    AppendRoutingInformation(trace, routing);

                    onProgress?.Invoke(
                        $"Excel Row #{trace.RowNum} | Routing resolved | Demand={trace.DemandId} | " +
                        $"Contract={trace.ResolvedContract} | " +
                        $"Product={DisplayValue(trace.ProductCode)} | " +
                        $"Process={DisplayValue(trace.ProcessCode)} | " +
                        $"Branch={trace.BranchDisplay} | " +
                        $"Status={trace.RoutingStatus}");
                }

                if (!ValidateLookupForActivation(item, itemIsDemandId, environment, lookup, routing, trace))
                {
                    CompleteFailedTrace(trace, contractStopwatch);
                    AddTraceOnce(traces, trace, ref traceAdded);
                    incrementError();
                    int processed = incrementProcessed();
                    onContractCompleted?.Invoke(trace);
                    ReportBatchProgress(onBatchProgress, trace, processed, totalItems);
                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] Excel Row #{item.RowNumber} ELIA lookup failed for {item.RawInput}: {trace.FailureReason}");
                    return;
                }

                string premium = automaticPlan != null
                    ? automaticPlan.GrossPremium.ToString("0.00", CultureInfo.InvariantCulture)
                    : ResolvePremium(lookup, routing);

                trace.RawPremium = premium;

                if (!HasPositivePremium(premium) &&
                    !skipPrime &&
                    !string.Equals(effectiveProcess, "C05", StringComparison.OrdinalIgnoreCase))
                {
                    ConfigurePremiumFailure(trace, lookup, environment);
                    CompleteFailedTrace(trace, contractStopwatch);
                    AddTraceOnce(traces, trace, ref traceAdded);
                    incrementError();
                    int processed = incrementProcessed();
                    onContractCompleted?.Invoke(trace);
                    ReportBatchProgress(onBatchProgress, trace, processed, totalItems);
                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] Excel Row #{item.RowNumber} Mainframe skipped for {trace.ResolvedContract}: missing or zero premium.");
                    return;
                }

                IBranch23FundSource? branch23FundSource =
                    BuildBranch23FundSource(effectiveBranch23, item.Funds, defaultBranch23Funds);

                await mainframeSemaphore.WaitAsync(token).ConfigureAwait(false);

                try
                {
                    token.ThrowIfCancellationRequested();

                    ReportBatchProgress(
                        onBatchProgress,
                        trace,
                        getProcessed(),
                        totalItems,
                        statusOverride: "RUNNING");

                    onProgress?.Invoke(
                        $"Excel Row #{trace.RowNum}: Activating {trace.ResolvedContract} ({trace.JclContract}), " +
                        $"process {effectiveProcess}, {trace.BranchDisplay}.");

                    await _dataService
                        .ExecuteActivationSequenceAsync(
                            trace.JclContract,
                            premium,
                            environment,
                            cus,
                            trace.Bucp,
                            trace.Cmdpmt,
                            effectiveProcess,
                            skipPrime,
                            skipLogon: true,
                            username,
                            password,
                            message => onProgress?.Invoke($"{trace.ResolvedContract}: {message}"),
                            token,
                            trace,
                            effectiveBranch23,
                            branch23FundSource,
                            branch23ActivationOnly: Settings.Branch23Activation.Branch23ActivationOnly,
                            jobSelection: jobSelection,
                            manualPaymentOverride: manualPaymentOverride)
                        .ConfigureAwait(false);
                }
                finally
                {
                    mainframeSemaphore.Release();
                }

                contractStopwatch.Stop();
                trace.EndTime = DateTime.Now;
                trace.TotalDurationMs = contractStopwatch.ElapsedMilliseconds;
                trace.FinalStatus = "SUCCESS";

                AddTraceOnce(traces, trace, ref traceAdded);
                incrementSuccess();
                int currentSuccess = incrementProcessed();
                onContractCompleted?.Invoke(trace);
                ReportBatchProgress(onBatchProgress, trace, currentSuccess, totalItems);

                onProgress?.Invoke(
                    $"[{currentSuccess}/{totalItems}] Excel Row #{trace.RowNum} Activated: {trace.ResolvedContract} " +
                    $"({trace.BranchDisplay}, {trace.TotalDurationMs / 1000.0:F1}s).");
            }
            catch (OperationCanceledException)
            {
                contractStopwatch.Stop();
                trace.EndTime = DateTime.Now;
                trace.TotalDurationMs = contractStopwatch.ElapsedMilliseconds;
                trace.FinalStatus = "CANCELLED";

                if (string.IsNullOrWhiteSpace(trace.FailureStage))
                    trace.FailureStage = "USER_CANCELLED";
                if (string.IsNullOrWhiteSpace(trace.FailureReason))
                    trace.FailureReason = "Batch execution was cancelled by the user.";

                AddTraceOnce(traces, trace, ref traceAdded);
                onContractCompleted?.Invoke(trace);
                throw;
            }
            catch (Exception exception)
            {
                contractStopwatch.Stop();
                trace.EndTime = DateTime.Now;
                trace.TotalDurationMs = contractStopwatch.ElapsedMilliseconds;

                int processed = incrementProcessed();

                if (string.Equals(exception.Message, "ALREADY_ACTIVE", StringComparison.Ordinal))
                {
                    trace.FinalStatus = "ALREADY_ACTIVE";
                    AddTraceOnce(traces, trace, ref traceAdded);
                    incrementAlreadyActive();
                    onContractCompleted?.Invoke(trace);
                    ReportBatchProgress(onBatchProgress, trace, processed, totalItems);
                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] Excel Row #{trace.RowNum} Already active: {trace.ResolvedContract}.");
                    return;
                }

                trace.FinalStatus = "FAILED";
                if (string.IsNullOrWhiteSpace(trace.FailureReason))
                    trace.FailureReason = exception.Message;
                if (string.IsNullOrWhiteSpace(trace.FailureStage))
                    trace.FailureStage = "BATCH_ITEM_EXECUTION";

                AddTraceOnce(traces, trace, ref traceAdded);
                incrementError();
                onContractCompleted?.Invoke(trace);
                ReportBatchProgress(onBatchProgress, trace, processed, totalItems);
                onProgress?.Invoke(
                    $"[{processed}/{totalItems}] Excel Row #{item.RowNumber} Failed: {item.RawInput}. Stage={trace.FailureStage}. {trace.FailureReason}");
            }
        }

        private async Task<PreActivationRoutingInfo?> TryResolveRoutingAsync(
            string demandId,
            string environmentSuffix,
            Action<string>? onProgress,
            CancellationToken token)
        {
            try
            {
                PreActivationRoutingInfo routing =
                    await _dataService
                        .ResolvePreActivationRoutingInEnvironmentAsync(demandId, environmentSuffix, token)
                        .ConfigureAwait(false);

                return routing.RoutingStatus == PreActivationRoutingStatus.DemandNotFound
                    ? null
                    : routing;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                onProgress?.Invoke(
                    $"Routing warning for Demand {demandId}: {exception.Message}. Legacy values will be used.");
                return null;
            }
        }

        private static string ResolveEffectiveProcess(
            string defaultProcess,
            PreActivationRoutingInfo? routing)
        {
            if (routing != null &&
                PreActivationRoutingService.IsSupportedActivationProcess(routing.ProcessCode))
            {
                return routing.ProcessCode.Trim().ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(defaultProcess)
                ? string.Empty
                : defaultProcess.Trim().ToUpperInvariant();
        }

        private static bool ResolveEffectiveBranch23(
            BatchInputItem item,
            bool markAllAsBranch23,
            PreActivationRoutingInfo? routing,
            ContractActivationTrace trace)
        {
            if (item.Branch23Override.HasValue)
            {
                trace.Branch23Source = "Input file BR23 override";
                return item.Branch23Override.Value;
            }

            if (routing?.IsBranch23 == true ||
                trace.BranchClassification == ContractBranchClassification.Branch23 ||
                trace.BranchClassification == ContractBranchClassification.CompositeWithFunds ||
                trace.FundFeatureCount > 0)
            {
                trace.Branch23Source = routing != null
                    ? $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}"
                    : $"{trace.BranchDetectionSource}: {trace.BranchDetectionReason}";
                return true;
            }

            if (routing?.BranchClassification == ContractBranchClassification.Standard ||
                trace.BranchClassification == ContractBranchClassification.Standard)
            {
                trace.Branch23Source = routing != null
                    ? $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}"
                    : $"{trace.BranchDetectionSource}: {trace.BranchDetectionReason}";
                return false;
            }

            if (markAllAsBranch23)
            {
                trace.Branch23Source = "Manual whole-batch override";
                return true;
            }

            trace.Branch23Source = "Branche 21 (Standard default)";
            return false;
        }

        private static void AppendRoutingInformation(
            ContractActivationTrace trace,
            PreActivationRoutingInfo routing)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(routing);

            string routingSummary = routing.ToDiagnosticSummary();

            trace.DbDiagnosticStatus =
                string.IsNullOrWhiteSpace(trace.DbDiagnosticStatus) ||
                string.Equals(trace.DbDiagnosticStatus, "N/A", StringComparison.OrdinalIgnoreCase)
                    ? routingSummary
                    : trace.DbDiagnosticStatus + " | " + routingSummary;

            if (!string.IsNullOrWhiteSpace(routing.Environment))
            {
                string resolvedEnvironment = NormalizeShortEnvironment(routing.Environment);
                trace.Environment = resolvedEnvironment;
                trace.EnvIms = resolvedEnvironment == "D" ? "T" : "C";
                trace.Q2Region = resolvedEnvironment == "D" ? "Q2T" : "Q2C";
                trace.FastCtrlDsn = resolvedEnvironment == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL";
            }

            trace.DemandId = routing.DemandId.ToString(CultureInfo.InvariantCulture);

            if (routing.EliaContractId.HasValue)
            {
                trace.EliaUconId = routing.EliaContractId.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                trace.ResolvedContract = routing.ContractNumber.Trim();
            }

            trace.ProductCode = routing.ProductCode ?? string.Empty;
            trace.ProcessCode = routing.ProcessCode ?? string.Empty;
            trace.EliaChannelCode = routing.ChannelCode ?? string.Empty;
            trace.BusinessChannel = routing.BusinessChannelName;
            trace.ApplicationChannelCode = routing.ApplicationChannelCode ?? string.Empty;
            trace.DemandStatus = routing.DemandStatus ?? string.Empty;

            if (PreActivationRoutingService.IsSupportedActivationProcess(routing.ProcessCode))
            {
                trace.Channel = routing.ProcessCode!.Trim().ToUpperInvariant();
            }

            if (routing.PremiumSequence.HasValue)
            {
                trace.PremiumSequence = routing.PremiumSequence.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                trace.RawPremium = routing.GrossPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                trace.NetPremium = routing.NetPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
            }

            trace.PaymentMethodCode = routing.PaymentMethodCode ?? string.Empty;
            trace.PremiumGroupType = routing.PremiumGroupType ?? string.Empty;
            trace.FundFeatureCount = routing.FundFeatureCount;
            trace.BranchClassification = routing.BranchClassification;
            trace.BranchDetectionSource = routing.BranchDetectionSource;
            trace.BranchDetectionReason = routing.BranchDetectionReason;
            trace.RoutingStatus = routing.RoutingStatus;

            string routingEvidence =
                $"BusinessChannel={routing.BusinessChannelName} | " +
                $"ChannelCode={routing.NormalizedChannelCode} | " +
                $"BUCP/CMDPMT={routing.ProposedBucp}/{routing.ProposedPaymentCommand} | " +
                $"Rule={routing.PaymentRoutingRule}";
            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(routing.RoutingDiagnostic)
                ? routingEvidence
                : routing.RoutingDiagnostic + " | " + routingEvidence;

            if (string.IsNullOrWhiteSpace(trace.Bucp))
                trace.Bucp = routing.ProposedBucp;
            if (string.IsNullOrWhiteSpace(trace.Cmdpmt))
                trace.Cmdpmt = routing.ProposedPaymentCommand;

            if (routing.IsBranch23)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source = $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}";
            }
            else if (routing.BranchClassification == ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source = $"{routing.BranchDetectionSource}: {routing.BranchDetectionReason}";
            }
        }

        private static bool ValidateLookupForActivation(
            BatchInputItem item,
            bool isDemandId,
            string environment,
            ActivationLookupResult lookup,
            PreActivationRoutingInfo? routing,
            ContractActivationTrace trace)
        {
            if (isDemandId && string.IsNullOrWhiteSpace(lookup.ContractNumber))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = routing?.RoutingStatus == PreActivationRoutingStatus.EliaContractNotLinked
                    ? "ELIA_CONTRACT_NOT_LINKED"
                    : "DB_DEMAND_LOOKUP";
                trace.FailureReason =
                    $"No contract number is currently associated with Demand ID '{item.RawInput}' in {environment}000.";
                trace.RootCauseAnalysis =
                    "The Demand exists without a linked UCON contract, or TB5UCON does not yet contain an external contract reference.";
                trace.RecommendedAction =
                    "Verify the Demand status in TB5HDMD and the links in TB5HELT/TB5UCON.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(lookup.ContractNumber))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = "DB_CONTRACT_NOT_FOUND";
                trace.FailureReason = $"Contract '{item.RawInput}' was not found in {environment}000.";
                trace.RootCauseAnalysis = "No matching contract reference was found in TB5UCON.";
                trace.RecommendedAction = "Verify the contract format and environment.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(lookup.JclContract))
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = "JCL_CONTRACT_NOT_RESOLVED";
                trace.FailureReason =
                    $"The JCL contract identifier could not be generated from '{lookup.ContractNumber}'.";
                trace.RootCauseAnalysis =
                    "The resolved contract reference does not match the expected extended contract format.";
                trace.RecommendedAction = "Verify the contract format before submitting jobs.";
                return false;
            }

            return true;
        }

        private static void ConfigurePremiumFailure(
            ContractActivationTrace trace,
            ActivationLookupResult lookup,
            string environment)
        {
            trace.FinalStatus = "FAILED";
            trace.FailureStage = lookup.FoundInElia ? "DB_PREMIUM_ZERO" : "DB_CONTRACT_NOT_FOUND";
            trace.FailureReason = lookup.FoundInElia
                ? $"Contract '{trace.ResolvedContract}' has no positive gross premium in TB5UPRP."
                : $"Contract '{trace.RawInput}' does not exist in ELIA environment {environment}000.";
            trace.RootCauseAnalysis = lookup.FoundInElia
                ? "ADDPRCT requires IT5UPRPUBRU to be greater than zero when Skip Prime is disabled."
                : "No ELIA contract was found for the supplied identifier.";
            trace.RecommendedAction = lookup.FoundInElia
                ? "Verify TB5UPRP, or enable Skip Prime if the contract requires activation without premium."
                : "Verify the input identifier and environment.";
        }
    }
}
