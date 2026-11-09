using AutoActivator.Config;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AutoActivator.Sql
{
    /// <summary>
    /// Centralise les requêtes SQL utilisées par AutoActivator.
    ///
    /// Principes :
    /// - les données ELIA préactivation sont lues dans
    ///   FJ5LIFE_x000 / FJ1 ;
    /// - les données LISA ne sont pas nécessaires pour déterminer
    ///   le produit, le processus ou le canal avant activation ;
    /// - LISA reste utilisée après activation et pour les contrôles ;
    /// - les requêtes sont construites et mises en cache par environnement ;
    /// - les environnements SQL actuellement supportés sont D et Q.
    /// </summary>
    public static class SqlQueries
    {
        private static readonly ConcurrentDictionary<
            string,
            ReadOnlyDictionary<string, string>>
            QueriesByEnvironment =
                new(
                    StringComparer.OrdinalIgnoreCase);

        // =============================================================
        // TABLES LISA
        // =============================================================

        public static readonly IReadOnlyList<string> LisaTables =
            Array.AsReadOnly(
                new[]
                {
                    "LV.PCONT0",
                    "LV.ELIAT0",
                    "LV.ELIHT0",
                    "LV.SCNTT0",
                    "LV.SWBGT0",
                    "LV.SAVTT0",
                    "LV.XRSTT0",
                    "LV.SPERT0",
                    "LV.ADMDT0",

                    "FJ1.TB5LPPL",
                    "FJ1.TB5LPPR",
                    "FJ1.TB5LGDR",

                    "LV.PRIST0",
                    "LV.PECHT0",
                    "LV.PFIET0",
                    "LV.PMNTT0",
                    "LV.PRCTT0",
                    "LV.PSUMT0",
                    "LV.SELTT0",

                    "FJ1.TB5LPPF",

                    "LV.FMVGT0",
                    "LV.FMVDT0",

                    "LV.SFTS",
                    "LV.PINCT0",
                    "LV.SCLST0",
                    "LV.SCLRT0",
                    "LV.SCLDT0",
                    "LV.BSPDT0",
                    "LV.BSPGT0",
                    "LV.BPBAT0",
                    "LV.BPPAT0",
                    "LV.MWBGT0"
                });

        // =============================================================
        // TABLES ELIA CONTRAT
        // =============================================================

        public static readonly IReadOnlyList<string> EliaTables =
            Array.AsReadOnly(
                new[]
                {
                    "FJ1.TB5HELT",
                    "FJ1.TB5UCON",
                    "FJ1.TB5UGAR",
                    "FJ1.TB5UGFF",
                    "FJ1.TB5UASU",
                    "FJ1.TB5UCCR",
                    "FJ1.TB5UAVE",
                    "FJ1.TB5UPNR",
                    "FJ1.TB5UPRP",
                    "FJ1.TB5UPRS",
                    "FJ1.TB5UPMP",
                    "FJ1.TB5URPP",
                    "FJ1.TB5UIVN",
                    "FJ1.TB5UPRF",
                    "FJ1.TB5UFML",
                    "FJ1.TB5UCRB",
                    "FJ1.TB5UDCR",
                    "FJ1.TB5UBEN"
                });

        // =============================================================
        // TABLES ELIA DEMAND
        // =============================================================

        public static readonly IReadOnlyList<string> EliaDemandTables =
            Array.AsReadOnly(
                new[]
                {
                    "FJ1.TB5HDMD",
                    "FJ1.TB5HDGM",
                    "FJ1.TB5HDGD",
                    "FJ1.TB5HPRO",
                    "FJ1.TB5HEPT",
                    "FJ1.TB5HDIC"
                });

        /// <summary>
        /// Dictionnaire Q000 conservé pour compatibilité avec le code
        /// historique.
        ///
        /// Le nouveau code doit utiliser GetQueries ou GetQuery.
        /// </summary>
        public static ReadOnlyDictionary<string, string> Queries
        {
            get;
        } = GetQueries("Q000");

        // =============================================================
        // API PUBLIQUE
        // =============================================================

        public static ReadOnlyDictionary<string, string> GetQueries(
            string envSuffix)
        {
            string environment =
                Settings.DbConfig.NormalizeEnvironment(
                    envSuffix);

            return QueriesByEnvironment.GetOrAdd(
                environment,
                BuildQueries);
        }

        public static string GetQuery(
            string key,
            string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "La clé SQL est obligatoire.",
                    nameof(key));
            }

            string normalizedKey =
                key.Trim();

            ReadOnlyDictionary<string, string> queries =
                GetQueries(envSuffix);

            if (!queries.TryGetValue(
                    normalizedKey,
                    out string? query))
            {
                string environment =
                    Settings.DbConfig.NormalizeEnvironment(
                        envSuffix);

                throw new KeyNotFoundException(
                    $"Requête SQL introuvable : '{normalizedKey}' " +
                    $"pour l'environnement '{environment}'.");
            }

            return query;
        }

        public static bool ContainsQuery(
            string key,
            string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return GetQueries(envSuffix)
                .ContainsKey(key.Trim());
        }

        /// <summary>
        /// Vide le cache des requêtes.
        ///
        /// Utile principalement pour les tests ou lorsqu'une configuration
        /// d'environnement est rechargée dynamiquement.
        /// </summary>
        public static void ClearCache()
        {
            QueriesByEnvironment.Clear();
        }

        // =============================================================
        // CONSTRUCTION DES REQUÊTES
        // =============================================================

        private static ReadOnlyDictionary<string, string> BuildQueries(
            string envSuffix)
        {
            string environment =
                Settings.DbConfig.NormalizeEnvironment(
                    envSuffix);

            string lifeDatabase =
                Settings.DbConfig.GetLifeDatabase(
                    environment);

            string fj1 =
                $"[{lifeDatabase}].[FJ1]";

            var queries =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            AddPreActivationQueries(
                queries,
                fj1);

            AddLegacyContextQueries(
                queries,
                fj1);

            AddBranch23MovementQueries(
                queries);

            AddLisaContractQueries(
                queries);

            AddLisaPlanQueries(
                queries,
                fj1);

            AddDemandExtractionQueries(
                queries,
                fj1);

            AddEliaContractQueries(
                queries,
                fj1);

            AddFinancialQueries(
                queries,
                fj1);

            AddClauseAndReserveQueries(
                queries,
                fj1);

            ValidateRequiredQueries(
                queries);

            return new ReadOnlyDictionary<string, string>(
                queries);
        }

        // =============================================================
        // ROUTING ELIA PRÉACTIVATION
        // =============================================================

        private static void AddPreActivationQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            /*
             * Requête principale.
             *
             * La donnée TB5HDMD est d'abord matérialisée dans DemandData.
             * Cela évite qu'une multiplication de lignes provenant de HELT,
             * UCON, UPRP, UPMP ou UGFF influence la sélection du produit.
             *
             * La sélection privilégie explicitement :
             * 1. une ligne avec ProductCode ;
             * 2. une ligne avec ProcessCode ;
             * 3. une ligne avec ChannelCode ;
             * 4. la ligne ELIA la plus récente.
             */
            queries["GET_PRE_ACTIVATION_ROUTING"] = $@"
