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
    /// - ActivationOrchestrator.cs : Séquence PREMELIA commune (ADDPRCT → LVPP06U → LVPG22U → LI1J04D0 → LI1J04D2) ;
    /// - ActivationOrchestrator.Submission.cs : Préparation JCL, soumission et suivi JES (partagé B21/B23) ;
    /// - ActivationOrchestrator.Diagnostics.cs : Spool, diagnostics et validation de routage ;
    /// - ActivationOrchestrator.Branch23ActivationOnly.cs : Runner JCL utilisé par le workflow Branche 23.
    ///
    /// NORMALISATION B21 / B23 : la Branche 23 n'emprunte plus jamais la séquence standard avec des
    /// « phases 3/4 BR23 » (code historiquement mort). Les deux branches partagent la même couche de
    /// soumission/suivi (<see cref="ProcessSubmitAndWaitAsync"/>), les mêmes variables JCL communes et
    /// la même authentification ; seuls les prérequis et étapes spécifiques BR23 vivent dans
    /// <see cref="Branch23Activation.Branch23ActivationWorkflow"/>.
    /// </summary>
    public sealed partial class ActivationOrchestrator
    {
        private readonly JclProcessorService _jclProcessor;
        private readonly MicroFocusApiService _apiService;

        private readonly HashSet<JobStepExecutionInfo> _branch23ActivationSteps = new();

        private static readonly (string JobName, string SubJobName, string Phase)[]
            SequenceDefinition =
            {
                ("ADDPRCT", "-", "Phase 1: Billing & Premium Injection (ADDPRCT)"),
                ("LVPP06U", "-", "Phase 1: Premium Accounting Validation (LVPP06U)"),
                ("LVPG22U", "-", "Phase 1: Financial Posting & Clearing (LVPG22U)"),
                ("LI1J04D0", "LI1J04D1", "Phase 2: Core IMS Contract Activation (LI1J04D0 -> LI1J04D1)"),
                ("LI1J04D2", "LI1J04D3", "Phase 2: Final IMS Synchronization (LI1J04D2 -> LI1J04D3)")
            };

        /// <summary>
        /// Templates JCL spécifiques Branche 23 (LVDQFG03 / LVDQFG01 / LVD1PE01) à précharger.
        /// </summary>
        public static string[] Branch23JclNames { get; } =
            new[]
            {
                Settings.Branch23.FundEvents.JclName,
                Settings.Branch23.Consolidation.JclName,
                Settings.Branch23.Fp01.JclName
            }
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

        /// <summary>
        /// Exécute la séquence PREMELIA standard (Branche 21).
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

            string currentEnvironment = generalVariables.TryGetValue("ENV", out string? environmentValue)
                ? NormalizeEnvironment(environmentValue)
                : "Q";

            if (!generalVariables.TryGetValue("CLASS", out string? currentJobClass) ||
                string.IsNullOrWhiteSpace(currentJobClass))
            {
                generalVariables["CLASS"] = Settings.ActivationAutomation.DefaultJclJobClass;
            }

            bool bypassBilling =
                skipPrime ||
                string.Equals(channel, "C05", StringComparison.OrdinalIgnoreCase);

            InitializeExecutionTrace(trace, bypassBilling, skipPrime, jobSelection);

            var combinedAddprctVars = BuildAddprctVariables(generalVariables, addprctSpecificVariables);
            bool hasSuppliedPaymentVars =
                combinedAddprctVars.TryGetValue("BUCP", out string? rawBucp) && !string.IsNullOrWhiteSpace(rawBucp) &&
                combinedAddprctVars.TryGetValue("CMDPMT", out string? rawCmd) && !string.IsNullOrWhiteSpace(rawCmd);

            if (!bypassBilling || hasSuppliedPaymentVars)
            {
                PaymentRoutingContext paymentRouting =
                    ResolveAndValidatePaymentRouting(combinedAddprctVars, trace);

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
            }

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
                        isBranch23: false,
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

                    Dictionary<string, string> variablesForJob = IsSameJob(step.JobName, "ADDPRCT")
                        ? new Dictionary<string, string>(addprctVariables, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(generalVariables, StringComparer.OrdinalIgnoreCase);

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
                                IsSameJob(candidate.JobName, "LI1J04D0") &&
                                string.Equals(candidate.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                            ?? step;

                        onProgress?.Invoke(
                            "LVPG22U completed. Validating persisted premium and assigned receipt before LI1J04D0.");

                        await RunPreLiBusinessValidationAsync(
                                beforeInitialLiValidationAsync,
                                blockedLiStep,
                                trace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                bool finalLisaValidationExecuted = afterActivationValidationAsync != null;

                if (afterActivationValidationAsync != null)
                {
                    onProgress?.Invoke("Standard sequence completed. Validating contract creation in LISA.");

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

                trace.FinalStatus = "SUCCESS";
                trace.EndTime = DateTime.Now;
                onProgress?.Invoke(
                    finalLisaValidationExecuted
                        ? "[SUCCESS] JCL sequence completed and final LISA validation succeeded."
                        : "[SUCCESS] JCL sequence completed.");
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

                (bool deselectedByUser, string deselectedReason) = ResolveUserDeselection(definition.JobName, jobSelection);

                bool skipped = (isPhaseOne && bypassBilling) || deselectedByUser;

                trace.Steps.Add(new JobStepExecutionInfo
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
                });
            }
        }

        private static (bool Deselected, string Reason) ResolveUserDeselection(
            string jobName,
            ActivationJobSelection? jobSelection)
        {
            if (jobSelection == null)
            {
                return (false, string.Empty);
            }

            const string unchecked_ = "Unchecked in custom job selection.";

            if (IsSameJob(jobName, "ADDPRCT") && (!jobSelection.RunAddPrct || jobSelection.UseSqlPrctInsert))
            {
                return (true, jobSelection.UseSqlPrctInsert && jobSelection.RunAddPrct
                    ? "Executed via direct SQL INSERT into LV.PRCTT0."
                    : unchecked_);
            }

            bool deselected =
                (IsSameJob(jobName, "LVPP06U") && !jobSelection.RunLvpp06u) ||
                (IsSameJob(jobName, "LVPG22U") && !jobSelection.RunLvpg22u) ||
                (IsSameJob(jobName, "LI1J04D0") && !jobSelection.RunLi1j04d0) ||
                (IsSameJob(jobName, "LI1J04D2") && !jobSelection.RunLi1j04d2);

            return deselected ? (true, unchecked_) : (false, string.Empty);
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
            Task preloadTask = isBranch23
                ? PreloadAllJclTemplatesAsync()
                : PreloadJclTemplatesAsync();

            if (skipLogon && IsActiveServerCompatibleWithEnvironment(environment))
            {
                onProgress?.Invoke(
                    $"Mainframe authentication skipped. Using the existing ESCWA session on {MicroFocusApiService.ActiveServer}.");

                await preloadTask.ConfigureAwait(false);
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
