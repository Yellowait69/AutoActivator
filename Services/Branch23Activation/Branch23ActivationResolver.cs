using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoActivator.Config;

namespace AutoActivator.Services.Branch23Activation
{
    /// <summary>
    /// Pure (no I/O) resolution of Branch 23 PREMELIA parameters for BANK and BROKER_NEW_GEN:
    /// Start/End, Run Date, AMOUNT, BUCP/CMDPMT, plus pure evaluation of
    /// PRCTT0 and SCNTT0 rows. Fully unit-testable.
    /// </summary>
    public static class Branch23ActivationResolver
    {
        private static readonly DateTime TechnicalDate1900 = new DateTime(1900, 1, 1);

        // =============================================================
        // START / END
        // =============================================================

        /// <summary>
        /// 182-2867013-42 -> 182286701342 -> drop 1st digit and 2 check digits -> 822867013.
        /// </summary>
        public static Branch23StartEnd ResolveStartEnd(string? fullContractNumber)
        {
            string raw = (fullContractNumber ?? string.Empty).Trim();
            string digits = raw.Replace("-", string.Empty).Replace(" ", string.Empty);

            if (digits.Length != 12 || !digits.All(char.IsAsciiDigit))
            {
                throw new Branch23ActivationException(
                    "BR23_INVALID_CONTRACT_NUMBER",
                    $"Contract number '{raw}' is not a 3-7-2 number with exactly 12 digits.");
            }

            string technical = digits.Substring(1, 9);

            if (technical.Length != 9 || !technical.All(char.IsAsciiDigit))
            {
                throw new Branch23ActivationException(
                    "BR23_INVALID_CONTRACT_NUMBER",
                    $"Conversion of '{raw}' did not produce nine digits.");
            }

            return new Branch23StartEnd
            {
                FullContractNumber = raw,
                DigitsOnly = digits,
                Start = technical,
                End = technical,
                Rule = "IT5UCONLREFEXN without dashes, first digit and two check digits removed (Start = End)"
            };
        }

        // =============================================================
        // RUN DATE
        // =============================================================

        public static bool IsValidBusinessDate(DateTime? value) =>
            value.HasValue && value.Value.Date > TechnicalDate1900;