;WITH DemandData AS
(
    SELECT TOP (1)
        d.IT5HDMDAIDN AS DemandId,
        d.IT5HDMDDCRE AS DemandCreationDate,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCPRD)),
            ''
        ) AS ProductCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCPCS)),
            ''
        ) AS ProcessCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCCDI)),
            ''
        ) AS ChannelCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCAPL)),
            ''
        ) AS ApplicationChannelCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCSTA)),
            ''
        ) AS DemandStatus,

        d.IT5HDMDAIDNDMDREF AS ReferenceDemandId,
        d.IT5HDMDAIDNDMDLIE AS LinkedDemandId,
        d.IT5HDMDNSEQAVE AS AddendumSequence,
        d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate

    FROM {fj1}.[TB5HDMD] AS d
    WITH (NOLOCK)

    WHERE d.IT5HDMDAIDN = @DemandId

    ORDER BY
        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(d.IT5HDMDCPRD)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(d.IT5HDMDCPCS)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(d.IT5HDMDCCDI)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        d.IT5HDMDDTISMAJDB2 DESC
)

SELECT TOP (1)
    demand.DemandId,
    demand.DemandCreationDate,
    demand.ProductCode,
    demand.ProcessCode,
    demand.ChannelCode,
    demand.ApplicationChannelCode,
    demand.DemandStatus,
    demand.ReferenceDemandId,
    demand.LinkedDemandId,
    demand.AddendumSequence,
    demand.LastEliaUpdate,

    h.IT5UCONAIDN AS EliaContractId,

    NULLIF(
        LTRIM(RTRIM(u.IT5UCONLREFEXN)),
        ''
    ) AS ContractNumber,

    premium.PremiumSequence,
    premium.GrossPremium,
    premium.NetPremium,

    payment.PaymentMethodCode,
    payment.PremiumGroupType,

    ISNULL(
        fundInfo.FundFeatureCount,
        0
    ) AS FundFeatureCount,

    CASE
        WHEN h.IT5UCONAIDN IS NULL
            THEN 'DEMAND_FOUND_NO_ELIA_CONTRACT'

        WHEN NULLIF(
                 LTRIM(RTRIM(u.IT5UCONLREFEXN)),
                 ''
             ) IS NULL
            THEN 'ELIA_CONTRACT_FOUND_NO_CONTRACT_NUMBER'

        WHEN demand.ProductCode IS NULL
            THEN 'PRODUCT_NOT_RESOLVED'

        WHEN demand.ProcessCode IS NULL
            THEN 'PROCESS_NOT_RESOLVED'

        WHEN demand.ChannelCode IS NULL
            THEN 'CHANNEL_NOT_RESOLVED'

        ELSE 'OK'
    END AS RoutingStatus

FROM DemandData AS demand

LEFT JOIN {fj1}.[TB5HELT] AS h
WITH (NOLOCK)
    ON h.IT5HDMDAIDN = demand.DemandId

LEFT JOIN {fj1}.[TB5UCON] AS u
WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN

OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium

    FROM {fj1}.[TB5UPRP] AS p
    WITH (NOLOCK)

    WHERE p.IT5UCONAIDN = h.IT5UCONAIDN

    ORDER BY
        CASE
            WHEN p.IT5UPRPUBRU IS NOT NULL
             AND p.IT5UPRPUBRU > 0
                THEN 0
            ELSE 1
        END,
        p.IT5UAVENSEQ DESC,
        CASE
            WHEN p.IT5UPRPUNET IS NOT NULL
                THEN 0
            ELSE 1
        END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium

OUTER APPLY
(
    SELECT TOP (1)
        NULLIF(
            LTRIM(RTRIM(pm.IT5UPMPCMDEPAI)),
            ''
        ) AS PaymentMethodCode,

        NULLIF(
            LTRIM(RTRIM(pm.IT5UPMPCCTYPGRPPRI)),
            ''
        ) AS PremiumGroupType

    FROM {fj1}.[TB5UPMP] AS pm
    WITH (NOLOCK)

    WHERE pm.IT5UCONAIDN = h.IT5UCONAIDN

    ORDER BY
        pm.IT5UAVENSEQ DESC,
        pm.IT5UGARCGAR
) AS payment

OUTER APPLY
(
    SELECT
        COUNT_BIG(*) AS FundFeatureCount

    FROM {fj1}.[TB5UGFF] AS ff
    WITH (NOLOCK)

    WHERE ff.IT5UCONAIDN = h.IT5UCONAIDN
      AND
      (
           NULLIF(
               LTRIM(RTRIM(ff.IT5UGFFCFND)),
               ''
           ) IS NOT NULL

        OR NULLIF(
               LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)),
               ''
           ) IS NOT NULL
      )
) AS fundInfo

ORDER BY
    CASE
        WHEN h.IT5UCONAIDN IS NULL
            THEN 1
        ELSE 0
    END,

    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(u.IT5UCONLREFEXN)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    h.IT5UCONAIDN DESC;";

            /*
             * Requête directe TB5HDMD servant de source de référence
             * pour ProductCode, ProcessCode, ChannelCode,
             * ApplicationChannelCode et DemandStatus.
             *
             * Cette requête correspond au SELECT validé directement
             * dans SQL Server Management Studio.
             */
            queries["GET_ELIA_DEMAND_ROUTING"] = $@"
SELECT TOP (1)
    d.IT5HDMDAIDN AS DemandId,
    d.IT5HDMDDCRE AS DemandCreationDate,

    NULLIF(
        LTRIM(RTRIM(d.IT5HDMDCPRD)),
        ''
    ) AS ProductCode,

    NULLIF(
        LTRIM(RTRIM(d.IT5HDMDCPCS)),
        ''
    ) AS ProcessCode,

    NULLIF(
        LTRIM(RTRIM(d.IT5HDMDCCDI)),
        ''
    ) AS ChannelCode,

    NULLIF(
        LTRIM(RTRIM(d.IT5HDMDCAPL)),
        ''
    ) AS ApplicationChannelCode,

    NULLIF(
        LTRIM(RTRIM(d.IT5HDMDCSTA)),
        ''
    ) AS DemandStatus,

    d.IT5HDMDAIDNDMDREF AS ReferenceDemandId,
    d.IT5HDMDAIDNDMDLIE AS LinkedDemandId,
    d.IT5HDMDNSEQAVE AS AddendumSequence,
    d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate

