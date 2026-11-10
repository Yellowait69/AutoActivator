using AutoActivator.Models;
using AutoActivator.Services.Branch23Activation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class ActivationOrchestrator
    {
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

            string jobName = stepInfo.JobName;
            if (string.IsNullOrWhiteSpace(jobName))
            {
                throw new InvalidOperationException("The JCL step has no job name.");
            }

            stepInfo.StartTime = DateTime.Now;
            stepInfo.ServerNode = MicroFocusApiService.ActiveServer ?? "-";
            stepInfo.ServerRegion = MicroFocusApiService.ActiveRegion ?? string.Empty;
            stepInfo.ServerNodeUrl = MicroFocusApiService.ActiveNodeUrl ?? string.Empty;
            var stopwatch = Stopwatch.StartNew();

            var variables = new Dictionary<string, string>(
                sourceVariables,
                StringComparer.OrdinalIgnoreCase)
            {
                ["JOBNAM"] = jobName
            };

            SetApplicationVariable(stepInfo, variables);

            stepInfo.SubmittedVariables.Clear();
            foreach (var kvp in variables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool isSecret = kvp.Key.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0;
                stepInfo.SubmittedVariables[kvp.Key] = isSecret ? "********" : (kvp.Value ?? string.Empty);
            }

            string templateName = string.IsNullOrWhiteSpace(stepInfo.JclName)
                ? jobName
                : stepInfo.JclName;

            onProgress?.Invoke($"[{count}] Preparing JCL {templateName} for job {jobName}.");

            string readyContent;

            try
            {
                if (_branch23ActivationSteps.Contains(stepInfo))
                {
                    IReadOnlyList<string> templateVariables =
                        await _jclProcessor
                            .GetMissingVariablesAsync(templateName, new Dictionary<string, string>())
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
                            $"Unresolved, empty or '?' JCL variable(s) for {templateName}: " +
                            string.Join(", ", invalid.Select(name => "%%" + name)));
                    }
                }
                else if (stepInfo.IsBranch23Step)
                {
                    IReadOnlyList<string> missingVariables =
                        await _jclProcessor
                            .GetMissingVariablesAsync(templateName, variables)
                            .ConfigureAwait(false);

                    if (missingVariables.Count > 0)
                    {
                        string missing = string.Join(", ", missingVariables.Select(variable => "%%" + variable));
                        throw new InvalidOperationException(
                            $"Missing JCL variable(s) for {templateName}: {missing}");
                    }
                }

                readyContent = await _jclProcessor
                    .GetPreparedJclAsync(templateName, variables, count)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                stepInfo.EndTime = DateTime.Now;
                stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
                stepInfo.Status = "JCLPrepFailed";
                stepInfo.ErrorMessage = exception.Message;
                stepInfo.DiagnosticHint = "Unable to load or prepare the JCL template.";

                trace.FailureStage = "JCL_PREPARATION";
                trace.FailedJobName = jobName;
                trace.FailureReason = $"Failed to prepare JCL template {templateName}: {exception.Message}";
                trace.RootCauseAnalysis = "The JCL template is missing, inaccessible or contains unresolved symbolic variables.";
                trace.RecommendedAction = "Verify VPN and network-share access, template existence and all required symbolic variables.";
                throw;
            }

            readyContent = ApplyJobSpecificTransformations(
                jobName,
                readyContent,
                variables,
                _branch23ActivationSteps.Contains(stepInfo) &&
                variables.TryGetValue("CLASS", out string? jobClass) &&
                !string.IsNullOrWhiteSpace(jobClass)
                    ? jobClass.Trim()
                    : "A");

            stepInfo.PreparedJclHeader = string.Join(
                Environment.NewLine,
                readyContent
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .Select(line => line.TrimEnd())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Take(10));

            onProgress?.Invoke($"[{count}] Submitting {jobName}.");

            var submission = await _apiService
                .SubmitJobAsync(readyContent, cancellationToken)
                .ConfigureAwait(false);

            if (!submission.Success)
            {
                stopwatch.Stop();
                stepInfo.EndTime = DateTime.Now;
                stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
                stepInfo.Status = "SubmitFailed";
                stepInfo.ErrorMessage = submission.Error ?? "Unknown submission error.";
                stepInfo.DiagnosticHint = "JES rejected the submission or the ESCWA session is no longer valid.";

                trace.FailureStage = "JOB_SUBMISSION";
                trace.FailedJobName = jobName;
                trace.FailureReason = $"Failed to submit {jobName}. Details: {submission.Error}";
                trace.RootCauseAnalysis = "The Micro Focus JES endpoint rejected the JCL payload, the session expired or the target batch region is unavailable.";
                trace.RecommendedAction = "Inspect the JES submission message and verify the ESCWA session, region and JCL job card.";
                throw new InvalidOperationException($"Failed to submit job {jobName}. Error: {submission.Error}");
            }

            string jobNumber = submission.JobNum;
            stepInfo.JobNum = jobNumber;
            stepInfo.Status = "Submitted";
            stepInfo.SubmissionServerMessage = MicroFocusApiService.GetSubmissionMessage(jobNumber);

            onProgress?.Invoke($"Job {jobName} ({jobNumber}) running on {stepInfo.ServerNode}.");

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
                    throw new InvalidOperationException("Generated job resolver is not configured.");

                string childNumber;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    try
                    {
                        childNumber = (await resolver(
                            parentJobNumber, parentStep.SubJobName, timeout.Token)
                            .ConfigureAwait(false))?.Trim() ?? string.Empty;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Timed out resolving {parentStep.SubJobName} generated by parent {parentJobNumber}.");
                    }
                }

                string normalizedChildNumber = NormalizeGeneratedJobNumber(childNumber);
                string normalizedParentNumber = NormalizeGeneratedJobNumber(parentJobNumber);

                if (string.IsNullOrWhiteSpace(normalizedChildNumber) ||
                    string.Equals(normalizedChildNumber, normalizedParentNumber, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"No valid correlated job number for {parentStep.SubJobName} generated by {parentJobNumber}. Resolver returned '{childNumber}'.");
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
                    $"Following generated job {child.JobName} ({childNumber}), parent {parentJobNumber}.");

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
                    if (!string.IsNullOrWhiteSpace(child.ServerJobMetadata))
                    {
                        parentStep.SubJobServerMetadata = child.ServerJobMetadata;
                    }
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
                variables["AP"] = "LV";
                return;
            }

            if (IsSameJob(step.JobName, "LI1J04D0") ||
                IsSameJob(step.JobName, "LI1J04D2"))
            {
                variables["AP"] = "LI";
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
                const string marker = "//ICEGENER IF";
                int markerIndex = readyContent.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    readyContent = readyContent.Substring(0, markerIndex);
                }
            }

            if (!IsSameJob(jobName, "LI1J04D0") &&
                !IsSameJob(jobName, "LI1J04D2"))
            {
                return readyContent;
            }

            string subJobName = IsSameJob(jobName, "LI1J04D0")
                ? "LI1J04D1"
                : "LI1J04D3";

            readyContent = readyContent.Replace("$JOBNAME", subJobName, StringComparison.Ordinal);

            string environmentLetter = variables.TryGetValue("ENVIMS", out string? envIms)
                ? envIms
                : "T";

            string notifyUser = variables.TryGetValue("USERNAME", out string? username)
                ? username
                : "XA3894";

            const string dataMarker = "DLM=##";
            int dataIndex = readyContent.IndexOf(dataMarker, StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                return readyContent;
            }

            int endOfLineIndex = readyContent.IndexOf('\n', dataIndex);
            if (endOfLineIndex < 0)
            {
                return readyContent;
            }

            string jobCard =
                $"//{subJobName} JOB CLASS={subJobClass}," +
                $"SCHENV=IM7{environmentLetter}," +
                $"NOTIFY={notifyUser}\r\n";

            return readyContent.Insert(endOfLineIndex + 1, jobCard);
        }

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

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int delay = GetPollingDelay(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                stepInfo.PollCount = attempt + 1;

                var statusResult = await _apiService
                    .CheckJobStatusAsync(jobNumber, cancellationToken)
                    .ConfigureAwait(false);

                stepInfo.Status = statusResult.Status ?? "Unknown";
                stepInfo.ReturnCode = statusResult.ReturnCode ?? string.Empty;
                stepInfo.ServerJobMetadata = MicroFocusApiService.GetServerJobMetadata(jobNumber);

                if (IsFailureStatus(stepInfo.Status))
                {
                    await HandleSystemFailureAsync(
                            stepInfo,
                            jobNumber,
                            stopwatch,
                            trace,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stepInfo.ServerJobMetadata = MicroFocusApiService.GetServerJobMetadata(jobNumber);

                    throw new InvalidOperationException(
                        $"System failure for job {stepInfo.JobName} ({jobNumber}). " +
                        $"Status={stepInfo.Status}, Code={stepInfo.ReturnCode}.");
                }

                if (!IsCompletionStatus(stepInfo.Status))
                {
                    continue;
                }

                int returnCode = NormalizeReturnCode(stepInfo.ReturnCode);
                if (returnCode == int.MaxValue)
                {
                    unknownReturnCodeCount++;
                    stepInfo.Status = "AwaitingReturnCode";
                    stepInfo.DiagnosticHint = "Job ended, but its numeric condition code is not confirmed.";
                    if (unknownReturnCodeCount < 8)
                    {
                        onProgress?.Invoke(
                            $"Job {stepInfo.JobName} ({jobNumber}): waiting for a confirmed return code.");
                        continue;
                    }

                    stopwatch.Stop();
                    stepInfo.EndTime = DateTime.Now;
                    stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
                    stepInfo.Status = "ReturnCodeUnknown";
                    stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(
                        jobNumber, cancellationToken).ConfigureAwait(false);
                    stepInfo.ServerJobMetadata = MicroFocusApiService.GetServerJobMetadata(jobNumber);
                    trace.FailureStage = "JOB_RETURN_CODE_UNKNOWN";
                    trace.FailedJobName = stepInfo.JobName;
                    trace.FailedJobNum = jobNumber;
                    trace.FailedReturnCode = stepInfo.ReturnCode;
                    trace.FailureReason = $"Cannot confirm the return code of {stepInfo.JobName} ({jobNumber}).";
                    trace.RootCauseAnalysis =
                        "A terminal JES status was returned without a recognized numeric RC. " +
                        "The workflow has stopped without resubmitting the job.";
                    trace.RecommendedAction =
                        "Inspect the existing job in ESCWA and correct RC extraction. " +
                        "Do not replay billing automatically.";
                    throw new InvalidOperationException(trace.FailureReason);
                }

                stopwatch.Stop();
                stepInfo.EndTime = DateTime.Now;
                stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;

                if (returnCode != 0 && returnCode != 4)
                {
                    stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(
                            jobNumber,
                            cancellationToken)
                        .ConfigureAwait(false);
                    stepInfo.ServerJobMetadata = MicroFocusApiService.GetServerJobMetadata(jobNumber);

                    if (IsSameJob(stepInfo.JobName, "ADDPRCT") &&
                        returnCode == 8 &&
                        _branch23ActivationSteps.Contains(stepInfo))
                    {
                        stepInfo.Status = "CompleteRc8";
                        stepInfo.DiagnosticHint =
                            "ADDPRCT RC=0008: DB2TIAD diagnostic kept; PRCTT0 is checked before any conclusion.";
                        onProgress?.Invoke($"Job {stepInfo.JobName} ({jobNumber}) {stepInfo.DiagnosticHint}");
                        return;
                    }

                    if (IsSameJob(stepInfo.JobName, "ADDPRCT") && returnCode == 8)
                    {
                        ConfigureAlreadyActiveDiagnostic(trace, stepInfo, jobNumber);
                        throw new InvalidOperationException("ALREADY_ACTIVE");
                    }

                    PopulateFailureDiagnostics(trace, stepInfo, isSystemCrash: false);

                    throw new InvalidOperationException(
                        $"Business error on job {stepInfo.JobName} ({jobNumber}). Return code={stepInfo.ReturnCode}.");
                }

                if (string.IsNullOrWhiteSpace(stepInfo.SpoolExcerpt))
                {
                    stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(
                            jobNumber,
                            cancellationToken,
                            maxLinesPerDd: 25)
                        .ConfigureAwait(false);
                    stepInfo.ServerJobMetadata = MicroFocusApiService.GetServerJobMetadata(jobNumber);
                }

                stepInfo.Status = "Complete";
                stepInfo.DiagnosticHint = returnCode == 4
                    ? "Completed with warning RC=0004."
                    : "Completed normally RC=0000.";

                onProgress?.Invoke($"Job {stepInfo.JobName} ({jobNumber}) {stepInfo.DiagnosticHint}");
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
                $"Job {jobNumber} ({stepInfo.JobName}) exceeded the maximum monitoring duration.");
        }

        private static int GetPollingDelay(int attempt)
        {
            if (attempt == 0) return 200;
            if (attempt < 6) return 250;
            if (attempt < 20) return 500;
            if (attempt < 50) return 1000;
            return 1500;
        }

        private static bool IsFailureStatus(string? status)
        {
            return string.Equals(status, "JCLError", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Abend", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCompletionStatus(string? status)
        {
            return string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Output", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Done", StringComparison.OrdinalIgnoreCase);
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
            stepInfo.EndTime = DateTime.Now;
            stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
            stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(jobNumber, cancellationToken)
                .ConfigureAwait(false);

            PopulateFailureDiagnostics(trace, stepInfo, isSystemCrash: true);
        }

        private async Task HandleTimeoutAsync(
            JobStepExecutionInfo stepInfo,
            string jobNumber,
            Stopwatch stopwatch,
            ContractActivationTrace trace,
            CancellationToken cancellationToken)
        {
            stopwatch.Stop();
            stepInfo.EndTime = DateTime.Now;
            stepInfo.DurationMs = stopwatch.ElapsedMilliseconds;
            stepInfo.Status = "Timeout";
            stepInfo.SpoolExcerpt = await SafeFetchSpoolDiagnosticsAsync(jobNumber, cancellationToken)
                .ConfigureAwait(false);

            trace.FailureStage = "JOB_TIMEOUT";
            trace.FailedJobName = stepInfo.JobName;
            trace.FailedJobNum = jobNumber;
            trace.FailedReturnCode = stepInfo.ReturnCode;
            trace.FailureReason =
                $"Job {stepInfo.JobName} ({jobNumber}) timed out after {stopwatch.Elapsed.TotalSeconds:F1} seconds. " +
                $"Last status={stepInfo.Status}.";
            trace.RootCauseAnalysis =
                "The job remained queued or active beyond the monitoring threshold. Possible causes include " +
                "initiator contention, dataset locking, DB2 locking or an unavailable IMS region.";
            trace.RecommendedAction =
                $"Inspect job {jobNumber} in ESCWA on server {stepInfo.ServerNode}. Check initiator class, " +
                "dataset locks, DB2 locks and IMS availability.";
        }
    }
}
