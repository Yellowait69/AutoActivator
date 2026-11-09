using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;

namespace AutoActivator.Services.Branch23Activation
{
    public enum Branch23Database
    {
        /// <summary>FJ5LIFE_x000 / FJ1 (ELIA).</summary>
        Elia,
        /// <summary>LV000PD0_x000 / LV (PRCTT0, PRIST0, PECHT0).</summary>
        LisaPayment,
        /// <summary>LV000SD0_x000 / LV (SCNTT0).</summary>
        LisaContract
    }

    /// <summary>Read-only SQL access, mockable in unit tests.</summary>
    public interface IBranch23SqlReader
    {
        string GetDatabaseName(Branch23Database database);

        Task<DataTable> QueryAsync(
            Branch23Database database,
            string sql,
            Dictionary<string, object> parameters,
            CancellationToken token);
    }

    /// <summary>
    /// Real implementation: one explicit connection per database.
    /// Never uses FJ0AGDB nor DG0001D0 for these checks.
    /// </summary>
    public sealed class Branch23SqlReader : IBranch23SqlReader
    {
        private readonly string _environment;

        public Branch23SqlReader(string envSuffix)
        {
            _environment = Settings.NormalizeEnvironmentSuffix(envSuffix);
        }

        public string GetDatabaseName(Branch23Database database) => database switch
        {
            Branch23Database.Elia => Settings.DbConfig.GetLifeDatabase(_environment),
            Branch23Database.LisaPayment => Settings.DbConfig.GetLisaPaymentDatabase(_environment),
            Branch23Database.LisaContract => Settings.DbConfig.GetLisaContractDatabase(_environment),
            _ => throw new ArgumentOutOfRangeException(nameof(database))
        };

        public Task<DataTable> QueryAsync(
            Branch23Database database,
            string sql,
            Dictionary<string, object> parameters,
            CancellationToken token)
        {
            var manager = new DatabaseManager(_environment, GetDatabaseName(database));
            return manager.GetDataAsync(sql, parameters, token);
        }
    }

    /// <summary>
    /// Read-only SQL queries of the Branch 23 activation flow.
    /// Tables are referenced through the explicit database connection.
    /// </summary>
    public static class Branch23ActivationQueries
    {
        public const string EliaContractByDemand = @"
SELECT
    h.IT5HDMDAIDN AS DemandId,
    u.IT5UCONAIDN AS EliaId,
    LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS NumeroContrat
FROM [FJ1].[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN [FJ1].[TB5UCON] AS u WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId;";

        public const string EliaContractByNumber = @"
SELECT
    h.IT5HDMDAIDN AS DemandId,
    u.IT5UCONAIDN AS EliaId,
    LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS NumeroContrat
FROM [FJ1].[TB5UCON] AS u WITH (NOLOCK)
LEFT JOIN [FJ1].[TB5HELT] AS h WITH (NOLOCK)
    ON h.IT5UCONAIDN = u.IT5UCONAIDN
WHERE REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', '') = REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', '');";

        public const string EliaDemandDetails = @"
SELECT TOP (1)
    d.IT5HDMDAIDN AS DemandId,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCPRD))), '') AS ProductCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCPCS))), '') AS ProcessCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCCDI))), '') AS ChannelCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCAPL))), '') AS ApplicationChannel,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCSTA))), '') AS DemandStatus,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), p.IT5UPMPCMDEPAI))), '') AS PaymentMethodCode
FROM [FJ1].[TB5HDMD] AS d WITH (NOLOCK)
LEFT JOIN [FJ1].[TB5HELT] AS h WITH (NOLOCK)
    ON h.IT5HDMDAIDN = d.IT5HDMDAIDN
LEFT JOIN [FJ1].[TB5UPMP] AS p WITH (NOLOCK)
    ON p.IT5UCONAIDN = h.IT5UCONAIDN
WHERE d.IT5HDMDAIDN = @DemandId
ORDER BY p.IT5UAVENSEQ DESC;";

        public const string EliaPremiums = @"
SELECT
    p.IT5UCONAIDN,
    p.IT5UAVENSEQ,
    p.IT5UGARCGAR,
    p.IT5UPRPCTYPGRPPRI,
    p.IT5UPRPCTYPPRI,
    p.IT5UPRPUBRU AS PrimeBrute,
    p.IT5UPRPUNET AS PrimeNette,
    p.IT5UPRPUTAX AS Taxe,
    p.IT5UPRPDPMRPAI AS DatePremierePrime,
    p.IT5UPRPDVRS AS DateVersement,
    u.IT5UCONDDEB AS DateDebutContrat,
    LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS NumeroContrat
