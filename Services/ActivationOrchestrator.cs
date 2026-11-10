using AutoActivator.Config;
using AutoActivator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Orchestre la préparation, la soumission et le suivi des jobs JCL
    /// nécessaires à l'activation d'un contrat.
    /// Découpé en classes partielles :
    /// - ActivationOrchestrator.cs : Planification et orchestration de la séquence ;
    /// - ActivationOrchestrator.Submission.cs : Préparation JCL, soumission et suivi JES ;
    /// - ActivationOrchestrator.Diagnostics.cs : Spool, diagnostics et validation de routage ;
    /// - ActivationOrchestrator.Branch23ActivationOnly.cs : Runner JCL dédié au workflow Branche 23.
    /// </summary>
    public sealed partial class ActivationOrchestrator
    {
        private readonly JclProcessorService _jclProcessor;
        private readonly MicroFocusApiService _apiService;

        private readonly HashSet<JobStepExecutionInfo> _branch23ActivationSteps = new();

        private static readonly (string JobName, string SubJobName, string Phase)[]
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
                    "Phase 2: Core IMS Contract Activation (LI1J04D0 -> LI1J04D1)"
                ),
                (
                    "LI1J04D2",
                    "LI1J04D3",
                    "Phase 2: Final IMS Synchronization (LI1J04D2 -> LI1J04D3)"
                )
            };

        private static readonly (string JobName, string JclName, string Phase)[]
            Branch23SequenceDefinition =
            {
                (
                    Settings.Branch23.FundEvents.JobName,
                    Settings.Branch23.FundEvents.JclName,
                    "Phase 3 BR23: Fund Events (LVDQFG03 -> LVFG03U)"
                ),
                (
                    Settings.Branch23.Consolidation.JobName,
                    Settings.Branch23.Consolidation.JclName,
                    "Phase 3 BR23: Consolidation Seq 1, STEP=1 (LVDQFG01 -> LVFG01U)"
                ),
                (
                    Settings.Branch23.Fp01.JobName,
                    Settings.Branch23.Fp01.JclName,
                    "Phase 3 BR23: FP01 Event Seq 3 (LVD1PE01 -> LVPE01U)"
                )
            };

        private static readonly (string JobName, string SubJobName, string Phase)[]
            Branch23FinalStandardPassDefinition =
            {
                (
                    "LI1J04D0",
                    "LI1J04D1",
                    "Phase 4 BR23: Final IMS Activation (LI1J04D0 -> LI1J04D1, Skip Prime)"
                ),
                (
                    "LI1J04D2",
                    "LI1J04D3",
                    "Phase 4 BR23: Final IMS Synchronization (LI1J04D2 -> LI1J04D3, Skip Prime)"
                )
            };

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
                    "Le répertoire contenant les JCL est obligatoire.",
                    nameof(jclDirectory));
            }

            _jclProcessor = new JclProcessorService(jclDirectory);
            _apiService = new MicroFocusApiService();
        }

        public Task PreloadJclTemplatesAsync()
        {
            return _jclProcessor.PreloadTemplatesAsync();
        }

        public Task PreloadAllJclTemplatesAsync()
        {
            IEnumerable<string> allTemplates =
                JclProcessorService.StandardActivationJobs
                    .Concat(Branch23JclNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

            return _jclProcessor.PreloadTemplatesAsync(allTemplates);
        }

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
            Dictionary<string, Dictionary<string, string>>? branch23JobVariables = null,
            Func<CancellationToken, Task>? prepareBranch23NavsAsync = null,
            Func<CancellationToken, Task>? beforeBranch23FundEventsAsync = null,
            Func<CancellationToken, Task>? afterBranch23FundEventsAsync = null,
            Func<CancellationToken, Task>? beforeInitialLiValidationAsync = null,
            Func<CancellationToken, Task>? afterActivationValidationAsync = null,
            Func<string, string, CancellationToken, Task<string>>? resolveGeneratedJobNumberAsync = null,
            ActivationJobSelection? jobSelection = null)
        {
            if (generalVariables == null)
                throw new ArgumentNullException(nameof(generalVariables));
            if (addprctSpecificVariables == null)
                throw new ArgumentNullException(nameof(addprctSpecificVariables));

            trace ??= new ContractActivationTrace();
            cancellationToken.ThrowIfCancellationRequested();

            bool isBranch23 = branch23JobVariables != null;

            string currentEnvironment = generalVariables.TryGetValue("ENV", out string? environmentValue)
                ? NormalizeEnvironment(environmentValue)
                : "Q";

            PaymentRoutingContext paymentRouting =
                ResolveAndValidatePaymentRouting(BuildAddprctVariables(generalVariables, addprctSpecificVariables), trace);

            bool bypassBilling =
                skipPrime ||
                string.Equals(channel, "C05", StringComparison.OrdinalIgnoreCase);

            InitializeExecutionTrace(
                trace,
                isBranch23,
                bypassBilling,
                skipPrime,
                jobSelection);

            trace.Bucp = paymentRouting.Bucp;
            trace.Cmdpmt = paymentRouting.PaymentCommand;
            generalVariables["BUCP"] = paymentRouting.Bucp;
            generalVariables["CMDPMT"] = paymentRouting.PaymentCommand;
            addprctSpecificVariables["BUCP"] = paymentRouting.Bucp;
            addprctSpecificVariables["CMDPMT"] = paymentRouting.PaymentCommand;
            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(trace.RoutingDiagnostic)
                ? paymentRouting.Diagnostic
                : trace.RoutingDiagnostic + " | " + paymentRouting.Diagnostic;

            onProgress?.Invoke(paymentRouting.Diagnostic + ".");

            try
            {
                if (trace.Steps.Any(step => !step.IsSkipped &&
                        !string.IsNullOrWhiteSpace(step.SubJobName) &&
                        step.SubJobName != "-") &&
                    resolveGeneratedJobNumberAsync == null)
                {
                    trace.FailureStage = "GENERATED_JOB_RESOLVER_REQUIRED";
                    trace.FailureReason =
                        "Generated LI job monitoring is not configured. No billing or activation job has been submitted.";
                    trace.RootCauseAnalysis =
                        "MicroFocusApiService must provide a correlated child job number for each LI generator execution.";
                    trace.RecommendedAction =
                        "Connect resolveGeneratedJobNumberAsync to a verified ESCWA parent/child lookup before starting the workflow.";
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

                trace.ServerNode = MicroFocusApiService.ActiveServer ?? "Unknown";

                var addprctVariables = BuildAddprctVariables(generalVariables, addprctSpecificVariables);
                int executionCounter = 1;

                foreach (JobStepExecutionInfo step in trace.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (step.IsSkipped)
                    {
                        onProgress?.Invoke($"[{step.StepOrder}] {step.JobName} skipped. {step.DiagnosticHint}");
                        continue;
                    }

                    bool isFinalStandardPassStep =
                        !step.IsBranch23Step &&
                        step.PhaseDescription != null &&
                        step.PhaseDescription.StartsWith("Phase 4 BR23:", StringComparison.OrdinalIgnoreCase);

                    if (isFinalStandardPassStep && IsSameJob(step.JobName, "LI1J04D0"))
                    {
                        onProgress?.Invoke(
                            "BR23 jobs completed. Starting the final standard IMS pass with premium jobs skipped.");
                    }

                    bool isFundEventsStep =
                        step.IsBranch23Step &&
                        IsSameJob(step.JobName, Settings.Branch23.FundEvents.JobName);

                    bool isConsolidationStep =
                        step.IsBranch23Step &&
                        IsSameJob(step.JobName, Settings.Branch23.Consolidation.JobName);

                    if (isFundEventsStep && beforeBranch23FundEventsAsync != null)
                    {
                        onProgress?.Invoke(
                            "BR23: resolving funds and preparing all NAV dates up to consolidation before LVDQFG03.");

                        await RunBranch23HookAsync(
                                beforeBranch23FundEventsAsync,
                                step,
                                trace,
                                "BR23_PREPARATION_BEFORE_FG03",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (isConsolidationStep && prepareBranch23NavsAsync != null)
                    {
                        onProgress?.Invoke("BR23: running the optional legacy NAV validation hook before LVDQFG01.");

                        await RunBranch23NavPreparationAsync(
                                prepareBranch23NavsAsync,
                                step,
                                trace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    Dictionary<string, string> variablesForJob = BuildVariablesForStep(
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
                                string.Equals(candidate.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                                (candidate.PhaseDescription == null ||
                                 !candidate.PhaseDescription.StartsWith("Phase 4 BR23:", StringComparison.OrdinalIgnoreCase)))
                            ?? step;

                        onProgress?.Invoke(
                            "LVPG22U completed. Validating persisted premium, assigned receipt and eligible PE01/PE03 before LI1J04D0.");

                        await RunPreLiBusinessValidationAsync(
                                beforeInitialLiValidationAsync,
                                blockedLiStep,
                                trace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (isFundEventsStep && afterBranch23FundEventsAsync != null)
                    {
                        onProgress?.Invoke("BR23: running the optional post-LVDQFG03 validation hook.");

                        JobStepExecutionInfo blockedStep =
                            trace.Steps.FirstOrDefault(candidate =>
                                candidate.IsBranch23Step &&
                                IsSameJob(candidate.JobName, Settings.Branch23.Consolidation.JobName))
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

                bool finalLisaValidationExecuted = afterActivationValidationAsync != null;

                if (afterActivationValidationAsync != null)
                {
                    onProgress?.Invoke("Final standard pass completed. Validating contract creation in LISA.");

                    try
                    {
                        await afterActivationValidationAsync(cancellationToken).ConfigureAwait(false);
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
                            trace.LisaInternalId = string.IsNullOrWhiteSpace(trace.LisaInternalId)
                                ? "PENDING_ASYNC_LISA_CREATION"
                                : trace.LisaInternalId;
                            trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(trace.RoutingDiagnostic)
                                ? "LISA finalization pending asynchronously: " + exception.Message
                                : trace.RoutingDiagnostic + " | LISA finalization pending asynchronously: " + exception.Message;
                            onProgress?.Invoke(
                                "All Branch 23 jobs completed. SCNTT0 is not yet visible; LISA creation is asynchronous and does not change the technical sequence to FAILED.");
                        }
                        else
                        {
                            trace.FinalStatus = "FAILED";
                            trace.FailureStage = string.IsNullOrWhiteSpace(trace.FailureStage)
                                ? "LISA_FINALIZATION_TIMEOUT"
                                : trace.FailureStage;
                            trace.FailureReason = string.IsNullOrWhiteSpace(trace.FailureReason)
                                ? exception.Message
                                : trace.FailureReason;
                            trace.RootCauseAnalysis = string.IsNullOrWhiteSpace(trace.RootCauseAnalysis)
                                ? "All JCL jobs returned accepted condition codes, but the contract was not confirmed in LV.SCNTT0."
                                : trace.RootCauseAnalysis;
                            trace.RecommendedAction = string.IsNullOrWhiteSpace(trace.RecommendedAction)
                                ? "Inspect the completed jobs and confirm the business state before choosing any retry."
                                : trace.RecommendedAction;
                            throw;
                        }
                    }
                }

                trace.FinalStatus = "SUCCESS";
                trace.EndTime = DateTime.Now;
                onProgress?.Invoke(
                    finalLisaValidationExecuted
                        ? "[SUCCESS] JCL sequence completed and final LISA validation succeeded."
                        : "[SUCCESS] JCL sequence completed. Final LISA validation was not requested by the caller.");
            }
            catch (OperationCanceledException)
            {
                trace.EndTime = DateTime.Now;
                MarkRemainingStepsAborted(trace, "Cancelled by user.");
                trace.FinalStatus = "CANCELLED";
                trace.FailureStage = "USER_CANCELLED";
                trace.FailureReason = "Operation was cancelled by the user.";
                onProgress?.Invoke("[CANCELLED] The activation sequence was stopped.");
                throw;
            }
            catch (Exception exception)
            {
                trace.EndTime = DateTime.Now;
                MarkRemainingStepsAborted(
                    trace,
                    "Not executed because the sequence stopped after a previous error.");

                if (string.Equals(exception.Message, "ALREADY_ACTIVE", StringComparison.Ordinal))
                {
                    trace.FinalStatus = "ALREADY_ACTIVE";
                    onProgress?.Invoke("[ALREADY ACTIVE] ADDPRCT returned RC=0008.");
                    throw;
                }

                trace.FinalStatus = "FAILED";
                if (string.IsNullOrWhiteSpace(trace.FailureReason))
                {
                    trace.FailureReason = exception.Message;
                }

                onProgress?.Invoke("[CRITICAL ERROR] Sequence interrupted: " + exception.Message);
                throw;
            }
        }

        private static void InitializeExecutionTrace(
            ContractActivationTrace trace,
            bool isBranch23,
            bool bypassBilling,
            bool skipPrime,
            ActivationJobSelection? jobSelection = null)
        {
            trace.Steps.Clear();
            trace.StartTime = DateTime.Now;
            trace.EndTime = default;
            trace.FinalStatus = "RUNNING";
            trace.FailureStage = string.Empty;
            trace.FailureReason = string.Empty;
            trace.RootCauseAnalysis = string.Empty;
            trace.RecommendedAction = string.Empty;
            trace.FailedJobName = "-";
            trace.FailedJobNum = "-";
            trace.FailedReturnCode = "-";

            int order = 1;

            foreach (var definition in SequenceDefinition)
            {
                bool isPhaseOne =
                    IsSameJob(definition.JobName, "ADDPRCT") ||
                    IsSameJob(definition.JobName, "LVPP06U") ||
                    IsSameJob(definition.JobName, "LVPG22U");

                bool deselectedByUser = false;
                string deselectedReason = string.Empty;

                if (jobSelection != null)
                {
                    if (IsSameJob(definition.JobName, "ADDPRCT") &&
                        (!jobSelection.RunAddPrct || jobSelection.UseSqlPrctInsert))
                    {
                        deselectedByUser = true;
                        deselectedReason = jobSelection.UseSqlPrctInsert && jobSelection.RunAddPrct
                            ? "Executed via direct SQL INSERT into LV.PRCTT0."
                            : "Unchecked in custom job selection.";
                    }
                    else if (IsSameJob(definition.JobName, "LVPP06U") && !jobSelection.RunLvpp06u)
                    {
                        deselectedByUser = true;
                        deselectedReason = "Unchecked in custom job selection.";
                    }
                    else if (IsSameJob(definition.JobName, "LVPG22U") && !jobSelection.RunLvpg22u)
                    {
                        deselectedByUser = true;
                        deselectedReason = "Unchecked in custom job selection.";
                    }
                    else if (IsSameJob(definition.JobName, "LI1J04D0") && !jobSelection.RunLi1j04d0)
                    {
                        deselectedByUser = true;
                        deselectedReason = "Unchecked in custom job selection.";
                    }
                    else if (IsSameJob(definition.JobName, "LI1J04D2") && !jobSelection.RunLi1j04d2)
                    {
                        deselectedByUser = true;
                        deselectedReason = "Unchecked in custom job selection.";
                    }
                }

                bool skipped = (isPhaseOne && bypassBilling) || deselectedByUser;

                var step = new JobStepExecutionInfo
                {
                    StepOrder = order++,
                    JobName = definition.JobName,
                    SubJobName = definition.SubJobName,
                    PhaseDescription = definition.Phase,
                    Status = skipped ? "Skipped" : "Pending",
                    DiagnosticHint = skipped
                        ? (deselectedByUser
                            ? deselectedReason
                            : skipPrime
                                ? "Bypassed by Skip Prime mode."
                                : "Bypassed automatically for C05.")
                        : string.Empty,
                    IsBranch23Step = false
                };

                trace.Steps.Add(step);
            }

            if (!isBranch23)
            {
                return;
            }

            foreach (var definition in Branch23SequenceDefinition)
            {
                bool deselected = false;
                if (jobSelection != null)
                {
                    if (IsSameJob(definition.JobName, Settings.Branch23.FundEvents.JobName) && !jobSelection.RunLvdqFg03)
                        deselected = true;
                    else if (IsSameJob(definition.JobName, Settings.Branch23.Consolidation.JobName) && !jobSelection.RunLvdqFg01)
                        deselected = true;
                    else if (IsSameJob(definition.JobName, Settings.Branch23.Fp01.JobName) && !jobSelection.RunLvd1Pe01)
                        deselected = true;
                }

                trace.Steps.Add(
                    new JobStepExecutionInfo
                    {
                        StepOrder = order++,
                        JobName = definition.JobName,
                        JclName = definition.JclName,
                        SubJobName = "-",
                        PhaseDescription = definition.Phase,
                        Status = deselected ? "Skipped" : "Pending",
                        DiagnosticHint = deselected ? "Unchecked in custom job selection." : string.Empty,
                        IsBranch23Step = true
                    });
            }

            foreach (var definition in Branch23FinalStandardPassDefinition)
            {
                bool deselected = false;
                if (jobSelection != null)
                {
                    if (IsSameJob(definition.JobName, "LI1J04D0") && !jobSelection.RunLi1j04d0)
                        deselected = true;
                    else if (IsSameJob(definition.JobName, "LI1J04D2") && !jobSelection.RunLi1j04d2)
                        deselected = true;
                }

                trace.Steps.Add(
                    new JobStepExecutionInfo
                    {
                        StepOrder = order++,
                        JobName = definition.JobName,
                        SubJobName = definition.SubJobName,
                        PhaseDescription = definition.Phase,
                        Status = deselected ? "Skipped" : "Pending",
                        DiagnosticHint = deselected
                            ? "Unchecked in custom job selection."
                            : "Automatic final standard pass after BR23. Premium jobs are not replayed.",
                        IsBranch23Step = false
                    });
            }
        }

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
            if (skipLogon && IsActiveServerCompatibleWithEnvironment(environment))
            {
                onProgress?.Invoke(
                    $"Mainframe authentication skipped. Using the existing ESCWA session on {MicroFocusApiService.ActiveServer}.");

                if (isBranch23)
                {
                    await PreloadAllJclTemplatesAsync().ConfigureAwait(false);
                }
                else
                {
                    await PreloadJclTemplatesAsync().ConfigureAwait(false);
                }

                return;
            }

            if (skipLogon)
            {
                onProgress?.Invoke(
                    $"The cached ESCWA node does not match environment {environment}. A new authentication is required.");
            }

            onProgress?.Invoke($"Authenticating with Mainframe ({environment}) and preloading JCL templates.");

            Task<bool> logonTask = _apiService.LogonAsync(
                username,
                password,
                environment,
                message => onProgress?.Invoke(message),
                cancellationToken);

            Task preloadTask = isBranch23
                ? PreloadAllJclTemplatesAsync()
                : PreloadJclTemplatesAsync();

            await Task.WhenAll(logonTask, preloadTask).ConfigureAwait(false);

            bool authenticated = await logonTask.ConfigureAwait(false);
            if (authenticated)
            {
                return;
            }

            trace.FailureStage = "MAINFRAME_AUTH";
            trace.FailureReason = $"Unable to authenticate on MicroFocus ESCWA for environment {environment}.";
            trace.RootCauseAnalysis = "All configured Mainframe nodes rejected the connection or timed out.";
            trace.RecommendedAction = "Verify VPN connectivity, Mainframe credentials, the target environment and ESCWA availability.";

            throw new InvalidOperationException(
                "Unable to connect to the MicroFocus server. Verify VPN connectivity and Mainframe credentials.");
        }

        private static Dictionary<string, string> BuildAddprctVariables(
            Dictionary<string, string> generalVariables,
            Dictionary<string, string> addprctSpecificVariables)
        {
            var result = new Dictionary<string, string>(
                generalVariables,
                StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> variable in addprctSpecificVariables)
            {
                result[variable.Key] = variable.Value;
            }

            return result;
        }

        private static Dictionary<string, string> BuildVariablesForStep(
            JobStepExecutionInfo step,
            Dictionary<string, string> generalVariables,
            Dictionary<string, string> addprctVariables,
            Dictionary<string, Dictionary<string, string>>? branch23JobVariables)
        {
            if (!step.IsBranch23Step)
            {
                return IsSameJob(step.JobName, "ADDPRCT")
                    ? new Dictionary<string, string>(addprctVariables, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(generalVariables, StringComparer.OrdinalIgnoreCase);
            }

            var variables = new Dictionary<string, string>(
                generalVariables,
                StringComparer.OrdinalIgnoreCase);

            if (branch23JobVariables == null)
            {
                return variables;
            }

            Dictionary<string, string>? specificVariables = null;

            if (!string.IsNullOrWhiteSpace(step.JclName))
            {
                branch23JobVariables.TryGetValue(step.JclName, out specificVariables);
            }

            if (specificVariables == null && !string.IsNullOrWhiteSpace(step.JobName))
            {
                branch23JobVariables.TryGetValue(step.JobName, out specificVariables);
            }

            if (specificVariables == null)
            {
                return variables;
            }

            foreach (KeyValuePair<string, string> variable in specificVariables)
            {
                variables[variable.Key] = variable.Value;
            }

            return variables;
        }

        private static async Task RunPreLiBusinessValidationAsync(
            Func<CancellationToken, Task> validation,
            JobStepExecutionInfo blockedLiStep,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            try
            {
                await validation(cancellationToken).ConfigureAwait(false);
                blockedLiStep.DiagnosticHint = string.IsNullOrWhiteSpace(blockedLiStep.DiagnosticHint)
                    ? "Pre-LI business prerequisites confirmed."
                    : blockedLiStep.DiagnosticHint + " Pre-LI business prerequisites confirmed.";
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
                    "LI1J04D0 was not submitted because the persisted premium, assigned receipt or eligible event is missing.";
                trace.FailureStage = "PRE_LI_BUSINESS_PREREQUISITE_MISSING";
                trace.FailedJobName = blockedLiStep.JobName;
                trace.FailedJobNum = "-";
                trace.FailedReturnCode = "-";
                trace.FailureReason = exception.Message;
                trace.RootCauseAnalysis =
                    "LVPG22U completed technically, but RC=0000 does not prove that PRIST0, PECHT0 and ELIAT0 were populated for the target contract.";
                trace.RecommendedAction =
                    "Inspect ADDPRCT, LVPP06U and LVPG22U business outputs. Do not replay LI or billing automatically.";
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
                await hook(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                blockedStep.Status = "FundsNotResolved";
                blockedStep.ErrorMessage = exception.Message;

                trace.FailureStage = exception is Branch23FundResolutionException fundResolutionException
                    ? fundResolutionException.FailureStage
                    : defaultFailureStage;
                trace.FailedJobName = blockedStep.JobName;
                trace.FailureReason = exception.Message;
                trace.RootCauseAnalysis = trace.FailureStage == "BR23_PREACTIVATION_FUNDS_MISSING"
                    ? "No business-confirmed BR23 fund was available before LVDQFG03. UI fallback funds have priority; ELIA TB5UGFF is only a secondary source."
                    : trace.FailureStage == "BR23_PREPARATION_BEFORE_FG03"
                        ? "BR23 fund or NAV preparation failed before LVDQFG03 could be submitted."
                        : "A BR23 workflow hook failed before the next JCL step could be submitted.";
                trace.RecommendedAction = trace.FailureStage == "BR23_PREACTIVATION_FUNDS_MISSING"
                    ? "Enter the confirmed fund code in BR23 fallback funds and rerun with Skip Prime."
                    : "Inspect the NAV preparation details for J, J+1 and J+2. Do not replay completed billing jobs.";
                throw;
            }
        }

        private static async Task RunBranch23NavPreparationAsync(
            Func<CancellationToken, Task> prepareNavsAsync,
            JobStepExecutionInfo consolidationStep,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            try
            {
                await prepareNavsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                consolidationStep.Status = "NavNotReady";
                consolidationStep.ErrorMessage = exception.Message;
                trace.FailureStage = "BR23_NAV_PREPARATION";
                trace.FailedJobName = consolidationStep.JobName;
                trace.FailureReason = exception.Message;
                trace.RootCauseAnalysis =
                    "The BR23 workflow requires every confirmed fund to have NAV values through the consolidation date. " +
                    "Closed funds use GFEVT0 and open funds use GFCRT0.";
                trace.RecommendedAction =
                    "Create or correct the missing NAV values, then replay only the unfinished BR23 steps.";
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
                if (!string.Equals(step.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                step.Status = "Aborted";
                step.DiagnosticHint = reason;
            }
        }
    }
}
