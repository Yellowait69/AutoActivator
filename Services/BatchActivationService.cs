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
    /// Traite les activations en masse depuis un fichier CSV.
    ///
    /// Le service :
    /// - lit et valide le fichier CSV ;
    /// - résout les données ELIA en parallèle ;
    /// - utilise PreActivationRoutingService via ActivationDataService ;
    /// - détermine automatiquement le process et la branche ;
    /// - conserve BR23/FUNDS comme overrides CSV facultatifs ;
    /// - sérialise les soumissions Mainframe ;
    /// - collecte une trace complète par contrat ;
    /// - génère un rapport détaillé, y compris après annulation.
    /// </summary>
    public sealed class BatchActivationService
    {
        private const int MaximumConcurrentDatabaseLookups =
            Settings.ActivationAutomation.BatchSqlConcurrency;
        private const int MaximumConcurrentMainframeExecutions = 1;

        private readonly ActivationDataService _dataService;

        public BatchActivationService(
            ActivationDataService dataService)
        {
            _dataService =
                dataService
                ?? throw new ArgumentNullException(
                    nameof(dataService));
        }

        /// <summary>
        /// Exécute un batch d'activations.
        ///
        /// Le fichier doit contenir une colonne Contract, Contrat,
        /// Demand, Value ou Lisa.
        ///
        /// Colonnes facultatives :
        /// - BR23 : override explicite de la classification automatique ;
        /// - FUNDS : fonds de fallback séparés par virgule, point-virgule,
        ///   pipe ou espace selon ConfiguredBranch23FundSource.Parse ;
        /// - AMOUNT, RUNDATE/DRUN, BUCP, CMDPMT/CMD, PROCESS et CHANNEL :
        ///   overrides utilisés uniquement par le mode SQL avec overrides.
        /// </summary>
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
                Action<ContractActivationTrace>?
                    onContractCompleted = null,
                bool markAllAsBranch23 = false,
                string? defaultBranch23Funds = null,
                Action<BatchProgressInfo>? onBatchProgress = null,
                ActivationInputMode inputMode =
                    ActivationInputMode.SqlAutocomplete)
        {
            ValidateArguments(
                filePath,
                envValue,
                username,
                outputDir);

            ServicePointManager.DefaultConnectionLimit = 500;
            ServicePointManager.Expect100Continue = false;

            var batchStopwatch = Stopwatch.StartNew();
            DateTime batchStartTime = DateTime.Now;

            int successCount = 0;
            int alreadyActiveCount = 0;
            int errorCount = 0;
            int processedItems = 0;

            var traces =
                new ConcurrentBag<ContractActivationTrace>();

            IReadOnlyList<BatchInputItem> items =
                await ReadInputFileAsync(
                        filePath,
                        token)
                    .ConfigureAwait(false);

            int totalItems = items.Count;

            if (totalItems == 0)
            {
                throw new InvalidOperationException(
                    "The input file does not contain any usable " +
                    "contract or Demand ID.");
            }

            onBatchProgress?.Invoke(
                new BatchProgressInfo
                {
                    CurrentItem = 0,
                    TotalItems = totalItems,
                    Status = "STARTING"
                });

            string normalizedEnvironment =
                NormalizeShortEnvironment(envValue);

            string environmentSuffix =
                Settings.NormalizeEnvironmentSuffix(
                    normalizedEnvironment);

            bool fileContainsBranch23Overrides =
                items.Any(item => item.Branch23Override == true);

            bool shouldPreloadBranch23Templates =
                markAllAsBranch23 ||
                fileContainsBranch23Overrides ||
                isDemandId;

            onProgress?.Invoke(
                "Authenticating with Mainframe and preloading " +
                "JCL templates.");

            await AuthenticateAndPreloadAsync(
                    normalizedEnvironment,
                    username,
                    password,
                    shouldPreloadBranch23Templates,
                    onProgress,
                    token)
                .ConfigureAwait(false);

            using var databaseSemaphore =
                new SemaphoreSlim(
                    MaximumConcurrentDatabaseLookups,
                    MaximumConcurrentDatabaseLookups);

            using var mainframeSemaphore =
                new SemaphoreSlim(
                    MaximumConcurrentMainframeExecutions,
                    MaximumConcurrentMainframeExecutions);

            onProgress?.Invoke(
                $"Starting parallel ELIA resolution for " +
                $"{totalItems} item(s).");

            List<Task> tasks =
                items.Select(
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
                        totalItems,
                        databaseSemaphore,
                        mainframeSemaphore,
                        traces,
                        () => Interlocked.Increment(ref processedItems),
                        () => Volatile.Read(ref processedItems),
                        () => Interlocked.Increment(ref successCount),
                        () => Interlocked.Increment(
                            ref alreadyActiveCount),
                        () => Interlocked.Increment(ref errorCount),
                        onProgress,
                        onContractCompleted,
                        onBatchProgress,
                        token))
                .ToList();

            bool batchCancelled = false;

            try
            {
                await Task.WhenAll(tasks)
                    .ConfigureAwait(false);

                onProgress?.Invoke(
                    "All batch items were processed. " +
                    "Generating the diagnostic report.");
            }
            catch (OperationCanceledException)
            {
                batchCancelled = true;

                onProgress?.Invoke(
                    "Batch cancelled. Generating a partial " +
                    "diagnostic report.");
            }

            batchStopwatch.Stop();

            List<ContractActivationTrace> orderedTraces =
                traces
                    .OrderBy(trace => trace.RowNum)
                    .ToList();

            string reportPath =
                BuildReportPath(
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
                        batchCancelled ||
                        token.IsCancellationRequested,
                        successCount,
                        alreadyActiveCount,
                        errorCount,
                        inputMode)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                reportPath =
                    BuildReportPath(
                        outputDir,
                        skipPrime,
                        batchCancelled ||
                        token.IsCancellationRequested,
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
                        batchCancelled ||
                        token.IsCancellationRequested,
                        successCount,
                        alreadyActiveCount,
                        errorCount,
                        inputMode)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                onProgress?.Invoke(
                    "CRITICAL ERROR: Unable to save the batch " +
                    $"report. {exception.Message}");

                throw;
            }

            onProgress?.Invoke(
                $"Batch report generated: {reportPath}");

            if (batchCancelled || token.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Batch execution was cancelled. A partial report " +
                    $"was generated at '{reportPath}'.",
                    token);
            }

            return (
                successCount,
                alreadyActiveCount,
                errorCount,
                reportPath);
        }

        // =============================================================
        // TRAITEMENT D'UNE LIGNE
        // =============================================================

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
            Action<ContractActivationTrace>?
                onContractCompleted,
            Action<BatchProgressInfo>? onBatchProgress,
            CancellationToken token)
        {
            var contractStopwatch = Stopwatch.StartNew();

            ContractActivationTrace trace =
                CreateInitialTrace(
                    item,
                    isDemandId,
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

                await databaseSemaphore
                    .WaitAsync(token)
                    .ConfigureAwait(false);

                try
                {
                    lookup =
                        await _dataService
                            .ResolveContractForActivationAsync(
                                item.RawInput,
                                isDemandId,
                                environmentSuffix,
                                token)
                            .ConfigureAwait(false);

                    if (isDemandId)
                    {
                        routing =
                            await TryResolveRoutingAsync(
                                    item.RawInput,
                                    environmentSuffix,
                                    onProgress,
                                    token)
                                .ConfigureAwait(false);
                    }
                }
                finally
                {
                    databaseSemaphore.Release();
                }

                AutomaticActivationPlan? automaticPlan = null;
                if (inputMode.UsesSqlAutocomplete())
                {
                    IReadOnlyDictionary<string, string> rowOverrides =
                        inputMode.AllowsManualOverrides()
                            ? item.Overrides
                            : new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase);

                    automaticPlan =
                        await _dataService.ResolveAutomaticActivationPlanAsync(
                                item.RawInput,
                                isDemandId,
                                environmentSuffix,
                                cus,
                                item.Branch23Override ?? markAllAsBranch23,
                                inputMode,
                                rowOverrides,
                                token)
                            .ConfigureAwait(false);

                    if (automaticPlan.ResumeStrategy ==
                        ActivationResumeStrategy.AlreadyActive)
                    {
                        throw new InvalidOperationException("ALREADY_ACTIVE");
                    }

                    IReadOnlyList<ActivationPlanIssue> blockingIssues =
                        automaticPlan.Validate()
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
                }

                PopulateTraceFromLookup(
                    trace,
                    lookup);

                if (automaticPlan != null)
                {
                    trace.Bucp = automaticPlan.Bucp;
                    trace.Cmdpmt = automaticPlan.PaymentCommand;
                    trace.EliaChannelCode = automaticPlan.ChannelCode;
                    trace.ProductCode = automaticPlan.ProductCode;
                    trace.ProcessCode = automaticPlan.ProcessCode;
                    trace.RawPremium = automaticPlan.GrossPremium.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture);
                    trace.RoutingDiagnostic =
                        automaticPlan.RoutingRule +
                        " | BusinessChannel=" +
                        ResolveBusinessChannelName(automaticPlan.ChannelCode) +
                        " | BUCP/CMDPMT=" + automaticPlan.Bucp + "/" +
                        automaticPlan.PaymentCommand +
                        " | AutomaticRoutingStatus=" +
                        automaticPlan.RoutingStatus +
                        " | RunDate=" + automaticPlan.Drun +
                        " | RunDateSource=" + automaticPlan.RunDateSource;
                }

                string effectiveProcess = automaticPlan != null
                    ? automaticPlan.ProcessCode
                    : ResolveEffectiveProcess(
                        defaultProcess,
                        routing);

                bool effectiveBranch23 = automaticPlan != null
                    ? automaticPlan.IsBranch23
                    : ResolveEffectiveBranch23(
                        item,
                        markAllAsBranch23,
                        routing,
                        trace);

                trace.Channel =
                    effectiveProcess;

                trace.IsBranch23 =
                    effectiveBranch23;

                if (routing != null)
                {
                    AppendRoutingInformation(
                        trace,
                        routing);

                    onProgress?.Invoke(
                        $"Routing resolved | Demand={trace.DemandId} | " +
                        $"Contract={trace.ResolvedContract} | " +
                        $"Product={DisplayValue(trace.ProductCode)} | " +
                        $"Process={DisplayValue(trace.ProcessCode)} | " +
                        $"Branch={trace.BranchClassification} | " +
                        $"Status={trace.RoutingStatus}");
                }

                if (!ValidateLookupForActivation(
                        item,
                        isDemandId,
                        environment,
                        lookup,
                        routing,
                        trace))
                {
                    CompleteFailedTrace(
                        trace,
                        contractStopwatch);

                    AddTraceOnce(
                        traces,
                        trace,
                        ref traceAdded);

                    incrementError();

                    int processed =
                        incrementProcessed();

                    onContractCompleted?.Invoke(trace);

                    ReportBatchProgress(
                        onBatchProgress,
                        trace,
                        processed,
                        totalItems);

                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] ELIA lookup " +
                        $"failed for {item.RawInput}: " +
                        trace.FailureReason);

                    return;
                }

                string premium = automaticPlan != null
                    ? automaticPlan.GrossPremium.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture)
                    : ResolvePremium(
                        lookup,
                        routing);

                trace.RawPremium =
                    premium;

                if (!HasPositivePremium(premium) &&
                    !skipPrime &&
                    !string.Equals(
                        effectiveProcess,
                        "C05",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ConfigurePremiumFailure(
                        trace,
                        lookup,
                        environment);

                    CompleteFailedTrace(
                        trace,
                        contractStopwatch);

                    AddTraceOnce(
                        traces,
                        trace,
                        ref traceAdded);

                    incrementError();

                    int processed =
                        incrementProcessed();

                    onContractCompleted?.Invoke(trace);

                    ReportBatchProgress(
                        onBatchProgress,
                        trace,
                        processed,
                        totalItems);

                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] Mainframe " +
                        $"skipped for {trace.ResolvedContract}: " +
                        "missing or zero premium.");

                    return;
                }

                IBranch23FundSource? branch23FundSource =
                    BuildBranch23FundSource(
                        effectiveBranch23,
                        item.Funds,
                        defaultBranch23Funds);

                await mainframeSemaphore
                    .WaitAsync(token)
                    .ConfigureAwait(false);

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
                        $"Activating {trace.ResolvedContract} " +
                        $"({trace.JclContract}), process " +
                        $"{effectiveProcess}, BR23=" +
                        $"{effectiveBranch23}.");

                    await _dataService
                        .ExecuteActivationSequenceAsync(
                            trace.JclContract,
                            premium,
                            environment,
                            cus,
                            automaticPlan?.Bucp ?? bucp,
                            automaticPlan?.PaymentCommand ?? cmdpmt,
                            effectiveProcess,
                            skipPrime,
                            skipLogon: true,
                            username,
                            password,
                            message =>
                            {
                                onProgress?.Invoke(
                                    $"{trace.ResolvedContract}: " +
                                    message);
                            },
                            token,
                            trace,
                            effectiveBranch23,
                            branch23FundSource)
                        .ConfigureAwait(false);
                }
                finally
                {
                    mainframeSemaphore.Release();
                }

                contractStopwatch.Stop();

                trace.EndTime =
                    DateTime.Now;

                trace.TotalDurationMs =
                    contractStopwatch.ElapsedMilliseconds;

                trace.FinalStatus =
                    "SUCCESS";

                AddTraceOnce(
                    traces,
                    trace,
                    ref traceAdded);

                incrementSuccess();

                int currentSuccess =
                    incrementProcessed();

                onContractCompleted?.Invoke(trace);

                ReportBatchProgress(
                    onBatchProgress,
                    trace,
                    currentSuccess,
                    totalItems);

                onProgress?.Invoke(
                    $"[{currentSuccess}/{totalItems}] Activated: " +
                    $"{trace.ResolvedContract} " +
                    $"({trace.TotalDurationMs / 1000.0:F1}s).");
            }
            catch (OperationCanceledException)
            {
                contractStopwatch.Stop();

                trace.EndTime =
                    DateTime.Now;

                trace.TotalDurationMs =
                    contractStopwatch.ElapsedMilliseconds;

                trace.FinalStatus =
                    "CANCELLED";

                if (string.IsNullOrWhiteSpace(
                        trace.FailureStage))
                {
                    trace.FailureStage =
                        "USER_CANCELLED";
                }

                if (string.IsNullOrWhiteSpace(
                        trace.FailureReason))
                {
                    trace.FailureReason =
                        "Batch execution was cancelled by the user.";
                }

                AddTraceOnce(
                    traces,
                    trace,
                    ref traceAdded);

                onContractCompleted?.Invoke(trace);

                throw;
            }
            catch (Exception exception)
            {
                contractStopwatch.Stop();

                trace.EndTime =
                    DateTime.Now;

                trace.TotalDurationMs =
                    contractStopwatch.ElapsedMilliseconds;

                int processed =
                    incrementProcessed();

                if (string.Equals(
                        exception.Message,
                        "ALREADY_ACTIVE",
                        StringComparison.Ordinal))
                {
                    trace.FinalStatus =
                        "ALREADY_ACTIVE";

                    AddTraceOnce(
                        traces,
                        trace,
                        ref traceAdded);

                    incrementAlreadyActive();

                    onContractCompleted?.Invoke(trace);

                    ReportBatchProgress(
                        onBatchProgress,
                        trace,
                        processed,
                        totalItems);

                    onProgress?.Invoke(
                        $"[{processed}/{totalItems}] Already active: " +
                        $"{trace.ResolvedContract}.");

                    return;
                }

                trace.FinalStatus =
                    "FAILED";

                if (string.IsNullOrWhiteSpace(
                        trace.FailureReason))
                {
                    trace.FailureReason =
                        exception.Message;
                }

                if (string.IsNullOrWhiteSpace(
                        trace.FailureStage))
                {
                    trace.FailureStage =
                        "BATCH_ITEM_EXECUTION";
                }

                AddTraceOnce(
                    traces,
                    trace,
                    ref traceAdded);

                incrementError();

                onContractCompleted?.Invoke(trace);

                ReportBatchProgress(
                    onBatchProgress,
                    trace,
                    processed,
                    totalItems);

                onProgress?.Invoke(
                    $"[{processed}/{totalItems}] Failed: " +
                    $"{item.RawInput}. Stage=" +
                    $"{trace.FailureStage}. " +
                    $"{trace.FailureReason}");
            }
        }

        // =============================================================
        // ROUTING ELIA
        // =============================================================

        private async Task<PreActivationRoutingInfo?>
            TryResolveRoutingAsync(
                string demandId,
                string environmentSuffix,
                Action<string>? onProgress,
                CancellationToken token)
        {
            try
            {
                PreActivationRoutingInfo routing =
                    await _dataService
                        .ResolvePreActivationRoutingInEnvironmentAsync(
                            demandId,
                            environmentSuffix,
                            token)
                        .ConfigureAwait(false);

                return routing.RoutingStatus ==
                       PreActivationRoutingStatus.DemandNotFound
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
                    $"Routing warning for Demand {demandId}: " +
                    $"{exception.Message}. Legacy values will be used.");

                return null;
            }
        }

        private static string ResolveEffectiveProcess(
            string defaultProcess,
            PreActivationRoutingInfo? routing)
        {
            if (routing != null &&
                PreActivationRoutingService
                    .IsSupportedActivationProcess(
                        routing.ProcessCode))
            {
                return routing.ProcessCode.Trim().ToUpperInvariant();
            }

            return string.IsNullOrWhiteSpace(defaultProcess)
                ? string.Empty
                : defaultProcess.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Ordre de priorité BR23 :
        /// 1. valeur explicite de la colonne CSV BR23 ;
        /// 2. classification ELIA fiable ;
        /// 3. option globale markAllAsBranch23 ;
        /// 4. false.
        ///
        /// La colonne CSV est considérée comme un override explicite.
        /// </summary>
        private static bool ResolveEffectiveBranch23(
            BatchInputItem item,
            bool markAllAsBranch23,
            PreActivationRoutingInfo? routing,
            ContractActivationTrace trace)
        {
            if (item.Branch23Override.HasValue)
            {
                trace.Branch23Source =
                    "Input file BR23 override";

                return item.Branch23Override.Value;
            }

            if (routing?.BranchClassification ==
                ContractBranchClassification.Branch23)
            {
                trace.Branch23Source =
                    routing.BranchDetectionSource +
                    ": " +
                    routing.BranchDetectionReason;

                return true;
            }

            if (routing?.BranchClassification ==
                ContractBranchClassification.Standard)
            {
                trace.Branch23Source =
                    routing.BranchDetectionSource +
                    ": " +
                    routing.BranchDetectionReason;

                return false;
            }

            if (routing?.BranchClassification ==
                ContractBranchClassification.CompositeWithFunds)
            {
                trace.Branch23Source =
                    "ELIA fund component detected, manual BR23 " +
                    "confirmation required";

                return markAllAsBranch23;
            }

            if (markAllAsBranch23)
            {
                trace.Branch23Source =
                    "Manual whole-batch override";

                return true;
            }

            trace.Branch23Source =
                "No reliable BR23 classification";

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
                string.Equals(
                    trace.DbDiagnosticStatus,
                    "N/A",
                    StringComparison.OrdinalIgnoreCase)
                        ? routingSummary
                        : trace.DbDiagnosticStatus + " | " + routingSummary;

            if (!string.IsNullOrWhiteSpace(routing.Environment))
            {
                string resolvedEnvironment =
                    NormalizeShortEnvironment(routing.Environment);

                trace.Environment = resolvedEnvironment;
                trace.EnvIms = resolvedEnvironment == "D" ? "T" : "C";
                trace.Q2Region = resolvedEnvironment == "D" ? "Q2T" : "Q2C";
                trace.FastCtrlDsn = resolvedEnvironment == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL";
            }

            trace.DemandId = routing.DemandId.ToString(
                CultureInfo.InvariantCulture);

            if (routing.EliaContractId.HasValue)
            {
                trace.EliaUconId = routing.EliaContractId.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                trace.ResolvedContract = routing.ContractNumber.Trim();
            }

            trace.ProductCode = routing.ProductCode ?? string.Empty;
            trace.ProcessCode = routing.ProcessCode ?? string.Empty;
            trace.EliaChannelCode = routing.ChannelCode ?? string.Empty;
            trace.ApplicationChannelCode =
                routing.ApplicationChannelCode ?? string.Empty;
            trace.DemandStatus = routing.DemandStatus ?? string.Empty;

            if (PreActivationRoutingService.IsSupportedActivationProcess(
                    routing.ProcessCode))
            {
                trace.Channel = routing.ProcessCode.Trim().ToUpperInvariant();
            }

            if (routing.PremiumSequence.HasValue)
            {
                trace.PremiumSequence =
                    routing.PremiumSequence.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                trace.RawPremium = routing.GrossPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                trace.NetPremium = routing.NetPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            trace.PaymentMethodCode =
                routing.PaymentMethodCode ?? string.Empty;
            trace.PremiumGroupType =
                routing.PremiumGroupType ?? string.Empty;
            trace.FundFeatureCount = routing.FundFeatureCount;
            trace.BranchClassification = routing.BranchClassification;
            trace.BranchDetectionSource = routing.BranchDetectionSource;
            trace.BranchDetectionReason = routing.BranchDetectionReason;
            trace.RoutingStatus = routing.RoutingStatus;
            string routingEvidence =
                $"BusinessChannel={routing.BusinessChannelName} | " +
                $"ChannelCode={routing.NormalizedChannelCode} | " +
                $"BUCP/CMDPMT={routing.ProposedBucp}/" +
                $"{routing.ProposedPaymentCommand} | " +
                $"Rule={routing.PaymentRoutingRule}";
            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(
                    routing.RoutingDiagnostic)
                ? routingEvidence
                : routing.RoutingDiagnostic + " | " + routingEvidence;
            if (string.IsNullOrWhiteSpace(trace.Bucp))
                trace.Bucp = routing.ProposedBucp;
            if (string.IsNullOrWhiteSpace(trace.Cmdpmt))
                trace.Cmdpmt = routing.ProposedPaymentCommand;

            if (routing.BranchClassification ==
                ContractBranchClassification.Branch23)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source =
                    $"{routing.BranchDetectionSource}: " +
                    routing.BranchDetectionReason;
            }
            else if (routing.BranchClassification ==
                     ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source =
                    $"{routing.BranchDetectionSource}: " +
                    routing.BranchDetectionReason;
            }
        }

        // =============================================================
        // VALIDATION DU LOOKUP
        // =============================================================

        private static bool ValidateLookupForActivation(
            BatchInputItem item,
            bool isDemandId,
            string environment,
            ActivationLookupResult lookup,
            PreActivationRoutingInfo? routing,
            ContractActivationTrace trace)
        {
            if (isDemandId &&
                string.IsNullOrWhiteSpace(
                    lookup.ContractNumber))
            {
                trace.FinalStatus =
                    "FAILED";

                trace.FailureStage =
                    routing?.RoutingStatus ==
                    PreActivationRoutingStatus
                        .EliaContractNotLinked
                        ? "ELIA_CONTRACT_NOT_LINKED"
                        : "DB_DEMAND_LOOKUP";

                trace.FailureReason =
                    $"No contract number is currently associated " +
                    $"with Demand ID '{item.RawInput}' in " +
                    $"{environment}000.";

                trace.RootCauseAnalysis =
                    "The Demand exists without a linked UCON contract, " +
                    "or TB5UCON does not yet contain an external " +
                    "contract reference.";

                trace.RecommendedAction =
                    "Verify the Demand status in TB5HDMD and the links " +
                    "in TB5HELT/TB5UCON. Retry after ELIA creates the " +
                    "contract reference.";

                return false;
            }

            if (string.IsNullOrWhiteSpace(
                    lookup.ContractNumber))
            {
                trace.FinalStatus =
                    "FAILED";

                trace.FailureStage =
                    "DB_CONTRACT_NOT_FOUND";

                trace.FailureReason =
                    $"Contract '{item.RawInput}' was not found in " +
                    $"{environment}000.";

                trace.RootCauseAnalysis =
                    "No matching contract reference was found in " +
                    "TB5UCON.";

                trace.RecommendedAction =
                    "Verify the contract format and environment.";

                return false;
            }

            if (string.IsNullOrWhiteSpace(
                    lookup.JclContract))
            {
                trace.FinalStatus =
                    "FAILED";

                trace.FailureStage =
                    "JCL_CONTRACT_NOT_RESOLVED";

                trace.FailureReason =
                    $"The JCL contract identifier could not be " +
                    $"generated from '{lookup.ContractNumber}'.";

                trace.RootCauseAnalysis =
                    "The resolved contract reference does not match " +
                    "the expected extended contract format.";

                trace.RecommendedAction =
                    "Verify the contract format before submitting jobs.";

                return false;
            }

            return true;
        }

        private static void ConfigurePremiumFailure(
            ContractActivationTrace trace,
            ActivationLookupResult lookup,
            string environment)
        {
            trace.FinalStatus =
                "FAILED";

            trace.FailureStage =
                lookup.FoundInElia
                    ? "DB_PREMIUM_ZERO"
                    : "DB_CONTRACT_NOT_FOUND";

            trace.FailureReason =
                lookup.FoundInElia
                    ? $"Contract '{trace.ResolvedContract}' " +
                      $"has no positive gross premium in TB5UPRP."
                    : $"Contract '{trace.RawInput}' does not exist " +
                      $"in ELIA environment {environment}000.";

            trace.RootCauseAnalysis =
                lookup.FoundInElia
                    ? "ADDPRCT requires IT5UPRPUBRU to be greater " +
                      "than zero when Skip Prime is disabled."
                    : "No ELIA contract was found for the supplied " +
                      "identifier.";

            trace.RecommendedAction =
                lookup.FoundInElia
                    ? "Verify TB5UPRP, or enable Skip Prime if the " +
                      "contract requires activation without premium."
                    : "Verify the input identifier and environment.";
        }

        // =============================================================
        // LECTURE CSV
        // =============================================================

        private static async Task<IReadOnlyList<BatchInputItem>>
            ReadInputFileAsync(
                string filePath,
                CancellationToken token)
        {
            var items =
                new List<BatchInputItem>();

            using var stream =
                new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

            using var reader =
                new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);

            string? line;
            char delimiter = ';';

            int identifierIndex = -1;
            int branch23Index = -1;
            int fundsIndex = -1;
            var overrideIndexes = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            bool headerFound = false;

            while ((line =
                    await reader.ReadLineAsync()
                        .ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();

                line = RemoveBom(line);

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line
                    .Replace("\"", string.Empty)
                    .TrimStart()
                    .StartsWith(
                        "Contract in",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                delimiter =
                    line.Count(character => character == ';') >
                    line.Count(character => character == ',')
                        ? ';'
                        : ',';

                List<string> headers =
                    CsvFormatter.ParseCsvLine(
                        line,
                        delimiter);

                for (int index = 0;
                     index < headers.Count;
                     index++)
                {
                    string header =
                        headers[index]
                            .Trim()
                            .ToLowerInvariant();

                    if (IsBranch23Header(header))
                    {
                        branch23Index = index;
                        continue;
                    }

                    if (IsFundsHeader(header))
                    {
                        fundsIndex = index;
                        continue;
                    }

                    string? overrideKey = GetOverrideKey(header);
                    if (overrideKey != null)
                    {
                        overrideIndexes[overrideKey] = index;
                        continue;
                    }
                    if (IsIdentifierHeader(header))
                    {
                        identifierIndex = index;
                    }
                }

                if (identifierIndex >= 0)
                {
                    headerFound = true;
                    break;
                }
            }

            if (!headerFound)
            {
                throw new InvalidOperationException(
                    "No Contract or Demand column was found in " +
                    "the input file.");
            }

            int currentRow = 1;

            while ((line =
                    await reader.ReadLineAsync()
                        .ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                List<string> columns =
                    CsvFormatter.ParseCsvLine(
                        line,
                        delimiter);

                if (columns.Count <= identifierIndex)
                {
                    continue;
                }

                string rawInput =
                    CleanCsvValue(
                        columns[identifierIndex]);

                if (string.IsNullOrWhiteSpace(rawInput) ||
                    string.Equals(
                        rawInput,
                        "End of File",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool? branch23Override =
                    branch23Index >= 0 &&
                    columns.Count > branch23Index
                        ? ParseNullableBoolean(
                            columns[branch23Index])
                        : null;

                string? funds =
                    fundsIndex >= 0 &&
                    columns.Count > fundsIndex
                        ? NullIfWhiteSpace(
                            CleanCsvValue(
                                columns[fundsIndex]))
                        : null;

                var rowOverrides = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, int> mapping in overrideIndexes)
                {
                    if (columns.Count <= mapping.Value)
                    {
                        continue;
                    }
                    string overrideValue = CleanCsvValue(columns[mapping.Value]);
                    if (!string.IsNullOrWhiteSpace(overrideValue))
                    {
                        rowOverrides[mapping.Key] = overrideValue;
                    }
                }

                items.Add(
                    new BatchInputItem
                    {
                        RawInput = rawInput,
                        RowNumber = currentRow++,
                        Branch23Override = branch23Override,
                        Funds = funds,
                        Overrides = rowOverrides
                    });
            }

            return items;
        }

        // =============================================================
        // AUTHENTIFICATION ET PRÉCHARGEMENT
        // =============================================================

        private static async Task AuthenticateAndPreloadAsync(
            string environment,
            string username,
            string password,
            bool preloadBranch23,
            Action<string>? onProgress,
            CancellationToken token)
        {
            var apiService =
                new MicroFocusApiService();

            var jclProcessor =
                new JclProcessorService(
                    Settings.JclNetworkFolder);

            Task<bool> logonTask =
                apiService.LogonAsync(
                    username,
                    password,
                    environment,
                    message =>
                        onProgress?.Invoke(message),
                    token);

            Task preloadTask =
                preloadBranch23
                    ? jclProcessor.PreloadTemplatesAsync(
                        JclProcessorService
                            .StandardActivationJobs
                            .Concat(
                                ActivationOrchestrator
                                    .Branch23JclNames)
                            .Distinct(
                                StringComparer.OrdinalIgnoreCase))
                    : jclProcessor.PreloadTemplatesAsync();

            await Task.WhenAll(
                    logonTask,
                    preloadTask)
                .ConfigureAwait(false);

            bool authenticated =
                await logonTask.ConfigureAwait(false);

            if (!authenticated)
            {
                throw new InvalidOperationException(
                    "Initial Mainframe authentication failed. " +
                    "Verify VPN connectivity, credentials and the " +
                    "selected environment.");
            }
        }

        // =============================================================
        // RAPPORT
        // =============================================================

        private static async Task WriteBatchReportAsync(
            string reportPath,
            DateTime batchStartTime,
            long totalBatchMilliseconds,
            bool skipPrime,
            string environment,
            string defaultProcess,
            string cus,
            string bucp,
            string cmdpmt,
            IReadOnlyList<ContractActivationTrace> orderedTraces,
            bool isCancelled,
            int successCount,
            int alreadyActiveCount,
            int errorCount,
            ActivationInputMode inputMode)
        {
            var builder =
                new StringBuilder(32768);

            int totalProcessed =
                orderedTraces.Count;

            double averageSeconds =
                totalProcessed > 0
                    ? totalBatchMilliseconds /
                      1000.0 /
                      totalProcessed
                    : 0.0;

            double activeRate =
                totalProcessed > 0
                    ? Math.Round(
                        (double)(
                            successCount +
                            alreadyActiveCount) /
                        totalProcessed *
                        100.0,
                        1)
                    : 0.0;

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                "               AUTO-ACTIVATOR - DETAILED BATCH ACTIVATION REPORT");

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                $"Execution Date     : " +
                $"{batchStartTime:yyyy-MM-dd HH:mm:ss} -> " +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            builder.AppendLine(
                $"Total Batch Time   : " +
                $"{totalBatchMilliseconds / 1000.0:F2}s " +
                $"({averageSeconds:F2}s average/item)");

            builder.AppendLine(
                $"Batch State        : " +
                $"{(isCancelled
                    ? "CANCELLED, PARTIAL REPORT"
                    : "COMPLETED")}");

            builder.AppendLine(
                $"Environment        : {environment}000");

            builder.AppendLine(
                $"ESCWA Node         : " +
                $"{MicroFocusApiService.ActiveServer ?? "N/A"}");

            builder.AppendLine(
                $"Input Mode         : {inputMode}");
            builder.AppendLine(
                $"Default Process    : {defaultProcess}");

            builder.AppendLine(
                $"Business Routing   : " +
                $"CUS='{cus}' | " +
                $"Default BUCP='{(bucp ?? string.Empty).PadLeft(5, '0')}' | " +
                $"Default CMDPMT='{cmdpmt}' | " +
                $"Per-row SQL routing={(inputMode.UsesSqlAutocomplete() ? "ENABLED" : "DISABLED")} | " +
                $"Skip Prime={(skipPrime ? "YES" : "NO")}");

            builder.AppendLine(
                $"Branch 23          : " +
                $"{orderedTraces.Count(trace => trace.IsBranch23)} " +
                "item(s)");

            builder.AppendLine(
                "---------------------------------------------------------------------------------------------------------");

            builder.AppendLine(
                $"Summary            : " +
                $"Processed={totalProcessed} | " +
                $"Activated={successCount} | " +
                $"Already Active={alreadyActiveCount} | " +
                $"Failed={errorCount} | " +
                $"Active Rate={activeRate:F1}%");

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine();

            AppendOverviewSection(
                builder,
                orderedTraces);

            AppendDiagnosticSection(
                builder,
                orderedTraces);

            AppendSuccessfulSection(
                builder,
                orderedTraces);

            using var writer =
                new StreamWriter(
                    reportPath,
                    append: false,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: true));

            await writer.WriteAsync(
                    builder.ToString())
                .ConfigureAwait(false);
        }

        private static void AppendOverviewSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                "SECTION 1 - QUICK OVERVIEW");

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                "Row  | Input / Demand   | Contract        | JCL ID    | " +
                "Product | Proc | Ch | Canal          | BUCP  | CMD | BR23 | Premium    | Duration | Status");

            builder.AppendLine(
                "-----+------------------+-----------------+-----------+" +
                "---------+------+----+----------------+-------+-----+------+------------+----------+----------------");

            foreach (ContractActivationTrace trace in traces)
            {
                string duration =
                    $"{trace.TotalDurationMs / 1000.0:F1}s";

                string status =
                    trace.FinalStatus switch
                    {
                        "SUCCESS" =>
                            "SUCCESS",

                        "ALREADY_ACTIVE" =>
                            "ALREADY ACTIVE",

                        "CANCELLED" =>
                            "CANCELLED",

                        _ =>
                            "FAILED: " +
                            trace.FailureStage
                    };

                builder.AppendLine(
                    $"{trace.RowNum,-4} | " +
                    $"{Truncate(trace.RawInput, 16),-16} | " +
                    $"{Truncate(trace.ResolvedContract, 15),-15} | " +
                    $"{Truncate(trace.JclContract, 9),-9} | " +
                    $"{Truncate(trace.ProductCode, 7),-7} | " +
                    $"{Truncate(trace.Channel, 4),-4} | " +
                    $"{Truncate(trace.EliaChannelCode, 2),-2} | " +
                    $"{Truncate(ResolveBusinessChannelName(trace.EliaChannelCode), 14),-14} | " +
                    $"{Truncate(trace.Bucp, 5),-5} | " +
                    $"{Truncate(trace.Cmdpmt, 3),-3} | " +
                    $"{(trace.IsBranch23 ? "YES" : "NO"),-4} | " +
                    $"{Truncate(trace.RawPremium, 10),-10} | " +
                    $"{duration,8} | " +
                    status);
            }

            builder.AppendLine();
        }

        private static void AppendDiagnosticSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            List<ContractActivationTrace> nonActivated =
                traces
                    .Where(
                        trace =>
                            !string.Equals(
                                trace.FinalStatus,
                                "SUCCESS",
                                StringComparison.OrdinalIgnoreCase))
                    .ToList();

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                $"SECTION 2 - NON-ACTIVATED DIAGNOSTICS " +
                $"({nonActivated.Count})");

            builder.AppendLine(
                "=========================================================================================================");

            if (nonActivated.Count == 0)
            {
                builder.AppendLine(
                    "All processed items completed successfully.");

                builder.AppendLine();

                return;
            }

            foreach (ContractActivationTrace trace in
                     nonActivated)
            {
                ActivationDataService
                    .AppendContractTraceDetails(
                        builder,
                        trace,
                        includeHeaderBanner: true);
            }
        }

        private static void AppendSuccessfulSection(
            StringBuilder builder,
            IReadOnlyList<ContractActivationTrace> traces)
        {
            List<ContractActivationTrace> successful =
                traces
                    .Where(
                        trace =>
                            string.Equals(
                                trace.FinalStatus,
                                "SUCCESS",
                                StringComparison.OrdinalIgnoreCase))
                    .ToList();

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                $"SECTION 3 - SUCCESSFUL EXECUTION TIMELINES " +
                $"({successful.Count})");

            builder.AppendLine(
                "=========================================================================================================");

            if (successful.Count == 0)
            {
                builder.AppendLine(
                    "No item completed with SUCCESS.");

                return;
            }

            foreach (ContractActivationTrace trace in
                     successful)
            {
                ActivationDataService
                    .AppendContractTraceDetails(
                        builder,
                        trace,
                        includeHeaderBanner: true);
            }
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private static ContractActivationTrace CreateInitialTrace(
            BatchInputItem item,
            bool isDemandId,
            string environment,
            string cus,
            string bucp,
            string cmdpmt,
            string process,
            bool skipPrime,
            string username)
        {
            return new ContractActivationTrace
            {
                RowNum = item.RowNumber,
                RawInput = item.RawInput,
                IsDemandId = isDemandId,
                Environment = environment,
                EnvIms = environment == "D"
                    ? "T"
                    : "C",
                Q2Region = environment == "D"
                    ? "Q2T"
                    : "Q2C",
                FastCtrlDsn = environment == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL",
                ServerNode =
                    MicroFocusApiService.ActiveServer
                    ?? "N/A",
                Channel = process,
                Cus = cus,
                Bucp = string.IsNullOrWhiteSpace(bucp)
                    ? string.Empty
                    : bucp.Trim().PadLeft(5, '0'),
                Cmdpmt = cmdpmt,
                SkipPrime = skipPrime,
                Username = username,
                StartTime = DateTime.Now,
                FinalStatus = "PENDING"
            };
        }

        private static void PopulateTraceFromLookup(
    ContractActivationTrace trace,
    ActivationLookupResult lookup)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(lookup);

            // =============================================================
            // RÉSOLUTION SQL ET DIAGNOSTIC
            // =============================================================

            trace.DbLookupDurationMs =
                lookup.LookupDurationMs;

            trace.DbDiagnosticStatus =
                lookup.DbDiagnosticStatus;

            trace.DbError =
                lookup.DbError;

            // =============================================================
            // IDENTIFIANTS ELIA ET LISA
            // =============================================================

            trace.EliaUconId =
                lookup.EliaUconId;

            trace.DemandId =
                lookup.DemandId;

            trace.LisaInternalId =
                lookup.LisaInternalId;

            trace.LisaContractNumber =
                lookup.LisaContractNumber;

            trace.ResolvedContract =
                lookup.ContractNumber;

            trace.JclContract =
                lookup.JclContract;

            // =============================================================
            // ENVIRONNEMENT RÉSOLU
            // =============================================================

            if (!string.IsNullOrWhiteSpace(
                    lookup.ResolvedEnvironment))
            {
                trace.Environment =
                    lookup.ResolvedEnvironment.Trim().ToUpperInvariant();

                trace.EnvIms =
                    string.Equals(
                        trace.Environment,
                        "D",
                        StringComparison.OrdinalIgnoreCase)
                        ? "T"
                        : "C";

                trace.Q2Region =
                    string.Equals(
                        trace.Environment,
                        "D",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Q2T"
                        : "Q2C";

                trace.FastCtrlDsn =
                    string.Equals(
                        trace.Environment,
                        "D",
                        StringComparison.OrdinalIgnoreCase)
                        ? "I0T.DB.CA.FIB.FASTCTRL"
                        : "I10.DB.CA.FIB.FASTCTRL";
            }

            // =============================================================
            // ROUTING ELIA
            // =============================================================

            trace.ProductCode =
                lookup.ProductCode;

            trace.ProcessCode =
                lookup.ProcessCode;

            trace.EliaChannelCode =
                lookup.ChannelCode;

            trace.ApplicationChannelCode =
                lookup.ApplicationChannelCode;

            trace.DemandStatus =
                lookup.DemandStatus;

            /*
             * Si le process ELIA est pris en charge, il devient le process
             * effectif utilisé pour sélectionner le workflow JCL.
             */
            if (PreActivationRoutingService
                .IsSupportedActivationProcess(
                    lookup.ProcessCode))
            {
                trace.Channel =
                    lookup.ProcessCode.Trim().ToUpperInvariant();
            }

            // =============================================================
            // PRIME
            // =============================================================

            trace.PremiumSequence =
                lookup.PremiumSequence;

            trace.RawPremium =
                lookup.RawPremium;

            trace.NetPremium =
                lookup.NetPremium;

            // =============================================================
            // MODE DE PAIEMENT ELIA
            // =============================================================

            trace.PaymentMethodCode =
                lookup.PaymentMethodCode;

            trace.PremiumGroupType =
                lookup.PremiumGroupType;

            /*
             * PaymentMethodCode reste une donnée ELIA informative.
             * Cette valeur ne doit pas remplacer trace.Cmdpmt tant que
             * le mapping ELIA vers CMDPMT n'est pas validé.
             */

            // =============================================================
            // FONDS ET CLASSIFICATION DU CONTRAT
            // =============================================================

            trace.FundFeatureCount =
                lookup.FundFeatureCount;

            trace.BranchClassification =
                lookup.BranchClassification;

            trace.BranchDetectionSource =
                lookup.BranchDetectionSource;

            trace.BranchDetectionReason =
                lookup.BranchDetectionReason;

            /*
             * La classification BR23 fiable est propagée dans la trace.
             *
             * CompositeWithFunds n'est pas automatiquement assimilé à BR23.
             * Unknown ne remplace pas une éventuelle sélection manuelle.
             */
            if (lookup.BranchClassification ==
                ContractBranchClassification.Branch23)
            {
                trace.IsBranch23 = true;

                trace.Branch23Source =
                    $"{lookup.BranchDetectionSource}: " +
                    $"{lookup.BranchDetectionReason}";
            }
            else if (lookup.BranchClassification ==
                     ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;

                trace.Branch23Source =
                    $"{lookup.BranchDetectionSource}: " +
                    $"{lookup.BranchDetectionReason}";
            }

            // =============================================================
            // STATUT ET DIAGNOSTIC DU ROUTING
            // =============================================================

            trace.RoutingStatus =
                lookup.RoutingStatus;

            trace.RoutingDiagnostic =
                lookup.RoutingDiagnostic;
        }

        private static void ReportBatchProgress(
            Action<BatchProgressInfo>? onBatchProgress,
            ContractActivationTrace trace,
            int currentItem,
            int totalItems,
            string? statusOverride = null)
        {
            onBatchProgress?.Invoke(
                new BatchProgressInfo
                {
                    RowNum = trace.RowNum,
                    ContractId = trace.ResolvedContract,
                    InternalId = trace.LisaInternalId,
                    Product = trace.ProductCode,
                    Premium = trace.RawPremium,
                    UconId = trace.EliaUconId,
                    DemandId = trace.DemandId,
                    ProcessCode = !string.IsNullOrWhiteSpace(trace.ProcessCode)
                        ? trace.ProcessCode
                        : trace.Channel,
                    IsBranch23 = trace.IsBranch23,
                    Status = string.IsNullOrWhiteSpace(statusOverride)
                        ? trace.FinalStatus
                        : statusOverride,
                    CurrentItem = currentItem,
                    TotalItems = totalItems
                });
        }

        private static string ResolvePremium(
            ActivationLookupResult lookup,
            PreActivationRoutingInfo? routing)
        {
            if (HasPositivePremium(
                    lookup.RawPremium))
            {
                return NormalizePremium(
                    lookup.RawPremium);
            }

            if (routing?.GrossPremium is > 0m)
            {
                return routing.GrossPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string NormalizePremium(
            string? value)
        {
            string cleaned =
                (value ?? "0")
                    .Replace(",", ".")
                    .Trim();

            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount)
                ? amount.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture)
                : "0";
        }

        private static bool HasPositivePremium(
            string? value)
        {
            string cleaned =
                (value ?? "0")
                    .Replace(",", ".")
                    .Trim();

            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount) &&
                   amount > 0m;
        }

        private static IBranch23FundSource?
            BuildBranch23FundSource(
                bool isBranch23,
                string? rowFunds,
                string? defaultFunds)
        {
            if (!isBranch23)
            {
                return null;
            }

            string? configuredFunds =
                !string.IsNullOrWhiteSpace(rowFunds)
                    ? rowFunds
                    : defaultFunds;

            IReadOnlyList<string> parsedFunds =
                ConfiguredBranch23FundSource.Parse(
                    configuredFunds);

            return new ConfiguredBranch23FundSource(
                parsedFunds);
        }

        private static void CompleteFailedTrace(
            ContractActivationTrace trace,
            Stopwatch stopwatch)
        {
            stopwatch.Stop();

            trace.EndTime =
                DateTime.Now;

            trace.TotalDurationMs =
                stopwatch.ElapsedMilliseconds;

            trace.FinalStatus =
                "FAILED";
        }

        private static void AddTraceOnce(
            ConcurrentBag<ContractActivationTrace> traces,
            ContractActivationTrace trace,
            ref bool traceAdded)
        {
            if (traceAdded)
            {
                return;
            }

            traces.Add(trace);
            traceAdded = true;
        }

        private static string BuildReportPath(
            string outputDirectory,
            bool skipPrime,
            bool cancelled,
            bool includeUniqueSuffix = false)
        {
            string skipSuffix =
                skipPrime
                    ? "_SKIP_PRIME"
                    : string.Empty;

            string cancellationSuffix =
                cancelled
                    ? "_PARTIAL_CANCELLED"
                    : string.Empty;

            string uniqueSuffix =
                includeUniqueSuffix
                    ? "_" +
                      Guid.NewGuid()
                          .ToString("N")
                          .Substring(0, 6)
                    : string.Empty;

            string fileName =
                $"Activation_Batch" +
                $"{skipSuffix}" +
                $"{cancellationSuffix}_" +
                $"{DateTime.Now:yyyyMMdd_HHmmss}" +
                $"{uniqueSuffix}.txt";

            return Path.Combine(
                outputDirectory,
                fileName);
        }

        private static void ValidateArguments(
            string filePath,
            string environment,
            string username,
            string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException(
                    "The input file path is required.",
                    nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    "The batch input file does not exist.",
                    filePath);
            }

            if (string.IsNullOrWhiteSpace(environment))
            {
                throw new ArgumentException(
                    "The environment is required.",
                    nameof(environment));
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException(
                    "The Mainframe username is required.",
                    nameof(username));
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException(
                    "The output directory is required.",
                    nameof(outputDirectory));
            }
        }

        private static string NormalizeShortEnvironment(
            string environment)
        {
            string normalized =
                (environment ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            if (normalized.StartsWith(
                    "D",
                    StringComparison.Ordinal))
            {
                return "D";
            }

            if (normalized.StartsWith(
                    "Q",
                    StringComparison.Ordinal))
            {
                return "Q";
            }

            throw new ArgumentException(
                $"Unsupported batch environment: '{environment}'. " +
                "Only D and Q are currently supported.",
                nameof(environment));
        }

        private static string CleanCsvValue(
            string? value)
        {
            string cleaned =
                (value ?? string.Empty)
                    .Replace("\u00A0", string.Empty)
                    .Replace("\uFEFF", string.Empty)
                    .Trim();

            if (cleaned.StartsWith(
                    "=",
                    StringComparison.Ordinal))
            {
                cleaned =
                    cleaned.Substring(1);
            }

            if (cleaned.Length >= 2 &&
                cleaned.StartsWith(
                    "\"",
                    StringComparison.Ordinal) &&
                cleaned.EndsWith(
                    "\"",
                    StringComparison.Ordinal))
            {
                cleaned =
                    cleaned.Substring(
                        1,
                        cleaned.Length - 2);
            }

            return cleaned
                .Replace("\"\"", "\"")
                .Trim();
        }

        private static string RemoveBom(
            string line)
        {
            return line.StartsWith(
                    "\uFEFF",
                    StringComparison.Ordinal)
                ? line.Substring(1)
                : line;
        }

        private static bool? ParseNullableBoolean(
            string? value)
        {
            string normalized =
                CleanCsvValue(value)
                    .Trim()
                    .ToUpperInvariant();

            if (normalized is
                "Y" or
                "YES" or
                "O" or
                "OUI" or
                "1" or
                "TRUE")
            {
                return true;
            }

            if (normalized is
                "N" or
                "NO" or
                "NON" or
                "0" or
                "FALSE")
            {
                return false;
            }

            return null;
        }

        private static string? GetOverrideKey(string header)
        {
            string normalized = (header ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToUpperInvariant();

            return normalized switch
            {
                "AMOUNT" or "AMOUNTOVERRIDE" or "PREMIUM" => "AMOUNT",
                "RUNDATE" or "DRUN" or "DATEOVERRIDE" => "DRUN",
                "BUCP" or "BUCPOVERRIDE" => "BUCP",
                "CMDPMT" or "CMD" or "PAYMENTCOMMAND" => "CMDPMT",
                "PROCESS" or "PROCESSCODE" => "PROCESS",
                "CHANNEL" or "CHANNELCODE" => "CHANNEL",
                _ => null
            };
        }

        private static bool IsIdentifierHeader(
            string header)
        {
            return header.Contains("contract") ||
                   header.Contains("contrat") ||
                   header.Contains("lisa") ||
                   header.Contains("value") ||
                   header.Contains("demand");
        }

        private static bool IsBranch23Header(
            string header)
        {
            return header == "br23" ||
                   header.Contains("branch23") ||
                   header.Contains("branch 23") ||
                   header.Contains("branche23") ||
                   header.Contains("branche 23");
        }

        private static bool IsFundsHeader(
            string header)
        {
            return header == "funds" ||
                   header == "fund" ||
                   header == "fonds" ||
                   header.Contains("br23 fund") ||
                   header.Contains("fund codes");
        }

        private static string ResolveBusinessChannelName(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank => "BANK",
                PreActivationBusinessChannel.BrokerNewGeneration =>
                    "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }

        private static string DisplayValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "-"
                : value.Trim();
        }

        private static string Truncate(
            string? value,
            int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            if (maximumLength <= 1)
            {
                return value.Substring(0, 1);
            }

            return value.Length <= maximumLength
                ? value
                : value.Substring(
                      0,
                      maximumLength - 1) +
                  "…";
        }

        private static string? NullIfWhiteSpace(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static bool IsMissingValue(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(
                       value.Trim(),
                       "N/A",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       value.Trim(),
                       "Not found",
                       StringComparison.OrdinalIgnoreCase);
        }

        private sealed class BatchInputItem
        {
            public string RawInput { get; init; } =
                string.Empty;

            public int RowNumber { get; init; }

            public bool? Branch23Override { get; init; }

            public string? Funds { get; init; }

            public IReadOnlyDictionary<string, string> Overrides { get; init; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

    }
}