FROM {fj1}.[TB5HDMD] AS d
WITH (NOLOCK)

WHERE d.IT5HDMDAIDN = @DemandId

ORDER BY
    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(d.IT5HDMDCPRD)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(d.IT5HDMDCPCS)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(d.IT5HDMDCCDI)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(d.IT5HDMDCAPL)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    d.IT5HDMDDTISMAJDB2 DESC;";

            /*
             * Rattachement demande/contrat : les sequences HELT restent
             * distinctes de TB5HDMD.IT5HDMDNSEQAVE (routing de la demande).
             * Aucun TOP (1), MAX ou remplacement automatique de 0 par 1.
             * Le service doit verifier l'unicite du contexte et la coherence
             * des sequences avant de selectionner les fonds.
             */
            queries["GET_ELIA_DEMAND_CONTRACT_CONTEXT"] = $@"
SELECT DISTINCT
    h.IT5HDMDAIDN AS DemandId,
    h.IT5HPRONSEQ AS ProposalSequence,
    h.IT5UCONAIDN AS EliaContractId,
    h.IT5HELTNSEQCON AS ContractSequence,
    h.IT5HELTNSEQAVEREF AS ReferenceAddendumSequence,
    h.IT5HELTNSEQAVENOU AS NewAddendumSequence,
    NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') AS ContractNumber
FROM {fj1}.[TB5HELT] AS h
LEFT JOIN {fj1}.[TB5UCON] AS u
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
ORDER BY
    EliaContractId,
    ProposalSequence,
    ContractSequence,
    ReferenceAddendumSequence,
    NewAddendumSequence;";

            queries["GET_ELIA_PREMIUM_BY_DEMAND"] = $@"
SELECT TOP (1)
    h.IT5HDMDAIDN AS DemandId,
    p.IT5UCONAIDN AS EliaContractId,
    p.IT5UAVENSEQ AS PremiumSequence,
    p.IT5UPRPUBRU AS GrossPremium,
    p.IT5UPRPUNET AS NetPremium

FROM {fj1}.[TB5HELT] AS h
WITH (NOLOCK)

INNER JOIN {fj1}.[TB5UPRP] AS p
WITH (NOLOCK)
    ON p.IT5UCONAIDN = h.IT5UCONAIDN

WHERE h.IT5HDMDAIDN = @DemandId

ORDER BY
    CASE
        WHEN p.IT5UPRPUBRU IS NOT NULL
         AND p.IT5UPRPUBRU > 0
            THEN 0
        ELSE 1
    END,
    p.IT5UAVENSEQ DESC,
    CASE
        WHEN p.IT5UPRPUNET IS NOT NULL
            THEN 0
        ELSE 1
    END,
    p.IT5UPRPUBRU DESC,
    p.IT5UPRPUNET DESC,
    p.IT5UCONAIDN DESC;";

            queries["GET_ELIA_PAYMENT_METHOD_BY_DEMAND"] = $@"
SELECT
    h.IT5HDMDAIDN AS DemandId,
    pm.IT5UCONAIDN AS EliaContractId,
    pm.IT5UAVENSEQ AS AddendumSequence,

    NULLIF(
        LTRIM(RTRIM(pm.IT5UGARCGAR)),
        ''
    ) AS GuaranteeCode,

    NULLIF(
        LTRIM(RTRIM(pm.IT5UPMPCMDEPAI)),
        ''
    ) AS PaymentMethodCode,

    NULLIF(
        LTRIM(RTRIM(pm.IT5UPMPCCTYPGRPPRI)),
        ''
    ) AS PremiumGroupType,

    pm.IT5UPMPDDEB AS StartDate,
    pm.IT5UPMPDDMD AS MandateDate,

    NULLIF(
        LTRIM(RTRIM(pm.IT5UPMPCSTATMDA)),
        ''
    ) AS MandateStatus,

    pm.IT5UPMPBIUTLBE AS UsageIndicator,
    pm.IT5UPMPBPRIPRT AS PremiumIndicator

FROM {fj1}.[TB5HELT] AS h
WITH (NOLOCK)

INNER JOIN {fj1}.[TB5UPMP] AS pm
WITH (NOLOCK)
    ON pm.IT5UCONAIDN = h.IT5UCONAIDN

WHERE h.IT5HDMDAIDN = @DemandId

ORDER BY
    pm.IT5UAVENSEQ DESC,
    pm.IT5UGARCGAR;";

            queries["GET_ELIA_FUND_FEATURES_BY_DEMAND"] = $@"
SELECT DISTINCT
    h.IT5HDMDAIDN AS DemandId,
    ff.IT5UCONAIDN AS EliaContractId,
    ff.IT5UAVENSEQ AS AddendumSequence,

    NULLIF(
        LTRIM(RTRIM(ff.IT5UGARCGAR)),
        ''
    ) AS GuaranteeCode,

    ff.IT5UGFFNSEQ AS FundSequence,

    NULLIF(
        LTRIM(RTRIM(ff.IT5UGFFCFND)),
        ''
    ) AS FundCode,

    NULLIF(
        LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)),
        ''
    ) AS SecondaryFundCode,

    ff.IT5UGFFDACF AS FundActivationDate,
    ff.IT5UGFFDIAC AS FundEndDate,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,

    NULLIF(
        LTRIM(RTRIM(ff.IT5UGFFBRAC)),
        ''
    ) AS FundIndicator

FROM {fj1}.[TB5HELT] AS h
WITH (NOLOCK)

INNER JOIN {fj1}.[TB5UGFF] AS ff
WITH (NOLOCK)
    ON ff.IT5UCONAIDN = h.IT5UCONAIDN

WHERE h.IT5HDMDAIDN = @DemandId
  AND
  (
       NULLIF(
           LTRIM(RTRIM(ff.IT5UGFFCFND)),
           ''
       ) IS NOT NULL

    OR NULLIF(
           LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)),
           ''
       ) IS NOT NULL
  )

ORDER BY
    AddendumSequence DESC,
    GuaranteeCode,
    FundSequence;";

            /*
             * Recherche des fonds par numero externe complet du contrat.
             * Parametre : @ContractNumber (3-7-2, avec ou sans tirets).
             * Aucun rapprochement par prefixe et aucune dependance a LISA.
             * Toutes les sequences sont retournees pour que le service puisse
             * selectionner explicitement l'avenant et la date applicables.
             * SecondaryFundCode reste distinct de FundCode.
             */
            queries["GET_ELIA_FUND_FEATURES_BY_CONTRACT"] = $@"
SELECT DISTINCT
    u.IT5UCONAIDN AS EliaContractId,
    LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS ContractNumber,
    ff.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(ff.IT5UGARCGAR)), '') AS GuaranteeCode,
    ff.IT5UGFFNSEQ AS FundSequence,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') AS FundCode,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') AS SecondaryFundCode,
    ff.IT5UGFFDACF AS FundActivationDate,
    ff.IT5UGFFDIAC AS FundEndDate,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFBRAC)), '') AS FundIndicator
