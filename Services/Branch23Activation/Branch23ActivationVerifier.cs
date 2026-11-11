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
        LisaContract,
        /// <summary>LV000FD0_x000 / LV (FMVDT0, FPLCT0).</summary>
        LisaFundMovement,
        /// <summary>LV000GD0_x000 / LV (GFCRT0, GFEVT0).</summary>
        LisaFundValuation
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
            Branch23Database.LisaFundMovement => Settings.DbConfig.GetFundMovementDatabase(_environment),
            Branch23Database.LisaFundValuation => Settings.DbConfig.GetFundValuationDatabase(_environment),
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
SELECT DISTINCT
    CONVERT(varchar(30), h.IT5HDMDAIDN) AS DemandId,
    CONVERT(varchar(30), u.IT5UCONAIDN) AS EliaId,
    LTRIM(RTRIM(CONVERT(varchar(30), u.IT5UCONLREFEXN))) AS NumeroContrat,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCSTA))), '') AS DemandStatus,
    d.IT5HDMDDCRE AS DemandCreationDate,
    h.IT5HELTDTISMAJDB2 AS LinkUpdateDate,
    TRY_CONVERT(int, h.IT5HPRONSEQ) AS ProcessSequence,
    TRY_CONVERT(int, h.IT5HELTNSEQCON) AS ContractSequence,
    TRY_CONVERT(int, h.IT5HELTNSEQAVENOU) AS AmendmentSequence
FROM [FJ1].[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN [FJ1].[TB5UCON] AS u WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
LEFT JOIN [FJ1].[TB5HDMD] AS d WITH (NOLOCK)
    ON d.IT5HDMDAIDN = h.IT5HDMDAIDN
WHERE TRY_CONVERT(decimal(15, 0), h.IT5HDMDAIDN) =
      TRY_CONVERT(decimal(15, 0), @DemandId);";

        public const string EliaContractByNumber = @"
SELECT DISTINCT
    CONVERT(varchar(30), h.IT5HDMDAIDN) AS DemandId,
    CONVERT(varchar(30), u.IT5UCONAIDN) AS EliaId,
    LTRIM(RTRIM(CONVERT(varchar(30), u.IT5UCONLREFEXN))) AS NumeroContrat,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCSTA))), '') AS DemandStatus,
    d.IT5HDMDDCRE AS DemandCreationDate,
    h.IT5HELTDTISMAJDB2 AS LinkUpdateDate,
    TRY_CONVERT(int, h.IT5HPRONSEQ) AS ProcessSequence,
    TRY_CONVERT(int, h.IT5HELTNSEQCON) AS ContractSequence,
    TRY_CONVERT(int, h.IT5HELTNSEQAVENOU) AS AmendmentSequence
FROM [FJ1].[TB5UCON] AS u WITH (NOLOCK)
LEFT JOIN [FJ1].[TB5HELT] AS h WITH (NOLOCK)
    ON h.IT5UCONAIDN = u.IT5UCONAIDN
LEFT JOIN [FJ1].[TB5HDMD] AS d WITH (NOLOCK)
    ON d.IT5HDMDAIDN = h.IT5HDMDAIDN
WHERE REPLACE(REPLACE(REPLACE(
          LTRIM(RTRIM(CONVERT(varchar(30), u.IT5UCONLREFEXN))),
          '-', ''), ' ', ''), '.', '') =
      REPLACE(REPLACE(REPLACE(
          LTRIM(RTRIM(@ContractNumber)),
          '-', ''), ' ', ''), '.', '')
   OR SUBSTRING(REPLACE(REPLACE(REPLACE(
          LTRIM(RTRIM(CONVERT(varchar(30), u.IT5UCONLREFEXN))),
          '-', ''), ' ', ''), '.', ''), 2, 9) =
      REPLACE(REPLACE(REPLACE(
          LTRIM(RTRIM(@ContractNumber)),
          '-', ''), ' ', ''), '.', '');";

        public const string EliaDemandDetails = @"
