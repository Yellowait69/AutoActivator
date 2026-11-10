using AutoActivator.Config;
using AutoActivator.Models;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AutoActivator.Services
{
    public sealed partial class ActivationDataService
    {
        public string FormatSingleActivationReport(ContractActivationTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            var builder = new StringBuilder(8192);

            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                "                     AUTO-ACTIVATOR - DETAILED SINGLE ACTIVATION REPORT");
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine(
                $"Generated At       : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine(
                $"Execution Window   : {trace.StartTime:HH:mm:ss.fff} -> {trace.EndTime:HH:mm:ss.fff} " +
                $"(Total: {trace.TotalDurationMs / 1000.0:F2}s / {trace.TotalDurationMs} ms)");
            builder.AppendLine(
                $"Final Status       : {GetStatusBadgeText(trace.FinalStatus)}");
            builder.AppendLine(
                $"Branch Detected    : {trace.BranchDisplay} ({trace.BranchClassification})");
            builder.AppendLine(
                "=========================================================================================================");
            builder.AppendLine();

            AppendContractTraceDetails(builder, trace, includeHeaderBanner: false);

            return builder.ToString();
        }

        public static void AppendContractTraceDetails(
            StringBuilder builder,
            ContractActivationTrace trace,
            bool includeHeaderBanner)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            if (includeHeaderBanner)
            {
                builder.AppendLine(
                    "---------------------------------------------------------------------------------------------------------");
                builder.AppendLine(
                    $"EXCEL ROW #{trace.RowNum,-4} | " +
                    $"INPUT: {trace.RawInput,-16} | " +
                    $"CONTRACT: {trace.ResolvedContract,-15} | " +
                    $"BRANCH: {trace.BranchDisplay,-10} | " +
                    $"STATUS: {GetStatusBadgeText(trace.FinalStatus)}");
                builder.AppendLine(
                    "---------------------------------------------------------------------------------------------------------");
            }

            string environmentSuffix = Settings.NormalizeEnvironmentSuffix(trace.Environment);
            string sqlServer = Settings.DbConfig.GetServerName(environmentSuffix);

            bool branch23ActivationOnly =
                trace.IsBranch23 &&
                (string.Equals(
                     trace.Branch23FundsSource,
                     "Not used (Branch23ActivationOnly)",
                     StringComparison.OrdinalIgnoreCase) ||
                 (trace.Branch23MovementState?.IndexOf(
                      "Branch23ActivationOnly",
                      StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

            string applicationDatabase = branch23ActivationOnly
                ? Settings.DbConfig.GetLisaContractDatabase(environmentSuffix)
                : Settings.DbConfig.GetApplicationDatabase(environmentSuffix);

            string paymentDatabase = Settings.DbConfig.GetLisaPaymentDatabase(environmentSuffix);
            string lifeDatabase = Settings.DbConfig.GetLifeDatabase(environmentSuffix);

            string resolvedBusinessChannel = !string.IsNullOrWhiteSpace(trace.BusinessChannel) &&
                                             !string.Equals(trace.BusinessChannel, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                ? trace.BusinessChannel
                : ResolveTraceBusinessChannel(trace.EliaChannelCode);
            string fundsSynthesis = trace.Branch23Funds != null && trace.Branch23Funds.Count > 0
                ? $"{string.Join(", ", trace.Branch23Funds)} ({DisplayValue(trace.Branch23FundsSource)})"
                : trace.FundFeatureCount > 0
                    ? $"Branche 23 ({trace.FundFeatureCount} feature(s) fonds détecté(s))"
                    : "Aucun (Branche 21 Standard)";
            string consolidationDisplay = trace.Branch23ConsolidationDate.HasValue
                ? trace.Branch23ConsolidationDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : DateTime.TryParseExact(trace.RunDate, new[] { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedRunDate)
                    ? parsedRunDate.AddDays(2).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                    : "-";
            string runDateFormatted = DateTime.TryParseExact(trace.RunDate, new[] { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedRun)
                ? parsedRun.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : DisplayValue(trace.RunDate);

            builder.AppendLine("[0. SYNTHÈSE AUTOCOMPLETE SQL & ROUTAGE MÉTIER]");
            builder.AppendLine(
                $"  • Contrat (3-7-2)      : {trace.ResolvedContract}  |  JCL (9 chiffres / NO_CNT) : {trace.JclContract}");
            builder.AppendLine(
                $"  • Demand ID / ELIA ID  : {trace.DemandId}  /  UCON {trace.EliaUconId} (Statut Demand : {DisplayValue(trace.DemandStatus)})");
            builder.AppendLine(
                $"  • État LISA (NO_CNT)   : {trace.LisaInternalId}");
            builder.AppendLine(
                $"  • Produit / Process    : {DisplayValue(trace.ProductCode)}  /  {DisplayValue(!string.IsNullOrWhiteSpace(trace.ProcessCode) ? trace.ProcessCode : trace.Channel)}  (ChannelCode ELIA : {DisplayValue(trace.EliaChannelCode)})");
            builder.AppendLine(
                $"  • Canal & Paiement     : {resolvedBusinessChannel}  ->  BUCP = {DisplayValue(trace.Bucp)} | CMDPMT = {DisplayValue(trace.Cmdpmt)} ({DisplayValue(!string.IsNullOrWhiteSpace(trace.RoutingRule) ? trace.RoutingRule : trace.RoutingDiagnostic)})");
            builder.AppendLine(
                $"  • Prime Brute / JCL    : {trace.RawPremium} € (Nette : {trace.NetPremium} €)  ->  AMOUNT = '{trace.FormattedAmount}'");
            builder.AppendLine(
                $"  • Date N (Run Date)    : {runDateFormatted} ({DisplayValue(trace.RunDateSource)})");
            builder.AppendLine(
                $"  • Date N+2 (Consolid.) : {consolidationDisplay} (LVDQFG01 / VNI LV000GD0.LV.GFCRT0)");
            builder.AppendLine(
                $"  • Branche & Fonds      : {trace.BranchDisplay} ({DisplayValue(!string.IsNullOrWhiteSpace(trace.BranchDetectionReason) ? trace.BranchDetectionReason : trace.Branch23Source)})  |  Fonds : {fundsSynthesis}");
            builder.AppendLine();

            builder.AppendLine("[1. CONTRACT IDENTITY & DATABASE RESOLUTION]");
            builder.AppendLine(
                $"  • Excel Row Number     : #{trace.RowNum}");
            builder.AppendLine(
                $"  • Input Provided       : {trace.RawInput} ({(trace.IsDemandId ? "Demand ID" : "Contract Number")})");
            builder.AppendLine(
                $"  • SQL Target           : Server: {sqlServer} | LISA DB: {applicationDatabase} | LIFE DB: {lifeDatabase}");

            if (branch23ActivationOnly)
            {
                builder.AppendLine("  • Execution Mode       : Branch23ActivationOnly");
                builder.AppendLine($"  • Payment Database     : {paymentDatabase}");
                builder.AppendLine($"  • Contract Database    : {applicationDatabase}");
            }

            builder.AppendLine($"  • DB Diagnostic Status : {trace.DbDiagnosticStatus}");
            builder.AppendLine($"  • Resolved Contract    : {trace.ResolvedContract}");
            builder.AppendLine($"  • LISA Extended        : {trace.LisaContractNumber}");
            builder.AppendLine($"  • Mainframe JCL ID     : {trace.JclContract}");
            builder.AppendLine($"  • ELIA UCON ID         : {trace.EliaUconId}");
            builder.AppendLine($"  • ELIA Demand ID       : {trace.DemandId}");
            builder.AppendLine($"  • ELIA ProductCode     : {DisplayValue(trace.ProductCode)}");
            builder.AppendLine($"  • ELIA ProcessCode     : {DisplayValue(trace.ProcessCode)}");
            builder.AppendLine($"  • ELIA ChannelCode     : {DisplayValue(trace.EliaChannelCode)}");
            builder.AppendLine($"  • Business Channel     : {ResolveTraceBusinessChannel(trace.EliaChannelCode)}");
            builder.AppendLine($"  • Payment Routing Rule : {DisplayValue(trace.RoutingDiagnostic)}");
            builder.AppendLine($"  • ELIA App Channel     : {DisplayValue(trace.ApplicationChannelCode)}");
            builder.AppendLine($"  • ELIA Demand Status   : {DisplayValue(trace.DemandStatus)}");
            builder.AppendLine($"  • Branch Classification: {trace.BranchDisplay} ({trace.BranchClassification})");
            builder.AppendLine($"  • LISA Internal NO_CNT : {trace.LisaInternalId}");
            builder.AppendLine(
                $"  • DB Premiums          : Gross: {trace.RawPremium} | Net: {trace.NetPremium} | " +
                $"Seq: {trace.PremiumSequence} | JCL AMOUNT: '{trace.FormattedAmount}'");
            builder.AppendLine($"  • SQL Lookup Time      : {trace.DbLookupDurationMs} ms");
            builder.AppendLine();

            builder.AppendLine("[2. MAINFRAME ENVIRONMENT & JCL PARAMETERS]");
            builder.AppendLine(
                $"  • Target Environment   : {trace.Environment}000 | IMS: IM7{trace.EnvIms} | DB2: {trace.Q2Region}");
            builder.AppendLine($"  • ESCWA Server Node    : {trace.ServerNode}");
            builder.AppendLine($"  • Process              : {trace.Channel}");
            builder.AppendLine($"  • Skip Prime           : {(trace.SkipPrime ? "YES" : "NO")}");
            builder.AppendLine(
                $"  • Payment Parameters   : CUS='{trace.Cus}' | BUCP='{trace.Bucp}' | CMDPMT='{trace.Cmdpmt}' | User='{trace.Username}'");
            builder.AppendLine(
                $"  • Routing Evidence     : ChannelCode={DisplayValue(trace.EliaChannelCode)} | " +
                $"Canal={ResolveTraceBusinessChannel(trace.EliaChannelCode)} | Rule={DisplayValue(trace.RoutingDiagnostic)}");
            builder.AppendLine($"  • FASTCTRL Dataset     : {trace.FastCtrlDsn}");

            builder.AppendLine(
                trace.IsBranch23
                    ? branch23ActivationOnly
                        ? $"  • Branch               : Branche 23 (YES) | Source: {trace.Branch23Source} | Mode: activation LISA uniquement"
                        : $"  • Branch               : Branche 23 (YES) | Source: {trace.Branch23Source} | Consolidation: {trace.Branch23ConsolidationDate:yyyy-MM-dd}"
                    : "  • Branch               : Branche 21 (Standard workflow only)");

            if (trace.IsBranch23 && !branch23ActivationOnly)
            {
                string funds = trace.Branch23Funds != null && trace.Branch23Funds.Count > 0
                    ? string.Join(", ", trace.Branch23Funds)
                    : "none resolved";

                builder.AppendLine($"  • BR23 Funds           : {funds} | Source: {trace.Branch23FundsSource}");
                builder.AppendLine(
                    $"  • BR23 Movement        : Last before FG03: {trace.Branch23LastMovementBefore?.ToString() ?? "N/A"} | " +
                    $"New: {trace.Branch23MovementNumber?.ToString() ?? "N/A"} | {trace.Branch23MovementState}");

                if (trace.Branch23NavSummary != null)
                {
                    foreach (string nav in trace.Branch23NavSummary)
                    {
                        builder.AppendLine($"      - NAV {nav}");
                    }
                }
            }

            if (branch23ActivationOnly)
            {
                builder.AppendLine("  • BR23 Funds / LVDQ     : NOT USED");
                builder.AppendLine("  • LI Sequence           : LI1J04D0 -> LI1J04D2 -> SCNTT0");
                builder.AppendLine($"  • BR23 Result           : {trace.Branch23MovementState}");
            }

            builder.AppendLine();

            if (!string.Equals(trace.FinalStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("[3. FAILURE DIAGNOSTICS & ROOT-CAUSE ANALYSIS]");
                builder.AppendLine($"  • Failure Stage        : {trace.FailureStage}");

                if (!string.IsNullOrWhiteSpace(trace.FailedJobName) && trace.FailedJobName != "-")
                {
                    builder.AppendLine(
                        $"  • Failed Job           : {trace.FailedJobName} ({trace.FailedJobNum}) | RC: {trace.FailedReturnCode}");
                }

                builder.AppendLine($"  • Failure Reason       : {trace.FailureReason}");

                if (!string.IsNullOrWhiteSpace(trace.RootCauseAnalysis))
                {
                    builder.AppendLine($"  • Root Cause           : {trace.RootCauseAnalysis}");
                }

                if (!string.IsNullOrWhiteSpace(trace.RecommendedAction))
                {
                    builder.AppendLine($"  • Recommended Action   : {trace.RecommendedAction}");
                }

                builder.AppendLine();
            }

            builder.AppendLine("[4. MAINFRAME EXECUTION TIMELINE]");

            if (trace.Steps == null || trace.Steps.Count == 0)
            {
                builder.AppendLine("  No Mainframe job was submitted.");
            }
            else
            {
                builder.AppendLine(
                    "  Step | Job      | JCL      | SubJob           | Job ID   | Status       | RC     | Duration | Polls | Notes");
                builder.AppendLine(
                    "  -----+----------+----------+------------------+----------+--------------+--------+----------+-------+--------------------------------");

                foreach (var step in trace.Steps)
                {
                    string duration = step.DurationMs > 0
                        ? $"{step.DurationMs / 1000.0:F2}s"
                        : "-";
                    string polls = step.PollCount > 0
                        ? step.PollCount.ToString(CultureInfo.InvariantCulture)
                        : "-";
                    string subJobCol = !string.IsNullOrWhiteSpace(step.SubJobNum)
                        ? $"{step.SubJobName}({step.SubJobNum})"
                        : step.SubJobName;
                    string note = !string.IsNullOrWhiteSpace(step.ErrorMessage)
                        ? step.ErrorMessage
                        : step.DiagnosticHint;

                    builder.AppendLine(
                        $"   #{step.StepOrder,-2} | " +
                        $"{step.JobName,-8} | " +
                        $"{step.EffectiveTemplateName,-8} | " +
                        $"{subJobCol,-16} | " +
                        $"{step.JobNum,-8} | " +
                        $"{step.Status,-12} | " +
                        $"{step.ReturnCode,-6} | " +
                        $"{duration,8} | " +
                        $"{polls,5} | " +
                        $"{note}");
                }

                builder.AppendLine();
                builder.AppendLine("[4B. FICHES DÉTAILLÉES DES JOBS EXÉCUTÉS (VARIABLES ENVOYÉES, SERVEUR ESCWA & SPOOL JES2)]");

                foreach (var step in trace.Steps)
                {
                    builder.AppendLine(
                        "  -------------------------------------------------------------------------------------------------------");
                    builder.AppendLine(
                        $"  ▶ STEP #{step.StepOrder} : {step.JobName} (Template JCL: {step.EffectiveTemplateName}) | " +
                        $"JobID: {step.JobNum} | Status: {step.Status} | RC: {step.ReturnCode}");
                    if (!string.IsNullOrWhiteSpace(step.PhaseDescription))
                    {
                        builder.AppendLine($"    • Phase Métier         : {step.PhaseDescription}");
                    }
                    if (step.StartTime.HasValue || step.EndTime.HasValue)
                    {
                        builder.AppendLine(
                            $"    • Fenêtre d'exécution  : {step.StartTime?.ToString("HH:mm:ss.fff") ?? "-"} -> " +
                            $"{step.EndTime?.ToString("HH:mm:ss.fff") ?? "-"} (Durée : {step.DurationDisplay} | Polls : {step.PollCount})");
                    }
                    if (!string.IsNullOrWhiteSpace(step.SubJobName) && step.SubJobName != "-")
                    {
                        builder.AppendLine(
                            $"    • Sous-Job généré      : {step.SubJobName} | SubJob ID : {DisplayValue(step.SubJobNum)} | SubJob RC : {DisplayValue(step.SubJobReturnCode)}");
                    }
                    builder.AppendLine(
                        $"    • Serveur ESCWA        : Node={DisplayValue(step.ServerNode)} | Region={DisplayValue(step.ServerRegion)} | Endpoint={DisplayValue(step.ServerNodeUrl)}");
                    if (!string.IsNullOrWhiteSpace(step.SubmissionServerMessage))
                    {
                        builder.AppendLine($"    • Réponse Soumission   : {step.SubmissionServerMessage}");
                    }
                    if (!string.IsNullOrWhiteSpace(step.ServerJobMetadata))
                    {
                        builder.AppendLine($"    • Métadonnées JobView  : {step.ServerJobMetadata}");
                    }
                    if (!string.IsNullOrWhiteSpace(step.SubJobServerMetadata))
                    {
                        builder.AppendLine($"    • Métadonnées SubJob   : {step.SubJobServerMetadata}");
                    }
                    if (step.SubmittedVariables != null && step.SubmittedVariables.Count > 0)
                    {
                        string varsFormatted = string.Join(
                            ", ",
                            step.SubmittedVariables.Select(kvp => $"{kvp.Key}='{kvp.Value}'"));
                        builder.AppendLine($"    • Variables JCL ({step.SubmittedVariables.Count,2})   : {varsFormatted}");
                    }
                    if (!string.IsNullOrWhiteSpace(step.PreparedJclHeader))
                    {
                        builder.AppendLine("    • En-tête JCL soumis   :");
                        foreach (string jclLine in step.PreparedJclHeader.Replace("\r", string.Empty).Split('\n'))
                        {
                            builder.AppendLine($"        {jclLine}");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(step.SpoolExcerpt))
                    {
                        builder.AppendLine(
                            $"    • Extrait Spool JES2 ({step.JobName} / {step.JobNum}) :");
                        foreach (string line in step.SpoolExcerpt.Replace("\r", string.Empty).Split('\n'))
                        {
                            builder.AppendLine($"      {line}");
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(trace.Branch23ActivationReport))
            {
                builder.AppendLine();
                builder.AppendLine("[5. RAPPORT DÉTAILLÉ WORKFLOW BRANCHE 23 (SQL AVANT/APRÈS, FONDS, VNI & MOUVEMENTS)]");
                foreach (string line in trace.Branch23ActivationReport.Replace("\r", string.Empty).Split('\n'))
                {
                    builder.AppendLine($"  {line}");
                }
            }

            builder.AppendLine();
        }

        private static string GetStatusBadgeText(string status)
        {
            return status switch
            {
                "SUCCESS" => "SUCCESS (ACTIVATED)",
                "ALREADY_ACTIVE" => "ALREADY ACTIVE (ADDPRCT RC=0008)",
                "CANCELLED" => "CANCELLED BY USER",
                _ => "FAILED (NOT ACTIVATED)"
            };
        }

        private static string ResolveTraceBusinessChannel(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank => "BANK",
                PreActivationBusinessChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }
    }
}