FROM {fj1}.[TB5UCON] AS u
INNER JOIN {fj1}.[TB5UGFF] AS ff
    ON ff.IT5UCONAIDN = u.IT5UCONAIDN
WHERE REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', '') =
      REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', '')
  AND
  (
      NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
      OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL
  )
ORDER BY
    EliaContractId,
    AddendumSequence DESC,
    GuaranteeCode,
    FundSequence;";

            // Fonds de prime : proportions brutes, sans conversion ni somme.
            // Les avenants, garanties et groupes restent distincts.
            // Le service doit choisir le contexte et dedupliquer les codes.
            queries["GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT_ID"] = $@"
SELECT DISTINCT
    p.IT5UCONAIDN AS EliaContractId,
    p.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(p.IT5UGARCGAR)), '') AS GuaranteeCode,
    NULLIF(LTRIM(RTRIM(p.IT5UPRPCTYPGRPPRI)), '') AS PremiumGroupType,
    NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') AS FundCode,
    p.IT5UPRFPRPIFND AS FundProportionRaw,
    p.IT5UPRFQETIFND AS FundQuantityRaw,
    p.IT5UPRFDTISMAJDB2 AS LastEliaUpdate,
    CAST(NULL AS varchar(6)) AS SecondaryFundCode,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,
    'TB5UPRF' AS FundSource
FROM {fj1}.[TB5UPRF] AS p
WHERE p.IT5UCONAIDN = @EliaContractId
  AND p.IT5UAVENSEQ = @AddendumSequence
  AND p.IT5UGARCGAR = @GuaranteeCode
  AND NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') IS NOT NULL
ORDER BY EliaContractId, AddendumSequence DESC, GuaranteeCode, PremiumGroupType, FundCode;";

            queries["GET_ELIA_PREMIUM_FUNDS_BY_DEMAND"] = $@"
SELECT DISTINCT
    h.IT5HDMDAIDN AS DemandId,
    p.IT5UCONAIDN AS EliaContractId,
    p.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(p.IT5UGARCGAR)), '') AS GuaranteeCode,
    NULLIF(LTRIM(RTRIM(p.IT5UPRPCTYPGRPPRI)), '') AS PremiumGroupType,
    NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') AS FundCode,
    p.IT5UPRFPRPIFND AS FundProportionRaw,
    p.IT5UPRFQETIFND AS FundQuantityRaw,
    p.IT5UPRFDTISMAJDB2 AS LastEliaUpdate,
    CAST(NULL AS varchar(6)) AS SecondaryFundCode,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,
    'TB5UPRF' AS FundSource
FROM {fj1}.[TB5HELT] AS h
INNER JOIN {fj1}.[TB5UPRF] AS p
    ON p.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
  AND NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') IS NOT NULL
ORDER BY EliaContractId, AddendumSequence DESC, GuaranteeCode, PremiumGroupType, FundCode;";

            queries["GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT"] = $@"
SELECT DISTINCT
    LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS ContractNumber,
    p.IT5UCONAIDN AS EliaContractId,
    p.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(p.IT5UGARCGAR)), '') AS GuaranteeCode,
    NULLIF(LTRIM(RTRIM(p.IT5UPRPCTYPGRPPRI)), '') AS PremiumGroupType,
    NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') AS FundCode,
    p.IT5UPRFPRPIFND AS FundProportionRaw,
    p.IT5UPRFQETIFND AS FundQuantityRaw,
    p.IT5UPRFDTISMAJDB2 AS LastEliaUpdate,
    CAST(NULL AS varchar(6)) AS SecondaryFundCode,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,
    'TB5UPRF' AS FundSource
FROM {fj1}.[TB5UCON] AS u
INNER JOIN {fj1}.[TB5UPRF] AS p
    ON p.IT5UCONAIDN = u.IT5UCONAIDN
WHERE REPLACE(REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', ''), ' ', '') =
      REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')
  AND NULLIF(LTRIM(RTRIM(p.IT5UPRFCFND)), '') IS NOT NULL
ORDER BY EliaContractId, AddendumSequence DESC, GuaranteeCode, PremiumGroupType, FundCode;";

            queries["GET_ELIA_FUND_FEATURE_COUNT_BY_DEMAND"] = $@"
SELECT
    COUNT_BIG(*) AS FundFeatureCount

FROM {fj1}.[TB5HELT] AS h
WITH (NOLOCK)

INNER JOIN {fj1}.[TB5UGFF] AS ff
WITH (NOLOCK)
    ON ff.IT5UCONAIDN = h.IT5UCONAIDN

WHERE h.IT5HDMDAIDN = @DemandId
  AND
  (
       NULLIF(
           LTRIM(RTRIM(ff.IT5UGFFCFND)),
           ''
       ) IS NOT NULL

    OR NULLIF(
           LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)),
           ''
       ) IS NOT NULL
  );";
        }

        // =============================================================
        // CONTEXTE LEGACY ET IDENTIFIANTS
        // =============================================================

        private static void AddLegacyContextQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["GET_INTERNAL_ID"] = @"
SELECT TOP (1)
    NO_CNT

FROM [LV].[SCNTT0]
WITH (NOLOCK)

WHERE REPLACE(
          LTRIM(RTRIM(NO_CNT_EXTENDED)),
          '-',
          ''
      ) =
      REPLACE(
          LTRIM(RTRIM(@ContractNumber)),
          '-',
          ''
      )

ORDER BY
    NO_CNT;";

            queries["GET_ELIA_ID"] = $@"
SELECT TOP (1)
    IT5UCONAIDN

FROM {fj1}.[TB5UCON]
WITH (NOLOCK)

WHERE REPLACE(
          LTRIM(RTRIM(IT5UCONLREFEXN)),
          '-',
          ''
      ) LIKE
      REPLACE(
          LTRIM(RTRIM(@ContractNumber)),
          '-',
          ''
      ) + '%'

ORDER BY
    IT5UCONAIDN DESC;";

            queries["GET_ELIA_DEMAND_IDS"] = $@"
SELECT DISTINCT
    IT5HDMDAIDN

FROM {fj1}.[TB5HELT]
WITH (NOLOCK)

WHERE IT5UCONAIDN = @EliaId

ORDER BY
    IT5HDMDAIDN;";

            queries["GET_CONTRACT_BY_DEMAND"] = $@"
SELECT TOP (1)
    NULLIF(
        LTRIM(RTRIM(u.IT5UCONLREFEXN)),
        ''
    ) AS IT5UCONLREFEXN

FROM {fj1}.[TB5HELT] AS h
WITH (NOLOCK)

