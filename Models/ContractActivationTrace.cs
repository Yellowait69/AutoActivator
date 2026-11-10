using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoActivator.Models
{
    /// <summary>
    /// Trace complète de la résolution et de l'activation d'un contrat
    /// (Branche 21 ou Branche 23).
    /// </summary>
    public sealed class ContractActivationTrace
    {
        public int RowNum { get; set; } = 1;

        public string RawInput { get; set; } = string.Empty;

        public bool IsDemandId { get; set; }

        public string ResolvedContract { get; set; } = "Not resolved";

        public string JclContract { get; set; } = "N/A";

        public string EliaUconId { get; set; } = "N/A";

        public string DemandId { get; set; } = "N/A";

        public string LisaInternalId { get; set; } = "N/A";

        public string LisaContractNumber { get; set; } = "N/A";

        public string ProductCode { get; set; } = string.Empty;

        public string ProcessCode { get; set; } = string.Empty;

        public string EliaChannelCode { get; set; } = string.Empty;

        public string ApplicationChannelCode { get; set; } = string.Empty;

        public string DemandStatus { get; set; } = string.Empty;

        public string PaymentMethodCode { get; set; } = string.Empty;

        public string PremiumGroupType { get; set; } = string.Empty;

        public int FundFeatureCount { get; set; }

        public ContractBranchClassification BranchClassification { get; set; } =
            ContractBranchClassification.Unknown;

        public BranchDetectionSource BranchDetectionSource { get; set; } =
            BranchDetectionSource.Unknown;

        public string BranchDetectionReason { get; set; } = string.Empty;

        public PreActivationRoutingStatus RoutingStatus { get; set; } =
            PreActivationRoutingStatus.Unknown;

        public string RoutingDiagnostic { get; set; } = string.Empty;

        public ActivationPaymentChannel PaymentChannel { get; set; } =
            ActivationPaymentChannel.Unknown;

        public string BusinessChannel { get; set; } = string.Empty;

        public string RoutingRule { get; set; } = string.Empty;

        public string RoutingSource { get; set; } = string.Empty;

        public string RunDate { get; set; } = string.Empty;

        public string RunDateSource { get; set; } = string.Empty;

        public string PremiumSequence { get; set; } = "N/A";

        public string RawPremium { get; set; } = "0";

        public string NetPremium { get; set; } = "0";

        public string FormattedAmount { get; set; } = "0000000.00";

        public string DbDiagnosticStatus { get; set; } = "N/A";

        public long DbLookupDurationMs { get; set; }

        public string DbError { get; set; } = string.Empty;

        public string Environment { get; set; } = "Q";

        public string EnvIms { get; set; } = "C";

        public string Q2Region { get; set; } = "Q2C";

        public string FastCtrlDsn { get; set; } = string.Empty;

        public string ServerNode { get; set; } = "N/A";

        public string Channel { get; set; } = "C01";

        public string Cus { get; set; } = "MDEEM";

        public string Bucp { get; set; } = string.Empty;

        public string Cmdpmt { get; set; } = string.Empty;

        public bool SkipPrime { get; set; }

        public string Username { get; set; } = string.Empty;

        public bool IsBranch23 { get; set; }

        public string BranchDisplay =>
            IsBranch23 ||
            BranchClassification == ContractBranchClassification.Branch23 ||
            BranchClassification == ContractBranchClassification.CompositeWithFunds ||
            FundFeatureCount > 0
                ? "Branche 23"
                : "Branche 21";

        public string Branch23Source { get; set; } = "N/A";

        public DateTime? Branch23ConsolidationDate { get; set; }

        public List<string> Branch23Funds { get; set; } = new List<string>();

        public List<string> Branch23NavSummary { get; set; } = new List<string>();

        public string Branch23FundsSource { get; set; } = "N/A";

        public string Branch23ActivationReport { get; set; } = string.Empty;

        public long? Branch23LastMovementBefore { get; set; }

        public long? Branch23MovementNumber { get; set; }

        public string Branch23MovementState { get; set; } = "N/A";

        public List<JobStepExecutionInfo> Steps { get; set; } = new List<JobStepExecutionInfo>();

        public string FinalStatus { get; set; } = "PENDING";

        public string FailureStage { get; set; } = "NONE";

        public string FailedJobName { get; set; } = "-";

        public string FailedJobNum { get; set; } = "-";

        public string FailedReturnCode { get; set; } = "-";

        public string FailureReason { get; set; } = string.Empty;

        public string RootCauseAnalysis { get; set; } = string.Empty;

        public string RecommendedAction { get; set; } = string.Empty;

        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime EndTime { get; set; }

        public long TotalDurationMs { get; set; }

        public bool IsCompleted =>
            string.Equals(FinalStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FinalStatus, "ALREADY_ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FinalStatus, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FinalStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(FinalStatus, "PENDING_ASYNC_LISA_CREATION", StringComparison.OrdinalIgnoreCase);

        public bool IsSuccessful =>
            string.Equals(FinalStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);

        public bool IsAlreadyActive =>
            string.Equals(FinalStatus, "ALREADY_ACTIVE", StringComparison.OrdinalIgnoreCase);

        public bool IsCancelled =>
            string.Equals(FinalStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase);

        public bool IsFailed =>
            string.Equals(FinalStatus, "FAILED", StringComparison.OrdinalIgnoreCase);

        public int SuccessfulStepsCount =>
            Steps?.Count(step => step.IsSuccess) ?? 0;

        public int SkippedStepsCount =>
            Steps?.Count(step => step.IsSkipped) ?? 0;

        public int FailedStepsCount =>
            Steps?.Count(step => step.IsFailed) ?? 0;

        public int ExecutableStepsCount =>
            Steps?.Count(step => !step.IsSkipped) ?? 0;

        public string ElapsedDisplay =>
            TotalDurationMs > 0 ? $"{TotalDurationMs / 1000.0:F2}s" : "-";

        public string StepsSummary
        {
            get
            {
                if (Steps == null || Steps.Count == 0)
                {
                    return "No JCL executed";
                }

                int successful = Steps.Count(step => step.IsSuccess);
                int executable = Steps.Count(step => !step.IsSkipped);
                int skipped = Steps.Count(step => step.IsSkipped);

                JobStepExecutionInfo? lastStep = Steps
                    .Where(step => !step.IsPending && !step.IsSkipped)
                    .OrderByDescending(step => step.StepOrder)
                    .FirstOrDefault();

                string lastInformation = lastStep != null
                    ? $"{lastStep.JobName} ({lastStep.JobNum}: RC={lastStep.ReturnCode}, Status={lastStep.Status})"
                    : "None";

                return $"{successful}/{executable} OK, {skipped} skipped, last: {lastInformation}";
            }
        }

        public string ToDiagnosticSummary()
        {
            return
                $"Row={RowNum}, " +
                $"Input={Display(RawInput)}, " +
                $"Environment={Display(Environment)}, " +
                $"Demand={Display(DemandId)}, " +
                $"UCON={Display(EliaUconId)}, " +
                $"Contract={Display(ResolvedContract)}, " +
                $"JCL={Display(JclContract)}, " +
                $"LISA={Display(LisaInternalId)}, " +
                $"Product={Display(ProductCode)}, " +
                $"Process={Display(Channel)}, " +
                $"EliaChannel={Display(EliaChannelCode)}, " +
                $"BusinessChannel={Display(BusinessChannel)}, " +
                $"BUCP/CMDPMT={Display(Bucp)}/{Display(Cmdpmt)}, " +
                $"RoutingRule={Display(RoutingRule)}, " +
                $"RunDate={Display(RunDate)}, " +
                $"RunDateSource={Display(RunDateSource)}, " +
                $"Premium={Display(RawPremium)}, " +
                $"Branch={BranchDisplay} ({BranchClassification}), " +
                $"BR23={IsBranch23}, " +
                $"Status={FinalStatus}, " +
                $"FailureStage={FailureStage}, " +
                $"Duration={ElapsedDisplay}";
        }

        public override string ToString() => ToDiagnosticSummary();

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }
}
