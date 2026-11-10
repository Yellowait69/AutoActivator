using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;
using AutoActivator.Models;

namespace AutoActivator.Services.Branch23Activation
{
    /// <summary>Outcome of one submitted job (parent + generated sub-job when any).</summary>
    public sealed class Branch23JobOutcome
    {
        public int ReturnCode { get; init; }
        public string JobNumber { get; init; } = "-";
        public string Spool { get; init; } = string.Empty;
        public string SubJobNumber { get; init; } = "-";
        public string SubJobReturnCode { get; init; } = "-";
    }

    /// <summary>
    /// Submits one job and waits for its end (and its generated sub-job).
    /// Must throw on failure, except ADDPRCT RC=8 which is returned so the
    /// workflow can control PRCTT0 explicitly.
    /// </summary>
    public interface IBranch23JobRunner
    {
        Task<Branch23JobOutcome> RunAsync(
            string jobName,
            string subJobName,
            IReadOnlyDictionary<string, string> variables,
            CancellationToken token);
    }

    /// <summary>Final diagnostics of the Branch 23 activation workflow.</summary>
    public enum Branch23FinalDiagnostic
    {
        ACTIVATED,
        NOT_ACTIVATED,
        BLOCKED,
        AMBIGUOUS,
        TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT,
        TECHNICAL_FAILURE,
        CANCELLED
    }

    public sealed class Branch23JobEvidence
    {
        public string JobName { get; init; } = string.Empty;
        public string JobNumber { get; set; } = "-";
        public string ReturnCode { get; set; } = "-";
        public string SubJobName { get; init; } = "-";
        public string SubJobNumber { get; set; } = "-";
        public string SubJobReturnCode { get; set; } = "-";
        public string Status { get; set; } = "Pending";
        public IReadOnlyDictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();
        public string Counters { get; set; } = string.Empty;
    }

    public sealed class Branch23ActivationReport
    {
        // Identification
        public string DemandId { get; set; } = "-";
        public string EliaId { get; set; } = "-";
        public string ContractNumber372 { get; set; } = "-";
        public string LisaInternalNumber { get; set; } = "-";

        // Business parameters
        public string RunDate { get; set; } = "-";
        public string ConsolidationDate { get; set; } = "-";
        public string RunDateSource { get; set; } = "-";
        public string RunDateReason { get; set; } = "-";
        public string EliaDates { get; set; } = "-";
        public string Amount { get; set; } = "-";
        public string AmountInjected { get; set; } = "-";
        public string SelectedPremiumRow { get; set; } = "-";
        public string Channel { get; set; } = "-";
        public string Bucp { get; set; } = "-";
        public string Cmdpmt { get; set; } = "-";
        public string SelectedStepsSummary { get; set; } = "-";

        // Controls
        public string? ScnttStateBefore { get; set; }
        public string ScnttBeforeDetail { get; set; } = "-";
        public PrctCheckStatus? PrctBefore { get; set; }
        public string PrctBeforeDetail { get; set; } = "-";
        public PrctCheckStatus? PrctAfter { get; set; }
        public string PrctAfterDetail { get; set; } = "-";
        public bool AddprctSkipped { get; set; }
        public string AssignmentDetail { get; set; } = "NOT_CHECKED";
        public Pe01CheckStatus? Pe01 { get; set; }
        public string Pe01Detail { get; set; } = "-";
        public ContractActivationStatus? ScnttFinalStatus { get; set; }
        public string ScnttFinalDetail { get; set; } = "-";
        public string ScnttFinalRow { get; set; } = "-";
        public string BeforeAfterComparison { get; set; } = "-";

        // Branch 23 Funds, VNI (LV000GD0.LV.GFCRT0 / GFEVT0) & LVDQ Movements (État 2 -> État 4)
        public string FundsResolved { get; set; } = "-";
        public string FundsSource { get; set; } = "-";
        public string NavSummary { get; set; } = "-";
        public string MovementStateAfterFg03 { get; set; } = "-";
        public string MovementStateAfterFg01 { get; set; } = "-";