INNER JOIN {fj1}.[TB5UCON] AS u
WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN

WHERE h.IT5HDMDAIDN = @DemandId

ORDER BY
    CASE
        WHEN NULLIF(
                 LTRIM(RTRIM(u.IT5UCONLREFEXN)),
                 ''
             ) IS NULL
            THEN 1
        ELSE 0
    END,

    u.IT5UCONAIDN DESC;";

            queries["GET_FULL_CONTEXT_BY_DEMAND"] = $@"
;WITH DemandData AS
(
    SELECT TOP (1)
        d.IT5HDMDAIDN AS DemandId,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCPRD)),
            ''
        ) AS ProductCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCPCS)),
            ''
        ) AS ProcessCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCCDI)),
            ''
        ) AS ChannelCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCAPL)),
            ''
        ) AS ApplicationChannelCode,

        NULLIF(
            LTRIM(RTRIM(d.IT5HDMDCSTA)),
            ''
        ) AS DemandStatus,

        d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate

    FROM {fj1}.[TB5HDMD] AS d
    WITH (NOLOCK)

    WHERE d.IT5HDMDAIDN = @DemandId

    ORDER BY
        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(d.IT5HDMDCPRD)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(d.IT5HDMDCPCS)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        d.IT5HDMDDTISMAJDB2 DESC
),
ContractData AS
(
    SELECT TOP (1)
        demand.DemandId,
        h.IT5UCONAIDN AS UconId,

        NULLIF(
            LTRIM(RTRIM(u.IT5UCONLREFEXN)),
            ''
        ) AS ContractNumber,

        demand.ProductCode,
        demand.ProcessCode,
        demand.ChannelCode,
        demand.ApplicationChannelCode,
        demand.DemandStatus,
        demand.LastEliaUpdate

    FROM DemandData AS demand

    LEFT JOIN {fj1}.[TB5HELT] AS h
    WITH (NOLOCK)
        ON h.IT5HDMDAIDN = demand.DemandId

    LEFT JOIN {fj1}.[TB5UCON] AS u
    WITH (NOLOCK)
        ON u.IT5UCONAIDN = h.IT5UCONAIDN

    ORDER BY
        CASE
            WHEN h.IT5UCONAIDN IS NULL
                THEN 1
            ELSE 0
        END,

        CASE
            WHEN NULLIF(
                     LTRIM(RTRIM(u.IT5UCONLREFEXN)),
                     ''
                 ) IS NULL
                THEN 1
            ELSE 0
        END,

        h.IT5UCONAIDN DESC
)

SELECT TOP (1)
    contractData.DemandId,
    contractData.UconId,
    contractData.ContractNumber,
    contractData.ProductCode,
    contractData.ProcessCode,
    contractData.ChannelCode,
    contractData.ApplicationChannelCode,
    contractData.DemandStatus,

    lisa.InternalId,
    lisa.LisaContractNumber,

    premium.PremiumSequence,
    premium.GrossPremium,
    premium.NetPremium,

    CASE
        WHEN contractData.DemandId IS NULL
            THEN 'DEMAND NOT FOUND'

        WHEN contractData.UconId IS NULL
            THEN 'DEMAND FOUND - ELIA CONTRACT NOT LINKED'

        WHEN contractData.ContractNumber IS NULL
            THEN 'ELIA FOUND - CONTRACT NUMBER NOT AVAILABLE'

        WHEN contractData.ProductCode IS NULL
            THEN 'ELIA FOUND - PRODUCT NOT FOUND'

        WHEN contractData.ProcessCode IS NULL
            THEN 'ELIA FOUND - PROCESS NOT FOUND'

        WHEN contractData.ChannelCode IS NULL
            THEN 'ELIA FOUND - CHANNEL NOT FOUND'

        WHEN lisa.InternalId IS NULL
            THEN 'ELIA FOUND - LISA NOT FOUND'

        WHEN premium.GrossPremium IS NULL
            THEN 'CONTRACT FOUND - PREMIUM NOT FOUND'

        ELSE 'OK'
    END AS DiagnosticStatus

FROM ContractData AS contractData

OUTER APPLY
(
    SELECT TOP (1)
        s.NO_CNT AS InternalId,
        LTRIM(RTRIM(s.NO_CNT_EXTENDED)) AS LisaContractNumber

    FROM [LV].[SCNTT0] AS s
    WITH (NOLOCK)

    WHERE REPLACE(
              LTRIM(RTRIM(s.NO_CNT_EXTENDED)),
              '-',
              ''
          ) =
          REPLACE(
              LTRIM(RTRIM(contractData.ContractNumber)),
              '-',
              ''
          )

    ORDER BY
        s.NO_CNT
) AS lisa

OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium

    FROM {fj1}.[TB5UPRP] AS p
    WITH (NOLOCK)

    WHERE p.IT5UCONAIDN = contractData.UconId

    ORDER BY
        CASE
            WHEN p.IT5UPRPUBRU IS NOT NULL
             AND p.IT5UPRPUBRU > 0
                THEN 0
            ELSE 1
        END,
        p.IT5UAVENSEQ DESC,
        CASE
            WHEN p.IT5UPRPUNET IS NOT NULL
                THEN 0
            ELSE 1
        END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium;";

            queries["GET_FULL_CONTEXT_BY_CONTRACT"] = $@"
;WITH ContractData AS
(
    SELECT TOP (1)
        demand.DemandId,
        u.IT5UCONAIDN AS UconId,

        NULLIF(
            LTRIM(RTRIM(u.IT5UCONLREFEXN)),
            ''
        ) AS ContractNumber,

        demand.ProductCode,
        demand.ProcessCode,
        demand.ChannelCode,
        demand.ApplicationChannelCode,
        demand.DemandStatus

    FROM {fj1}.[TB5UCON] AS u
    WITH (NOLOCK)

    OUTER APPLY
    (
        SELECT TOP (1)
            h.IT5HDMDAIDN AS DemandId,

            NULLIF(
                LTRIM(RTRIM(d.IT5HDMDCPRD)),
                ''
            ) AS ProductCode,

            NULLIF(
                LTRIM(RTRIM(d.IT5HDMDCPCS)),
                ''
            ) AS ProcessCode,

            NULLIF(
                LTRIM(RTRIM(d.IT5HDMDCCDI)),
                ''
            ) AS ChannelCode,

            NULLIF(
                LTRIM(RTRIM(d.IT5HDMDCAPL)),
                ''
            ) AS ApplicationChannelCode,

            NULLIF(
                LTRIM(RTRIM(d.IT5HDMDCSTA)),
                ''
            ) AS DemandStatus,

            d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate

        FROM {fj1}.[TB5HELT] AS h
        WITH (NOLOCK)

        LEFT JOIN {fj1}.[TB5HDMD] AS d
        WITH (NOLOCK)
            ON d.IT5HDMDAIDN = h.IT5HDMDAIDN

        WHERE h.IT5UCONAIDN = u.IT5UCONAIDN

        ORDER BY
            CASE
                WHEN NULLIF(
                         LTRIM(RTRIM(d.IT5HDMDCPRD)),
                         ''
                     ) IS NULL
                    THEN 1
                ELSE 0
            END,

            CASE
                WHEN NULLIF(
                         LTRIM(RTRIM(d.IT5HDMDCPCS)),
                         ''
                     ) IS NULL
                    THEN 1
                ELSE 0
            END,

            CASE
                WHEN NULLIF(
                         LTRIM(RTRIM(d.IT5HDMDCCDI)),
                         ''
                     ) IS NULL
                    THEN 1
                ELSE 0
            END,

            d.IT5HDMDDTISMAJDB2 DESC,
            h.IT5HDMDAIDN DESC
    ) AS demand

    WHERE REPLACE(
              LTRIM(RTRIM(u.IT5UCONLREFEXN)),
              '-',
              ''
          ) LIKE
          REPLACE(
              LTRIM(RTRIM(@ContractNumber)),
              '-',
              ''
          ) + '%'

    ORDER BY
        u.IT5UCONAIDN DESC
)

