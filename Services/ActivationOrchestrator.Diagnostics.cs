using AutoActivator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class ActivationOrchestrator
    {
        private async Task<string> SafeFetchSpoolDiagnosticsAsync(
            string jobNumber,
            CancellationToken cancellationToken,
            int maxLinesPerDd = 45)
        {
            try
            {
                return await _apiService
                    .GetJobDiagnosticDetailsAsync(jobNumber, maxLinesPerDd, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return "Unable to retrieve Spool output: " + exception.Message;
            }
        }

        private static void ConfigureAlreadyActiveDiagnostic(
            ContractActivationTrace trace,
            JobStepExecutionInfo stepInfo,
            string jobNumber)
        {
            stepInfo.DiagnosticHint =
                "ADDPRCT returned RC=0008. The premium may already be paid or assigned.";

            trace.FailureStage = "JOB_EXECUTION_ADDPRCT_RC0008";
            trace.FailedJobName = stepInfo.JobName;
            trace.FailedJobNum = jobNumber;
            trace.FailedReturnCode = stepInfo.ReturnCode;
            trace.FailureReason =
                "The contract is already active or the premium has already been assigned.";
            trace.RootCauseAnalysis =
                "ADDPRCT returned condition code 0008. The billing premium is already registered for this contract.";
            trace.RecommendedAction =
                "If the contract still requires IMS activation, rerun with Skip Prime to bypass ADDPRCT, LVPP06U and LVPG22U.";
        }

        private static void PopulateFailureDiagnostics(
            ContractActivationTrace trace,
            JobStepExecutionInfo stepInfo,
            bool isSystemCrash)
        {
            string jobName = stepInfo.JobName;
            string returnCode = string.IsNullOrWhiteSpace(stepInfo.ReturnCode)
                ? "Unknown"
                : stepInfo.ReturnCode;

            trace.FailureStage = isSystemCrash
                ? $"MAINFRAME_CRASH_{stepInfo.Status}"
                : $"BUSINESS_RC_ERROR_{jobName}";
            trace.FailedJobName = jobName;
            trace.FailedJobNum = stepInfo.JobNum;
            trace.FailedReturnCode = returnCode;
            trace.FailureReason = isSystemCrash
                ? $"System failure on {jobName} ({stepInfo.JobNum}). Status={stepInfo.Status}, Code={returnCode}."
                : $"Business error on {jobName} ({stepInfo.JobNum}). Return code={returnCode}.";

            if (string.Equals(stepInfo.Status, "JCLError", StringComparison.OrdinalIgnoreCase))
            {
                stepInfo.DiagnosticHint =
                    "JCL error, missing dataset, allocation error or invalid job-card parameter.";
                trace.RootCauseAnalysis =
                    $"{jobName} failed with JCLError before or during allocation. Possible causes include a missing " +
                    "dataset, a locked dataset, an invalid symbolic parameter or an invalid SCHENV.";
                trace.RecommendedAction =
                    "Inspect JESYSMSG and JESMSGLG to identify the DD, DSN or symbolic parameter that caused the error.";
                return;
            }

            if (string.Equals(stepInfo.Status, "Abend", StringComparison.OrdinalIgnoreCase))
            {
                stepInfo.DiagnosticHint = $"Program Abend {returnCode} during {jobName}.";
                trace.RootCauseAnalysis =
                    $"{jobName} terminated abnormally. Possible causes include invalid numeric data, a missing load module, " +
                    "a DB2 failure, an IMS failure or a data inconsistency.";
                trace.RecommendedAction =
                    "Inspect SYSOUT, SYSPRINT and dumps. Verify contract, premium, BUCP, CMDPMT, CUS and environment values.";
                return;
            }

            if (IsSameJob(jobName, "ADDPRCT"))
            {
                stepInfo.DiagnosticHint = $"ADDPRCT rejected the payment injection with RC={returnCode}.";
                trace.RootCauseAnalysis =
                    $"ADDPRCT rejected CMDPMT={trace.Cmdpmt}, BUCP={trace.Bucp}, contract={trace.JclContract} or the supplied premium.";
                trace.RecommendedAction =
                    "Inspect the ADDPRCT Spool. Verify BUCP/CMDPMT mapping, CUS, contract and applicable distribution channel.";
                return;
            }

            if (IsSameJob(jobName, "LVPP06U") || IsSameJob(jobName, "LVPG22U"))
            {
                stepInfo.DiagnosticHint = $"Premium accounting failed in {jobName} with RC={returnCode}.";
                trace.RootCauseAnalysis =
                    $"{jobName} failed after ADDPRCT. The premium, accounting office, payment method or financial state may be inconsistent.";
                trace.RecommendedAction =
                    "Inspect SYSOUT and SYSPRINT. If ADDPRCT already registered the premium, use Skip Prime on retry.";
                return;
            }

            if (IsSameJob(jobName, "LI1J04D0") || IsSameJob(jobName, "LI1J04D2"))
            {
                stepInfo.DiagnosticHint = $"IMS activation failed in {jobName} with RC={returnCode}.";
                trace.RootCauseAnalysis =
                    $"{jobName}, including subjob {stepInfo.SubJobName}, failed in IMS environment IM7{trace.EnvIms}. FASTCTRL={trace.FastCtrlDsn}.";
                trace.RecommendedAction =
                    "Inspect the Spool and IMS messages. Since billing may already have succeeded, retry with Skip Prime after resolving the IMS issue.";
                return;
            }

            if (stepInfo.IsBranch23Step)
            {
                stepInfo.DiagnosticHint =
                    $"BR23 job {jobName}, template {stepInfo.JclName}, failed with RC={returnCode}.";
                trace.RootCauseAnalysis =
                    $"The BR23 phase failed after the standard activation. Fund events, NAV consolidation or FP01 processing was rejected. " +
                    $"Consolidation={trace.Branch23ConsolidationDate:yyyyMMdd}.";
                trace.RecommendedAction =
                    "Inspect the Spool. Verify the generated movement, fund codes, NAV values and BR23 JCL parameters. " +
                    "Do not replay completed billing steps.";
                return;
            }

            stepInfo.DiagnosticHint = $"Job {jobName} failed. Status={stepInfo.Status}, RC={returnCode}.";
            trace.RootCauseAnalysis =
                $"{jobName} did not return an accepted condition code. AutoActivator accepts only RC=0000 and RC=0004.";
            trace.RecommendedAction =
                "Inspect the Mainframe Spool and correct the reported business or technical error.";
        }

        private sealed record PaymentRoutingContext(
            string BusinessChannel,
            string Bucp,
            string PaymentCommand,
            string Diagnostic);

        private static PaymentRoutingContext ResolveAndValidatePaymentRouting(
            IReadOnlyDictionary<string, string> variables,
            ContractActivationTrace trace)
        {
            string bucp = GetRequiredRoutingVariable(variables, "BUCP", trace)
                .PadLeft(5, '0');
            string paymentCommand = GetRequiredRoutingVariable(variables, "CMDPMT", trace)
                .ToUpperInvariant();

            string businessChannel;
            if (bucp == "00382" && paymentCommand == "8")
            {
                businessChannel = "BANK";
            }
            else if (bucp == "00182" && paymentCommand == "8")
            {
                businessChannel = "BANK_P48LV";
            }
            else if (bucp == "80819" && paymentCommand == "H")
            {
                businessChannel = "BROKER_NEW_GEN";
            }
            else if (bucp == "49797" && paymentCommand == "X")
            {
                businessChannel = "BROKER_LEGACY_TARGET";
            }
            else if (bucp == "49797" && paymentCommand == "H")
            {
                businessChannel = "BROKER_LEGACY_INVEST";
            }
            else
            {
                trace.FailureStage = "PAYMENT_ROUTING_NOT_SUPPORTED";
                trace.FailureReason =
                    $"BUCP/CMDPMT {bucp}/{paymentCommand} is not supported.";
                trace.RootCauseAnalysis =
                    "The activation plan did not provide a supported Bank or Broker payment route.";
                trace.RecommendedAction =
                    "Supported routes: Bank (00382/8 or 00182/8), Broker New Gen / 2P (80819/H), " +
                    "or Legacy Broker AGF+ / Target+ (49797/X or 49797/H).";
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
            string key,
            ContractActivationTrace? trace = null)
        {
            string? fallback = string.Equals(key, "BUCP", StringComparison.OrdinalIgnoreCase)
                ? (!string.IsNullOrWhiteSpace(trace?.Bucp)
                    ? trace.Bucp
                    : PreActivationRoutingService.ResolveConfirmedPaymentRouting(trace?.EliaChannelCode).Bucp)
                : string.Equals(key, "CMDPMT", StringComparison.OrdinalIgnoreCase)
                    ? (!string.IsNullOrWhiteSpace(trace?.Cmdpmt)
                        ? trace.Cmdpmt
                        : PreActivationRoutingService.ResolveConfirmedPaymentRouting(trace?.EliaChannelCode).Cmdpmt)
                    : null;

            if ((!variables.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) &&
                !string.IsNullOrWhiteSpace(fallback))
            {
                return fallback.Trim();
            }

            if (!variables.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Required activation variable '{key}' is missing.");
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

        private static bool IsSameJob(string? left, string? right)
        {
            return string.Equals(
                left?.Trim(),
                right?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveServerCompatibleWithEnvironment(string environment)
        {
            string server = MicroFocusApiService.ActiveServer?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(server))
            {
                return false;
            }

            string normalizedEnvironment = NormalizeEnvironment(environment);
            return normalizedEnvironment switch
            {
                "D" => server.StartsWith("sdmfas", StringComparison.Ordinal),
                "Q" => server.StartsWith("sqmfas", StringComparison.Ordinal),
                "A" => server.StartsWith("samfas", StringComparison.Ordinal),
                "P" => server.StartsWith("spmfas", StringComparison.Ordinal),
                _ => false
            };
        }

        private static string NormalizeEnvironment(string? environment)
        {
            if (string.IsNullOrWhiteSpace(environment))
            {
                return "Q";
            }

            string normalized = environment.Trim().ToUpperInvariant();
            if (normalized.StartsWith("D", StringComparison.Ordinal)) return "D";
            if (normalized.StartsWith("Q", StringComparison.Ordinal)) return "Q";
            if (normalized.StartsWith("A", StringComparison.Ordinal)) return "A";
            if (normalized.StartsWith("P", StringComparison.Ordinal)) return "P";
            return normalized;
        }
    }
}