        public static Branch23RunDate ResolveRunDate(
            DateTime? paymentDate,
            DateTime? firstPremiumDate,
            DateTime? contractStartDate)
        {
            string rejection = GetPaymentDateRejection(paymentDate, firstPremiumDate, contractStartDate);
            var warnings = new List<string>();

            if (rejection.Length == 0)
            {
                return Build(paymentDate!.Value, "DateVersement",
                    "DateVersement is valid (> 1900-01-01) and consistent with DateDebutContrat and DatePremierePrime.");
            }

            if (IsValidBusinessDate(paymentDate))
            {
                warnings.Add(
                    $"BR23_PAYMENT_DATE_INCONSISTENT: DateVersement={Display(paymentDate)} rejected ({rejection}); " +
                    $"DatePremierePrime={Display(firstPremiumDate)}; DateDebutContrat={Display(contractStartDate)}.");
            }

            if (IsValidBusinessDate(firstPremiumDate))
            {
                return Build(firstPremiumDate!.Value, "DatePremierePrime",
                    $"DateVersement {Display(paymentDate)} rejected: {rejection}; DatePremierePrime used.");
            }

            if (IsValidBusinessDate(contractStartDate))
            {
                return Build(contractStartDate!.Value, "DateDebutContrat",
                    $"DateVersement {Display(paymentDate)} rejected: {rejection}; DatePremierePrime {Display(firstPremiumDate)} is technical/absent; DateDebutContrat used.");
            }

            throw new Branch23ActivationException(
                "BR23_RUN_DATE_UNRESOLVED",
                $"No valid business date: DVRS={Display(paymentDate)}, DPMRPAI={Display(firstPremiumDate)}, DDEB={Display(contractStartDate)}. " +
                "The current date is never used as a fallback.");

            Branch23RunDate Build(DateTime date, string source, string reason) => new Branch23RunDate
            {
                RunDate = date.Date,
                Source = source,
                Reason = reason,
                PaymentDate = paymentDate,
                FirstPremiumDate = firstPremiumDate,
                ContractStartDate = contractStartDate,
                PaymentDateRejection = rejection,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Empty when DateVersement is a valid candidate; otherwise the rejection reason.
        /// </summary>
        public static string GetPaymentDateRejection(
            DateTime? paymentDate,
            DateTime? firstPremiumDate,
            DateTime? contractStartDate)
        {
            if (!IsValidBusinessDate(paymentDate))
                return "date technique ou absente (<= 1900-01-01)";

            if (IsValidBusinessDate(contractStartDate) && paymentDate!.Value.Date < contractStartDate!.Value.Date)
                return "date antérieure au début du contrat";

            if (IsValidBusinessDate(firstPremiumDate) && paymentDate!.Value.Date < firstPremiumDate!.Value.Date)
                return "date antérieure à la date de première prime";

            return string.Empty;
        }

        /// <summary>All JCL date variables derived from the Run Date.</summary>
        public static Dictionary<string, string> BuildDateVariables(DateTime runDate)
        {
            DateTime d = runDate.Date;
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DRUN"] = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["CYMD"] = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["YYMMDD"] = d.ToString("yyMMdd", CultureInfo.InvariantCulture),
                ["MMDD"] = d.ToString("MMdd", CultureInfo.InvariantCulture),
                ["YYYY"] = d.ToString("yyyy", CultureInfo.InvariantCulture),
                ["MM"] = d.ToString("MM", CultureInfo.InvariantCulture),
                ["DD"] = d.ToString("dd", CultureInfo.InvariantCulture)
            };
        }

        // =============================================================
        // PREMIUM / AMOUNT
        // =============================================================

        public static Branch23PremiumSelection SelectConclusionPremium(
            IReadOnlyList<Branch23PremiumRow> rows)
        {
            rows ??= Array.Empty<Branch23PremiumRow>();

            if (rows.Count == 0)
            {
                throw new Branch23ActivationException(
                    "BR23_PREMIUM_MISSING", "No row in FJ1.TB5UPRP for this ELIA contract.");
            }

            var rejected = new List<string>();
            var candidates = new List<Branch23PremiumRow>();

            foreach (var row in rows)
            {
                string? reason =
                    !row.GrossPremium.HasValue ? "IT5UPRPUBRU is NULL" :
                    row.GrossPremium.Value <= 0m ? "IT5UPRPUBRU is not strictly positive" :
                    NormalizeCode(row.PremiumGroup) != Settings.Branch23Activation.ConclusionPremiumGroup
                        ? $"premium group {row.PremiumGroup} != {Settings.Branch23Activation.ConclusionPremiumGroup}" :
                    NormalizeCode(row.PremiumType) != Settings.Branch23Activation.ConclusionPremiumType
                        ? $"premium type {row.PremiumType} != {Settings.Branch23Activation.ConclusionPremiumType}" :
                    IsValidBusinessDate(row.FirstPremiumDate) && IsValidBusinessDate(row.ContractStartDate) &&
                    row.FirstPremiumDate!.Value.Date < row.ContractStartDate!.Value.Date
                        ? "first premium date is earlier than the contract start date" :
                    null;

                if (reason == null) candidates.Add(row);
                else rejected.Add($"{row} -> rejected: {reason}");
            }

            if (candidates.Count == 0)
            {
                throw new Branch23ActivationException(
                    "BR23_PREMIUM_MISSING",
                    "No conclusion premium candidate (group 01, type 02, gross > 0). Rows: " +
                    string.Join(" | ", rejected));
            }

            if (candidates.Count > 1)
            {
                throw new Branch23ActivationException(
                    "BR23_PREMIUM_AMBIGUOUS",
                    $"{candidates.Count} conclusion premium candidates; none is chosen arbitrarily. Candidates: " +
                    string.Join(" | ", candidates.Select(c => c.ToString())));
            }

            var selected = candidates[0];

            return new Branch23PremiumSelection
            {
                Selected = selected,
                TotalRows = rows.Count,
                Candidates = candidates.Select(c => c.ToString()).ToList(),
                Rejected = rejected,
                Amount = FormatAmount(selected.GrossPremium!.Value),
                Reason = "Single TB5UPRP row with IT5UPRPUBRU > 0, group 01, type 02."
            };
        }

        /// <summary>Invariant format with a dot separator: 5000.00.</summary>
        public static string FormatAmount(decimal amount) =>
            amount.ToString("0.00", CultureInfo.InvariantCulture);

        // =============================================================
        // BUCP / CMDPMT
        // =============================================================

        public static Branch23PaymentChannel IdentifyChannel(
            string? bucp,
            string? cmdpmt)
        {
            return Branch23PaymentParameters.ResolveFromPaymentPair(
                bucp,
                cmdpmt).Channel;
        }

        public static Branch23PaymentParameters ResolvePaymentParameters(
            string? channelCode)
        {
            Branch23PaymentParameters result =
                Branch23PaymentParameters.ResolveFromChannelCode(channelCode);

            if (!result.IsResolved)
            {
                throw new Branch23ActivationException(
                    "BR23_CHANNEL_NOT_SUPPORTED",
                    $"ELIA ChannelCode '{NormalizeCode(channelCode)}' is not " +
                    "mapped to BANK or BROKER_NEW_GEN.");
            }

            return result;
        }

        public static Branch23PaymentParameters ResolvePaymentParameters(
            Branch23PaymentChannel channel)
        {
            return channel switch
            {
                Branch23PaymentChannel.Bank =>
                    Branch23PaymentParameters.ResolveFromPaymentPair("00382", "8"),
                Branch23PaymentChannel.BrokerNewGeneration =>
                    Branch23PaymentParameters.ResolveFromPaymentPair("80819", "H"),
                _ => throw new Branch23ActivationException(
                    "BR23_CHANNEL_NOT_SUPPORTED",
                    $"Channel '{channel}' is not supported. Supported routes are " +
                    "BANK 00382/8 and BROKER_NEW_GEN 80819/H.")
            };
        }

        public static Branch23PaymentParameters ResolvePaymentParameters(
            string? channelCode,
            string? bucp,
            string? cmdpmt,
            bool overrideApplied)
        {
            Branch23PaymentParameters expected =
                ResolvePaymentParameters(channelCode);
            Branch23PaymentParameters supplied =
                Branch23PaymentParameters.ResolveFromPaymentPair(
                    bucp,
                    cmdpmt,
                    channelCode,
                    overrideApplied);

            if (!supplied.IsResolved || supplied.Channel != expected.Channel)
            {
                throw new Branch23ActivationException(
                    "BR23_PAYMENT_ROUTING_MISMATCH",
                    $"ChannelCode {expected.ChannelCode} expects " +
                    $"{expected.Bucp}/{expected.Cmdpmt} " +
                    $"({expected.BusinessChannelName}), but received " +
                    $"{supplied.NormalizedBucp}/{supplied.NormalizedCmdpmt}.");
            }

            return new Branch23PaymentParameters
            {
                Channel = supplied.Channel,
                ChannelCode = expected.ChannelCode,
                Bucp = supplied.Bucp,
                Cmdpmt = supplied.Cmdpmt,
                RoutingRule = expected.RoutingRule,
                RoutingSource = supplied.RoutingSource,
                Source = supplied.Source,
                OverrideApplied = supplied.OverrideApplied,
                OriginalBucp = supplied.OriginalBucp,
                OriginalCmdpmt = supplied.OriginalCmdpmt
            };
        }

        // =============================================================
        // PRCTT0 EVALUATION
        // =============================================================

        public static PrctCheckResult EvaluatePrct(
            IReadOnlyList<PrctRow> rows,
            string start,
            DateTime runDate,
            decimal amount,
            string bucp,
            string cmdpmt)
        {
            rows ??= Array.Empty<PrctRow>();
            decimal contract = decimal.Parse(start, CultureInfo.InvariantCulture);
            decimal expectedBucp = decimal.Parse(bucp, CultureInfo.InvariantCulture);

            var sameContract = rows.Where(r => r.ContractNumber == contract).ToList();

            bool Matches(PrctRow r) =>
                string.Equals(r.Ste.Trim(), Settings.Branch23Activation.Ste, StringComparison.Ordinal) &&
                string.Equals(r.PaymentMode.Trim(), cmdpmt.Trim(), StringComparison.OrdinalIgnoreCase) &&
                r.ReferenceDate.HasValue && r.ReferenceDate.Value.Date == runDate.Date &&
                r.Amount.HasValue && r.Amount.Value == amount &&
                r.Bucp.HasValue && r.Bucp.Value == expectedBucp;

            var matching = sameContract.Where(Matches).ToList();
            string expected =
                $"expected C_STE={Settings.Branch23Activation.Ste}, NO_CNT={start}, C_MD_PMT={cmdpmt}, " +
                $"D_REF_PRM={runDate:yyyy-MM-dd}, M_PAY={FormatAmount(amount)}, NO_BISM_BUCP={bucp}";

            // All rows of the contract are analysed first: receipts for the same amount
            // with different payment modes are conflicting and never auto-selected.
            var sameAmount = sameContract.Where(r => r.Amount.HasValue && r.Amount.Value == amount).ToList();
            int distinctModes = sameAmount
                .Select(r => (r.PaymentMode ?? string.Empty).Trim().ToUpperInvariant())
                .Distinct()
                .Count();

            if (sameAmount.Count > 1 && distinctModes > 1)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_CONFLICTING_RECEIPTS,
                    MatchingRows = matching,
                    AllRows = sameContract,
                    Detail = $"{sameAmount.Count} conflicting receipts for NO_CNT={start}, M_PAY={FormatAmount(amount)} " +
                             $"with different payment modes: {string.Join(" | ", sameAmount)} ({expected}). No automatic selection."
                };
            }

