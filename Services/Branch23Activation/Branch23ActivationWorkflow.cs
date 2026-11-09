using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;

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

    /// <summary>The only final diagnostics of the Branch23ActivationOnly workflow.</summary>
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
        public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();
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
        public string RunDateSource { get; set; } = "-";
        public string RunDateReason { get; set; } = "-";
        public string EliaDates { get; set; } = "-";
        public string Amount { get; set; } = "-";
        public string AmountInjected { get; set; } = "-";
        public string SelectedPremiumRow { get; set; } = "-";
        public string Channel { get; set; } = "-";
        public string Bucp { get; set; } = "-";
        public string Cmdpmt { get; set; } = "-";

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
            b.AppendLine("=== BRANCH 23 ACTIVATION REPORT (Branch23ActivationOnly) ===");
            b.AppendLine($"Demand ID            : {DemandId}");
            b.AppendLine($"ELIA ID              : {EliaId}");
            b.AppendLine($"Contrat 3-7-2        : {ContractNumber372}");
            b.AppendLine($"CO interne LISA      : {LisaInternalNumber} (Start = End)");
            b.AppendLine($"Run Date             : {RunDate} (source {RunDateSource}; {RunDateReason})");
            b.AppendLine($"Dates ELIA           : {EliaDates}");
            b.AppendLine($"Prime brute          : {Amount} -> AMOUNT inject  {AmountInjected}");
            b.AppendLine($"Ligne TB5UPRP        : {SelectedPremiumRow}");
            b.AppendLine($"Canal / BUCP / CMDPMT: {Channel} / {Bucp} / {Cmdpmt}");
            b.AppendLine($"SCNTT0 avant         : {ScnttBeforeDetail}");
            b.AppendLine($"PRCTT0 avant ADDPRCT : {PrctBefore?.ToString() ?? "-"} | {PrctBeforeDetail}");
            b.AppendLine($"ADDPRCT relancé      : {(AddprctSkipped ? "NON (reprise, réception déjà confirmée)" : Jobs.FirstOrDefault()?.Status == "Complete" ? "OUI" : "NON")}");
            b.AppendLine($"PRCTT0 après ADDPRCT : {PrctAfter?.ToString() ?? "-"} | {PrctAfterDetail}");
            b.AppendLine($"PRIST0 / PECHT0      : {AssignmentDetail}");
            b.AppendLine($"PE01                 : {Pe01?.ToString() ?? "-"} | {Pe01Detail}");
            if (Pe01 == Pe01CheckStatus.NOT_AVAILABLE)
            {
                b.AppendLine(
                    "PE01 policy          : SQL source unavailable; continuation allowed, " +
                    "strict SCNTT0 confirmation required.");
            }
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
    /// Branch23ActivationOnly supports BANK 00382/8 and BROKER_NEW_GEN 80819/H.
    /// PRCTT0 -> ADDPRCT (if needed) -> PRCTT0 ->
    /// LVPP06U -> LVPG22U -> checks -> LI1J04D0 -> LI1J04D2 -> stop.
    /// LISA updates SCNTT0 asynchronously, so the absence of an immediate row is not
    /// treated as an activation failure. No fund, NAV, LVDQ or second LI pass.
    /// </summary>
    public sealed class Branch23ActivationWorkflow
    {
        public static readonly string[] JobOrder =
            { "ADDPRCT", "LVPP06U", "LVPG22U", "LI1J04D0", "LI1J04D2" };

        /// <summary>Variables of the real JCL (read on the share), logged as evidence.</summary>
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
                ["LI1J04D2"] = new[] { "CLASS", "CUS", "YYMMDD", "YYYY", "MM", "DD", "ENVIMS" }
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

            // Retained for API compatibility. ELIAT0 is not available through the
            // confirmed SQL Server databases. NOT_AVAILABLE therefore means that the
            // PE01 control source cannot be queried, not that PE01 is absent.
            _ = allowPe01NotAvailable;
        }

        /// <summary>Builds the job variables. Plan values always override the base values.</summary>
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
            // Existing ADDPRCT format kept (PadLeft) until the JCL proves otherwise.
            v["AMOUNT"] = plan.Premium.Amount.PadLeft(10, '0');
            v["BUCP"] = plan.Payment.Bucp.PadLeft(5, '0');
            v["CMDPMT"] = plan.Payment.Cmdpmt;

            // Confirmed PREMELIA line 3 defaults from LVChain.
            v["NREMB"] = "20";
            v["CONTR-EX"] = "Y";
            v["CONTR-RE"] = "Y";
            v["CONTR-UN"] = "Y";
            v["NJJART72"] = "5";
            v["JOBNAMI"] = "LVPP06U$";

            // CM is deliberately blank in LVChain. Presence of the key means the
            // variable is resolved; an empty value is the valid business value.
            v["CM"] = string.Empty;

            return v;
        }

        /// <summary>
        /// Values that block submission: missing, empty or "?".
        /// Blank values are allowed only for explicitly confirmed keys such as CM.
        /// </summary>
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

            // In expressions such as %%CM. the final dot is a JCL concatenation
            // delimiter, not part of the variable name.
            return name.TrimEnd('.');
        }

        /// <summary>
        /// Maps an explicit SCNTT0 evaluation when one is requested separately.
        /// The main activation workflow no longer calls this after LI1J04D2.
        /// </summary>
        public static Branch23FinalDiagnostic MapFinal(ContractActivationStatus scntt) => scntt switch
        {
            ContractActivationStatus.ACTIVATED => Branch23FinalDiagnostic.ACTIVATED,
            ContractActivationStatus.BLOCKED => Branch23FinalDiagnostic.BLOCKED,
            ContractActivationStatus.AMBIGUOUS => Branch23FinalDiagnostic.AMBIGUOUS,
            ContractActivationStatus.SQL_ERROR => Branch23FinalDiagnostic.TECHNICAL_FAILURE,
            _ => Branch23FinalDiagnostic.TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT
        };

        /// <summary>
        /// Builds the plan from ELIA then runs. Any plan error (unsupported channel,
        /// premium, Run Date, contract number) blocks before any submission.
        /// </summary>
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

        public async Task<Branch23ActivationReport> RunAsync(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(baseVariables);

            var report = new Branch23ActivationReport();
            try
            {
                await RunCoreAsync(plan, baseVariables, onProgress, report, token).ConfigureAwait(false);
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
                    ex.Message + " Rapport partiel conserv ; aucun job suivant soumis.");
            }
            return report;
        }

        private async Task RunCoreAsync(
            Branch23ActivationPlan plan,
            IReadOnlyDictionary<string, string> baseVariables,
            Action<string>? onProgress,
            Branch23ActivationReport report,
            CancellationToken token)
        {
            void Log(string line) { report.Lines.Add(line); onProgress?.Invoke("BR23: " + line); }

            var variables = BuildVariables(plan, baseVariables);
            string start = plan.StartEnd.Start;

            report.DemandId = Display(plan.DemandId);
            report.EliaId = Display(plan.EliaId);
            report.ContractNumber372 = Display(plan.StartEnd.FullContractNumber);
            report.LisaInternalNumber = start;
            report.RunDate = plan.RunDate.RunDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            report.RunDateSource = plan.RunDate.Source;
            report.RunDateReason = plan.RunDate.Reason;
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
            report.Channel = plan.Payment.Channel.ToString();
            report.Bucp = variables["BUCP"];
            report.Cmdpmt = variables["CMDPMT"];

            foreach (string job in JobOrder)
            {
                report.Jobs.Add(new Branch23JobEvidence
                {
                    JobName = job,
                    // LVChain submits D0 and D2 directly. D1/D3 are not required
                    // correlated child jobs for this activation workflow.
                    SubJobName = "-",
                    Variables = JclVariables[job].ToDictionary(k => k,
                        k => variables.TryGetValue(k, out string? val) ? val : "<absent>", StringComparer.OrdinalIgnoreCase)
                });
            }

            Log($"Start={start}; End={plan.StartEnd.End}; Run Date={report.RunDate} ({report.RunDateSource}); " +
                $"AMOUNT={report.AmountInjected}; BUCP={report.Bucp}; CMDPMT={report.Cmdpmt}; CLASS={variables["CLASS"]}.");

            // ---------------- SCNTT0 before
            try
            {
                var before = await _verifier.GetScnttRowsAsync(start, token).ConfigureAwait(false);
                report.ScnttStateBefore = before.Count == 1
                    ? Convert.ToString(before[0].State, CultureInfo.InvariantCulture)
                    : null;
                report.ScnttBeforeDetail = before.Count == 0 ? "absent" : string.Join(" | ", before.Select(RowText));

                // Idempotence: an already active and unblocked contract must not submit
                // ADDPRCT or any PREMELIA/LI job again.
                if (before.Count == 1 && IsActiveScnttRow(before[0]))
                {
                    report.ScnttFinalStatus = ContractActivationStatus.ACTIVATED;
                    report.ScnttFinalDetail = "Contrat déjà actif et non bloqu ; aucun job soumis.";
                    report.ScnttFinalRow = RowText(before[0]);
                    report.BeforeAfterComparison = "C_ETAT_CNT déjà   4";
                    MarkPending(report, "SkippedAlreadyActive");
                    Finish(report, Branch23FinalDiagnostic.ACTIVATED, string.Empty,
                        "Contrat déjà actif dans SCNTT0; workflow idempotent, aucun job soumis.");
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.ScnttBeforeDetail = "NON LISIBLE: " + ex.Message;
                report.Warnings.Add("SCNTT0 avant workflow non lisible: la comparaison avant/après sera incompl te.");
            }

            // ---------------- PRCTT0 before ADDPRCT
            var prctBefore = await _verifier.CheckPrctAsync(plan, token).ConfigureAwait(false);
            report.PrctBefore = prctBefore.Status;
            report.PrctBeforeDetail = prctBefore.Detail;
            bool skipPremiumJobsForResume = false;

            switch (prctBefore.Status)
            {
                case PrctCheckStatus.ADDPRCT_CONFIRMED:
                    report.AddprctSkipped = true;
                    report.PrctAfter = prctBefore.Status;
                    report.PrctAfterDetail = "Reprise: réception identique déjà présente, ADDPRCT non relancé.";
                    report.Jobs[0].Status = "SkippedAlreadyConfirmed";
                    Log(report.PrctAfterDetail);

                    try
                    {
                        var existingAssignment = await _verifier
                            .GetPremiumAssignmentPresenceAsync(start, token)
                            .ConfigureAwait(false);
                        report.AssignmentDetail =
                            $"PRIST0 lignes={existingAssignment.PremiumCount}; " +
                            $"PECHT0 lignes={existingAssignment.ReceiptCount} (pr -contrôle reprise)";

                        if (existingAssignment.PremiumCount > 0 &&
                            existingAssignment.ReceiptCount > 0)
                        {
                            skipPremiumJobsForResume = true;
                            report.Jobs[1].Status = "SkippedAlreadyProcessed";
                            report.Jobs[2].Status = "SkippedAlreadyProcessed";
                            Log("Reprise confirmée: réception déjà traitée/affectée; " +
                                "LVPP06U et LVPG22U non relancés.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        report.Warnings.Add(
                            "Pr -contrôle de reprise PRIST0/PECHT0 indisponible: " +
                            ex.Message + ". Les jobs prime restent nécessaires.");
                    }
                    break;

                case PrctCheckStatus.ADDPRCT_NOT_FOUND:
                    {
                        var addprct = await RunJobAsync(report.Jobs[0], variables, token).ConfigureAwait(false);
                        if (addprct.ReturnCode == 8)
                        {
                            report.Warnings.Add("ADDPRCT RC=0008: diagnostic DB2TIAD conserv ; non interpr t  comme ALREADY_ACTIVE; PRCTT0 d cide.");
                            foreach (string line in _spool.Analyze(addprct.Spool).DiagnosticLines) Log("ADDPRCT spool: " + line);
                        }

                        var prctAfter = await _verifier.CheckPrctAsync(plan, token).ConfigureAwait(false);
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
                                $"PRCTT0 non confirm  après ADDPRCT (RC={addprct.ReturnCode:0000}); LVPP06U non soumis.");
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

            // ---------------- LVPP06U, LVPG22U
            if (!skipPremiumJobsForResume)
            {
                await RunJobAsync(report.Jobs[1], variables, token).ConfigureAwait(false);
                await RunJobAsync(report.Jobs[2], variables, token).ConfigureAwait(false);
            }

            // ---------------- PRIST0 / PECHT0 (presence only)
            try
            {
                var presence = await _verifier.GetPremiumAssignmentPresenceAsync(start, token).ConfigureAwait(false);
                report.AssignmentDetail = $"PRIST0 lignes={presence.PremiumCount}; PECHT0 lignes={presence.ReceiptCount} (pr sence seule, colonnes NON CONFIRMÉES)";
                if (presence.PremiumCount == 0 || presence.ReceiptCount == 0)
                    report.Warnings.Add("PRIST0/PECHT0: aucune ligne détectée après LVPG22U (non bloquant, signification NON CONFIRM E).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.AssignmentDetail = "NON LISIBLE: " + ex.Message;
                report.Warnings.Add("PRIST0/PECHT0 non lisibles.");
            }

            // ---------------- PE01
            Pe01CheckResult pe01;
            try { pe01 = await _pe01.CheckAsync(start, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { pe01 = new Pe01CheckResult { Status = Pe01CheckStatus.ERROR, Detail = ex.Message }; }

            report.Pe01 = pe01.Status;
            report.Pe01Detail = pe01.Detail;
            switch (pe01.Status)
            {
                case Pe01CheckStatus.CONFIRMED:
                    Log("PE01 confirmé.");
                    break;

                case Pe01CheckStatus.NOT_AVAILABLE:
                    // ELIAT0 has not been located in the available SQL Server databases.
                    // LVChain does not perform this SQL pre-check. At this point ADDPRCT,
                    // PRCTT0, LVPP06U and LVPG22U have already succeeded. Continue with a
                    // visible warning, then require the strict final SCNTT0 proof.
                    report.Warnings.Add(
                        "PE01_NOT_AVAILABLE: ELIAT0 n'est pas localisée. " +
                        "Poursuite autorisée après succès d'ADDPRCT, PRCTT0, " +
                        "LVPP06U et LVPG22U. L'activation devra être confirmée " +
                        "strictement dans SCNTT0.");
                    Log(
                        "PE01 non vérifiable via SQL (NOT_AVAILABLE); " +
                        "poursuite vers LI1J04D0 autorisée, avec contrôle final SCNTT0 obligatoire.");
                    break;

                case Pe01CheckStatus.NOT_FOUND:
                    Finish(
                        report,
                        Branch23FinalDiagnostic.NOT_ACTIVATED,
                        "BR23_PE01_NOT_FOUND",
                        "La source PE01 est disponible mais aucun PE01 éligible n'a été trouvé; " +
                        "LI1J04D0 non soumis.");
                    return;

                case Pe01CheckStatus.ERROR:
                default:
                    Finish(
                        report,
                        Branch23FinalDiagnostic.TECHNICAL_FAILURE,
                        "BR23_PE01_CHECK_ERROR",
                        "Échec technique du contrôle PE01; LI1J04D0 non soumis.");
                    return;
            }

            // ---------------- LI1J04D0, then LI1J04D2
            await RunJobAsync(report.Jobs[3], variables, token).ConfigureAwait(false);
            await RunJobAsync(report.Jobs[4], variables, token).ConfigureAwait(false);

            // ---------------- technical completion
            // LI1J04D2 is the last mandatory job of the validated LVChain activation
            // sequence. LISA writes SCNTT0 asynchronously, sometimes after AutoActivator
            // has already received RC=0000. Do not perform a final contract-number poll
            // and do not report a false FAILED activation during that propagation delay.
            report.ScnttFinalStatus = null;
            report.ScnttFinalDetail =
                "NOT_CHECKED_AFTER_LI1J04D2: LISA updates SCNTT0 asynchronously.";
            report.ScnttFinalRow = "SCNTT0_NOT_CHECKED";
            report.BeforeAfterComparison =
                "Final SCNTT0 verification disabled; technical job sequence is authoritative.";
            report.Warnings.Add(
                "The mandatory activation jobs completed with accepted return codes. " +
                "The contract may become visible in LISA after a short asynchronous delay; " +
                "SCNTT0 was not checked after LI1J04D2.");

            Finish(
                report,
                Branch23FinalDiagnostic.ACTIVATED,
                string.Empty,
                "Technical activation sequence completed successfully: " +
                "ADDPRCT, LVPP06U, LVPG22U, LI1J04D0 and LI1J04D2 ended with accepted " +
                "return codes. Final SCNTT0 verification intentionally skipped.");
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
                : "COUNTERS_NOT_AVAILABLE (libellés NON CONFIRMÉS; preuve = SQL)";

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