FROM [FJ1].[TB5UPRP] AS p WITH (NOLOCK)
INNER JOIN [FJ1].[TB5UCON] AS u WITH (NOLOCK)
    ON u.IT5UCONAIDN = p.IT5UCONAIDN
WHERE p.IT5UCONAIDN = @EliaId
ORDER BY p.IT5UAVENSEQ, p.IT5UPRPCTYPPRI, p.IT5UGARCGAR;";

        public const string PrctRows = @"
SELECT
    C_STE, NO_CNT, C_MD_PMT, D_REF_PRM, NO_ORD_RCP, M_PAY, NO_BISM_BUCP,
    C_ETAT_RCP, TSTAMP_CRT_RCT, NM_AUTEUR_CRT, NM_JOB_DMOD, D_JOB_DMOD
FROM [LV].[PRCTT0] WITH (NOLOCK)
WHERE NO_CNT = @CoInterne
ORDER BY D_REF_PRM DESC, TSTAMP_CRT_RCT DESC;";

        /// <summary>
        /// Presence of premium (PRIST0) and receipt (PECHT0) rows.
        /// Only C_STE / NO_CNT are used: the meaning of other columns
        /// (e.g. D_AF_QUITT) is NOT CONFIRMED.
        /// </summary>
        public const string PremiumAssignmentPresence = @"
SELECT
    (SELECT COUNT_BIG(*) FROM [LV].[PRIST0] WITH (NOLOCK) WHERE C_STE = @Company AND NO_CNT = @CoInterne) AS PremiumCount,
    (SELECT COUNT_BIG(*) FROM [LV].[PECHT0] WITH (NOLOCK) WHERE C_STE = @Company AND NO_CNT = @CoInterne) AS ReceiptCount;";

        public const string ScnttStatus = @"
SELECT
    C_STE, NO_CNT, LTRIM(RTRIM(NO_CNT_EXTENDED)) AS NumeroContrat,
    C_ETAT_CNT, D_ETAT_CNT, C_TRAIT_CNT, D_TRAIT_CNT, C_MSGS_ERR,
    C_BLOQ_PRM, C_DEM_INVAL, C_BLOQ_MVT, TSTAMP_DMOD, NM_JOB_DMOD, D_JOB_DMOD