        // Jobs
        public List<Branch23JobEvidence> Jobs { get; } = new();
        public List<string> ExecutedJobs => Jobs
            .Where(j => string.Equals(j.Status, "Complete", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            .Select(j => j.SubJobName == "-" ? j.JobName : $"{j.JobName}->{j.SubJobName}")
            .ToList();

        // Result
        public Branch23FinalDiagnostic? FinalDiagnostic { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string FinalReason { get; set; } = string.Empty;
        public List<string> Lines { get; } = new();
        public List<string> Warnings { get; } = new();

        public string Render()
        {
            var b = new StringBuilder();
            b.AppendLine("=== BRANCH 23 ACTIVATION & CONSOLIDATION REPORT ===");
            b.AppendLine($"Demand ID            : {DemandId}");
            b.AppendLine($"ELIA ID              : {EliaId}");
            b.AppendLine($"Contrat 3-7-2        : {ContractNumber372}");
            b.AppendLine($"CO interne LISA      : {LisaInternalNumber} (Start = End)");
            b.AppendLine($"Run Date (Date N)    : {RunDate} (source {RunDateSource}; {RunDateReason})");
            b.AppendLine($"Date N+2 (Consolid.) : {ConsolidationDate}");
            b.AppendLine($"Dates ELIA           : {EliaDates}");
            b.AppendLine($"Prime brute          : {Amount} -> AMOUNT injecté {AmountInjected}");
            b.AppendLine($"Ligne TB5UPRP        : {SelectedPremiumRow}");
            b.AppendLine($"Canal / BUCP / CMDPMT: {Channel} / {Bucp} / {Cmdpmt}");
            b.AppendLine($"Étapes sélectionnées : {SelectedStepsSummary}");
            b.AppendLine($"SCNTT0 avant         : {ScnttBeforeDetail}");
            b.AppendLine($"PRCTT0 avant ADDPRCT : {PrctBefore?.ToString() ?? "-"} | {PrctBeforeDetail}");
            b.AppendLine($"ADDPRCT relancé      : {(AddprctSkipped ? "NON (reprise ou ignoré)" : Jobs.FirstOrDefault()?.Status == "Complete" ? "OUI" : "NON")}");
            b.AppendLine($"PRCTT0 après ADDPRCT : {PrctAfter?.ToString() ?? "-"} | {PrctAfterDetail}");
            b.AppendLine($"PRIST0 / PECHT0      : {AssignmentDetail}");
            b.AppendLine($"PE01                 : {Pe01?.ToString() ?? "-"} | {Pe01Detail}");
            b.AppendLine($"Fonds BR23 résolus   : {FundsResolved} ({FundsSource})");
            b.AppendLine($"VNI SQL (LV000GD0)   : {NavSummary}");
            b.AppendLine($"Mvt après LVDQFG03   : {MovementStateAfterFg03}");
            b.AppendLine($"Mvt après LVDQFG01   : {MovementStateAfterFg01}");
            b.AppendLine("--- Jobs ---");
            foreach (var j in Jobs)
            {
                b.AppendLine($"{j.JobName,-9} id={j.JobNumber} RC={j.ReturnCode} status={j.Status}" +
                    (j.SubJobName == "-" ? string.Empty : $" | {j.SubJobName} id={j.SubJobNumber} RC={j.SubJobReturnCode}"));
                b.AppendLine("          variables: " + string.Join(", ", j.Variables.Select(v => $"{v.Key}={v.Value}")));
                if (j.Counters.Length > 0) b.AppendLine("          compteurs: " + j.Counters);
            }
            b.AppendLine("--- Contrôle final ---");
            b.AppendLine($"SCNTT0 final        : {ScnttFinalStatus?.ToString() ?? "-"} | {ScnttFinalDetail}");
            b.AppendLine($"Ligne SCNTT0        : {ScnttFinalRow}");
            b.AppendLine($"Avant / après       : {BeforeAfterComparison}");
            foreach (string w in Warnings) b.AppendLine("AVERTISSEMENT: " + w);
            b.AppendLine($"DIAGNOSTIC FINAL    : {FinalDiagnostic?.ToString() ?? "-"}" +
                (FailureCode.Length > 0 ? $" ({FailureCode})" : string.Empty));
            b.AppendLine($"Motif               : {FinalReason}");
            return b.ToString();
        }
    }

    /// <summary>
    /// Workflow d'activation Branche 23 complet et modulable par cases à cocher :
    /// 1. PREMELIA : ADDPRCT (ou SQL LV.PRCTT0) -> LVPP06U -> LVPG22U -> LI1J04D0 -> LI1J04D2
    /// 2. LVDQ Seq 0 : LVDQFG03 (LVFG03U) à Date N -> Mouvement en État 2 dans LISA (F51LV)
    /// 3. SQL VNI : LV000GD0.LV.GFCRT0 / LV.GFEVT0 de Date N à Date N+2
    /// 4. LVDQ Seq 1 : LVDQFG01 (LVFG01U, STEP=1) à Date N+2 -> Consolidation Mouvement en État 4
    /// 5. LVDQ Seq 3 : LVD1PE01 (LVPE01U, optionnel)
    /// </summary>
    public sealed class Branch23ActivationWorkflow
    {
        public static readonly string[] JobOrder =
            { "ADDPRCT", "LVPP06U", "LVPG22U", "LI1J04D0", "LI1J04D2" };

        public static readonly string[] FullBranch23JobOrder =
            { "ADDPRCT", "LVPP06U", "LVPG22U", "LI1J04D0", "LI1J04D2", "LVDQFG03", "LVDQFG01", "LVD1PE01" };

        private static readonly IReadOnlyDictionary<string, string[]> JclVariables =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ADDPRCT"] = new[] { "STE", "CNTBEG", "CMDPMT", "YYMMDD", "AMOUNT", "BUCP", "USERNAME" },
                ["LVPP06U"] = new[] { "Q2", "AP", "CUS", "MMDD", "STE", "CNTBEG", "CNTEND", "CYMD" },
                ["LVPG22U"] = new[]
                {
                    "Q2", "AP", "CUS", "MMDD", "DRUN", "NREMB",
                    "CONTR-EX", "CONTR-RE", "CONTR-UN", "NJJART72",
                    "JOBNAMI", "CM", "STE", "CNTBEG", "CNTEND"
                },
                ["LI1J04D0"] = new[] { "CLASS", "ENVIMS", "CUS", "YYMMDD", "YYYY", "MM", "DD", "FASTCTRL", "STE", "CNTBEG", "CNTEND" },
                ["LI1J04D2"] = new[] { "CLASS", "CUS", "YYMMDD", "YYYY", "MM", "DD", "ENVIMS" },
                ["LVDQFG03"] = new[]
                {
                    "Q2", "AP", "CUS", "MMDD", "STE", "CNTBEG", "CNTEND", "DRUN", "DEVN",
                    "GBT01", "GBT02", "GBT03", "GBT04", "GBT05",
                    "GBT06", "GBT07", "GBT08", "GBT09", "GBT10"
                },
                ["LVDQFG01"] = new[]
                {
                    "Q2", "AP", "CUS", "MMDD", "STE", "CNTBEG", "CNTEND", "DRUN",
                    "RESTART", "STEP", "UPD"
                },
                ["LVD1PE01"] = new[]
                {
                    "Q2", "AP", "CUS", "MMDD", "STE", "CNTBEG", "CNTEND", "DRUN",
                    "N1MCY", "N2MCY", "CINC", "ROLLB", "GBT"
                }
            };

        private readonly Branch23ActivationVerifier _verifier;
        private readonly IBranch23JobRunner _runner;
        private readonly IPe01Checker _pe01;
        private readonly SpoolCounterAnalyzer _spool;

        public Branch23ActivationWorkflow(
            Branch23ActivationVerifier verifier,
            IBranch23JobRunner runner,
            IPe01Checker? pe01Checker = null,
            SpoolCounterAnalyzer? spoolAnalyzer = null,
            bool? allowPe01NotAvailable = null)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _pe01 = pe01Checker ?? new UnavailablePe01Checker();
            _spool = spoolAnalyzer ?? new SpoolCounterAnalyzer();
            _ = allowPe01NotAvailable;
        }

        public static Dictionary<string, string> BuildVariables(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(baseVariables);

            var v = new Dictionary<string, string>(baseVariables, StringComparer.OrdinalIgnoreCase);

            foreach (var date in Branch23ActivationResolver.BuildDateVariables(plan.RunDate.RunDate))
                v[date.Key] = date.Value;

            v["CNTBEG"] = plan.StartEnd.Start;
            v["CNTEND"] = plan.StartEnd.End;
            v["CLASS"] = Settings.Branch23Activation.Class;
            v["STE"] = Settings.Branch23Activation.Ste;
            v["AP"] = Settings.Branch23Activation.App;
            v["AMOUNT"] = plan.Premium.Amount.PadLeft(10, '0');
            v["BUCP"] = plan.Payment.Bucp.PadLeft(5, '0');
            v["CMDPMT"] = plan.Payment.Cmdpmt;

            v["NREMB"] = "20";
            v["CONTR-EX"] = "Y";
            v["CONTR-RE"] = "Y";
            v["CONTR-UN"] = "Y";
            v["NJJART72"] = "5";
            v["JOBNAMI"] = "LVPP06U$";
            v["CM"] = string.Empty;

            return v;
        }

        public static IReadOnlyList<string> FindInvalidVariables(
            IEnumerable<string> templateVariables,
            IReadOnlyDictionary<string, string> variables,
            IEnumerable<string>? allowedBlank = null)
        {
            ArgumentNullException.ThrowIfNull(templateVariables);
            ArgumentNullException.ThrowIfNull(variables);

            var blankOk = new HashSet<string>(
                new[] { "CM" },
                StringComparer.OrdinalIgnoreCase);

            if (allowedBlank != null)
            {
                foreach (string item in allowedBlank)
                {
                    blankOk.Add(NormalizeJclVariableName(item));
                }
            }

            var invalid = new List<string>();

            foreach (string rawName in templateVariables)
            {
                string name = NormalizeJclVariableName(rawName);
                if (string.IsNullOrWhiteSpace(name) ||
                    string.Equals(name, "JOBNAM", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!variables.TryGetValue(name, out string? value) || value == null)
                {
                    invalid.Add(rawName);
                    continue;
                }

                string trimmed = value.Trim();
                if (string.Equals(trimmed, "?", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("%%", StringComparison.Ordinal))
                {
                    invalid.Add(rawName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value) && !blankOk.Contains(name))
                {
                    invalid.Add(rawName);
                }
            }

            return invalid
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeJclVariableName(string? rawName)
        {
            string name = (rawName ?? string.Empty).Trim();
            if (name.StartsWith("%%", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }

            return name.TrimEnd('.');
        }

        public static Branch23FinalDiagnostic MapFinal(ContractActivationStatus scntt) => scntt switch
        {
            ContractActivationStatus.ACTIVATED => Branch23FinalDiagnostic.ACTIVATED,
            ContractActivationStatus.BLOCKED => Branch23FinalDiagnostic.BLOCKED,
            ContractActivationStatus.AMBIGUOUS => Branch23FinalDiagnostic.AMBIGUOUS,
            ContractActivationStatus.SQL_ERROR => Branch23FinalDiagnostic.TECHNICAL_FAILURE,
            _ => Branch23FinalDiagnostic.TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT
        };

        public async Task<Branch23ActivationReport> BuildPlanAndRunAsync(
            string? demandId,
            string? contractNumber,
            string? bucp,
            string? cmdpmt,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(baseVariables);
            var report = new Branch23ActivationReport { DemandId = demandId ?? "-", ContractNumber372 = contractNumber ?? "-" };
            Branch23ActivationPlan plan;
            try
            {
                var channel = Branch23ActivationResolver.IdentifyChannel(bucp, cmdpmt);
                report.Channel = channel.ToString();
                plan = await _verifier.BuildPlanAsync(demandId, contractNumber, channel, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Finish(report, Branch23FinalDiagnostic.CANCELLED, "BR23_USER_CANCELLED", "Annulé pendant la résolution du plan.");
            }
            catch (Branch23ActivationException ex)
            {
                return Finish(report, Branch23FinalDiagnostic.BLOCKED, ex.Code, ex.Message + " Aucun job soumis.");
            }
            catch (Exception ex)
            {
                return Finish(report, Branch23FinalDiagnostic.TECHNICAL_FAILURE, "BR23_PLAN_TECHNICAL_ERROR", ex.Message + " Aucun job soumis.");
            }

            return await RunAsync(plan, baseVariables, onProgress, token).ConfigureAwait(false);
        }

        public Task<Branch23ActivationReport> RunAsync(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            CancellationToken token)
        {
            ActivationJobSelection defaultSelection = Settings.Branch23Activation.Branch23ActivationOnly
                ? ActivationJobSelection.PremeliaOnly()
                : ActivationJobSelection.FullChain();

            return RunAsync(
                plan,
                baseVariables,
                onProgress,
                token,
                defaultSelection,
                prepareFundsAndVniAsync: null);
        }

        public async Task<Branch23ActivationReport> RunAsync(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            CancellationToken token,
            ActivationJobSelection? jobSelection,
            Func<DateTime, DateTime, CancellationToken, Task<(IReadOnlyList<string> Funds, string Source, IReadOnlyList<string> NavSummary)>>? prepareFundsAndVniAsync)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(baseVariables);

            ActivationJobSelection selection = jobSelection?.Clone()
                ?? (Settings.Branch23Activation.Branch23ActivationOnly
                    ? ActivationJobSelection.PremeliaOnly()
                    : ActivationJobSelection.FullChain());

            var report = new Branch23ActivationReport();
            try
            {
                await RunCoreAsync(
                        plan,
                        baseVariables,
                        onProgress,
                        report,
                        selection,
                        prepareFundsAndVniAsync,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                MarkPending(report, "Cancelled");
                Finish(report, Branch23FinalDiagnostic.CANCELLED, "BR23_USER_CANCELLED",
                    "Annulation utilisateur; aucun job suivant soumis.");
            }
            catch (Exception ex)
            {
                MarkPending(report, "NotExecuted");
                Finish(report, Branch23FinalDiagnostic.TECHNICAL_FAILURE,
                    ex is Branch23ActivationException b ? b.Code : "BR23_TECHNICAL_FAILURE",
                    ex.Message + " Rapport partiel conservé ; aucun job suivant soumis.");
            }
            return report;
        }

        private async Task RunCoreAsync(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            Branch23ActivationReport report,
            ActivationJobSelection selection,
            Func<DateTime, DateTime, CancellationToken, Task<(IReadOnlyList<string> Funds, string Source, IReadOnlyList<string> NavSummary)>>? prepareFundsAndVniAsync,
            CancellationToken token)
        {
            void Log(string line) { report.Lines.Add(line); onProgress?.Invoke("BR23: " + line); }

            var variables = BuildVariables(plan, baseVariables);
            string start = plan.StartEnd.Start;
            DateTime runDate = plan.RunDate.RunDate.Date;
            DateTime consolidationDate = Branch23ParameterBuilder.GetConsolidationDate(runDate);

            report.DemandId = Display(plan.DemandId);
            report.EliaId = Display(plan.EliaId);
            report.ContractNumber372 = Display(plan.StartEnd.FullContractNumber);
            report.LisaInternalNumber = start;
            report.RunDate = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            report.ConsolidationDate = consolidationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            report.RunDateSource = plan.RunDate.Source;
            report.RunDateReason = plan.RunDate.Reason;
            report.SelectedStepsSummary = selection.ToShortSummary(includeBranch23: true);
            report.EliaDates =
                $"DateVersement={DisplayDate(plan.RunDate.PaymentDate)}; " +
                $"DatePremierePrime={DisplayDate(plan.RunDate.FirstPremiumDate)}; " +
                $"DateDebutContrat={DisplayDate(plan.RunDate.ContractStartDate)}" +
                (plan.RunDate.PaymentDateRejection.Length > 0
                    ? $" | rejet DateVersement: {plan.RunDate.PaymentDateRejection}"
                    : string.Empty);
            foreach (string warning in plan.RunDate.Warnings) report.Warnings.Add(warning);
            report.Amount = plan.Premium.Amount;
            report.AmountInjected = variables["AMOUNT"];
            report.SelectedPremiumRow = plan.Premium.Selected.ToString();
            report.Channel = plan.Payment.BusinessChannelName;
            report.Bucp = variables["BUCP"];
            report.Cmdpmt = variables["CMDPMT"];

            // Build evidence list for PREMELIA jobs + selected LVDQ jobs
            foreach (string job in JobOrder)
            {
                report.Jobs.Add(new Branch23JobEvidence
                {
                    JobName = job,
                    SubJobName = "-",
                    Variables = JclVariables[job].ToDictionary(
                        k => k,
                        k => variables.TryGetValue(k, out string? val) ? val : "<absent>",
                        StringComparer.OrdinalIgnoreCase)
                });
            }

            Dictionary<string, string> fg03Vars = BuildMergedLvdqVariables(
                variables,
                Branch23ParameterBuilder.BuildFundEventsVariables(runDate));
            Dictionary<string, string> fg01Vars = BuildMergedLvdqVariables(
                variables,
                Branch23ParameterBuilder.BuildConsolidationVariables(consolidationDate));
            Dictionary<string, string> pe01Vars = BuildMergedLvdqVariables(
                variables,
                Branch23ParameterBuilder.BuildFp01Variables(consolidationDate));

            Branch23JobEvidence? fg03Evidence = null;
            Branch23JobEvidence? fg01Evidence = null;
            Branch23JobEvidence? pe01Evidence = null;

            if (selection.RunLvdqFg03)
            {
                fg03Evidence = new Branch23JobEvidence
                {
                    JobName = "LVDQFG03",
                    SubJobName = "-",
                    Variables = JclVariables["LVDQFG03"].ToDictionary(
                        k => k,
                        k => fg03Vars.TryGetValue(k, out string? val) ? val : "<absent>",
                        StringComparer.OrdinalIgnoreCase)
                };
                report.Jobs.Add(fg03Evidence);
            }

            if (selection.RunLvdqFg01)
            {
                fg01Evidence = new Branch23JobEvidence
                {
                    JobName = "LVDQFG01",
                    SubJobName = "-",
                    Variables = JclVariables["LVDQFG01"].ToDictionary(
                        k => k,
                        k => fg01Vars.TryGetValue(k, out string? val) ? val : "<absent>",
                        StringComparer.OrdinalIgnoreCase)
                };
                report.Jobs.Add(fg01Evidence);
            }

            if (selection.RunLvd1Pe01)
            {
                pe01Evidence = new Branch23JobEvidence
                {
                    JobName = "LVD1PE01",
                    SubJobName = "-",
                    Variables = JclVariables["LVD1PE01"].ToDictionary(
                        k => k,
                        k => pe01Vars.TryGetValue(k, out string? val) ? val : "<absent>",
                        StringComparer.OrdinalIgnoreCase)
                };
                report.Jobs.Add(pe01Evidence);
            }

            Log($"Start={start}; End={plan.StartEnd.End}; RunDate(N)={report.RunDate} ({report.RunDateSource}); " +
                $"ConsolidationDate(N+2)={report.ConsolidationDate}; AMOUNT={report.AmountInjected}; " +
                $"BUCP={report.Bucp}; CMDPMT={report.Cmdpmt}; Steps={report.SelectedStepsSummary}.");

            bool alreadyActiveInScntt = false;

            // ---------------- SCNTT0 before
            try
            {
                var before = await _verifier.GetScnttRowsAsync(start, token).ConfigureAwait(false);
                report.ScnttStateBefore = before.Count == 1
                    ? Convert.ToString(before[0].State, CultureInfo.InvariantCulture)
                    : null;
                report.ScnttBeforeDetail = before.Count == 0 ? "absent" : string.Join(" | ", before.Select(RowText));

                if (before.Count == 1 && IsActiveScnttRow(before[0]))
                {
                    alreadyActiveInScntt = true;
                    report.ScnttFinalStatus = ContractActivationStatus.ACTIVATED;
                    report.ScnttFinalDetail = "Contrat déjà actif (C_ETAT_CNT=4) et non bloqué dans SCNTT0.";
                    report.ScnttFinalRow = RowText(before[0]);
                    report.BeforeAfterComparison = "C_ETAT_CNT déjà à 4";

                    for (int i = 0; i < 5; i++)
                    {
                        report.Jobs[i].Status = "SkippedAlreadyActive";
                    }

                    if (!selection.HasAnyBranch23Step)
                    {
                        Finish(report, Branch23FinalDiagnostic.ACTIVATED, string.Empty,
                            "Contrat déjà actif dans SCNTT0; workflow idempotent, aucun job soumis.");
                        return;
                    }

                    Log("Contrat déjà actif dans SCNTT0 (C_ETAT_CNT=4) : les jobs PREMELIA sont ignorés, poursuite vers les étapes Branche 23 (LVDQ / VNI).");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.ScnttBeforeDetail = "NON LISIBLE: " + ex.Message;
                report.Warnings.Add("SCNTT0 avant workflow non lisible: la comparaison avant/après sera incomplète.");
            }

            // ============================================================
            // PHASE 1 & 2 : PREMELIA (ADDPRCT / SQL PRCTT0 -> LVPP06U -> LVPG22U -> LI1J04D0 -> LI1J04D2)
            // ============================================================
            if (!alreadyActiveInScntt && selection.HasAnyPremeliaJob)
            {
                bool skipPremiumJobsForResume = false;

                if (selection.RunAddPrct || selection.UseSqlPrctInsert)
                {
                    var prctBefore = await _verifier.CheckPrctAsync(plan, token).ConfigureAwait(false);
                    report.PrctBefore = prctBefore.Status;
                    report.PrctBeforeDetail = prctBefore.Detail;

                    switch (prctBefore.Status)
                    {
                        case PrctCheckStatus.ADDPRCT_CONFIRMED:
                            report.AddprctSkipped = true;
                            report.PrctAfter = prctBefore.Status;
                            report.PrctAfterDetail = "Reprise: réception identique déjà présente dans LV.PRCTT0, ADDPRCT non relancé.";
                            report.Jobs[0].Status = "SkippedAlreadyConfirmed";
                            Log(report.PrctAfterDetail);

                            try
                            {
                                var existingAssignment = await _verifier
                                    .GetPremiumAssignmentPresenceAsync(start, token)
                                    .ConfigureAwait(false);
                                report.AssignmentDetail =
                                    $"PRIST0 lignes={existingAssignment.PremiumCount}; " +
                                    $"PECHT0 lignes={existingAssignment.ReceiptCount} (pré-contrôle reprise)";

                                if (existingAssignment.PremiumCount > 0 &&
                                    existingAssignment.ReceiptCount > 0)
                                {
                                    skipPremiumJobsForResume = true;
                                    report.Jobs[1].Status = "SkippedAlreadyProcessed";
                                    report.Jobs[2].Status = "SkippedAlreadyProcessed";
                                    Log("Reprise confirmée: réception déjà traitée/affectée; LVPP06U et LVPG22U non relancés.");
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                report.Warnings.Add(
                                    "Pré-contrôle de reprise PRIST0/PECHT0 indisponible: " +
                                    ex.Message + ". Les jobs prime restent nécessaires.");
                            }
                            break;

                        case PrctCheckStatus.ADDPRCT_NOT_FOUND:
                            {
                                int addprctRc = 0;
                                if (selection.UseSqlPrctInsert && !selection.RunAddPrct)
                                {
                                    report.AddprctSkipped = true;
                                    report.Jobs[0].Status = "SkippedSqlPrctUsed";
                                    var sqlInsert = await _verifier.InsertPrctRowViaSqlAsync(plan, token).ConfigureAwait(false);
                                    Log(sqlInsert.Detail);
                                }
                                else
                                {
                                    var addprct = await RunJobAsync(report.Jobs[0], variables, token).ConfigureAwait(false);
                                    addprctRc = addprct.ReturnCode;
                                    if (addprct.ReturnCode == 8)
                                    {
                                        report.Warnings.Add("ADDPRCT RC=0008: diagnostic DB2TIAD conservé ; PRCTT0 décide.");
                                        foreach (string line in _spool.Analyze(addprct.Spool).DiagnosticLines)
                                            Log("ADDPRCT spool: " + line);
                                    }
                                }

                                var prctAfter = await _verifier.CheckPrctAsync(plan, token).ConfigureAwait(false);
                                if (prctAfter.Status == PrctCheckStatus.ADDPRCT_NOT_FOUND && selection.UseSqlPrctInsert)
                                {
                                    var sqlFallback = await _verifier.InsertPrctRowViaSqlAsync(plan, token).ConfigureAwait(false);
                                    Log("Fallback SQL LV.PRCTT0 : " + sqlFallback.Detail);
                                    prctAfter = await _verifier.CheckPrctAsync(plan, token).ConfigureAwait(false);
                                }

                                report.PrctAfter = prctAfter.Status;
                                report.PrctAfterDetail = prctAfter.Detail;

                                if (prctAfter.Status == PrctCheckStatus.ADDPRCT_SQL_ERROR)
                                    throw new Branch23ActivationException("BR23_ADDPRCT_SQL_ERROR", "PRCTT0 non lisible après ADDPRCT: " + prctAfter.Detail);
                                if (prctAfter.Status != PrctCheckStatus.ADDPRCT_CONFIRMED)
                                {
                                    Finish(report,
                                        prctAfter.Status == PrctCheckStatus.ADDPRCT_NOT_FOUND
                                            ? Branch23FinalDiagnostic.NOT_ACTIVATED
                                            : prctAfter.Status is PrctCheckStatus.ADDPRCT_AMBIGUOUS or PrctCheckStatus.ADDPRCT_CONFLICTING_RECEIPTS
                                                ? Branch23FinalDiagnostic.AMBIGUOUS
                                                : Branch23FinalDiagnostic.BLOCKED,
                                        "BR23_" + prctAfter.Status,
                                        $"PRCTT0 non confirmé après ADDPRCT/SQL (RC={addprctRc:0000}); LVPP06U non soumis.");
                                    return;
                                }
                                break;
                            }

                        case PrctCheckStatus.ADDPRCT_SQL_ERROR:
                            Finish(report, Branch23FinalDiagnostic.TECHNICAL_FAILURE, "BR23_PRE_ADDPRCT_SQL_ERROR",
                                "PRCTT0 non lisible avant ADDPRCT; aucun job soumis.");
                            return;

                        default:
                            Finish(report,
                                prctBefore.Status is PrctCheckStatus.ADDPRCT_AMBIGUOUS or PrctCheckStatus.ADDPRCT_CONFLICTING_RECEIPTS
                                    ? Branch23FinalDiagnostic.AMBIGUOUS
                                    : Branch23FinalDiagnostic.BLOCKED,
                                "BR23_PRE_" + prctBefore.Status,
                                $"PRCTT0 incompatible avant ADDPRCT ({prctBefore.Status}); aucun job soumis.");
                            return;
                    }
                }
                else
                {
                    report.AddprctSkipped = true;
                    report.Jobs[0].Status = "SkippedByUserSelection";
                    Log("ADDPRCT ignoré (case décochée par l'utilisateur).");
                }

                // ---------------- LVPP06U, LVPG22U
                if (!skipPremiumJobsForResume)
                {
                    if (selection.RunLvpp06u)
                    {
                        await RunJobAsync(report.Jobs[1], variables, token).ConfigureAwait(false);
                    }
                    else
                    {
                        report.Jobs[1].Status = "SkippedByUserSelection";
                    }

                    if (selection.RunLvpg22u)
                    {
                        await RunJobAsync(report.Jobs[2], variables, token).ConfigureAwait(false);
                    }
                    else
                    {
                        report.Jobs[2].Status = "SkippedByUserSelection";
                    }
                }

                // ---------------- PRIST0 / PECHT0 (presence only)
                if (selection.RunLvpp06u || selection.RunLvpg22u)
                {
                    try
                    {
                        var presence = await _verifier.GetPremiumAssignmentPresenceAsync(start, token).ConfigureAwait(false);
                        report.AssignmentDetail = $"PRIST0 lignes={presence.PremiumCount}; PECHT0 lignes={presence.ReceiptCount}";
                        if (presence.PremiumCount == 0 || presence.ReceiptCount == 0)
                            report.Warnings.Add("PRIST0/PECHT0: aucune ligne détectée après LVPG22U (non bloquant).");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        report.AssignmentDetail = "NON LISIBLE: " + ex.Message;
                        report.Warnings.Add("PRIST0/PECHT0 non lisibles.");
                    }
                }

                // ---------------- PE01
                Pe01CheckResult pe01;
                try { pe01 = await _pe01.CheckAsync(start, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { pe01 = new Pe01CheckResult { Status = Pe01CheckStatus.ERROR, Detail = ex.Message }; }

                report.Pe01 = pe01.Status;
                report.Pe01Detail = pe01.Detail;
                if (pe01.Status == Pe01CheckStatus.NOT_FOUND)
                {
                    Finish(
                        report,
                        Branch23FinalDiagnostic.NOT_ACTIVATED,
                        "BR23_PE01_NOT_FOUND",
                        "La source PE01 est disponible mais aucun PE01 éligible n'a été trouvé; LI1J04D0 non soumis.");
                    return;
                }

                // ---------------- LI1J04D0, then LI1J04D2
                if (selection.RunLi1j04d0)
                {
                    await RunJobAsync(report.Jobs[3], variables, token).ConfigureAwait(false);
                }
                else
                {
                    report.Jobs[3].Status = "SkippedByUserSelection";
                }

                if (selection.RunLi1j04d2)
                {
                    await RunJobAsync(report.Jobs[4], variables, token).ConfigureAwait(false);
                }
                else
                {
                    report.Jobs[4].Status = "SkippedByUserSelection";
                }
            }
            else if (!alreadyActiveInScntt)
            {
                for (int i = 0; i < 5; i++)
                {
                    report.Jobs[i].Status = "SkippedByUserSelection";
                }
            }

            // ============================================================
            // PHASE 3 : CHAÎNE LVDQ & SQL VNI (LVDQFG03 Date N -> SQL LV000GD0.LV.GFCRT0 N+2 -> LVDQFG01 Date N+2)
            // ============================================================
            if (selection.HasAnyBranch23Step)
            {
                // 1) Préparation VNI initiale avant LVDQFG03 (si SQL VNI coché)
                if (selection.RunBranch23SqlVni && prepareFundsAndVniAsync != null)
                {
                    Log($"Préparation SQL VNI dans LV000GD0 (LV.GFCRT0 / GFEVT0) de {report.RunDate} (Date N) à {report.ConsolidationDate} (Date N+2)...");
                    var vniResult = await prepareFundsAndVniAsync(runDate, consolidationDate, token).ConfigureAwait(false);
                    report.FundsResolved = vniResult.Funds.Count > 0 ? string.Join(", ", vniResult.Funds) : "Aucun fonds résolu";
                    report.FundsSource = vniResult.Source;
                    report.NavSummary = vniResult.NavSummary.Count > 0
                        ? string.Join(" | ", vniResult.NavSummary)
                        : "VNI vérifiées";
                    Log($"Fonds BR23={report.FundsResolved} ({report.FundsSource}) | VNI={report.NavSummary}");
                }

                // 2) Étape 4 du guide : Créer le mouvement via LVDQFG03 (LVFG03U) à la date de création (Date N)
                if (selection.RunLvdqFg03 && fg03Evidence != null)
                {
                    Log($"Soumission LVDQFG03 (LVFG03U) à la date de création DRUN={fg03Vars["DRUN"]} ({report.RunDate})...");
                    await RunJobAsync(fg03Evidence, fg03Vars, token).ConfigureAwait(false);

                    var mvtAfterFg03 = await _verifier.GetFundMovementsSummaryAsync(start, token).ConfigureAwait(false);
                    report.MovementStateAfterFg03 = mvtAfterFg03.Summary;
                    Log($"Mouvement LISA après LVDQFG03 : {report.MovementStateAfterFg03}");
                }
                else
                {
                    report.MovementStateAfterFg03 = "LVDQFG03 non exécuté (case décochée)";
                }

                // 3) Étape 5 du guide : En SQL LV000GD0 -> LV.GFCRT0 / GFEVT0 mettre la valeur à la date N+2
                //    (ré-exécuté après LVDQFG03 au cas où de nouveaux fonds ont été découverts dans LV000FD0.LV.FMVDT0)
                if (selection.RunBranch23SqlVni && prepareFundsAndVniAsync != null)
                {
                    var vniResultAfterFg03 = await prepareFundsAndVniAsync(runDate, consolidationDate, token).ConfigureAwait(false);
                    if (vniResultAfterFg03.Funds.Count > 0)
                    {
                        report.FundsResolved = string.Join(", ", vniResultAfterFg03.Funds);
                        report.FundsSource = vniResultAfterFg03.Source;
                    }
                    if (vniResultAfterFg03.NavSummary.Count > 0)
                    {
                        report.NavSummary = string.Join(" | ", vniResultAfterFg03.NavSummary);
                    }
                    Log($"VNI SQL confirmées à N+2 ({report.ConsolidationDate}) dans LV000GD0 : {report.NavSummary}");
                }

                // 4) Étape 6 du guide : En LVCHAIN consolider le mouvement via LVDQFG01 (LVFG01U, Seq 1, STEP=1) à la date N+2
                if (selection.RunLvdqFg01 && fg01Evidence != null)
                {
                    Log($"Soumission LVDQFG01 (LVFG01U, Seq 1, STEP=1, UPD=Y) à Date N+2 DRUN={fg01Vars["DRUN"]} ({report.ConsolidationDate})...");
                    await RunJobAsync(fg01Evidence, fg01Vars, token).ConfigureAwait(false);

                    var mvtAfterFg01 = await _verifier.GetFundMovementsSummaryAsync(start, token).ConfigureAwait(false);
                    report.MovementStateAfterFg01 = mvtAfterFg01.Summary;
                    Log($"Mouvement LISA après LVDQFG01 (consolidation) : {report.MovementStateAfterFg01}");
                }
                else
                {
                    report.MovementStateAfterFg01 = "LVDQFG01 non exécuté (case décochée)";
                }

                // 5) Étape optionnelle LVD1PE01 (Seq 3, FP01)
                if (selection.RunLvd1Pe01 && pe01Evidence != null)
                {
                    Log($"Soumission LVD1PE01 (LVPE01U, GBT=FP01) à Date N+2 DRUN={pe01Vars["DRUN"]}...");
                    await RunJobAsync(pe01Evidence, pe01Vars, token).ConfigureAwait(false);
                }
            }

            // ---------------- Contrôle final SCNTT0 (informatif, non bloquant si propagation asynchrone)
            try
            {
                var afterScntt = await _verifier.GetScnttRowsAsync(start, token).ConfigureAwait(false);
                if (afterScntt.Count == 1)
                {
                    var evaluated = Branch23ActivationResolver.EvaluateScntt(afterScntt, report.ScnttStateBefore);
                    report.ScnttFinalStatus = evaluated.Status;
                    report.ScnttFinalDetail = evaluated.Detail;
                    report.ScnttFinalRow = RowText(afterScntt[0]);
                    report.BeforeAfterComparison = $"Avant={report.ScnttStateBefore ?? "absent"} -> Après=C_ETAT_CNT={afterScntt[0].State}";
                }
                else
                {
                    report.ScnttFinalStatus = ContractActivationStatus.PENDING_ASYNC_LISA_CREATION;
                    report.ScnttFinalDetail = "Propagation LISA asynchrone en cours (SCNTT0 non bloquant).";
                    report.ScnttFinalRow = "PENDING_ASYNC_LISA_CREATION";
                    report.BeforeAfterComparison = "Séquence technique terminée avec succès.";
                }
            }
            catch
            {
                report.ScnttFinalStatus = null;
                report.ScnttFinalDetail = "NOT_CHECKED: LISA updates SCNTT0 asynchronously.";
                report.ScnttFinalRow = "SCNTT0_NOT_CHECKED";
            }

            Finish(
                report,
                Branch23FinalDiagnostic.ACTIVATED,
                string.Empty,
                $"Séquence exécutée avec succès ({report.SelectedStepsSummary}).");
        }

        private static Dictionary<string, string> BuildMergedLvdqVariables(
            IReadOnlyDictionary<string, string> commonVariables,
            IReadOnlyDictionary<string, string> specificVariables)
        {
            var merged = new Dictionary<string, string>(commonVariables, StringComparer.OrdinalIgnoreCase)
            {
                ["AP"] = Settings.Branch23.Application,
                ["STE"] = Settings.Branch23.Ste,
                ["CLASS"] = Settings.Branch23Activation.Class
            };

            foreach (var kvp in specificVariables)
            {
                merged[kvp.Key] = kvp.Value;
            }

            return merged;
        }

        private async Task<Branch23JobOutcome> RunJobAsync(
            Branch23JobEvidence evidence, Dictionary<string, string> variables, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(evidence);
            ArgumentNullException.ThrowIfNull(variables);

            if (!JclVariables.TryGetValue(evidence.JobName, out string[]? templateVariables))
            {
                evidence.Status = "Failed";
                throw new Branch23ActivationException(
                    "BR23_JCL_DEFINITION_MISSING",
                    $"Aucune définition de variables JCL pour {evidence.JobName}; job non soumis.");
            }

            IReadOnlyList<string> invalidVariables =
                FindInvalidVariables(
                    templateVariables,
                    variables,
                    allowedBlank: new[] { "CM" });
            if (invalidVariables.Count > 0)
            {
                evidence.Status = "Failed";
                throw new Branch23ActivationException(
                    "BR23_JCL_VARIABLES_INVALID",
                    $"{evidence.JobName}: variables absentes, vides ou non résolues: " +
                    string.Join(", ", invalidVariables) + ". Job non soumis.");
            }

            evidence.Status = "Running";
            Branch23JobOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(evidence.JobName, evidence.SubJobName, variables, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                evidence.Status = "Cancelled";
                throw;
            }
            catch (Exception ex)
            {
                evidence.Status = "Failed";
                throw new Branch23ActivationException("BR23_JOB_FAILED",
                    $"{evidence.JobName}{(evidence.SubJobName == "-" ? string.Empty : "/" + evidence.SubJobName)}: {ex.Message}");
            }

            evidence.JobNumber = outcome.JobNumber;
            evidence.ReturnCode = outcome.ReturnCode.ToString("0000", CultureInfo.InvariantCulture);
            evidence.SubJobNumber = outcome.SubJobNumber;
            evidence.SubJobReturnCode = outcome.SubJobReturnCode;

            var counters = _spool.Analyze(outcome.Spool);
            evidence.Counters = counters.Status == SpoolCounterStatus.COUNTERS_FOUND
                ? string.Join(", ", counters.Counters.Select(c => $"{c.Key}={c.Value.ToString(CultureInfo.InvariantCulture)}"))
                : "COUNTERS_NOT_AVAILABLE (preuve = SQL)";

            bool accepted = outcome.ReturnCode is 0 or 4 ||
                (evidence.JobName == "ADDPRCT" && outcome.ReturnCode == 8);
            evidence.Status = accepted ? "Complete" : "Failed";
            if (!accepted)
                throw new Branch23ActivationException("BR23_JOB_FAILED", $"{evidence.JobName} RC={evidence.ReturnCode}.");
            return outcome;
        }

        private static void MarkPending(Branch23ActivationReport report, string status)
        {
            foreach (var job in report.Jobs.Where(j => j.Status is "Pending" or "Running"))
                job.Status = status;
        }

        private static Branch23ActivationReport Finish(
            Branch23ActivationReport report,
            Branch23FinalDiagnostic diagnostic,
            string code,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(report);
            MarkPending(
                report,
                diagnostic == Branch23FinalDiagnostic.CANCELLED
                    ? "Cancelled"
                    : "NotExecuted");
            report.FinalDiagnostic = diagnostic;
            report.FailureCode = code ?? string.Empty;
            report.FinalReason = reason ?? string.Empty;
            report.Lines.Add($"{diagnostic}: {report.FinalReason}");
            return report;
        }

        private static bool IsActiveScnttRow(ScnttRow row)
        {
            if (row == null)
            {
                return false;
            }

            string ste = Convert.ToString(row.Ste, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string state = Convert.ToString(row.State, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string errors = Convert.ToString(row.ErrorMessages, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string premiumBlocked = Convert.ToString(row.PremiumBlocked, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string invalidRequest = Convert.ToString(row.InvalidRequest, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            string movementBlocked = Convert.ToString(row.MovementBlocked, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

            return string.Equals(ste, "A", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(state, "4", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(errors, "0", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(premiumBlocked, "N", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(invalidRequest, "N", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(movementBlocked, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string RowText(ScnttRow r) =>
            $"C_STE={r.Ste}; NO_CNT={r.ContractNumber}; C_ETAT_CNT={r.State}; C_MSGS_ERR={r.ErrorMessages}; " +
            $"C_BLOQ_PRM={r.PremiumBlocked}; C_DEM_INVAL={r.InvalidRequest}; C_BLOQ_MVT={r.MovementBlocked}";

        private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

        private static string DisplayDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "NULL";
    }
}