SELECT TOP (1)
    contractData.DemandId,
    contractData.UconId,
    contractData.ContractNumber,
    contractData.ProductCode,
    contractData.ProcessCode,
    contractData.ChannelCode,
    contractData.ApplicationChannelCode,
    contractData.DemandStatus,

    lisa.InternalId,
    lisa.LisaContractNumber,

    premium.PremiumSequence,
    premium.GrossPremium,
    premium.NetPremium,

    CASE
        WHEN contractData.ContractNumber IS NULL
            THEN 'CONTRACT NOT FOUND'

        WHEN contractData.ProductCode IS NULL
            THEN 'ELIA FOUND - PRODUCT NOT FOUND'

        WHEN contractData.ProcessCode IS NULL
            THEN 'ELIA FOUND - PROCESS NOT FOUND'

        WHEN contractData.ChannelCode IS NULL
            THEN 'ELIA FOUND - CHANNEL NOT FOUND'

        WHEN lisa.InternalId IS NULL
            THEN 'ELIA FOUND - LISA NOT FOUND'

        WHEN premium.GrossPremium IS NULL
            THEN 'CONTRACT FOUND - PREMIUM NOT FOUND'

        ELSE 'OK'
    END AS DiagnosticStatus

FROM ContractData AS contractData

OUTER APPLY
(
    SELECT TOP (1)
        s.NO_CNT AS InternalId,
        LTRIM(RTRIM(s.NO_CNT_EXTENDED)) AS LisaContractNumber

    FROM [LV].[SCNTT0] AS s
    WITH (NOLOCK)

    WHERE REPLACE(
              LTRIM(RTRIM(s.NO_CNT_EXTENDED)),
              '-',
              ''
          ) =
          REPLACE(
              LTRIM(RTRIM(contractData.ContractNumber)),
              '-',
              ''
          )

    ORDER BY
        s.NO_CNT
) AS lisa

OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium

    FROM {fj1}.[TB5UPRP] AS p
    WITH (NOLOCK)

    WHERE p.IT5UCONAIDN = contractData.UconId

    ORDER BY
        CASE
            WHEN p.IT5UPRPUBRU IS NOT NULL
             AND p.IT5UPRPUBRU > 0
                THEN 0
            ELSE 1
        END,
        p.IT5UAVENSEQ DESC,
        CASE
            WHEN p.IT5UPRPUNET IS NOT NULL
                THEN 0
            ELSE 1
        END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium;";
        }

        // =============================================================
        // MOUVEMENTS DE FONDS BRANCHE 23
        // =============================================================

        private static void AddBranch23MovementQueries(
            IDictionary<string, string> queries)
        {
            queries["BR23_LAST_FUND_MOVEMENT"] = @"
SELECT
    ISNULL(
        MAX(movement.NO_ORD_MVT_FONDS),
        0
    ) AS LastMovementNumber

FROM [LV].[FMVGT0] AS movement
WITH (NOLOCK)

WHERE movement.C_STE = @Company
  AND movement.NO_CNT = @Contract;";

            queries["BR23_NEW_FUND_MOVEMENT_FUNDS"] = @"
SELECT
    movement.NO_ORD_MVT_FONDS,
    movement.TY_MVT_FONDS,
    movement.C_ETAT_MVT_FONDS,

    NULLIF(
        LTRIM(RTRIM(movement.NM_JOB_DMOD)),
        ''
    ) AS LastJob,

    NULLIF(
        LTRIM(RTRIM(detail.C_FONDS)),
        ''
    ) AS FundCode

FROM [LV].[FMVGT0] AS movement
WITH (NOLOCK)

INNER JOIN [LV].[FMVDT0] AS detail
WITH (NOLOCK)
    ON detail.C_STE = movement.C_STE
   AND detail.NO_CNT = movement.NO_CNT
   AND detail.NO_ORD_MVT_FONDS =
       movement.NO_ORD_MVT_FONDS

WHERE movement.C_STE = @Company
  AND movement.NO_CNT = @Contract
  AND movement.NO_ORD_MVT_FONDS >
      @LastMovementBefore
  AND NULLIF(
          LTRIM(RTRIM(detail.C_FONDS)),
          ''
      ) IS NOT NULL

ORDER BY
    movement.NO_ORD_MVT_FONDS,
    detail.C_FONDS;";
        }

        // =============================================================
        // TABLES LISA DE BASE
        // =============================================================

        private static void AddLisaContractQueries(
            IDictionary<string, string> queries)
        {
            queries["LV.PCONT0"] =
                "SELECT * FROM [LV].[LV5P02TPCONT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.ELIAT0"] =
                "SELECT * FROM [LV].[ELIAT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.ELIHT0"] =
                "SELECT * FROM [LV].[ELIHT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.SCNTT0"] =
                "SELECT * FROM [LV].[SCNTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.SWBGT0"] =
                "SELECT * FROM [LV].[SWBGT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, C_PROP DESC;";

            queries["LV.SAVTT0"] =
                "SELECT * FROM [LV].[SAVTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, NO_CNT;";

            queries["LV.XRSTT0"] =
                "SELECT * FROM [LV].[XRSTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.SPERT0"] =
                "SELECT * FROM [LV].[SPERT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.ADMDT0"] =
                "SELECT * FROM [LV].[ADMDT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";
        }

        // =============================================================
        // PLANS ET GARANTIES
        // =============================================================

        private static void AddLisaPlanQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["FJ1.TB5LPPL"] =
                $"SELECT * FROM {fj1}.[TB5LPPL] WITH (NOLOCK) " +
                "WHERE IT5LPPLNCON = @InternalId " +
                "ORDER BY IT5LPPLNAVEREF, " +
                "IT5LPPLLGAR, IT5LPPLNCON;";

            queries["FJ1.TB5LPPR"] =
                $"SELECT * FROM {fj1}.[TB5LPPR] WITH (NOLOCK) " +
                "WHERE IT5LPPRNCON = @InternalId " +
                "ORDER BY IT5LPPRNIDNPLNPRI DESC;";

            queries["FJ1.TB5LGDR"] =
                $"SELECT * FROM {fj1}.[TB5LGDR] WITH (NOLOCK) " +
                "WHERE IT5LGDRNCON = @InternalId;";
        }

        // =============================================================
        // EXTRACTIONS DEMAND ELIA
        // =============================================================

        private static void AddDemandExtractionQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["FJ1.TB5HDMD"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HDMD");

            queries["FJ1.TB5HDGM"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HDGM");

            queries["FJ1.TB5HDGD"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HDGD");

            queries["FJ1.TB5HPRO"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HPRO");

            queries["FJ1.TB5HEPT"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HEPT");

            queries["FJ1.TB5HDIC"] =
                BuildDemandIdsQuery(
                    fj1,
                    "TB5HDIC");
        }

        // =============================================================
        // TABLES CONTRAT ELIA
        // =============================================================

        private static void AddEliaContractQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["FJ1.TB5HELT"] =
                $"SELECT * FROM {fj1}.[TB5HELT] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UCON"] =
                $"SELECT * FROM {fj1}.[TB5UCON] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UGAR"] =
                $"SELECT * FROM {fj1}.[TB5UGAR] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UGFF"] =
                $"SELECT * FROM {fj1}.[TB5UGFF] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId " +
                "ORDER BY IT5UAVENSEQ DESC, " +
                "IT5UGARCGAR, IT5UGFFNSEQ;";

            queries["FJ1.TB5UASU"] = $@"
SELECT *
FROM {fj1}.[TB5UASU]
WITH (NOLOCK)

WHERE IT5UASUAIDN IN
(
    SELECT IT5UASUAIDN
    FROM {fj1}.[TB5UGAR]
    WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["FJ1.TB5UCCR"] =
                $"SELECT * FROM {fj1}.[TB5UCCR] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UAVE"] =
                $"SELECT * FROM {fj1}.[TB5UAVE] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UPNR"] = $@"