FROM [LV].[SCNTT0] WITH (NOLOCK)
WHERE NO_CNT = @CoInterneLisa;";
    }

    /// <summary>PE01 check. LV.ELIAT0 SQL location is NOT CONFIRMED.</summary>
    public interface IPe01Checker
    {
        Task<Pe01CheckResult> CheckAsync(string start, CancellationToken token);
    }

    /// <summary>
    /// Default PE01 checker: returns NOT_AVAILABLE (never CONFIRMED)
    /// until the location of LV.ELIAT0 is confirmed.
    /// </summary>
    public sealed class UnavailablePe01Checker : IPe01Checker
    {
        public Task<Pe01CheckResult> CheckAsync(string start, CancellationToken token) =>
            Task.FromResult(new Pe01CheckResult
            {
                Status = Pe01CheckStatus.NOT_AVAILABLE,
                Detail = "LV.ELIAT0 is not accessible from SQL Server (location NOT CONFIRMED). PE01 not checked directly."
            });
    }

    /// <summary>
    /// Extensible spool analyzer. No counter label is confirmed yet, so by
    /// default it returns COUNTERS_NOT_AVAILABLE and never converts a
    /// missing counter into zero.
    /// </summary>
    public sealed class SpoolCounterAnalyzer
    {
        private readonly IReadOnlyDictionary<string, Regex> _patterns;

        public SpoolCounterAnalyzer(IReadOnlyDictionary<string, Regex>? confirmedPatterns = null)
        {
            _patterns = confirmedPatterns ?? new Dictionary<string, Regex>();
        }

        public SpoolCounterResult Analyze(string? spool)
        {
            var lines = (spool ?? string.Empty)
                .Replace("\r", string.Empty)
                .Split('\n')
                .Where(l => Regex.IsMatch(l, @"\b(RECORD|RECORDS|ENREG|LUS|READ|WRITTEN|INSERT|REJECT|TOTAL|COUNT)\b", RegexOptions.IgnoreCase))
                .Select(l => l.Trim())
                .Take(50)
                .ToList();

            var counters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in _patterns)
            {
                var match = pattern.Value.Match(spool ?? string.Empty);
                if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                {
                    counters[pattern.Key] = value;
                }
            }

            return new SpoolCounterResult
            {
                Status = counters.Count > 0 ? SpoolCounterStatus.COUNTERS_FOUND : SpoolCounterStatus.COUNTERS_NOT_AVAILABLE,
                Counters = counters,
                DiagnosticLines = lines
            };
        }
    }

    public sealed record Branch23EliaDemandDetails(
        string ProductCode,
        string ProcessCode,
        string ChannelCode,
        string ApplicationChannel,
        string DemandStatus,
        string PaymentMethodCode);

    /// <summary>
    /// SQL read/verification of the Branch 23 activation flow.
    /// Automatically resolves Bank or Broker New Generation from ELIA ChannelCode.
    /// </summary>
    public sealed class Branch23ActivationVerifier
    {
        private readonly IBranch23SqlReader _sql;

        public Branch23ActivationVerifier(IBranch23SqlReader sql)
        {
            _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        }

        public IBranch23SqlReader Sql => _sql;

        // ---------------------------------------------------------- ELIA

        public async Task<(string DemandId, string EliaId, string ContractNumber)> ResolveEliaContractAsync(
            string? demandId, string? contractNumber, CancellationToken token)
        {
            DataTable table;
            if (!string.IsNullOrWhiteSpace(demandId) && demandId != "N/A")
            {
                table = await _sql.QueryAsync(Branch23Database.Elia, Branch23ActivationQueries.EliaContractByDemand,
                    new Dictionary<string, object> { ["@DemandId"] = demandId.Trim() }, token).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(contractNumber))
            {
                table = await _sql.QueryAsync(Branch23Database.Elia, Branch23ActivationQueries.EliaContractByNumber,
                    new Dictionary<string, object> { ["@ContractNumber"] = contractNumber.Trim() }, token).ConfigureAwait(false);
            }
            else
            {
                throw new Branch23ActivationException("BR23_ELIA_CONTRACT_NOT_FOUND", "No Demand ID or contract number provided.");
            }

            var rows = table.Rows.Cast<DataRow>()
                .Select(r => (Demand: Str(r, "DemandId"), Elia: Str(r, "EliaId"), Number: Str(r, "NumeroContrat")))
                .Where(r => r.Elia.Length > 0)
                .Distinct()
                .ToList();

            if (rows.Count == 0)
                throw new Branch23ActivationException("BR23_ELIA_CONTRACT_NOT_FOUND", "Contract not found in FJ1.TB5HELT / FJ1.TB5UCON.");

            if (rows.Select(r => r.Elia).Distinct().Count() > 1)
                throw new Branch23ActivationException("BR23_ELIA_CONTRACT_AMBIGUOUS",
                    "Several ELIA contracts match: " + string.Join(", ", rows.Select(r => r.Elia).Distinct()));

            var first = rows[0];
            if (first.Number.Length == 0)
                throw new Branch23ActivationException("BR23_ELIA_CONTRACT_NUMBER_MISSING", "TB5UCON.IT5UCONLREFEXN is empty.");

            return (first.Demand, first.Elia, first.Number);
        }

        public async Task<IReadOnlyList<Branch23PremiumRow>> GetPremiumRowsAsync(string eliaId, CancellationToken token)
        {
            var table = await _sql.QueryAsync(Branch23Database.Elia, Branch23ActivationQueries.EliaPremiums,
                new Dictionary<string, object> { ["@EliaId"] = eliaId }, token).ConfigureAwait(false);

            return table.Rows.Cast<DataRow>().Select(r => new Branch23PremiumRow
            {
                EliaId = Str(r, "IT5UCONAIDN"),
                AmendmentSequence = Str(r, "IT5UAVENSEQ"),
                Guarantee = Str(r, "IT5UGARCGAR"),
                PremiumGroup = Str(r, "IT5UPRPCTYPGRPPRI"),
                PremiumType = Str(r, "IT5UPRPCTYPPRI"),
                GrossPremium = Dec(r, "PrimeBrute"),
                NetPremium = Dec(r, "PrimeNette"),
                Tax = Dec(r, "Taxe"),
                FirstPremiumDate = Date(r, "DatePremierePrime"),
                PaymentDate = Date(r, "DateVersement"),
                ContractStartDate = Date(r, "DateDebutContrat"),
                ContractNumber = Str(r, "NumeroContrat")
            }).ToList();
        }

        public async Task<Branch23EliaDemandDetails> GetDemandDetailsAsync(
            string demandId,
            CancellationToken token)
        {
            DataTable table = await _sql.QueryAsync(
                    Branch23Database.Elia,
                    Branch23ActivationQueries.EliaDemandDetails,
                    new Dictionary<string, object>
                    {
                        ["@DemandId"] = demandId
                    },
                    token)
                .ConfigureAwait(false);

            if (table.Rows.Count == 0)
            {
                throw new Branch23ActivationException(
                    "BR23_DEMAND_DETAILS_NOT_FOUND",
                    $"No TB5HDMD data was found for Demand ID {demandId}.");
            }

            DataRow row = table.Rows[0];
            return new Branch23EliaDemandDetails(
                Str(row, "ProductCode"),
                Str(row, "ProcessCode"),
                Str(row, "ChannelCode"),
                Str(row, "ApplicationChannel"),
                Str(row, "DemandStatus"),
                Str(row, "PaymentMethodCode"));
        }

        /// <summary>
        /// ELIA -> complete plan (Start/End, Run Date, AMOUNT, BUCP/CMDPMT).
        /// </summary>
        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            Branch23PaymentChannel channel,
            CancellationToken token)
        {
            // Compatibility overload. The ELIA ChannelCode remains authoritative.
            Branch23ActivationPlan plan = await BuildPlanAsync(
                    demandId,
                    contractNumber,
                    token)
                .ConfigureAwait(false);

            if (channel != Branch23PaymentChannel.Unknown &&
                channel != plan.Payment.Channel)
            {
                throw new Branch23ActivationException(
                    "BR23_PAYMENT_ROUTING_MISMATCH",
                    $"Requested channel {channel} conflicts with ELIA " +
                    $"ChannelCode {plan.ChannelCode} resolved to " +
                    $"{plan.Payment.BusinessChannelName}.");
            }

            return plan;
        }

        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            CancellationToken token)
        {
            var elia = await ResolveEliaContractAsync(
                    demandId,
                    contractNumber,
                    token)
                .ConfigureAwait(false);

            Branch23EliaDemandDetails details =
                await GetDemandDetailsAsync(elia.DemandId, token)
                    .ConfigureAwait(false);

            Branch23PaymentParameters payment =
                Branch23ActivationResolver.ResolvePaymentParameters(
                    details.ChannelCode);

            Branch23StartEnd startEnd =
                Branch23ActivationResolver.ResolveStartEnd(elia.ContractNumber);
            IReadOnlyList<Branch23PremiumRow> rows =
                await GetPremiumRowsAsync(elia.EliaId, token)
                    .ConfigureAwait(false);
            Branch23PremiumSelection premium =
                Branch23ActivationResolver.SelectConclusionPremium(rows);
            Branch23PremiumRow selected = premium.Selected;
            Branch23RunDate runDate =
                Branch23ActivationResolver.ResolveRunDate(
                    selected.PaymentDate,
                    selected.FirstPremiumDate,
                    selected.ContractStartDate);

            return new Branch23ActivationPlan
            {
                DemandId = elia.DemandId,
                EliaId = elia.EliaId,
                ProductCode = details.ProductCode,
                ProcessCode = details.ProcessCode,
                ChannelCode = details.ChannelCode,
                ApplicationChannel = details.ApplicationChannel,
                StartEnd = startEnd,
                RunDate = runDate,
                Premium = premium,
                Payment = payment,
                Diagnostic =
                    $"ChannelCode={details.ChannelCode}; " +
                    $"Channel={payment.BusinessChannelName}; " +
                    $"BUCP/CMDPMT={payment.Bucp}/{payment.Cmdpmt}; " +
                    $"Rule={payment.RoutingRule}; " +
                    $"PaymentMethodCode={details.PaymentMethodCode} " +
                    "(informational only)"
            };
        }

        // ------------------------------------------------------- PRCTT0

        public async Task<PrctCheckResult> CheckPrctAsync(Branch23ActivationPlan plan, CancellationToken token)
        {
            try
            {
                var table = await _sql.QueryAsync(Branch23Database.LisaPayment, Branch23ActivationQueries.PrctRows,
                    new Dictionary<string, object>
                    {
                        ["@CoInterne"] = decimal.Parse(plan.StartEnd.Start, CultureInfo.InvariantCulture)
                    }, token).ConfigureAwait(false);

                var rows = table.Rows.Cast<DataRow>().Select(r => new PrctRow
                {
                    Ste = Str(r, "C_STE"),
                    ContractNumber = Dec(r, "NO_CNT"),
                    PaymentMode = Str(r, "C_MD_PMT"),
                    ReferenceDate = Date(r, "D_REF_PRM"),
                    Amount = Dec(r, "M_PAY"),
                    Bucp = Dec(r, "NO_BISM_BUCP"),
                    ReceiptState = Str(r, "C_ETAT_RCP"),
                    ReceiptOrder = Str(r, "NO_ORD_RCP"),
                    CreatedTimestamp = Str(r, "TSTAMP_CRT_RCT"),
                    CreatedBy = Str(r, "NM_AUTEUR_CRT"),
                    LastJob = Str(r, "NM_JOB_DMOD"),
                    LastJobDate = Str(r, "D_JOB_DMOD")
                }).ToList();

                return Branch23ActivationResolver.EvaluatePrct(
                    rows,
                    plan.StartEnd.Start,
                    plan.RunDate.RunDate,
                    plan.Premium.Selected.GrossPremium!.Value,
                    plan.Payment.Bucp,
                    plan.Payment.Cmdpmt);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PrctCheckResult
                {
                    Status = PrctCheckStatus.ADDPRCT_SQL_ERROR,
                    Detail = $"[{_sql.GetDatabaseName(Branch23Database.LisaPayment)}].[LV].[PRCTT0]: {ex.Message}"
                };
            }
        }

        // ------------------------------------------------ PRIST0 / PECHT0

        public async Task<(long PremiumCount, long ReceiptCount)> GetPremiumAssignmentPresenceAsync(
            string start, CancellationToken token)
        {
            var table = await _sql.QueryAsync(Branch23Database.LisaPayment, Branch23ActivationQueries.PremiumAssignmentPresence,
                new Dictionary<string, object>
                {
                    ["@Company"] = Settings.Branch23Activation.Ste,
                    ["@CoInterne"] = decimal.Parse(start, CultureInfo.InvariantCulture)
                }, token).ConfigureAwait(false);

            if (table.Rows.Count != 1)
                throw new Branch23ActivationException("BR23_SQL_RESULT_INVALID", "PRIST0/PECHT0 presence query returned no summary row.");

            return (Convert.ToInt64(table.Rows[0]["PremiumCount"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(table.Rows[0]["ReceiptCount"], CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------- SCNTT0

        public async Task<IReadOnlyList<ScnttRow>> GetScnttRowsAsync(string start, CancellationToken token)
        {
            var table = await _sql.QueryAsync(Branch23Database.LisaContract, Branch23ActivationQueries.ScnttStatus,
                new Dictionary<string, object>
                {
                    ["@CoInterneLisa"] = decimal.Parse(start, CultureInfo.InvariantCulture)
                }, token).ConfigureAwait(false);

            return table.Rows.Cast<DataRow>().Select(r => new ScnttRow
            {
                Ste = Str(r, "C_STE"),
                ContractNumber = Str(r, "NO_CNT"),
                ExtendedNumber = Str(r, "NumeroContrat"),
                State = Str(r, "C_ETAT_CNT"),
                StateDate = Str(r, "D_ETAT_CNT"),
                TreatmentCode = Str(r, "C_TRAIT_CNT"),
                TreatmentDate = Str(r, "D_TRAIT_CNT"),
                ErrorMessages = Str(r, "C_MSGS_ERR"),
                PremiumBlocked = Str(r, "C_BLOQ_PRM"),
                InvalidRequest = Str(r, "C_DEM_INVAL"),
                MovementBlocked = Str(r, "C_BLOQ_MVT"),
                ModificationTimestamp = Str(r, "TSTAMP_DMOD"),
                LastJob = Str(r, "NM_JOB_DMOD"),
                LastJobDate = Str(r, "D_JOB_DMOD")
            }).ToList();
        }

        public async Task<ScnttCheckResult> CheckScnttAsync(string start, string? stateBefore, CancellationToken token)
        {
            try
            {
                var rows = await GetScnttRowsAsync(start, token).ConfigureAwait(false);
                return Branch23ActivationResolver.EvaluateScntt(rows, stateBefore);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ScnttCheckResult
                {
                    Status = ContractActivationStatus.SQL_ERROR,
                    Detail = $"[{_sql.GetDatabaseName(Branch23Database.LisaContract)}].[LV].[SCNTT0]: {ex.Message}"
                };
            }
        }

        // ------------------------------------------------------ helpers

        private static string Str(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value
                ? Convert.ToString(row[column], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
                : string.Empty;

        private static decimal? Dec(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value
                ? Convert.ToDecimal(row[column], CultureInfo.InvariantCulture)
                : null;

        private static DateTime? Date(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value
                ? Convert.ToDateTime(row[column], CultureInfo.InvariantCulture)
                : null;
    }
}