SELECT TOP (1)
    d.IT5HDMDAIDN AS DemandId,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCPRD))), '') AS ProductCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCPCS))), '') AS ProcessCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCCDI))), '') AS ChannelCode,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCAPL))), '') AS ApplicationChannel,
    NULLIF(LTRIM(RTRIM(CONVERT(varchar(20), d.IT5HDMDCSTA))), '') AS DemandStatus,
    d.IT5HDMDDCRE AS DemandCreationDate,
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
        string PaymentMethodCode,
        DateTime? DemandCreationDate = null);

    internal sealed record Branch23EliaContractCandidate(
        string DemandId,
        string EliaId,
        string ContractNumber,
        string DemandStatus,
        DateTime? DemandCreationDate,
        DateTime? LinkUpdateDate,
        int ProcessSequence,
        int ContractSequence,
        int AmendmentSequence)
    {
        public bool IsActiveDemand =>
            string.Equals(DemandStatus, "A3", StringComparison.OrdinalIgnoreCase);

        public string Evidence =>
            $"DemandId={DemandId}, EliaId={EliaId}, " +
            $"Contract={ContractNumber}, Status={DemandStatus}, " +
            $"DemandDate={DemandCreationDate:yyyy-MM-dd}, " +
            $"LinkUpdate={LinkUpdateDate:yyyy-MM-dd HH:mm:ss}, " +
            $"Seq={ProcessSequence}/{ContractSequence}/{AmendmentSequence}";
    }

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
            string? demandId,
            string? contractNumber,
            CancellationToken token)
        {
            string normalizedDemandId = NormalizeIdentifier(demandId);
            string normalizedContract = NormalizeIdentifier(contractNumber);

            if (normalizedDemandId.Length == 0 && normalizedContract.Length == 0)
            {
                throw new Branch23ActivationException(
                    "BR23_ELIA_CONTRACT_NOT_FOUND",
                    "No Demand ID or contract number provided.");
            }

            bool resolvedByDemand = normalizedDemandId.Length > 0;
            DataTable table = resolvedByDemand
                ? await _sql.QueryAsync(
                        Branch23Database.Elia,
                        Branch23ActivationQueries.EliaContractByDemand,
                        new Dictionary<string, object>
                        {
                            ["@DemandId"] = normalizedDemandId
                        },
                        token)
                    .ConfigureAwait(false)
                : await _sql.QueryAsync(
                        Branch23Database.Elia,
                        Branch23ActivationQueries.EliaContractByNumber,
                        new Dictionary<string, object>
                        {
                            ["@ContractNumber"] = normalizedContract
                        },
                        token)
                    .ConfigureAwait(false);

            List<Branch23EliaContractCandidate> candidates =
                ReadDistinctEliaCandidates(table);

            if (candidates.Count == 0 && resolvedByDemand && normalizedContract.Length > 0)
            {
                resolvedByDemand = false;
                table = await _sql.QueryAsync(
                        Branch23Database.Elia,
                        Branch23ActivationQueries.EliaContractByNumber,
                        new Dictionary<string, object>
                        {
                            ["@ContractNumber"] = normalizedContract
                        },
                        token)
                    .ConfigureAwait(false);
                candidates = ReadDistinctEliaCandidates(table);
            }

            if (candidates.Count == 0)
            {
                throw new Branch23ActivationException(
                    "BR23_ELIA_CONTRACT_NOT_FOUND",
                    resolvedByDemand
                        ? $"No ELIA contract is linked to Demand ID {normalizedDemandId}."
                        : $"Contract '{normalizedContract}' was not found in " +
                          "FJ1.TB5HELT / FJ1.TB5UCON.");
            }

            Branch23EliaContractCandidate selected = resolvedByDemand
                ? SelectUniqueDemandCandidate(candidates, normalizedDemandId)
                : SelectCurrentContractCandidate(candidates, normalizedContract);

            return (selected.DemandId, selected.EliaId, selected.ContractNumber);
        }

        private static List<Branch23EliaContractCandidate> ReadDistinctEliaCandidates(
            DataTable table)
        {
            return table.Rows.Cast<DataRow>()
                .Select(row => new Branch23EliaContractCandidate(
                    NormalizeIdentifier(Str(row, "DemandId")),
                    NormalizeIdentifier(Str(row, "EliaId")),
                    Str(row, "NumeroContrat"),
                    Str(row, "DemandStatus"),
                    Date(row, "DemandCreationDate"),
                    Date(row, "LinkUpdateDate"),
                    Int(row, "ProcessSequence"),
                    Int(row, "ContractSequence"),
                    Int(row, "AmendmentSequence")))
                .Where(candidate =>
                    candidate.EliaId.Length > 0 &&
                    candidate.ContractNumber.Length > 0)
                .GroupBy(candidate => new
                {
                    candidate.DemandId,
                    candidate.EliaId,
                    Contract = NormalizeContract(candidate.ContractNumber)
                })
                .Select(group => group
                    .OrderByDescending(candidate => candidate.LinkUpdateDate)
                    .ThenByDescending(candidate => candidate.DemandCreationDate)
                    .First())
                .ToList();
        }

        private static Branch23EliaContractCandidate SelectUniqueDemandCandidate(
            IReadOnlyList<Branch23EliaContractCandidate> candidates,
            string demandId)
        {
            List<Branch23EliaContractCandidate> distinct = DistinctByEliaAndContract(
                candidates.Where(candidate => string.Equals(
                    candidate.DemandId,
                    demandId,
                    StringComparison.OrdinalIgnoreCase)).ToList());

            if (distinct.Count == 1)
            {
                return distinct[0];
            }

            throw new Branch23ActivationException(
                "BR23_ELIA_DEMAND_AMBIGUOUS",
                $"Demand ID {demandId} is linked to {distinct.Count} " +
                "distinct ELIA contracts: " +
                string.Join(" | ", distinct.Select(candidate => candidate.Evidence)));
        }

        private static Branch23EliaContractCandidate SelectCurrentContractCandidate(
            IReadOnlyList<Branch23EliaContractCandidate> candidates,
            string contractNumber)
        {
            List<Branch23EliaContractCandidate> distinct =
                DistinctByEliaAndContract(candidates);

            if (distinct.Count == 1)
            {
                return distinct[0];
            }

            List<Branch23EliaContractCandidate> active =
                DistinctByEliaAndContract(
                    distinct.Where(candidate => candidate.IsActiveDemand).ToList());

            if (active.Count == 1)
            {
                return active[0];
            }

            throw new Branch23ActivationException(
                "BR23_ELIA_CONTRACT_AMBIGUOUS",
                $"Contract '{contractNumber}' matches {distinct.Count} distinct " +
                "ELIA contracts. " +
                (active.Count == 0
                    ? "No unique active A3 Demand could be selected. "
                    : $"{active.Count} active A3 contracts remain. ") +
                "Candidates: " +
                string.Join(" | ", distinct.Select(candidate => candidate.Evidence)));
        }

        private static List<Branch23EliaContractCandidate> DistinctByEliaAndContract(
            IReadOnlyList<Branch23EliaContractCandidate> candidates)
        {
            return candidates
                .GroupBy(candidate => new
                {
                    candidate.EliaId,
                    Contract = NormalizeContract(candidate.ContractNumber)
                })
                .Select(group => group
                    .OrderByDescending(candidate => candidate.IsActiveDemand)
                    .ThenByDescending(candidate => candidate.ProcessSequence)
                    .ThenByDescending(candidate => candidate.ContractSequence)
                    .ThenByDescending(candidate => candidate.AmendmentSequence)
                    .ThenByDescending(candidate => candidate.LinkUpdateDate)
                    .ThenByDescending(candidate => candidate.DemandCreationDate)
                    .First())
                .ToList();
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
            if (string.IsNullOrWhiteSpace(demandId))
            {
                return new Branch23EliaDemandDetails(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }

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
                return new Branch23EliaDemandDetails(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }

            DataRow row = table.Rows[0];
            return new Branch23EliaDemandDetails(
                Str(row, "ProductCode"),
                Str(row, "ProcessCode"),
                Str(row, "ChannelCode"),
                Str(row, "ApplicationChannel"),
                Str(row, "DemandStatus"),
                Str(row, "PaymentMethodCode"),
                Date(row, "DemandCreationDate"));
        }

        /// <summary>
        /// ELIA -> complete plan (Start/End, Run Date, AMOUNT, BUCP/CMDPMT).
        /// En mode automatique, le ChannelCode ELIA fait foi.
        /// </summary>
        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            Branch23PaymentChannel channel,
            CancellationToken token)
        {
            _ = channel;
            return await BuildPlanAsync(
                    demandId,
                    contractNumber,
                    token)
                .ConfigureAwait(false);
        }

        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            string? bucpOverride,
            string? cmdpmtOverride,
            bool overrideApplied,
            CancellationToken token)
        {
            return await BuildPlanAsync(
                    demandId,
                    contractNumber,
                    bucpOverride,
                    cmdpmtOverride,
                    overrideApplied,
                    runDateOverride: null,
                    amountOverride: null,
                    token)
                .ConfigureAwait(false);
        }

        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            string? bucpOverride,
            string? cmdpmtOverride,
            bool overrideApplied,
            DateTime? runDateOverride,
            decimal? amountOverride,
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
                Branch23PaymentParameters.ResolveFromChannelCode(details.ChannelCode);

            if ((overrideApplied || !payment.IsResolved) &&
                (!string.IsNullOrWhiteSpace(bucpOverride) || !string.IsNullOrWhiteSpace(cmdpmtOverride)))
            {
                var fallbackOrManualPayment = Branch23PaymentParameters.ResolveFromPaymentPair(
                    string.IsNullOrWhiteSpace(bucpOverride) ? payment.Bucp : bucpOverride,
                    string.IsNullOrWhiteSpace(cmdpmtOverride) ? payment.Cmdpmt : cmdpmtOverride,
                    details.ChannelCode,
                    overrideApplied: overrideApplied);

                if (fallbackOrManualPayment.IsResolved)
                {
                    payment = fallbackOrManualPayment;
                }
            }

            if (!payment.IsResolved)
            {
                throw new Branch23ActivationException(
                    "BR23_CHANNEL_NOT_SUPPORTED",
                    $"ELIA ChannelCode '{details.ChannelCode}' is not mapped to BANK or BROKER_NEW_GEN " +
                    $"and no valid BUCP/CMDPMT override ({bucpOverride}/{cmdpmtOverride}) was provided.");
            }

            Branch23StartEnd startEnd =
                Branch23ActivationResolver.ResolveStartEnd(elia.ContractNumber);

            IReadOnlyList<Branch23PremiumRow> rows =
                await GetPremiumRowsAsync(elia.EliaId, token)
                    .ConfigureAwait(false);

            Branch23PremiumSelection premium;
            if (overrideApplied && amountOverride.HasValue && amountOverride.Value > 0m)
            {
                Branch23PremiumRow? baseRow = rows.FirstOrDefault(r => r.GrossPremium.HasValue && r.GrossPremium.Value > 0m)
                    ?? rows.FirstOrDefault();

                DateTime? fallbackDate = runDateOverride ?? baseRow?.PaymentDate ?? baseRow?.FirstPremiumDate ?? baseRow?.ContractStartDate;
                premium = new Branch23PremiumSelection
                {
                    Selected = new Branch23PremiumRow
                    {
                        EliaId = elia.EliaId,
                        ContractNumber = startEnd.FullContractNumber,
                        GrossPremium = amountOverride.Value,
                        NetPremium = baseRow?.NetPremium ?? amountOverride.Value,
                        FirstPremiumDate = baseRow?.FirstPremiumDate ?? fallbackDate,
                        PaymentDate = baseRow?.PaymentDate ?? fallbackDate,
                        ContractStartDate = baseRow?.ContractStartDate ?? fallbackDate
                    },
                    TotalRows = rows.Count,
                    Amount = Branch23ActivationResolver.FormatAmount(amountOverride.Value),
                    Reason = "Manual override"
                };
            }
            else if (rows.Count == 0 && amountOverride.HasValue && amountOverride.Value > 0m)
            {
                premium = new Branch23PremiumSelection
                {
                    Selected = new Branch23PremiumRow
                    {
                        EliaId = elia.EliaId,
                        ContractNumber = startEnd.FullContractNumber,
                        GrossPremium = amountOverride.Value,
                        NetPremium = amountOverride.Value,
                        FirstPremiumDate = runDateOverride,
                        PaymentDate = runDateOverride,
                        ContractStartDate = runDateOverride
                    },
                    TotalRows = 0,
                    Amount = Branch23ActivationResolver.FormatAmount(amountOverride.Value),
                    Reason = "Fallback amount (TB5UPRP empty)"
                };
            }
            else
            {
                premium = Branch23ActivationResolver.SelectConclusionPremium(rows);
            }

            Branch23PremiumRow selected = premium.Selected;
            Branch23RunDate runDate;
            if (runDateOverride.HasValue && runDateOverride.Value > new DateTime(1900, 1, 1))
            {
                runDate = new Branch23RunDate
                {
                    RunDate = runDateOverride.Value.Date,
                    Source = "Manual override",
                    Reason = $"RunDate explicit ({runDateOverride.Value:yyyy-MM-dd})",
                    PaymentDate = selected.PaymentDate,
                    FirstPremiumDate = selected.FirstPremiumDate,
                    ContractStartDate = selected.ContractStartDate
                };
            }
            else
            {
                runDate = Branch23ActivationResolver.ResolveRunDate(
                    selected.PaymentDate,
                    selected.FirstPremiumDate,
                    selected.ContractStartDate,
                    details.DemandCreationDate);
            }

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
                    "(informational only); " +
                    $"ELIA selection=DemandId {elia.DemandId}, " +
                    $"EliaId {elia.EliaId}"
            };
        }

        public async Task<Branch23ActivationPlan> BuildPlanAsync(
            string? demandId,
            string? contractNumber,
            CancellationToken token)
        {
            return await BuildPlanAsync(
                    demandId,
                    contractNumber,
                    bucpOverride: null,
                    cmdpmtOverride: null,
                    overrideApplied: false,
                    runDateOverride: null,
                    amountOverride: null,
                    token)
                .ConfigureAwait(false);
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

        // ------------------------------------------------------- SQL PRCTT0 INSERT & LISA FMVDT0 MOVEMENTS

        /// <summary>
        /// Insertion directe via SQL dans [LV000PD0_x].[LV].[PRCTT0] en copiant une ligne modèle
        /// (identique à la procédure manuelle SSMS pour Broker / PremElia Ligne 1).
        /// </summary>
        public async Task<(bool Inserted, string Detail)> InsertPrctRowViaSqlAsync(
            Branch23ActivationPlan plan,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(plan);
            token.ThrowIfCancellationRequested();

            const string dynamicInsertSql = @"
DECLARE @Cols nvarchar(max) = N'';
DECLARE @Exprs nvarchar(max) = N'';

SELECT
    @Cols = @Cols + CASE WHEN @Cols = N'' THEN N'' ELSE N', ' END + QUOTENAME(c.name),
    @Exprs = @Exprs + CASE WHEN @Exprs = N'' THEN N'' ELSE N', ' END +
        CASE UPPER(c.name)
            WHEN 'C_STE' THEN N'@Ste'
            WHEN 'NO_CNT' THEN N'@NoCnt'
            WHEN 'C_MD_PMT' THEN N'@CmdPmt'
            WHEN 'D_REF_PRM' THEN N'@RunDate'
            WHEN 'D_BISM_DVA' THEN N'@RunDate'
            WHEN 'D_BISM_DCOR' THEN N'@RunDate'
            WHEN 'M_PAY' THEN N'@Amount'
            WHEN 'NO_BISM_BUCP' THEN N'@Bucp'
            WHEN 'NO_ORD_RCP' THEN N'1'
            WHEN 'C_ETAT_RCP' THEN N'''1'''
            WHEN 'C_TY_RCT' THEN N'''1'''
            ELSE N'src.' + QUOTENAME(c.name)
        END
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'[LV].[PRCTT0]')
  AND c.is_identity = 0
  AND c.is_computed = 0
ORDER BY c.column_id;

IF @Cols = N''
BEGIN
    SELECT 0 AS InsertedRows, N'Table [LV].[PRCTT0] introuvable.' AS Message;
    RETURN;
END

DECLARE @Sql nvarchar(max) = N'
DECLARE @Inserted int = 0;
IF NOT EXISTS (
    SELECT 1 FROM [LV].[PRCTT0] WITH (UPDLOCK, HOLDLOCK)
    WHERE C_STE = @Ste AND NO_CNT = @NoCnt AND D_REF_PRM = @RunDate AND M_PAY = @Amount
)
BEGIN
    INSERT INTO [LV].[PRCTT0] (' + @Cols + N')
    SELECT TOP (1) ' + @Exprs + N'
    FROM [LV].[PRCTT0] AS src
    WHERE src.C_STE = @Ste
    ORDER BY CASE WHEN src.C_MD_PMT = @CmdPmt THEN 0 ELSE 1 END, src.D_REF_PRM DESC;
    SET @Inserted = @@ROWCOUNT;
END
SELECT @Inserted AS InsertedRows, N''OK'' AS Message;';

EXEC sp_executesql
    @Sql,
    N'@Ste varchar(4), @NoCnt decimal(15,0), @CmdPmt varchar(4), @RunDate date, @Amount decimal(18,3), @Bucp decimal(10,0)',
    @Ste = @Ste,
    @NoCnt = @NoCnt,
    @CmdPmt = @CmdPmt,
    @RunDate = @RunDate,
    @Amount = @Amount,
    @Bucp = @Bucp;";

            try
            {
                decimal noCnt = decimal.Parse(plan.StartEnd.Start, CultureInfo.InvariantCulture);
                decimal amount = plan.Premium.Selected.GrossPremium!.Value;
                decimal bucp = decimal.Parse(plan.Payment.Bucp, CultureInfo.InvariantCulture);

                return await InsertPrctRowViaSqlCoreAsync(
                        noCnt,
                        plan.Payment.Cmdpmt,
                        bucp.ToString(CultureInfo.InvariantCulture),
                        plan.RunDate.RunDate,
                        amount,
                        dynamicInsertSql,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"SQL INSERT LV.PRCTT0 erreur : {ex.Message}");
            }
        }

        public async Task<string> InsertPrctRowViaSqlAsync(
            decimal noCnt,
            string cmdpmt,
            string bucp,
            DateTime runDate,
            decimal amount,
            CancellationToken token)
        {
            var plan = new Branch23ActivationPlan
            {
                StartEnd = new Branch23StartEnd
                {
                    Start = noCnt.ToString("0", CultureInfo.InvariantCulture),
                    End = noCnt.ToString("0", CultureInfo.InvariantCulture),
                    FullContractNumber = noCnt.ToString("0", CultureInfo.InvariantCulture)
                },
                RunDate = new Branch23RunDate { RunDate = runDate.Date },
                Premium = new Branch23PremiumSelection
                {
                    Selected = new Branch23PremiumRow { GrossPremium = amount },
                    Amount = amount.ToString("0.00", CultureInfo.InvariantCulture)
                },
                Payment = new Branch23PaymentParameters
                {
                    Bucp = string.IsNullOrWhiteSpace(bucp) ? "00382" : bucp.Trim(),
                    Cmdpmt = string.IsNullOrWhiteSpace(cmdpmt) ? "8" : cmdpmt.Trim().ToUpperInvariant()
                }
            };

            var res = await InsertPrctRowViaSqlAsync(plan, token).ConfigureAwait(false);
            return res.Detail;
        }

        private async Task<(bool Inserted, string Detail)> InsertPrctRowViaSqlCoreAsync(
            decimal noCnt,
            string cmdpmt,
            string bucpText,
            DateTime runDate,
            decimal amount,
            string dynamicInsertSql,
            CancellationToken token)
        {
            decimal bucp = decimal.TryParse(bucpText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal b) ? b : 0m;
            DataTable result = await _sql.QueryAsync(
                    Branch23Database.LisaPayment,
                    dynamicInsertSql,
                    new Dictionary<string, object>
                    {
                        ["@Ste"] = Settings.Branch23Activation.Ste,
                        ["@NoCnt"] = noCnt,
                        ["@CmdPmt"] = cmdpmt,
                        ["@RunDate"] = runDate.Date,
                        ["@Amount"] = amount,
                        ["@Bucp"] = bucp
                    },
                    token)
                .ConfigureAwait(false);

            int inserted = result.Rows.Count > 0 && result.Rows[0]["InsertedRows"] != DBNull.Value
                ? Convert.ToInt32(result.Rows[0]["InsertedRows"], CultureInfo.InvariantCulture)
                : 0;

            return (inserted > 0,
                inserted > 0
                    ? $"SQL INSERT LV.PRCTT0 réussi (NO_CNT={noCnt}, M_PAY={amount:0.00}, C_MD_PMT={cmdpmt}, BUCP={bucp}, D_REF_PRM={runDate:yyyy-MM-dd})."
                    : "SQL LV.PRCTT0 : aucune ligne insérée (déjà présente ou aucune ligne modèle trouvée).");
        }

        /// <summary>
        /// Lit l'état des mouvements de fonds dans [LV000FD0_x].[LV].[FMVDT0]
        /// (État 2 après LVDQFG03, État 4 après LVDQFG01).
        /// </summary>
        public async Task<(IReadOnlyList<string> Funds, string Summary, bool HasState4, bool HasState2)>
            GetFundMovementsSummaryAsync(string start, CancellationToken token)
        {
            const string sql = @"
SELECT TOP (20) *
FROM [LV].[FMVDT0] WITH (NOLOCK)
WHERE C_STE = @Company
  AND NO_CNT = @CoInterne;";

            try
            {
                decimal noCnt = decimal.Parse(start, CultureInfo.InvariantCulture);
                DataTable table = await _sql.QueryAsync(
                        Branch23Database.LisaFundMovement,
                        sql,
                        new Dictionary<string, object>
                        {
                            ["@Company"] = Settings.Branch23Activation.Ste,
                            ["@CoInterne"] = noCnt
                        },
                        token)
                    .ConfigureAwait(false);

                if (table.Rows.Count == 0)
                {
                    return (Array.Empty<string>(), "Aucun mouvement dans LV000FD0.LV.FMVDT0", false, false);
                }

                var funds = new List<string>();
                var states = new List<string>();
                bool hasState4 = false;
                bool hasState2 = false;

                foreach (DataRow row in table.Rows)
                {
                    string fund = Str(row, "C_FONDS");
                    if (!string.IsNullOrWhiteSpace(fund))
                    {
                        funds.Add(fund.ToUpperInvariant());
                    }

                    string state = FirstNonEmpty(
                        Str(row, "C_ETAT_MVT_FONDS"),
                        Str(row, "C_ETAT_MVT"),
                        Str(row, "ETAT_MVT"));
                    string mvtNo = FirstNonEmpty(
                        Str(row, "NO_ORD_MVT_FONDS"),
                        Str(row, "NO_ORD_MVT"),
                        Str(row, "NO_MVT"));
                    string mvtType = FirstNonEmpty(
                        Str(row, "TY_MVT_FONDS"),
                        Str(row, "C_TY_MVT"),
                        Str(row, "TY_MVT"));

                    if (state == "4") hasState4 = true;
                    if (state == "2") hasState2 = true;

                    states.Add($"Mvt={DisplayOrDash(mvtNo)}(Ty={DisplayOrDash(mvtType)},Fonds={DisplayOrDash(fund)},État={DisplayOrDash(state)})");
                }

                var distinctFunds = funds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).ToList();
                string summary = string.Join(" | ", states.Distinct().Take(6));
                return (distinctFunds, summary, hasState4, hasState2);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (Array.Empty<string>(), $"LV000FD0.LV.FMVDT0 non lisible: {ex.Message}", false, false);
            }
        }

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        private static string DisplayOrDash(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

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

        private static int Int(DataRow row, string column) =>
            row.Table.Columns.Contains(column) && row[column] != DBNull.Value &&
            int.TryParse(
                Convert.ToString(row[column], CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                    ? value
                    : 0;

        private static string NormalizeIdentifier(string? value)
        {
            string normalized = (value ?? string.Empty).Trim();
            return string.Equals(normalized, "N/A", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : normalized;
        }

        private static string NormalizeContract(string? value) =>
            new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}