            if (matching.Count == 1)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_CONFIRMED,
                    MatchingRows = matching,
                    AllRows = sameContract,
                    Detail = "Receipt confirmed: " + matching[0]
                };
            }

            if (matching.Count > 1)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_DUPLICATE,
                    MatchingRows = matching,
                    AllRows = sameContract,
                    Detail = $"{matching.Count} identical receipts ({expected})."
                };
            }

            var sameDate = sameContract
                .Where(r => r.ReferenceDate.HasValue && r.ReferenceDate.Value.Date == runDate.Date)
                .ToList();

            if (sameDate.Count == 1)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_MISMATCH,
                    AllRows = sameContract,
                    Detail = $"Receipt found on {runDate:yyyy-MM-dd} with different values: {sameDate[0]} ({expected})."
                };
            }

            if (sameDate.Count > 1)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_AMBIGUOUS,
                    AllRows = sameContract,
                    Detail = $"{sameDate.Count} non-matching receipts on {runDate:yyyy-MM-dd} ({expected})."
                };
            }

            return new PrctCheckResult
            {
                Status = PrctCheckStatus.ADDPRCT_NOT_FOUND,
                AllRows = sameContract,
                Detail = $"No receipt for NO_CNT={start} on {runDate:yyyy-MM-dd} ({expected})."
            };
        }

        // =============================================================
        // SCNTT0 EVALUATION
        // =============================================================

        /// <param name="rows">Rows of SCNTT0 for NO_CNT after LI1J04D2/D3.</param>
        /// <param name="stateBefore">C_ETAT_CNT before the workflow, null if the contract was absent.</param>
        public static ScnttCheckResult EvaluateScntt(
            IReadOnlyList<ScnttRow> rows,
            string? stateBefore)
        {
            rows ??= Array.Empty<ScnttRow>();

            if (rows.Count > 1)
            {
                return new ScnttCheckResult
                {
                    Status = ContractActivationStatus.AMBIGUOUS,
                    Detail = $"{rows.Count} rows in LV.SCNTT0 for the same NO_CNT."
                };
            }

            if (rows.Count == 0)
            {
                return new ScnttCheckResult
                {
                    Status = ContractActivationStatus.TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT,
                    Detail = "All jobs ended with accepted return codes, but the contract is absent from LV.SCNTT0."
                };
            }

            var row = rows[0];
            string state = row.State.Trim();

            if (!string.Equals(row.Ste.Trim(), Settings.Branch23Activation.Ste, StringComparison.Ordinal))
            {
                return new ScnttCheckResult
                {
                    Status = ContractActivationStatus.AMBIGUOUS,
                    Row = row,
                    Detail = $"C_STE={row.Ste} instead of {Settings.Branch23Activation.Ste}."
                };
            }

            if (state != Settings.Branch23Activation.ActiveContractState)
            {
                bool unchanged = stateBefore != null && string.Equals(stateBefore.Trim(), state, StringComparison.Ordinal);
                return new ScnttCheckResult
                {
                    Status = unchanged
                        ? ContractActivationStatus.TECHNICAL_SUCCESS_WITHOUT_BUSINESS_EFFECT
                        : ContractActivationStatus.NOT_ACTIVATED,
                    Row = row,
                    Detail = $"C_ETAT_CNT={state} (expected {Settings.Branch23Activation.ActiveContractState}); state before={stateBefore ?? "absent"}."
                };
            }

            var blocks = new List<string>();
            if (!IsZero(row.ErrorMessages)) blocks.Add($"C_MSGS_ERR={row.ErrorMessages}");
            if (!string.Equals(row.PremiumBlocked.Trim(), "N", StringComparison.OrdinalIgnoreCase)) blocks.Add($"C_BLOQ_PRM={row.PremiumBlocked}");
            if (!string.Equals(row.InvalidRequest.Trim(), "N", StringComparison.OrdinalIgnoreCase)) blocks.Add($"C_DEM_INVAL={row.InvalidRequest}");
            if (!IsZero(row.MovementBlocked)) blocks.Add($"C_BLOQ_MVT={row.MovementBlocked}");

            if (blocks.Count > 0)
            {
                return new ScnttCheckResult
                {
                    Status = ContractActivationStatus.BLOCKED,
                    Row = row,
                    Detail = "C_ETAT_CNT=4 but blocked: " + string.Join(", ", blocks)
                };
            }

            return new ScnttCheckResult
            {
                Status = ContractActivationStatus.ACTIVATED,
                Row = row,
                Detail = "C_STE=A, C_ETAT_CNT=4, C_MSGS_ERR=0, C_BLOQ_PRM=N, C_DEM_INVAL=N, C_BLOQ_MVT=0."
            };
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private static string NormalizeCode(string? code)
        {
            string value = (code ?? string.Empty).Trim();
            return value.Length == 1 ? "0" + value : value;
        }

        private static bool IsZero(string? value)
        {
            string v = (value ?? string.Empty).Trim();
            return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && d == 0m;
        }

        private static string Display(DateTime? value) =>
            value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "NULL";
    }
}