SELECT *
FROM {fj1}.[TB5UPNR]
WITH (NOLOCK)

WHERE IT5UPNRAIDN IN
(
    SELECT IT5UPNRAIDN
    FROM {fj1}.[TB5UAVE]
    WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["FJ1.TB5UPRP"] = $@"
SELECT *
FROM {fj1}.[TB5UPRP]
WITH (NOLOCK)

WHERE IT5UCONAIDN = @EliaId

ORDER BY
    CASE
        WHEN IT5UPRPUBRU IS NOT NULL
         AND IT5UPRPUBRU > 0
            THEN 0
        ELSE 1
    END,
    IT5UAVENSEQ DESC,
    CASE
        WHEN IT5UPRPUNET IS NOT NULL
            THEN 0
        ELSE 1
    END,
    IT5UPRPUBRU DESC,
    IT5UPRPUNET DESC;";

            queries["FJ1.TB5UPRS"] =
                $"SELECT * FROM {fj1}.[TB5UPRS] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UPMP"] =
                $"SELECT * FROM {fj1}.[TB5UPMP] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId " +
                "ORDER BY IT5UAVENSEQ DESC;";

            queries["FJ1.TB5URPP"] =
                $"SELECT * FROM {fj1}.[TB5URPP] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UIVN"] =
                $"SELECT * FROM {fj1}.[TB5UIVN] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";
        }

        // =============================================================
        // DONNÉES FINANCIÈRES ET FONDS
        // =============================================================

        private static void AddFinancialQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["LV.PRIST0"] =
                "SELECT * FROM [LV].[PRIST0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, D_ECH;";

            queries["LV.PECHT0"] =
                "SELECT * FROM [LV].[PECHT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, D_ECH, NO_ORD_QUITT;";

            queries["LV.PFIET0"] =
                "SELECT * FROM [LV].[PFIET0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY D_CRT_INST, NO_ORD_INST;";

            queries["LV.PMNTT0"] =
                "SELECT * FROM [LV].[PMNTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, NO_ORD_QUITT;";

            queries["LV.PRCTT0"] =
                "SELECT * FROM [LV].[PRCTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.PSUMT0"] =
                "SELECT * FROM [LV].[PSUMT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.SELTT0"] =
                "SELECT * FROM [LV].[SELTT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["FJ1.TB5LPPF"] =
                $"SELECT * FROM {fj1}.[TB5LPPF] WITH (NOLOCK) " +
                "WHERE IT5LPPFNCON = @InternalId " +
                "ORDER BY IT5LPPFCFDS;";

            queries["FJ1.TB5UPRF"] =
                $"SELECT * FROM {fj1}.[TB5UPRF] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UFML"] =
                $"SELECT * FROM {fj1}.[TB5UFML] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["LV.FMVGT0"] =
                "SELECT * FROM [LV].[FMVGT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY TSTAMP_DMOD;";

            queries["LV.FMVDT0"] =
                "SELECT * FROM [LV].[FMVDT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY TSTAMP_DMOD;";

            queries["LV.SFTS"] =
                "SELECT * FROM [LV].[LV5S18TSFTST0] " +
                "WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.PINCT0"] =
                "SELECT * FROM [LV].[PINCT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";
        }

        // =============================================================
        // CLAUSES ET RÉSERVES
        // =============================================================

        private static void AddClauseAndReserveQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["LV.SCLST0"] =
                "SELECT * FROM [LV].[SCLST0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_AVT, NO_ORD_CLS;";

            queries["LV.SCLRT0"] =
                "SELECT * FROM [LV].[LV5S16TSCLRT0] " +
                "WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.SCLDT0"] =
                "SELECT * FROM [LV].[LV5S17TSCLDT0] " +
                "WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["FJ1.TB5UCRB"] =
                $"SELECT * FROM {fj1}.[TB5UCRB] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UDCR"] =
                $"SELECT * FROM {fj1}.[TB5UDCR] WITH (NOLOCK) " +
                "WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UBEN"] = $@"
SELECT *
FROM {fj1}.[TB5UBEN]
WITH (NOLOCK)

