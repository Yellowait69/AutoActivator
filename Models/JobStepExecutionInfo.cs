using System;
using System.Collections.Generic;

namespace AutoActivator.Models
{
    /// <summary>
    /// Représente l'exécution d'une étape Mainframe (JCL / sous-job JES).
    /// </summary>
    public sealed class JobStepExecutionInfo
    {
        public string SubJobNum { get; set; } = string.Empty;

        public string SubJobReturnCode { get; set; } = string.Empty;

        public int StepOrder { get; set; }

        public string JobName { get; set; } = string.Empty;

        public string SubJobName { get; set; } = string.Empty;

        public string JclName { get; set; } = string.Empty;

        public string PhaseDescription { get; set; } = string.Empty;

        public string JobNum { get; set; } = "-";

        public string ServerNode { get; set; } = "-";

        public string ServerRegion { get; set; } = string.Empty;

        public string ServerNodeUrl { get; set; } = string.Empty;

        public string SubmissionServerMessage { get; set; } = string.Empty;

        public string ServerJobMetadata { get; set; } = string.Empty;

        public string SubJobServerMetadata { get; set; } = string.Empty;

        public Dictionary<string, string> SubmittedVariables { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string PreparedJclHeader { get; set; } = string.Empty;

        public string Status { get; set; } = "Pending";

        public string ReturnCode { get; set; } = "-";

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public long DurationMs { get; set; }

        public int PollCount { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;

        public string SpoolExcerpt { get; set; } = string.Empty;

        public string DiagnosticHint { get; set; } = string.Empty;

        public string BusinessChannel { get; set; } = string.Empty;

        public string Bucp { get; set; } = string.Empty;

        public string Cmdpmt { get; set; } = string.Empty;

        public string ChannelCode { get; set; } = string.Empty;

        public string RoutingRule { get; set; } = string.Empty;

        public bool IsBranch23Step { get; set; }

        public bool IsSuccess =>
            string.Equals(Status, "Complete", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "Output", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "Done", StringComparison.OrdinalIgnoreCase);

        public bool IsSkipped =>
            string.Equals(Status, "Skipped", StringComparison.OrdinalIgnoreCase);

        public bool IsPending =>
            string.Equals(Status, "Pending", StringComparison.OrdinalIgnoreCase);

        public bool IsAborted =>
            string.Equals(Status, "Aborted", StringComparison.OrdinalIgnoreCase);

        public bool IsFailed =>
            string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "Abend", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "JCLError", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "SubmitFailed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "JCLPrepFailed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "Timeout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "NavNotReady", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "FundsNotResolved", StringComparison.OrdinalIgnoreCase);

        public string EffectiveTemplateName =>
            string.IsNullOrWhiteSpace(JclName) ? JobName : JclName;

        public string DurationDisplay =>
            DurationMs > 0 ? $"{DurationMs / 1000.0:F2}s" : "-";

        public string ToDiagnosticSummary()
        {
            return
                $"Step={StepOrder}, " +
                $"Job={JobName}, " +
                $"Template={EffectiveTemplateName}, " +
                $"SubJob={SubJobName}, " +
                $"JobId={JobNum}, " +
                $"Status={Status}, " +
                $"RC={ReturnCode}, " +
                $"Channel={BusinessChannel}, " +
                $"BUCP/CMDPMT={Bucp}/{Cmdpmt}, " +
                $"Duration={DurationDisplay}, " +
                $"Polls={PollCount}, " +
                $"BR23={IsBranch23Step}";
        }

        public override string ToString() => ToDiagnosticSummary();
    }
}
