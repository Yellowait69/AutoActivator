using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services.Branch23Activation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Orchestre la pr paration, la soumission et le suivi des jobs JCL
    /// n cessaires   l'activation d'un contrat.
    ///
    /// Le workflow standard contient :
    /// - ADDPRCT ;
    /// - LVPP06U ;
    /// - LVPG22U ;
    /// - LI1J04D0 / LI1J04D1 ;
    /// - LI1J04D2 / LI1J04D3.
    ///
    /// Pour un contrat Branche 23, le workflow est ex cut  en trois phases :
    /// - activation standard initiale ;
    /// - LVDQFG03, LVDQFG01 et LVD1PE01 ;
    /// - seconde passe IMS LI1J04D0/D1 puis LI1J04D2/D3, sans prime.
    /// La validation LISA est ex cut e seulement apr s cette seconde passe.
    ///
    /// Ce service :
    /// - ne d termine pas lui-m me si le contrat est BR23 ;
    /// - re oit le workflow BR23 depuis ActivationDataService ;
    /// - capture les m triques d'ex cution ;
    /// - r cup re le Spool en cas d'erreur ;
    /// - arr te imm diatement la s quence lorsqu'une  tape est bloquante.
    /// </summary>
    public sealed partial class ActivationOrchestrator
    {
        private readonly JclProcessorService _jclProcessor;
        private readonly MicroFocusApiService _apiService;

        // Steps executed by the Branch23ActivationOnly runner: strict variables,
        // job card CLASS from variables, ADDPRCT RC=8 returned for PRCTT0 control.
        private readonly HashSet<JobStepExecutionInfo> _branch23ActivationSteps = new();

        private static readonly
            (string JobName, string SubJobName, string Phase)[]
            SequenceDefinition =
            {
                (
                    "ADDPRCT",
                    "-",
                    "Phase 1: Billing & Premium Injection (ADDPRCT)"
                ),
                (
                    "LVPP06U",
                    "-",
                    "Phase 1: Premium Accounting Validation (LVPP06U)"
                ),
                (
                    "LVPG22U",
                    "-",
                    "Phase 1: Financial Posting & Clearing (LVPG22U)"
                ),
                (
                    "LI1J04D0",
                    "LI1J04D1",
                    "Phase 2: Core IMS Contract Activation " +
                    "(LI1J04D0 -> LI1J04D1)"
                ),
                (
                    "LI1J04D2",
                    "LI1J04D3",
                    "Phase 2: Final IMS Synchronization " +
                    "(LI1J04D2 -> LI1J04D3)"
                )
            };

        private static readonly
            (string JobName, string JclName, string Phase)[]
            Branch23SequenceDefinition =
            {
                (
                    Settings.Branch23.FundEvents.JobName,
                    Settings.Branch23.FundEvents.JclName,
                    "Phase 3 BR23: Fund Events " +
                    "(LVDQFG03 -> LVFG03U)"
                ),
                (
                    Settings.Branch23.Consolidation.JobName,
                    Settings.Branch23.Consolidation.JclName,
                    "Phase 3 BR23: Consolidation Seq 1, STEP=1 " +
                    "(LVDQFG01 -> LVFG01U)"
                ),
                (
                    Settings.Branch23.Fp01.JobName,
                    Settings.Branch23.Fp01.JclName,
                    "Phase 3 BR23: FP01 Event Seq 3 " +
                    "(LVD1PE01 -> LVPE01U)"
                )
            };

        /// <summary>
        /// Seconde passe standard obligatoire apr s les trois jobs BR23.
        /// Elle rejoue uniquement les interfaces IMS et ne rejoue jamais
        /// ADDPRCT, LVPP06U ou LVPG22U.
        /// </summary>
        private static readonly
            (string JobName, string SubJobName, string Phase)[]
            Branch23FinalStandardPassDefinition =
            {
                (
                    "LI1J04D0",
                    "LI1J04D1",
                    "Phase 4 BR23: Final IMS Activation " +
                    "(LI1J04D0 -> LI1J04D1, Skip Prime)"
                ),
                (
                    "LI1J04D2",
                    "LI1J04D3",
                    "Phase 4 BR23: Final IMS Synchronization " +
                    "(LI1J04D2 -> LI1J04D3, Skip Prime)"
                )
            };

        /// <summary>
        /// Noms des templates JCL n cessaires au workflow BR23.
        /// </summary>
        public static string[] Branch23JclNames =>
            Branch23SequenceDefinition
                .Select(definition => definition.JclName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public ActivationOrchestrator(string jclDirectory)
        {
            if (string.IsNullOrWhiteSpace(jclDirectory))
            {
                throw new ArgumentException(
                    "Le r pertoire contenant les JCL est obligatoire.",
                    nameof(jclDirectory));
            }

            _jclProcessor =
                new JclProcessorService(jclDirectory);

            _apiService =
                new MicroFocusApiService();
        }

        /// <summary>
        /// Pr charge les templates standards.
        /// </summary>
        public Task PreloadJclTemplatesAsync()
        {
            return _jclProcessor.PreloadTemplatesAsync();
        }

        /// <summary>
        /// Pr charge les templates standards et les templates BR23.
        /// </summary>
        public Task PreloadAllJclTemplatesAsync()
        {
            IEnumerable<string> allTemplates =
                JclProcessorService.StandardActivationJobs
                    .Concat(Branch23JclNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            return _jclProcessor.PreloadTemplatesAsync(
                allTemplates);
        }

        /// <summary>
        /// Ex cute la s quence compl te d'activation.
        ///
        /// Le workflow BR23 est activ  uniquement lorsque
        /// branch23JobVariables n'est pas null.
        /// </summary>
        public async Task RunActivationSequenceAsync(
            Dictionary<string, string> generalVariables,
            Dictionary<string, string> addprctSpecificVariables,
            string username,
            string password,
            string channel,
            bool skipPrime,
            bool skipLogon,
            Action<string>? onProgress,
            CancellationToken cancellationToken = default,
            ContractActivationTrace? trace = null,
            Dictionary<string, Dictionary<string, string>>?
                branch23JobVariables = null,
            Func<CancellationToken, Task>?
                prepareBranch23NavsAsync = null,
            Func<CancellationToken, Task>?
                beforeBranch23FundEventsAsync = null,
            Func<CancellationToken, Task>?
                afterBranch23FundEventsAsync = null,
            Func<CancellationToken, Task>?
                beforeInitialLiValidationAsync = null,
            Func<CancellationToken, Task>?
                afterActivationValidationAsync = null,
            Func<string, string, CancellationToken, Task<string>>?
                resolveGeneratedJobNumberAsync = null)
        {
            if (generalVariables == null)
            {
                throw new ArgumentNullException(
                    nameof(generalVariables));
            }

            if (addprctSpecificVariables == null)
            {
                throw new ArgumentNullException(
                    nameof(addprctSpecificVariables));
            }

            trace ??=
                new ContractActivationTrace();

            cancellationToken.ThrowIfCancellationRequested();

            bool isBranch23 =
                branch23JobVariables != null;

            string currentEnvironment =
                generalVariables.TryGetValue(
                    "ENV",
                    out string? environmentValue)
                    ? NormalizeEnvironment(environmentValue)
                    : "Q";

            PaymentRoutingContext paymentRouting =
                ResolveAndValidatePaymentRouting(generalVariables, trace);

            bool bypassBilling =
                skipPrime ||
                string.Equals(
                    channel,
                    "C05",
                    StringComparison.OrdinalIgnoreCase);

            InitializeExecutionTrace(
                trace,
                isBranch23,
                bypassBilling,
                skipPrime);

            trace.Bucp = paymentRouting.Bucp;
            trace.Cmdpmt = paymentRouting.PaymentCommand;
            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(
                    trace.RoutingDiagnostic)
                ? paymentRouting.Diagnostic
                : trace.RoutingDiagnostic + " | " + paymentRouting.Diagnostic;

            onProgress?.Invoke(paymentRouting.Diagnostic + ".");

            try
            {
                // Validate the monitoring dependency before any billing job is submitted.
                // The resolver must correlate the child with this exact parent execution.
                // Never select the latest job by name or guess parent number + 1.
                if (trace.Steps.Any(step => !step.IsSkipped &&
                        !string.IsNullOrWhiteSpace(step.SubJobName) &&
                        step.SubJobName != "-") &&
                    resolveGeneratedJobNumberAsync == null)
                {
                    trace.FailureStage = "GENERATED_JOB_RESOLVER_REQUIRED";
                    trace.FailureReason =
                        "Generated LI job monitoring is not configured. " +
                        "No billing or activation job has been submitted.";
                    trace.RootCauseAnalysis =
                        "MicroFocusApiService must provide a correlated child job number " +
                        "for each LI generator execution.";
                    trace.RecommendedAction =
                        "Connect resolveGeneratedJobNumberAsync to a verified ESCWA " +
                        "parent/child lookup before starting the workflow.";
                    throw new InvalidOperationException(trace.FailureReason);
                }

                await AuthenticateAndPreloadAsync(
                        username,
                        password,
                        currentEnvironment,
                        skipLogon,
                        isBranch23,
                        onProgress,
                        trace,
                        cancellationToken)
                    .ConfigureAwait(false);

                trace.ServerNode =
                    MicroFocusApiService.ActiveServer
                    ?? "Unknown";

                var addprctVariables =
                    BuildAddprctVariables(
                        generalVariables,
                        addprctSpecificVariables);

                int executionCounter = 1;

                foreach (JobStepExecutionInfo step in trace.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (step.IsSkipped)
                    {
                        onProgress?.Invoke(
                            $"[{step.StepOrder}] " +
                            $"{step.JobName} skipped. " +
                            $"{step.DiagnosticHint}");

                        continue;
                    }

                    bool isFinalStandardPassStep =
                        !step.IsBranch23Step &&
                        step.PhaseDescription != null &&
                        step.PhaseDescription.StartsWith(
                            "Phase 4 BR23:",
                            StringComparison.OrdinalIgnoreCase);

                    if (isFinalStandardPassStep &&
                        IsSameJob(step.JobName, "LI1J04D0"))
                    {
                        onProgress?.Invoke(
                            "BR23 jobs completed. Starting the final " +
                            "standard IMS pass with premium jobs skipped.");
                    }

                    bool isFundEventsStep =
                        step.IsBranch23Step &&
                        IsSameJob(
                            step.JobName,
                            Settings.Branch23.FundEvents.JobName);

                    bool isConsolidationStep =
                        step.IsBranch23Step &&
                        IsSameJob(
                            step.JobName,
                            Settings.Branch23.Consolidation.JobName);

                    if (isFundEventsStep &&
                        beforeBranch23FundEventsAsync != null)
                    {
                        onProgress?.Invoke(
                            "BR23: resolving funds and preparing all NAV " +
                            "dates up to consolidation before LVDQFG03.");

                        await RunBranch23HookAsync(
                                beforeBranch23FundEventsAsync,
                                step,
                                trace,
                                "BR23_PREPARATION_BEFORE_FG03",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (isConsolidationStep &&
                        prepareBranch23NavsAsync != null)
                    {
                        onProgress?.Invoke(
                            "BR23: running the optional legacy NAV " +
                            "validation hook before LVDQFG01.");

                        await RunBranch23NavPreparationAsync(
                                prepareBranch23NavsAsync,
                                step,
                                trace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Dictionary<string, string> variablesForJob =
                        BuildVariablesForStep(
                            step,
                            generalVariables,
                            addprctVariables,
                            branch23JobVariables);

                    await ProcessSubmitAndWaitAsync(
                            step,
                            variablesForJob,
                            executionCounter++,
                            onProgress,
                            trace,
                            cancellationToken,
                            resolveGeneratedJobNumberAsync)
                        .ConfigureAwait(false);

                    if (!bypassBilling &&
                        IsSameJob(step.JobName, "LVPG22U") &&
                        beforeInitialLiValidationAsync != null)
                    {
                        JobStepExecutionInfo blockedLiStep =
                            trace.Steps.FirstOrDefault(candidate =>
                                !candidate.IsBranch23Step &&
                                IsSameJob(candidate.JobName, "LI1J04D0") &&
                                string.Equals(candidate.Status, "Pending",
                                    StringComparison.OrdinalIgnoreCase) &&
                                (candidate.PhaseDescription == null ||
                                 !candidate.PhaseDescription.StartsWith(
                                     "Phase 4 BR23:",
                                     StringComparison.OrdinalIgnoreCase)))
                            ?? step;

                        onProgress?.Invoke(
                            "LVPG22U completed. Validating persisted premium, " +
                            "assigned receipt and eligible PE01/PE03 before LI1J04D0.");

                        await RunPreLiBusinessValidationAsync(
                                beforeInitialLiValidationAsync,
                                blockedLiStep,
                                trace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (isFundEventsStep &&
                        afterBranch23FundEventsAsync != null)
                    {
                        onProgress?.Invoke(
                            "BR23: running the optional post-LVDQFG03 " +
                            "validation hook.");

                        JobStepExecutionInfo blockedStep =
                            trace.Steps.FirstOrDefault(
                                candidate =>
                                    candidate.IsBranch23Step &&
                                    IsSameJob(
                                        candidate.JobName,
                                        Settings.Branch23
                                            .Consolidation.JobName))
                            ?? step;

                        await RunBranch23HookAsync(
                                afterBranch23FundEventsAsync,
                                blockedStep,
                                trace,
                                "BR23_POST_FG03_VALIDATION",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                bool finalLisaValidationExecuted =
                    afterActivationValidationAsync != null;

                if (afterActivationValidationAsync != null)
                {
                    onProgress?.Invoke(
                        "Final standard pass completed. Validating contract creation in LISA.");

                    try
                    {
                        await afterActivationValidationAsync(
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (isBranch23)
                        {
                            finalLisaValidationExecuted = false;
                            trace.LisaInternalId = string.IsNullOrWhiteSpace(
                                    trace.LisaInternalId)
                                ? "PENDING_ASYNC_LISA_CREATION"
                                : trace.LisaInternalId;
                            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(
                                    trace.RoutingDiagnostic)
                                ? "LISA finalization pending asynchronously: " +
                                  exception.Message
                                : trace.RoutingDiagnostic +
                                  " | LISA finalization pending asynchronously: " +
                                  exception.Message;
                            onProgress?.Invoke(
                                "All Branch 23 jobs completed. SCNTT0 is not yet " +
                                "visible; LISA creation is asynchronous and does " +
                                "not change the technical sequence to FAILED.");
                        }
                        else
                        {
                            trace.FinalStatus = "FAILED";
                            trace.FailureStage =
                                string.IsNullOrWhiteSpace(trace.FailureStage)
                                    ? "LISA_FINALIZATION_TIMEOUT"
                                    : trace.FailureStage;
                            trace.FailureReason =
                                string.IsNullOrWhiteSpace(trace.FailureReason)
                                    ? exception.Message
                                    : trace.FailureReason;
                            trace.RootCauseAnalysis =
                                string.IsNullOrWhiteSpace(trace.RootCauseAnalysis)
                                    ? "All JCL jobs returned accepted condition " +
                                      "codes, but the contract was not confirmed " +
                                      "in LV.SCNTT0."
                                    : trace.RootCauseAnalysis;
                            trace.RecommendedAction =
                                string.IsNullOrWhiteSpace(trace.RecommendedAction)
                                    ? "Inspect the completed jobs and confirm the " +
                                      "business state before choosing any retry."
                                    : trace.RecommendedAction;
                            throw;
                        }
                    }
                }

                trace.FinalStatus = "SUCCESS";
                trace.EndTime = DateTime.Now;
                onProgress?.Invoke(
                    finalLisaValidationExecuted
                        ? "[SUCCESS] JCL sequence completed and final LISA " +
                          "validation succeeded."
                        : "[SUCCESS] JCL sequence completed. Final LISA " +
                          "validation was not requested by the caller.");
            }
            catch (OperationCanceledException)
            {
                trace.EndTime =
                    DateTime.Now;

                MarkRemainingStepsAborted(
                    trace,
                    "Cancelled by user.");

                trace.FinalStatus =
                    "CANCELLED";

                trace.FailureStage =
                    "USER_CANCELLED";

                trace.FailureReason =
                    "Operation was cancelled by the user.";

                onProgress?.Invoke(
                    "[CANCELLED] The activation sequence was stopped.");

                throw;
            }
            catch (Exception exception)
            {
                trace.EndTime =
                    DateTime.Now;

                MarkRemainingStepsAborted(
                    trace,
                    "Not executed because the sequence stopped " +
                    "after a previous error.");

                if (string.Equals(
                        exception.Message,
                        "ALREADY_ACTIVE",
                        StringComparison.Ordinal))
                {
                    trace.FinalStatus =
                        "ALREADY_ACTIVE";

                    onProgress?.Invoke(
                        "[ALREADY ACTIVE] ADDPRCT returned RC=0008.");

                    throw;
                }

                trace.FinalStatus =
                    "FAILED";

                if (string.IsNullOrWhiteSpace(
                        trace.FailureReason))
                {
                    trace.FailureReason =
                        exception.Message;
                }

                onProgress?.Invoke(
                    "[CRITICAL ERROR] Sequence interrupted: " +
                    exception.Message);

                throw;
            }
        }

        // =============================================================
        // INITIALISATION DE LA TRACE
        // =============================================================

        private static void InitializeExecutionTrace(
            ContractActivationTrace trace,
            bool isBranch23,
            bool bypassBilling,
            bool skipPrime)
        {
            trace.Steps.Clear();

            trace.StartTime =
                DateTime.Now;

            trace.EndTime =
                default;

            trace.FinalStatus =
                "RUNNING";

            trace.FailureStage =
                string.Empty;

            trace.FailureReason =
                string.Empty;

            trace.RootCauseAnalysis =
                string.Empty;

            trace.RecommendedAction =
                string.Empty;

            trace.FailedJobName =
                "-";

            trace.FailedJobNum =
                "-";

            trace.FailedReturnCode =
                "-";

            int order = 1;

            foreach (var definition in SequenceDefinition)
            {
                bool isPhaseOne =
                    IsSameJob(
                        definition.JobName,
                        "ADDPRCT") ||
                    IsSameJob(
                        definition.JobName,
                        "LVPP06U") ||
                    IsSameJob(
                        definition.JobName,
                        "LVPG22U");

                bool skipped =
                    isPhaseOne &&
                    bypassBilling;

                var step =
                    new JobStepExecutionInfo
                    {
                        StepOrder = order++,
                        JobName = definition.JobName,
                        SubJobName = definition.SubJobName,
                        PhaseDescription = definition.Phase,
                        Status = skipped
                            ? "Skipped"
                            : "Pending",
                        DiagnosticHint = skipped
                            ? skipPrime
                                ? "Bypassed by Skip Prime mode."
                                : "Bypassed automatically for C05."
                            : string.Empty,
                        IsBranch23Step = false
                    };

                trace.Steps.Add(step);
            }

            if (!isBranch23)
            {
                return;
            }

            foreach (var definition in
                     Branch23SequenceDefinition)
            {
                trace.Steps.Add(
                    new JobStepExecutionInfo
                    {
                        StepOrder = order++,
                        JobName = definition.JobName,
                        JclName = definition.JclName,
                        SubJobName = "-",
                        PhaseDescription = definition.Phase,
                        Status = "Pending",
                        IsBranch23Step = true
                    });
            }

            foreach (var definition in
                     Branch23FinalStandardPassDefinition)
            {
                trace.Steps.Add(
                    new JobStepExecutionInfo
                    {
                        StepOrder = order++,
                        JobName = definition.JobName,
                        SubJobName = definition.SubJobName,
                        PhaseDescription = definition.Phase,
                        Status = "Pending",
                        DiagnosticHint =
                            "Automatic final standard pass after BR23. " +
                            "Premium jobs are not replayed.",
                        IsBranch23Step = false
                    });
            }
        }

        // =============================================================
        // AUTHENTIFICATION ET PR CHARGEMENT
        // =============================================================

        private async Task AuthenticateAndPreloadAsync(
            string username,
            string password,
            string environment,
            bool skipLogon,
            bool isBranch23,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            if (skipLogon &&
                IsActiveServerCompatibleWithEnvironment(environment))
            {
                onProgress?.Invoke(
                    "Mainframe authentication skipped. " +
                    $"Using the existing ESCWA session on " +
                    $"{MicroFocusApiService.ActiveServer}.");

                if (isBranch23)
                {
                    await PreloadAllJclTemplatesAsync()
                        .ConfigureAwait(false);
                }
                else
                {
                    await PreloadJclTemplatesAsync()
                        .ConfigureAwait(false);
                }

                return;
            }

            if (skipLogon)
            {
                onProgress?.Invoke(
                    "The cached ESCWA node does not match environment " +
                    $"{environment}. A new authentication is required.");
            }

            onProgress?.Invoke(
                $"Authenticating with Mainframe ({environment}) " +
                "and preloading JCL templates.");

            Task<bool> logonTask =
                _apiService.LogonAsync(
                    username,
                    password,
                    environment,
                    message =>
                        onProgress?.Invoke(message),
                    cancellationToken);

            Task preloadTask =
                isBranch23
                    ? PreloadAllJclTemplatesAsync()
                    : PreloadJclTemplatesAsync();

            await Task.WhenAll(
                    logonTask,
                    preloadTask)
                .ConfigureAwait(false);

            bool authenticated =
                await logonTask.ConfigureAwait(false);

            if (authenticated)
            {
                return;
            }

            trace.FailureStage =
                "MAINFRAME_AUTH";

            trace.FailureReason =
                $"Unable to authenticate on MicroFocus ESCWA " +
                $"for environment {environment}.";

            trace.RootCauseAnalysis =
                "All configured Mainframe nodes rejected the " +
                "connection or timed out.";

            trace.RecommendedAction =
                "Verify VPN connectivity, Mainframe credentials, " +
                "the target environment and ESCWA availability.";

            throw new InvalidOperationException(
                "Unable to connect to the MicroFocus server. " +
                "Verify VPN connectivity and Mainframe credentials.");
        }

        // =============================================================
        // VARIABLES JCL
        // =============================================================

        private static Dictionary<string, string>
            BuildAddprctVariables(
                Dictionary<string, string> generalVariables,
                Dictionary<string, string>
                    addprctSpecificVariables)
        {
            var result =
                new Dictionary<string, string>(
                    generalVariables,
                    StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> variable in
                     addprctSpecificVariables)
            {
                result[variable.Key] =
                    variable.Value;
            }

            return result;
        }

        private static Dictionary<string, string>
            BuildVariablesForStep(
                JobStepExecutionInfo step,
                Dictionary<string, string> generalVariables,
                Dictionary<string, string> addprctVariables,
                Dictionary<string, Dictionary<string, string>>?
                    branch23JobVariables)
        {
            if (!step.IsBranch23Step)
            {
                return IsSameJob(
                    step.JobName,
                    "ADDPRCT")
                    ? new Dictionary<string, string>(
                        addprctVariables,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        generalVariables,
                        StringComparer.OrdinalIgnoreCase);
            }

            var variables =
                new Dictionary<string, string>(
                    generalVariables,
                    StringComparer.OrdinalIgnoreCase);

            if (branch23JobVariables == null)
            {
                return variables;
            }

            Dictionary<string, string>? specificVariables =
                null;

            if (!string.IsNullOrWhiteSpace(step.JclName))
            {
                branch23JobVariables.TryGetValue(
                    step.JclName,
                    out specificVariables);
            }

            if (specificVariables == null &&
                !string.IsNullOrWhiteSpace(step.JobName))
            {
                branch23JobVariables.TryGetValue(
                    step.JobName,
                    out specificVariables);
            }

            if (specificVariables == null)
            {
                return variables;
            }

            foreach (KeyValuePair<string, string> variable in
                     specificVariables)
            {
                variables[variable.Key] =
                    variable.Value;
            }

            return variables;
        }

        // =============================================================
        // HOOKS BR23
        // =============================================================

        private static async Task RunPreLiBusinessValidationAsync(
            Func<CancellationToken, Task> validation,
            JobStepExecutionInfo blockedLiStep,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            try
            {
                await validation(cancellationToken).ConfigureAwait(false);
                blockedLiStep.DiagnosticHint =
                    string.IsNullOrWhiteSpace(blockedLiStep.DiagnosticHint)
                        ? "Pre-LI business prerequisites confirmed."
                        : blockedLiStep.DiagnosticHint +
                          " Pre-LI business prerequisites confirmed.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockedLiStep.Status = "PrerequisiteMissing";
                blockedLiStep.ErrorMessage = exception.Message;
                blockedLiStep.DiagnosticHint =
                    "LI1J04D0 was not submitted because the persisted " +
                    "premium, assigned receipt or eligible event is missing.";
                trace.FailureStage = "PRE_LI_BUSINESS_PREREQUISITE_MISSING";
                trace.FailedJobName = blockedLiStep.JobName;
                trace.FailedJobNum = "-";
                trace.FailedReturnCode = "-";
                trace.FailureReason = exception.Message;
                trace.RootCauseAnalysis =
                    "LVPG22U completed technically, but RC=0000 does not " +
                    "prove that PRIST0, PECHT0 and ELIAT0 were populated " +
                    "for the target contract.";
                trace.RecommendedAction =
                    "Inspect ADDPRCT, LVPP06U and LVPG22U business outputs. " +
                    "Do not replay LI or billing automatically.";
                throw new InvalidOperationException(
                    "PRE_LI_BUSINESS_PREREQUISITE_MISSING: " + exception.Message,
                    exception);
            }
        }

        private static async Task RunBranch23HookAsync(
            Func<CancellationToken, Task> hook,
            JobStepExecutionInfo blockedStep,
            ContractActivationTrace trace,
            string defaultFailureStage,
            CancellationToken cancellationToken)
        {
            try
            {
                await hook(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockedStep.Status =
                    "FundsNotResolved";

                blockedStep.ErrorMessage =
                    exception.Message;

                trace.FailureStage =
                    exception is Branch23FundResolutionException
                        fundResolutionException
                        ? fundResolutionException.FailureStage
                        : defaultFailureStage;

                trace.FailedJobName =
                    blockedStep.JobName;

                trace.FailureReason =
                    exception.Message;

                trace.RootCauseAnalysis =
                    trace.FailureStage ==
                    "BR23_PREACTIVATION_FUNDS_MISSING"
                        ? "No business-confirmed BR23 fund was available " +
                          "before LVDQFG03. UI fallback funds have priority; " +
                          "ELIA TB5UGFF is only a secondary source."
                        : trace.FailureStage ==
                          "BR23_PREPARATION_BEFORE_FG03"
                            ? "BR23 fund or NAV preparation failed before " +
                              "LVDQFG03 could be submitted."
                            : "A BR23 workflow hook failed before the next " +
                              "JCL step could be submitted.";

                trace.RecommendedAction =
                    trace.FailureStage ==
                    "BR23_PREACTIVATION_FUNDS_MISSING"
                        ? "Enter the confirmed fund code, for example " +
                          "SRID2P when applicable, in BR23 fallback funds " +
                          "and rerun with Skip Prime."
                        : "Inspect the NAV preparation details for J, J+1 " +
                          "and J+2. Do not replay completed billing jobs.";

                throw;
            }
        }

        private static async Task
            RunBranch23NavPreparationAsync(
                Func<CancellationToken, Task> prepareNavsAsync,
                JobStepExecutionInfo consolidationStep,
                ContractActivationTrace trace,
                CancellationToken cancellationToken)
        {
            try
            {
                await prepareNavsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                consolidationStep.Status =
                    "NavNotReady";

                consolidationStep.ErrorMessage =
                    exception.Message;

                trace.FailureStage =
                    "BR23_NAV_PREPARATION";

                trace.FailedJobName =
                    consolidationStep.JobName;

                trace.FailureReason =
                    exception.Message;

                trace.RootCauseAnalysis =
                    "The BR23 workflow requires every confirmed fund " +
                    "to have NAV values through the consolidation date. " +
                    "Closed funds use GFEVT0 and open funds use GFCRT0.";

                trace.RecommendedAction =
                    "Create or correct the missing NAV values, then " +
                    "replay only the unfinished BR23 steps.";

                throw;
            }
        }

        private static void MarkRemainingStepsAborted(
            ContractActivationTrace trace,
            string reason)
        {
            if (trace.Steps == null)
            {
                return;
            }

            foreach (JobStepExecutionInfo step in trace.Steps)
            {
                if (!string.Equals(
                        step.Status,
                        "Pending",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                step.Status =
                    "Aborted";

                step.DiagnosticHint =
                    reason;
            }
        }

        // =============================================================
        // PR PARATION, SOUMISSION ET MONITORING D'UN JOB
        // =============================================================

        private async Task ProcessSubmitAndWaitAsync(
            JobStepExecutionInfo stepInfo,
            Dictionary<string, string> sourceVariables,
            int count,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken cancellationToken,
            Func<string, string, CancellationToken, Task<string>>?
                resolveGeneratedJobNumberAsync)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string jobName =
                stepInfo.JobName;

            if (string.IsNullOrWhiteSpace(jobName))
            {
                throw new InvalidOperationException(
                    "The JCL step has no job name.");
            }

            stepInfo.StartTime =
                DateTime.Now;

            stepInfo.ServerNode =
                MicroFocusApiService.ActiveServer
                ?? "-";

            var stopwatch =
                Stopwatch.StartNew();

            var variables =
                new Dictionary<string, string>(
                    sourceVariables,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["JOBNAM"] = jobName
                };

            SetApplicationVariable(
                stepInfo,
                variables);

            string templateName =
                string.IsNullOrWhiteSpace(stepInfo.JclName)
                    ? jobName
                    : stepInfo.JclName;

            onProgress?.Invoke(
                $"[{count}] Preparing JCL {templateName} " +
                $"for job {jobName}.");

            string readyContent;

            try
            {
                if (_branch23ActivationSteps.Contains(stepInfo))
                {
                    IReadOnlyList<string> templateVariables =
                        await _jclProcessor
                            .GetMissingVariablesAsync(
                                templateName,
                                new Dictionary<string, string>())
                            .ConfigureAwait(false);

                    IReadOnlyList<string> invalid =
                        Branch23ActivationWorkflow.FindInvalidVariables(
                            templateVariables,
                            variables,
                            new[] { "CM", "CM." });

                    onProgress?.Invoke(
                        $"[{count}] {templateName} variables: " +
                        string.Join(", ", templateVariables.Distinct(StringComparer.OrdinalIgnoreCase)
                            .Select(name => $"{name}={(variables.TryGetValue(name, out string? value) ? value : "<absent>")}")));

                    if (invalid.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Unresolved, empty or '?' JCL variable(s) for " +
                            $"{templateName}: " +
                            string.Join(", ", invalid.Select(name => "%%" + name)));
                    }
                }
                else if (stepInfo.IsBranch23Step)
                {
                    IReadOnlyList<string> missingVariables =
                        await _jclProcessor
                            .GetMissingVariablesAsync(
                                templateName,
                                variables)
                            .ConfigureAwait(false);

                    if (missingVariables.Count > 0)
                    {
                        string missing =
                            string.Join(
                                ", ",
                                missingVariables.Select(
                                    variable =>
                                        "%%" + variable));

                        throw new InvalidOperationException(
                            $"Missing JCL variable(s) for " +
                            $"{templateName}: {missing}");
                    }
                }

                readyContent =
                    await _jclProcessor
                        .GetPreparedJclAsync(
                            templateName,
                            variables,
                            count)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();

                stepInfo.EndTime =
                    DateTime.Now;

                stepInfo.DurationMs =
                    stopwatch.ElapsedMilliseconds;

                stepInfo.Status =
                    "JCLPrepFailed";

                stepInfo.ErrorMessage =
                    exception.Message;

                stepInfo.DiagnosticHint =
                    "Unable to load or prepare the JCL template.";

                trace.FailureStage =
                    "JCL_PREPARATION";

                trace.FailedJobName =
                    jobName;

                trace.FailureReason =
                    $"Failed to prepare JCL template " +
                    $"{templateName}: {exception.Message}";

                trace.RootCauseAnalysis =
                    "The JCL template is missing, inaccessible or " +
                    "contains unresolved symbolic variables.";

                trace.RecommendedAction =
                    "Verify VPN and network-share access, template " +
                    "existence and all required symbolic variables.";

                throw;
            }

            readyContent =
                ApplyJobSpecificTransformations(
                    jobName,
                    readyContent,
                    variables,
                    _branch23ActivationSteps.Contains(stepInfo) &&
                    variables.TryGetValue("CLASS", out string? jobClass) &&
                    !string.IsNullOrWhiteSpace(jobClass)
                        ? jobClass.Trim()
                        : "A");

            onProgress?.Invoke(
                $"[{count}] Submitting {jobName}.");

            var submission =
                await _apiService
                    .SubmitJobAsync(
                        readyContent,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!submission.Success)
            {
                stopwatch.Stop();

                stepInfo.EndTime =
                    DateTime.Now;

                stepInfo.DurationMs =
                    stopwatch.ElapsedMilliseconds;

                stepInfo.Status =
                    "SubmitFailed";

                stepInfo.ErrorMessage =
                    submission.Error
                    ?? "Unknown submission error.";

                stepInfo.DiagnosticHint =
                    "JES rejected the submission or the ESCWA " +
                    "session is no longer valid.";

                trace.FailureStage =
                    "JOB_SUBMISSION";

                trace.FailedJobName =
                    jobName;

                trace.FailureReason =
                    $"Failed to submit {jobName}. " +
                    $"Details: {submission.Error}";

                trace.RootCauseAnalysis =
                    "The Micro Focus JES endpoint rejected the JCL " +
                    "payload, the session expired or the target " +
                    "batch region is unavailable.";

                trace.RecommendedAction =
                    "Inspect the JES submission message and verify " +
                    "the ESCWA session, region and JCL job card.";

                throw new InvalidOperationException(
                    $"Failed to submit job {jobName}. " +
                    $"Error: {submission.Error}");
            }

            string jobNumber =
                submission.JobNum;

            stepInfo.JobNum =
                jobNumber;

            stepInfo.Status =
                "Submitted";

            onProgress?.Invoke(
                $"Job {jobName} ({jobNumber}) running on " +
                $"{stepInfo.ServerNode}.");

            await WaitForJobCompletionAsync(
                    stepInfo,
                    jobNumber,
                    stopwatch,
                    onProgress,
                    trace,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stepInfo.SubJobName) &&
                stepInfo.SubJobName != "-")
            {
                await WaitForGeneratedJobAsync(
                        stepInfo,
                        jobNumber,
                        resolveGeneratedJobNumberAsync,
                        onProgress,
                        trace,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

        }

        /// <summary>
        /// Waits for the exact child execution returned by a caller-supplied resolver.
        /// Resolver arguments: parent job number, expected child name, cancellation.
        /// The resolver must return only an authoritatively correlated child number.
        /// This method does not submit or resubmit a child job.
        /// </summary>
        private async Task WaitForGeneratedJobAsync(
            JobStepExecutionInfo parentStep,
            string parentJobNumber,
            Func<string, string, CancellationToken, Task<string>>? resolver,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            DateTime monitoringStart = DateTime.Now;
            string parentHint = parentStep.DiagnosticHint;
            var timer = Stopwatch.StartNew();
            JobStepExecutionInfo? child = null;

            try
            {
                parentStep.Status = "WaitingForSubJob";
                onProgress?.Invoke(
                    $"Parent {parentStep.JobName} ({parentJobNumber}) ended. " +
                    $"Resolving its generated job {parentStep.SubJobName}.");

                if (resolver == null)
                    throw new InvalidOperationException(
                        "Generated job resolver is not configured.");

                string childNumber;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    try
                    {
                        childNumber = (await resolver(
                            parentJobNumber, parentStep.SubJobName, timeout.Token)
                            .ConfigureAwait(false))?.Trim() ?? string.Empty;
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Timed out resolving {parentStep.SubJobName} " +
                            $"generated by parent {parentJobNumber}.");
                    }
                }

                string normalizedChildNumber =
                    NormalizeGeneratedJobNumber(childNumber);
                string normalizedParentNumber =
                    NormalizeGeneratedJobNumber(parentJobNumber);

                if (string.IsNullOrWhiteSpace(normalizedChildNumber) ||
                    string.Equals(
                        normalizedChildNumber,
                        normalizedParentNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"No valid correlated job number for {parentStep.SubJobName} " +
                        $"generated by {parentJobNumber}. Resolver returned " +
                        $"'{childNumber}'.");
                }

                childNumber = normalizedChildNumber;
                child = new JobStepExecutionInfo
                {
                    StepOrder = parentStep.StepOrder,
                    JobName = parentStep.SubJobName.Trim(),
                    SubJobName = "-",
                    JobNum = childNumber,
                    ServerNode = parentStep.ServerNode,
                    PhaseDescription = parentStep.PhaseDescription,
                    Status = "Submitted",
                    StartTime = monitoringStart,
                    IsBranch23Step = parentStep.IsBranch23Step
                };

                onProgress?.Invoke(
                    $"Following generated job {child.JobName} ({childNumber}), " +
                    $"parent {parentJobNumber}.");

                await WaitForJobCompletionAsync(
                    child, childNumber, timer, onProgress, trace, cancellationToken)
                    .ConfigureAwait(false);

                parentStep.Status = "Complete";
                parentStep.SubJobNum = child.JobNum;
                parentStep.SubJobReturnCode = child.ReturnCode;
                parentStep.DiagnosticHint = parentHint +
                    $" Generated job {child.JobName} ({childNumber}): " +
                    child.DiagnosticHint;
            }
            catch (OperationCanceledException)
            {
                parentStep.Status = "Cancelled";
                throw;
            }
            catch (Exception exception)
            {
                parentStep.Status = "SubJobFailed";
                parentStep.ErrorMessage = exception.Message;
                parentStep.DiagnosticHint =
                    "Parent finished, but generated LI processing is not confirmed successful.";
                if (string.IsNullOrWhiteSpace(trace.FailureStage))
                {
                    trace.FailureStage = "GENERATED_JOB_NOT_CONFIRMED";
                    trace.FailedJobName = parentStep.SubJobName;
                    trace.FailedJobNum = child?.JobNum ?? "-";
                    trace.FailureReason = exception.Message;
                    trace.RootCauseAnalysis =
                        "The generated job could not be correlated or confirmed successful. " +
                        "No following workflow job was submitted.";
                    trace.RecommendedAction =
                        $"Inspect parent {parentJobNumber} and its generated job in ESCWA. " +
                        "Do not resubmit the generator or billing automatically.";
                }
                throw;
            }
            finally
            {
                timer.Stop();
                parentStep.EndTime = DateTime.Now;
                parentStep.DurationMs += (long)(DateTime.Now - monitoringStart).TotalMilliseconds;
                if (child != null)
                {
                    parentStep.PollCount += child.PollCount;
                    if (!string.IsNullOrWhiteSpace(child.SpoolExcerpt))
                    {
                        parentStep.SpoolExcerpt = (parentStep.SpoolExcerpt ?? string.Empty) +
                            Environment.NewLine +
                            $"Generated job {child.JobName} ({child.JobNum}):" +
                            Environment.NewLine + child.SpoolExcerpt;
                    }
                }
            }
        }

        private static void SetApplicationVariable(
            JobStepExecutionInfo step,
            Dictionary<string, string> variables)
        {
            if (IsSameJob(step.JobName, "LVPP06U") ||
                IsSameJob(step.JobName, "LVPG22U") ||
                step.IsBranch23Step)
            {
                variables["AP"] =
                    "LV";

                return;
            }

            if (IsSameJob(step.JobName, "LI1J04D0") ||
                IsSameJob(step.JobName, "LI1J04D2"))
            {
                variables["AP"] =
                    "LI";
            }
        }

        private static string ApplyJobSpecificTransformations(
            string jobName,
            string readyContent,
            Dictionary<string, string> variables,
            string subJobClass = "A")
        {
            if (IsSameJob(jobName, "LVPG22U"))
            {
                const string marker =
                    "//ICEGENER IF";

                int markerIndex =
                    readyContent.IndexOf(
                        marker,
                        StringComparison.Ordinal);

                if (markerIndex >= 0)
                {
                    readyContent =
                        readyContent.Substring(
                            0,
                            markerIndex);
                }
            }

            if (!IsSameJob(jobName, "LI1J04D0") &&
                !IsSameJob(jobName, "LI1J04D2"))
            {
                return readyContent;
            }

            string subJobName =
                IsSameJob(jobName, "LI1J04D0")
                    ? "LI1J04D1"
                    : "LI1J04D3";

            readyContent =
                readyContent.Replace(
                    "$JOBNAME",
                    subJobName,
                    StringComparison.Ordinal);

            string environmentLetter =
                variables.TryGetValue(
                    "ENVIMS",
                    out string? envIms)
                    ? envIms
                    : "T";

            string notifyUser =
                variables.TryGetValue(
                    "USERNAME",
                    out string? username)
                    ? username
                    : "XA3894";

            const string dataMarker =
                "DLM=##";

            int dataIndex =
                readyContent.IndexOf(
                    dataMarker,
                    StringComparison.Ordinal);

            if (dataIndex < 0)
            {
                return readyContent;
            }

            int endOfLineIndex =
                readyContent.IndexOf(
                    '\n',
                    dataIndex);

            if (endOfLineIndex < 0)
            {
                return readyContent;
            }

            string jobCard =
                $"//{subJobName} JOB CLASS={subJobClass}," +
                $"SCHENV=IM7{environmentLetter}," +
                $"NOTIFY={notifyUser}\r\n";

            return readyContent.Insert(
                endOfLineIndex + 1,
                jobCard);
        }

        // =============================================================
        // MONITORING JES
        // =============================================================

        private async Task WaitForJobCompletionAsync(
            JobStepExecutionInfo stepInfo,
            string jobNumber,
            Stopwatch stopwatch,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 400;
            int unknownReturnCodeCount = 0;

            for (int attempt = 0;
                 attempt < maxAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int delay =
                    GetPollingDelay(attempt);

                await Task.Delay(
                        delay,
                        cancellationToken)
                    .ConfigureAwait(false);

                stepInfo.PollCount =
                    attempt + 1;

                var statusResult =
                    await _apiService
                        .CheckJobStatusAsync(
                            jobNumber,
                            cancellationToken)
                        .ConfigureAwait(false);

                stepInfo.Status =
                    statusResult.Status
                    ?? "Unknown";

                stepInfo.ReturnCode =
                    statusResult.ReturnCode
                    ?? string.Empty;

                if (IsFailureStatus(
                        stepInfo.Status))
                {
                    await HandleSystemFailureAsync(
                            stepInfo,
                            jobNumber,
                            stopwatch,
                            trace,
                            cancellationToken)
                        .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"System failure for job " +
                        $"{stepInfo.JobName} ({jobNumber}). " +
                        $"Status={stepInfo.Status}, " +
                        $"Code={stepInfo.ReturnCode}.");
                }

                if (!IsCompletionStatus(
                        stepInfo.Status))
                {
                    continue;
                }

                int returnCode = NormalizeReturnCode(stepInfo.ReturnCode);
                if (returnCode == int.MaxValue)
                {
                    unknownReturnCodeCount++;
                    stepInfo.Status = "AwaitingReturnCode";
                    stepInfo.DiagnosticHint =
                        "Job ended, but its numeric condition code is not confirmed.";
                    if (unknownReturnCodeCount < 8)
                    {
                        onProgress?.Invoke(
                            $"Job {stepInfo.JobName} ({jobNumber}): " +
                            "waiting for a confirmed return code.");
                        continue;
                    }

                    stopwatch.Stop();
                    stepInfo.EndTime = DateTime.Now;
                    stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
                    stepInfo.Status = "ReturnCodeUnknown";
                    stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(
                        jobNumber, cancellationToken).ConfigureAwait(false);
                    trace.FailureStage = "JOB_RETURN_CODE_UNKNOWN";
                    trace.FailedJobName = stepInfo.JobName;
                    trace.FailedJobNum = jobNumber;
                    trace.FailedReturnCode = stepInfo.ReturnCode;
                    trace.FailureReason =
                        $"Cannot confirm the return code of {stepInfo.JobName} ({jobNumber}).";
                    trace.RootCauseAnalysis =
                        "A terminal JES status was returned without a recognized numeric RC. " +
                        "The workflow has stopped without resubmitting the job.";
                    trace.RecommendedAction =
                        "Inspect the existing job in ESCWA and correct RC extraction. " +
                        "Do not replay billing automatically.";
                    throw new InvalidOperationException(trace.FailureReason);
                }

                stopwatch.Stop();

                stepInfo.EndTime =
                    DateTime.Now;

                stepInfo.DurationMs =
                    stopwatch.ElapsedMilliseconds;

                if (returnCode != 0 &&
                    returnCode != 4)
                {
                    stepInfo.SpoolExcerpt =
                        await SafeFetchSpoolDiagnosticsAsync(
                                jobNumber,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (IsSameJob(
                            stepInfo.JobName,
                            "ADDPRCT") &&
                        returnCode == 8 &&
                        _branch23ActivationSteps.Contains(stepInfo))
                    {
                        stepInfo.Status = "CompleteRc8";
                        stepInfo.DiagnosticHint =
                            "ADDPRCT RC=0008: DB2TIAD diagnostic kept; " +
                            "PRCTT0 is checked before any conclusion.";
                        onProgress?.Invoke(
                            $"Job {stepInfo.JobName} ({jobNumber}) {stepInfo.DiagnosticHint}");
                        return;
                    }

                    if (IsSameJob(
                            stepInfo.JobName,
                            "ADDPRCT") &&
                        returnCode == 8)
                    {
                        ConfigureAlreadyActiveDiagnostic(
                            trace,
                            stepInfo,
                            jobNumber);

                        throw new InvalidOperationException(
                            "ALREADY_ACTIVE");
                    }

                    PopulateFailureDiagnostics(
                        trace,
                        stepInfo,
                        isSystemCrash: false);

                    throw new InvalidOperationException(
                        $"Business error on job " +
                        $"{stepInfo.JobName} ({jobNumber}). " +
                        $"Return code={stepInfo.ReturnCode}.");
                }

                stepInfo.Status =
                    "Complete";

                stepInfo.DiagnosticHint =
                    returnCode == 4
                        ? "Completed with warning RC=0004."
                        : "Completed normally RC=0000.";

                onProgress?.Invoke(
                    $"Job {stepInfo.JobName} ({jobNumber}) " +
                    $"{stepInfo.DiagnosticHint}");

                return;
            }

            await HandleTimeoutAsync(
                    stepInfo,
                    jobNumber,
                    stopwatch,
                    trace,
                    cancellationToken)
                .ConfigureAwait(false);

            throw new TimeoutException(
                $"Job {jobNumber} ({stepInfo.JobName}) exceeded " +
                "the maximum monitoring duration.");
        }

        private static int GetPollingDelay(
            int attempt)
        {
            if (attempt == 0)
            {
                return 200;
            }

            if (attempt < 6)
            {
                return 250;
            }

            if (attempt < 20)
            {
                return 500;
            }

            if (attempt < 50)
            {
                return 1000;
            }

            return 1500;
        }

        private static bool IsFailureStatus(
            string? status)
        {
            return string.Equals(
                       status,
                       "JCLError",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       status,
                       "Abend",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       status,
                       "Failed",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompletionStatus(
            string? status)
        {
            return string.Equals(
                       status,
                       "Complete",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       status,
                       "Output",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       status,
                       "Done",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static int NormalizeReturnCode(string? returnCode)
        {
            if (string.IsNullOrWhiteSpace(returnCode))
                return int.MaxValue;

            Match match = Regex.Match(
                returnCode.Trim(),
                @"^(?:(?:RC|CC|COND\s+CODE|CONDITION\s+CODE|RETURN\s+CODE)\s*[:=]?\s*)?([0-9]{1,4})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return match.Success && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int result)
                    ? result
                    : int.MaxValue;
        }

        private async Task HandleSystemFailureAsync(
            JobStepExecutionInfo stepInfo,
            string jobNumber,
            Stopwatch stopwatch,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            stopwatch.Stop();

            stepInfo.EndTime =
                DateTime.Now;

            stepInfo.DurationMs =
                stopwatch.ElapsedMilliseconds;

            stepInfo.SpoolExcerpt =
                await SafeFetchSpoolDiagnosticsAsync(
                        jobNumber,
                        cancellationToken)
                    .ConfigureAwait(false);

            PopulateFailureDiagnostics(
                trace,
                stepInfo,
                isSystemCrash: true);
        }

        private async Task HandleTimeoutAsync(
            JobStepExecutionInfo stepInfo,
            string jobNumber,
            Stopwatch stopwatch,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            stopwatch.Stop();

            stepInfo.EndTime =
                DateTime.Now;

            stepInfo.DurationMs =
                stopwatch.ElapsedMilliseconds;

            stepInfo.Status =
                "Timeout";

            stepInfo.SpoolExcerpt =
                await SafeFetchSpoolDiagnosticsAsync(
                        jobNumber,
                        cancellationToken)
                    .ConfigureAwait(false);

            trace.FailureStage =
                "JOB_TIMEOUT";

            trace.FailedJobName =
                stepInfo.JobName;

            trace.FailedJobNum =
                jobNumber;

            trace.FailedReturnCode =
                stepInfo.ReturnCode;

            trace.FailureReason =
                $"Job {stepInfo.JobName} ({jobNumber}) timed out " +
                $"after {stopwatch.Elapsed.TotalSeconds:F1} seconds. " +
                $"Last status={stepInfo.Status}.";

            trace.RootCauseAnalysis =
                "The job remained queued or active beyond the " +
                "monitoring threshold. Possible causes include " +
                "initiator contention, dataset locking, DB2 locking " +
                "or an unavailable IMS region.";

            trace.RecommendedAction =
                $"Inspect job {jobNumber} in ESCWA on server " +
                $"{stepInfo.ServerNode}. Check initiator class, " +
                "dataset locks, DB2 locks and IMS availability.";
        }

        // =============================================================
        // SPOOL ET DIAGNOSTIC
        // =============================================================

        private async Task<string> SafeFetchSpoolDiagnosticsAsync(
            string jobNumber,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _apiService
                    .GetJobDiagnosticDetailsAsync(
                        jobNumber,
                        45,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return
                    "Unable to retrieve Spool output: " +
                    exception.Message;
            }
        }

        private static void ConfigureAlreadyActiveDiagnostic(
            ContractActivationTrace trace,
            JobStepExecutionInfo stepInfo,
            string jobNumber)
        {
            stepInfo.DiagnosticHint =
                "ADDPRCT returned RC=0008. The premium may already " +
                "be paid or assigned.";

            trace.FailureStage =
                "JOB_EXECUTION_ADDPRCT_RC0008";

            trace.FailedJobName =
                stepInfo.JobName;

            trace.FailedJobNum =
                jobNumber;

            trace.FailedReturnCode =
                stepInfo.ReturnCode;

            trace.FailureReason =
                "The contract is already active or the premium has " +
                "already been assigned.";

            trace.RootCauseAnalysis =
                "ADDPRCT returned condition code 0008. The billing " +
                "premium is already registered for this contract.";

            trace.RecommendedAction =
                "If the contract still requires IMS activation, rerun " +
                "with Skip Prime to bypass ADDPRCT, LVPP06U and " +
                "LVPG22U.";
        }

        private static void PopulateFailureDiagnostics(
            ContractActivationTrace trace,
            JobStepExecutionInfo stepInfo,
            bool isSystemCrash)
        {
            string jobName =
                stepInfo.JobName;

            string returnCode =
                string.IsNullOrWhiteSpace(
                    stepInfo.ReturnCode)
                    ? "Unknown"
                    : stepInfo.ReturnCode;

            trace.FailureStage =
                isSystemCrash
                    ? $"MAINFRAME_CRASH_{stepInfo.Status}"
                    : $"BUSINESS_RC_ERROR_{jobName}";

            trace.FailedJobName =
                jobName;

            trace.FailedJobNum =
                stepInfo.JobNum;

            trace.FailedReturnCode =
                returnCode;

            trace.FailureReason =
                isSystemCrash
                    ? $"System failure on {jobName} " +
                      $"({stepInfo.JobNum}). " +
                      $"Status={stepInfo.Status}, " +
                      $"Code={returnCode}."
                    : $"Business error on {jobName} " +
                      $"({stepInfo.JobNum}). " +
                      $"Return code={returnCode}.";

            if (string.Equals(
                    stepInfo.Status,
                    "JCLError",
                    StringComparison.OrdinalIgnoreCase))
            {
                stepInfo.DiagnosticHint =
                    "JCL error, missing dataset, allocation error or " +
                    "invalid job-card parameter.";

                trace.RootCauseAnalysis =
                    $"{jobName} failed with JCLError before or during " +
                    "allocation. Possible causes include a missing " +
                    "dataset, a locked dataset, an invalid symbolic " +
                    "parameter or an invalid SCHENV.";

                trace.RecommendedAction =
                    "Inspect JESYSMSG and JESMSGLG to identify the DD, " +
                    "DSN or symbolic parameter that caused the error.";

                return;
            }

            if (string.Equals(
                    stepInfo.Status,
                    "Abend",
                    StringComparison.OrdinalIgnoreCase))
            {
                stepInfo.DiagnosticHint =
                    $"Program Abend {returnCode} during {jobName}.";

                trace.RootCauseAnalysis =
                    $"{jobName} terminated abnormally. Possible causes " +
                    "include invalid numeric data, a missing load module, " +
                    "a DB2 failure, an IMS failure or a data inconsistency.";

                trace.RecommendedAction =
                    "Inspect SYSOUT, SYSPRINT and dumps. Verify contract, " +
                    "premium, BUCP, CMDPMT, CUS and environment values.";

                return;
            }

            if (IsSameJob(
                    jobName,
                    "ADDPRCT"))
            {
                stepInfo.DiagnosticHint =
                    $"ADDPRCT rejected the payment injection " +
                    $"with RC={returnCode}.";

                trace.RootCauseAnalysis =
                    $"ADDPRCT rejected CMDPMT={trace.Cmdpmt}, " +
                    $"BUCP={trace.Bucp}, contract={trace.JclContract} " +
                    $"or the supplied premium.";

                trace.RecommendedAction =
                    "Inspect the ADDPRCT Spool. Verify BUCP/CMDPMT " +
                    "mapping, CUS, contract and applicable distribution " +
                    "channel.";

                return;
            }

            if (IsSameJob(
                    jobName,
                    "LVPP06U") ||
                IsSameJob(
                    jobName,
                    "LVPG22U"))
            {
                stepInfo.DiagnosticHint =
                    $"Premium accounting failed in {jobName} " +
                    $"with RC={returnCode}.";

                trace.RootCauseAnalysis =
                    $"{jobName} failed after ADDPRCT. The premium, " +
                    "accounting office, payment method or financial " +
                    "state may be inconsistent.";

                trace.RecommendedAction =
                    "Inspect SYSOUT and SYSPRINT. If ADDPRCT already " +
                    "registered the premium, use Skip Prime on retry.";

                return;
            }

            if (IsSameJob(
                    jobName,
                    "LI1J04D0") ||
                IsSameJob(
                    jobName,
                    "LI1J04D2"))
            {
                stepInfo.DiagnosticHint =
                    $"IMS activation failed in {jobName} " +
                    $"with RC={returnCode}.";

                trace.RootCauseAnalysis =
                    $"{jobName}, including subjob " +
                    $"{stepInfo.SubJobName}, failed in IMS " +
                    $"environment IM7{trace.EnvIms}. FASTCTRL=" +
                    $"{trace.FastCtrlDsn}.";

                trace.RecommendedAction =
                    "Inspect the Spool and IMS messages. Since billing " +
                    "may already have succeeded, retry with Skip Prime " +
                    "after resolving the IMS issue.";

                return;
            }

            if (stepInfo.IsBranch23Step)
            {
                stepInfo.DiagnosticHint =
                    $"BR23 job {jobName}, template " +
                    $"{stepInfo.JclName}, failed with " +
                    $"RC={returnCode}.";

                trace.RootCauseAnalysis =
                    $"The BR23 phase failed after the standard " +
                    $"activation. Fund events, NAV consolidation or " +
                    $"FP01 processing was rejected. Consolidation=" +
                    $"{trace.Branch23ConsolidationDate:yyyyMMdd}.";

                trace.RecommendedAction =
                    "Inspect the Spool. Verify the generated movement, " +
                    "fund codes, NAV values and BR23 JCL parameters. " +
                    "Do not replay completed billing steps.";

                return;
            }

            stepInfo.DiagnosticHint =
                $"Job {jobName} failed. Status={stepInfo.Status}, " +
                $"RC={returnCode}.";

            trace.RootCauseAnalysis =
                $"{jobName} did not return an accepted condition " +
                "code. AutoActivator accepts only RC=0000 and RC=0004.";

            trace.RecommendedAction =
                "Inspect the Mainframe Spool and correct the reported " +
                "business or technical error.";
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private sealed record PaymentRoutingContext(
            string BusinessChannel,
            string Bucp,
            string PaymentCommand,
            string Diagnostic);

        private static PaymentRoutingContext ResolveAndValidatePaymentRouting(
            IReadOnlyDictionary<string, string> variables,
            ContractActivationTrace trace)
        {
            string bucp = GetRequiredRoutingVariable(variables, "BUCP")
                .PadLeft(5, '0');
            string paymentCommand = GetRequiredRoutingVariable(
                    variables,
                    "CMDPMT")
                .ToUpperInvariant();

            string businessChannel;
            if (bucp == "00382" && paymentCommand == "8")
            {
                businessChannel = "BANK";
            }
            else if (bucp == "80819" && paymentCommand == "H")
            {
                businessChannel = "BROKER_NEW_GEN";
            }
            else
            {
                trace.FailureStage = "PAYMENT_ROUTING_NOT_SUPPORTED";
                trace.FailureReason =
                    $"BUCP/CMDPMT {bucp}/{paymentCommand} is not supported.";
                trace.RootCauseAnalysis =
                    "The activation plan did not provide a confirmed Bank or " +
                    "Broker New Gen payment route.";
                trace.RecommendedAction =
                    "Resolve the ELIA ChannelCode before job submission. " +
                    "Supported routes are Bank 00382/8 and Broker New Gen 80819/H.";
                throw new InvalidOperationException(trace.FailureReason);
            }

            string channelCode = string.IsNullOrWhiteSpace(trace.EliaChannelCode)
                ? "-"
                : trace.EliaChannelCode.Trim();
            string diagnostic =
                $"Payment routing={businessChannel} | ChannelCode={channelCode} | " +
                $"BUCP={bucp} | CMDPMT={paymentCommand}";

            return new PaymentRoutingContext(
                businessChannel,
                bucp,
                paymentCommand,
                diagnostic);
        }

        private static string GetRequiredRoutingVariable(
            IReadOnlyDictionary<string, string> variables,
            string key)
        {
            if (!variables.TryGetValue(key, out string? value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Required activation variable '{key}' is missing.");
            }

            return value.Trim();
        }

        private static string NormalizeGeneratedJobNumber(string? jobNumber)
        {
            string value = (jobNumber ?? string.Empty).Trim();
            Match match = Regex.Match(
                value,
                @"^[Jj]?(?<number>[0-9]{5,10})$",
                RegexOptions.CultureInvariant);

            if (!match.Success ||
                match.Groups["number"].Value.All(character => character == '0'))
            {
                return string.Empty;
            }

            return "J" + match.Groups["number"].Value;
        }

        private static bool IsSameJob(
            string? left,
            string? right)
        {
            return string.Equals(
                left?.Trim(),
                right?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveServerCompatibleWithEnvironment(
            string environment)
        {
            string server =
                MicroFocusApiService.ActiveServer?.Trim().ToLowerInvariant()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(server))
            {
                return false;
            }

            string normalizedEnvironment =
                NormalizeEnvironment(environment);

            return normalizedEnvironment switch
            {
                "D" => server.StartsWith("sdmfas", StringComparison.Ordinal),
                "Q" => server.StartsWith("sqmfas", StringComparison.Ordinal),
                "A" => server.StartsWith("samfas", StringComparison.Ordinal),
                "P" => server.StartsWith("spmfas", StringComparison.Ordinal),
                _ => false
            };
        }

        private static string NormalizeEnvironment(
            string? environment)
        {
            if (string.IsNullOrWhiteSpace(environment))
            {
                return "Q";
            }

            string normalized =
                environment
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

            if (normalized.StartsWith(
                    "A",
                    StringComparison.Ordinal))
            {
                return "A";
            }

            if (normalized.StartsWith(
                    "P",
                    StringComparison.Ordinal))
            {
                return "P";
            }

            return normalized;
        }
    }
}