WHERE IT5UBENAIDN IN
(
    SELECT IT5UBENAIDN
    FROM {fj1}.[TB5UDCR]
    WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["LV.BSPDT0"] =
                "SELECT * FROM [LV].[BSPDT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.BSPGT0"] =
                "SELECT * FROM [LV].[BSPGT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.BPBAT0"] =
                "SELECT * FROM [LV].[BPBAT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.BPPAT0"] =
                "SELECT * FROM [LV].[BPPAT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId;";

            queries["LV.MWBGT0"] =
                "SELECT * FROM [LV].[MWBGT0] WITH (NOLOCK) " +
                "WHERE NO_CNT = @InternalId " +
                "ORDER BY NO_PRJ, C_PROP;";
        }

        // =============================================================
        // VALIDATION
        // =============================================================

        private static void ValidateRequiredQueries(
            IReadOnlyDictionary<string, string> queries)
        {
            ArgumentNullException.ThrowIfNull(queries);

            string[] requiredKeys =
            {
                "GET_PRE_ACTIVATION_ROUTING",
                "GET_ELIA_DEMAND_ROUTING",
                "GET_ELIA_DEMAND_CONTRACT_CONTEXT",
                "GET_ELIA_PREMIUM_BY_DEMAND",
                "GET_ELIA_PAYMENT_METHOD_BY_DEMAND",
                "GET_ELIA_FUND_FEATURES_BY_DEMAND",
                "GET_ELIA_FUND_FEATURE_COUNT_BY_DEMAND",
                "GET_ELIA_FUND_FEATURES_BY_CONTRACT",
                "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT_ID",
                "GET_ELIA_PREMIUM_FUNDS_BY_DEMAND",
                "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT",

                "GET_INTERNAL_ID",
                "GET_ELIA_ID",
                "GET_ELIA_DEMAND_IDS",
                "GET_CONTRACT_BY_DEMAND",
                "GET_FULL_CONTEXT_BY_DEMAND",
                "GET_FULL_CONTEXT_BY_CONTRACT",
                "BR23_LAST_FUND_MOVEMENT",
                "BR23_NEW_FUND_MOVEMENT_FUNDS",
                "FJ1.TB5UPRP"
            };

            string[] missingKeys =
                requiredKeys
                    .Where(
                        key =>
                            !queries.TryGetValue(
                                key,
                                out string? query) ||
                            string.IsNullOrWhiteSpace(query))
                    .ToArray();

            if (missingKeys.Length > 0)
            {
                throw new InvalidOperationException(
                    "Requêtes SQL obligatoires absentes : " +
                    string.Join(
                        ", ",
                        missingKeys));
            }

            ValidateQueryContains(
                queries,
                "GET_ELIA_DEMAND_CONTRACT_CONTEXT",
                "@DemandId",
                "TB5HELT",
                "IT5HELTNSEQCON",
                "IT5HELTNSEQAVEREF",
                "IT5HELTNSEQAVENOU",
                "EliaContractId",
                "ContractNumber");

            ValidateQueryContains(
                queries,
                "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT_ID",
                "@EliaContractId",
                "@AddendumSequence",
                "@GuaranteeCode",
                "TB5UPRF",
                "FundCode",
                "FundProportionRaw");

            ValidateQueryContains(
                queries,
                "GET_ELIA_PREMIUM_FUNDS_BY_DEMAND",
                "@DemandId",
                "TB5UPRF",
                "FundCode",
                "FundProportionRaw");

            ValidateQueryContains(
                queries,
                "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT",
                "@ContractNumber",
                "TB5UPRF",
                "FundCode",
                "FundProportionRaw");

            ValidateQueryContains(
                queries,
                "GET_ELIA_FUND_FEATURES_BY_CONTRACT",
                "@ContractNumber",
                "TB5UCON",
                "TB5UGFF",
                "AddendumSequence",
                "FundCode",
                "SecondaryFundCode");

            ValidateQueryContains(
                queries,
                "GET_PRE_ACTIVATION_ROUTING",
                "ProductCode",
                "@DemandId");

            ValidateQueryContains(
                queries,
                "GET_ELIA_DEMAND_ROUTING",
                "ProductCode",
                "ProcessCode",
                "ChannelCode",
                "ApplicationChannelCode",
                "DemandStatus",
                "@DemandId");

            ValidateQueryContains(
                queries,
                "GET_PRE_ACTIVATION_ROUTING",
                "IT5UPRPUBRU > 0");

            ValidateQueryContains(
                queries,
                "GET_ELIA_PREMIUM_BY_DEMAND",
                "IT5UPRPUBRU > 0");

            ValidateQueryContains(
                queries,
                "GET_FULL_CONTEXT_BY_DEMAND",
                "IT5UPRPUBRU > 0");

            ValidateQueryContains(
                queries,
                "GET_FULL_CONTEXT_BY_CONTRACT",
                "IT5UPRPUBRU > 0");
        }

        private static void ValidateQueryContains(
            IReadOnlyDictionary<string, string> queries,
            string key,
            params string[] requiredFragments)
        {
            if (!queries.TryGetValue(
                    key,
                    out string? query) ||
                string.IsNullOrWhiteSpace(query))
            {
                throw new InvalidOperationException(
                    $"Requête SQL obligatoire absente : {key}.");
            }

            string[] missingFragments =
                requiredFragments
                    .Where(
                        fragment =>
                            query.IndexOf(
                                fragment,
                                StringComparison.OrdinalIgnoreCase) < 0)
                    .ToArray();

            if (missingFragments.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"La requête '{key}' ne contient pas les éléments " +
                $"obligatoires suivants : " +
                string.Join(
                    ", ",
                    missingFragments));
        }

        // =============================================================
        // HELPERS SQL
        // =============================================================

        private static string BuildDemandIdsQuery(
            string fj1,
            string tableName)
        {
            if (string.IsNullOrWhiteSpace(fj1))
            {
                throw new ArgumentException(
                    "Le nom qualifié du schéma FJ1 est obligatoire.",
                    nameof(fj1));
            }

            string validatedTableName =
                ValidateSqlIdentifier(
                    tableName,
                    nameof(tableName));

            return $@"
SELECT *
FROM {fj1}.[{validatedTableName}]
WITH (NOLOCK)

WHERE IT5HDMDAIDN IN
(
    SELECT
        TRY_CONVERT(
            decimal(18, 0),
            LTRIM(RTRIM(value)))

    FROM STRING_SPLIT(
        @DemandIds,
        ',')

    WHERE TRY_CONVERT(
              decimal(18, 0),
              LTRIM(RTRIM(value)))
          IS NOT NULL
);";
        }

        private static string ValidateSqlIdentifier(
            string value,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "L'identifiant SQL est obligatoire.",
                    parameterName);
            }

            string identifier =
                value.Trim();

            foreach (char character in identifier)
            {
                if (!char.IsLetterOrDigit(character) &&
                    character != '_')
                {
                    throw new ArgumentException(
                        $"Identifiant SQL invalide : '{value}'.",
                        parameterName);
                }
            }

            return identifier;
        }
    }
}