using AutoActivator.Models;
using AutoActivator.Services.Branch23Activation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Support for the Branch23ActivationOnly workflow.
    /// Jobs are submitted one at a time through the existing
    /// ProcessSubmitAndWaitAsync pipeline.
    ///
    /// Payment routing is supplied by the SQL activation plan:
    /// Bank = 00382/8; Broker New Gen = 80819/H.
    /// This partial class does not restrict Branch 23 to Bank.
    /// </summary>
    public sealed partial class ActivationOrchestrator
    {
        private const int GeneratedJobLookupAttempts = 20;

        private static readonly TimeSpan GeneratedJobLookupDelay =
            TimeSpan.FromSeconds(3);

        /// <summary>
        /// Authenticates and preloads the standard Branch 23 templates.
        /// No job is submitted by this method.
        /// </summary>
        public Task PrepareBranch23ActivationSessionAsync(
            string username,
            string password,
            string environment,
            bool skipLogon,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(trace);

            trace.Steps.Clear();
            _branch23ActivationSteps.Clear();
            trace.StartTime = DateTime.Now;
            trace.FinalStatus = "RUNNING";
            trace.IsBranch23 = true;

            onProgress?.Invoke(
                "Preparing Branch 23 session. Payment routing will use the " +
                "SQL-resolved Bank or Broker New Gen variables.");

            return AuthenticateAndPreloadAsync(
                username,
                password,
                NormalizeEnvironment(environment),
                skipLogon,
                isBranch23: true,
                onProgress,
                trace,
                cancellationToken);
        }

        /// <summary>
        /// Creates the production job runner. Generated LI sub-jobs are resolved with
        /// bounded retries. A parent job is never resubmitted when its child cannot be
        /// correlated.
        /// </summary>
        public IBranch23JobRunner CreateBranch23JobRunner(
            ContractActivationTrace trace,
            Action<string>? onProgress)
        {
            return CreateBranch23JobRunner(
                trace,
                onProgress,
                ResolveGeneratedJobWithRetryAsync);
        }

        /// <summary>
        /// Creates a runner with an injected generated-job resolver.
        /// This overload is retained for deterministic unit tests.
        /// </summary>
        public IBranch23JobRunner CreateBranch23JobRunner(
            ContractActivationTrace trace,
            Action<string>? onProgress,
            Func<string, string, CancellationToken, Task<string>>
                resolveGeneratedJobNumberAsync)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(resolveGeneratedJobNumberAsync);

            return new Branch23JobRunner(
                this,
                trace,
                onProgress,
                resolveGeneratedJobNumberAsync);
        }

        /// <summary>
        /// Repeats the read-only ESCWA child lookup because generated LI jobs can
        /// become visible shortly after the parent has already reached RC=0000.
        /// </summary>
        private async Task<string> ResolveGeneratedJobWithRetryAsync(
            string parentJobNumber,
            string expectedChildJobName,
            CancellationToken token)
        {
            string parent = NormalizeBranch23JobNumber(parentJobNumber);
            string expectedChild = (expectedChildJobName ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException(
                    "Cannot resolve a generated job without the parent Job ID.");
            }

            if (string.IsNullOrWhiteSpace(expectedChild) || expectedChild == "-")
            {
                return "-";
            }

            Exception? lastError = null;

            for (int attempt = 1;
                 attempt <= GeneratedJobLookupAttempts;
                 attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    string resolved = await _apiService
                        .ResolveGeneratedJobNumberAsync(
                            parent,
                            expectedChild,
                            token)
                        .ConfigureAwait(false);

                    resolved = (resolved ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(resolved) && resolved != "-")
                    {
                        return resolved;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException exception)
                    when (IsDeterministicGeneratedJobFailure(exception))
                {
                    // Ambiguous or contradictory correlation is not transient.
                    throw;
                }
                catch (Exception exception)
                {
                    // The lookup is read-only. ESCWA may expose the child later.
                    lastError = exception;
                }

                if (attempt < GeneratedJobLookupAttempts)
                {
                    await Task.Delay(GeneratedJobLookupDelay, token)
                        .ConfigureAwait(false);
                }
            }

            string reason = lastError == null
                ? "ESCWA returned no correlated child job."
                : $"Last lookup error: {lastError.Message}";

            double totalSeconds =
                GeneratedJobLookupAttempts * GeneratedJobLookupDelay.TotalSeconds;

            throw new InvalidOperationException(
                $"Parent {parent} does not confirm generation of " +
                $"{expectedChild} after {GeneratedJobLookupAttempts} attempts " +
                $"({totalSeconds:0}s window). {reason} " +
                "The parent was not resubmitted.",
                lastError);
        }

        private static string NormalizeBranch23JobNumber(string? jobNumber)
        {
            string value = (jobNumber ?? string.Empty).Trim();

            if (value.StartsWith("J", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            if (string.IsNullOrWhiteSpace(value) ||
                !Regex.IsMatch(
                    value,
                    @"^[0-9]{5,10}$",
                    RegexOptions.CultureInvariant) ||
                value.TrimStart('0').Length == 0)
            {
                return string.Empty;
            }

            return "J" + value;
        }

        private static bool IsDeterministicGeneratedJobFailure(
            InvalidOperationException exception)
        {
            string message = exception.Message ?? string.Empty;

            return message.IndexOf(
                       "Multiple",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf(
                       "multiple matching",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf(
                       "ambiguous",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class Branch23JobRunner : IBranch23JobRunner
        {
            private readonly ActivationOrchestrator _owner;
            private readonly ContractActivationTrace _trace;
            private readonly Action<string>? _onProgress;
            private readonly Func<string, string, CancellationToken, Task<string>>
                _resolver;

            private int _counter = 1;

            public Branch23JobRunner(
                ActivationOrchestrator owner,
                ContractActivationTrace trace,
                Action<string>? onProgress,
                Func<string, string, CancellationToken, Task<string>> resolver)
            {
                _owner = owner ??
                    throw new ArgumentNullException(nameof(owner));
                _trace = trace ??
                    throw new ArgumentNullException(nameof(trace));
                _onProgress = onProgress;
                _resolver = resolver ??
                    throw new ArgumentNullException(nameof(resolver));
            }

            public async Task<Branch23JobOutcome> RunAsync(
                string jobName,
                string subJobName,
                IReadOnlyDictionary<string, string> variables,
                CancellationToken token)
            {
                token.ThrowIfCancellationRequested();

                string normalizedJobName = (jobName ?? string.Empty).Trim();
                string normalizedSubJobName = string.IsNullOrWhiteSpace(subJobName)
                    ? "-"
                    : subJobName.Trim();

                if (string.IsNullOrWhiteSpace(normalizedJobName))
                {
                    throw new ArgumentException(
                        "A Branch 23 job name is required.",
                        nameof(jobName));
                }

                ArgumentNullException.ThrowIfNull(variables);

                var normalizedVariables = new Dictionary<string, string>(
                    variables,
                    StringComparer.OrdinalIgnoreCase);

                ValidatePaymentRouting(normalizedVariables);
                RecordPaymentRouting(normalizedVariables);

                string jclTemplateName = ResolveBranch23JclTemplateName(normalizedJobName);

                var step = new JobStepExecutionInfo
                {
                    StepOrder = _trace.Steps.Count + 1,
                    JobName = normalizedJobName,
                    JclName = jclTemplateName,
                    SubJobName = normalizedSubJobName,
                    PhaseDescription =
                        "Branch23: " + normalizedJobName +
                        (string.Equals(jclTemplateName, normalizedJobName, StringComparison.OrdinalIgnoreCase)
                            ? string.Empty
                            : $" ({jclTemplateName})") +
                        (normalizedSubJobName == "-"
                            ? string.Empty
                            : " -> " + normalizedSubJobName),
                    Status = "Pending",
                    IsBranch23Step = true
                };

                _trace.Steps.Add(step);
                _owner._branch23ActivationSteps.Add(step);

                await _owner.ProcessSubmitAndWaitAsync(
                        step,
                        normalizedVariables,
                        _counter++,
                        _onProgress,
                        _trace,
                        token,
                        _resolver)
                    .ConfigureAwait(false);

                return new Branch23JobOutcome
                {
                    ReturnCode = NormalizeReturnCode(step.ReturnCode),
                    JobNumber = string.IsNullOrWhiteSpace(step.JobNum)
                        ? "-"
                        : step.JobNum.Trim(),
                    Spool = step.SpoolExcerpt ?? string.Empty,
                    SubJobNumber = string.IsNullOrWhiteSpace(step.SubJobNum)
                        ? "-"
                        : step.SubJobNum.Trim(),
                    SubJobReturnCode = string.IsNullOrWhiteSpace(
                            step.SubJobReturnCode)
                        ? "-"
                        : NormalizeReturnCode(step.SubJobReturnCode)
                            .ToString("0000", CultureInfo.InvariantCulture)
                };
            }

            private static string ResolveBranch23JclTemplateName(string jobName)
            {
                return jobName.Trim().ToUpperInvariant() switch
                {
                    "LVDQFG03" => Config.Settings.Branch23.FundEvents.JclName,
                    "LVDQFG01" => Config.Settings.Branch23.Consolidation.JclName,
                    "LVD1PE01" => Config.Settings.Branch23.Fp01.JclName,
                    _ => jobName.Trim()
                };
            }

            private void ValidatePaymentRouting(
                IReadOnlyDictionary<string, string> variables)
            {
                string bucp = GetVariable(variables, "BUCP").PadLeft(5, '0');
                string command = GetVariable(variables, "CMDPMT")
                    .ToUpperInvariant();

                if (!Config.Settings.ActivationAutomation.IsSupportedManualPair(bucp, command))
                {
                    throw new InvalidOperationException(
                        "Unsupported Branch 23 payment routing. " +
                        $"BUCP/CMDPMT={bucp}/{command}. " +
                        "Supported routes are BANK 00382/8 (or 00182/8), " +
                        "BROKER_NEW_GEN 80819/H and BROKER_LEGACY 49797/X or 49797/H.");
                }
            }

            private void RecordPaymentRouting(
                IReadOnlyDictionary<string, string> variables)
            {
                string bucp = GetVariable(variables, "BUCP").PadLeft(5, '0');
                string command = GetVariable(variables, "CMDPMT")
                    .ToUpperInvariant();
                string businessChannel = (bucp == "00382" || bucp == "00182")
                    ? "BANK"
                    : bucp == "49797"
                        ? "BROKER_LEGACY"
                        : "BROKER_NEW_GEN";

                _trace.Bucp = bucp;
                _trace.Cmdpmt = command;

                string diagnostic =
                    $"BR23 payment route={businessChannel} | " +
                    $"BUCP={bucp} | CMDPMT={command}";

                _trace.RoutingDiagnostic = string.IsNullOrWhiteSpace(
                        _trace.RoutingDiagnostic)
                    ? diagnostic
                    : _trace.RoutingDiagnostic + " | " + diagnostic;

                _onProgress?.Invoke(diagnostic + ".");
            }

            private static string GetVariable(
                IReadOnlyDictionary<string, string> variables,
                string key)
            {
                if (!variables.TryGetValue(key, out string? value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException(
                        $"Required Branch 23 variable '{key}' is missing.");
                }

                return value.Trim();
            }
        }
    }
}