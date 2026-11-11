using System.Collections.Generic;

namespace AutoActivator.Sql
{
    public static partial class SqlQueries
    {
        private static void AddPreActivationQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["GET_PRE_ACTIVATION_ROUTING"] = $@"
;WITH DemandData AS
(
    SELECT TOP (1)
        d.IT5HDMDAIDN AS DemandId,
        d.IT5HDMDDCRE AS DemandCreationDate,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') AS ProductCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') AS ProcessCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') AS ChannelCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCAPL)), '') AS ApplicationChannelCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCSTA)), '') AS DemandStatus,
        d.IT5HDMDAIDNDMDREF AS ReferenceDemandId,
        d.IT5HDMDAIDNDMDLIE AS LinkedDemandId,
        d.IT5HDMDNSEQAVE AS AddendumSequence,
        d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate
    FROM {fj1}.[TB5HDMD] AS d WITH (NOLOCK)
    WHERE d.IT5HDMDAIDN = @DemandId
    ORDER BY
        CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') IS NULL THEN 1 ELSE 0 END,
        CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') IS NULL THEN 1 ELSE 0 END,
        CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') IS NULL THEN 1 ELSE 0 END,
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
    NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') AS ContractNumber,
    premium.PremiumSequence,
    premium.GrossPremium,
    premium.NetPremium,
    payment.PaymentMethodCode,
    payment.PremiumGroupType,
    ISNULL(fundInfo.FundFeatureCount, 0) AS FundFeatureCount,
    CASE
        WHEN h.IT5UCONAIDN IS NULL THEN 'DEMAND_FOUND_NO_ELIA_CONTRACT'
        WHEN NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') IS NULL THEN 'ELIA_CONTRACT_FOUND_NO_CONTRACT_NUMBER'
        WHEN demand.ProductCode IS NULL THEN 'PRODUCT_NOT_RESOLVED'
        WHEN demand.ProcessCode IS NULL THEN 'PROCESS_NOT_RESOLVED'
        WHEN demand.ChannelCode IS NULL THEN 'CHANNEL_NOT_RESOLVED'
        ELSE 'OK'
    END AS RoutingStatus
FROM DemandData AS demand
LEFT JOIN {fj1}.[TB5HELT] AS h WITH (NOLOCK)
    ON (
           h.IT5HDMDAIDN = demand.DemandId
        OR (demand.ReferenceDemandId IS NOT NULL AND h.IT5HDMDAIDN = demand.ReferenceDemandId)
        OR (demand.LinkedDemandId IS NOT NULL AND h.IT5HDMDAIDN = demand.LinkedDemandId)
       )
LEFT JOIN {fj1}.[TB5UCON] AS u WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium
    FROM {fj1}.[TB5UPRP] AS p WITH (NOLOCK)
    WHERE p.IT5UCONAIDN = h.IT5UCONAIDN
    ORDER BY
        CASE WHEN p.IT5UPRPUBRU IS NOT NULL AND p.IT5UPRPUBRU > 0 THEN 0 ELSE 1 END,
        p.IT5UAVENSEQ DESC,
        CASE WHEN p.IT5UPRPUNET IS NOT NULL THEN 0 ELSE 1 END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium
OUTER APPLY
(
    SELECT TOP (1)
        NULLIF(LTRIM(RTRIM(pm.IT5UPMPCMDEPAI)), '') AS PaymentMethodCode,
        NULLIF(LTRIM(RTRIM(pm.IT5UPMPCCTYPGRPPRI)), '') AS PremiumGroupType
    FROM {fj1}.[TB5UPMP] AS pm WITH (NOLOCK)
    WHERE pm.IT5UCONAIDN = h.IT5UCONAIDN
    ORDER BY pm.IT5UAVENSEQ DESC, pm.IT5UGARCGAR
) AS payment
OUTER APPLY
(
    SELECT
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UGFF] AS ff WITH (NOLOCK)
            WHERE ff.IT5UCONAIDN = h.IT5UCONAIDN
              AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
        ) +
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UPRF] AS prf WITH (NOLOCK)
            WHERE prf.IT5UCONAIDN = h.IT5UCONAIDN
              AND NULLIF(LTRIM(RTRIM(prf.IT5UPRFCFND)), '') IS NOT NULL
        ) AS FundFeatureCount
) AS fundInfo
ORDER BY
    CASE WHEN h.IT5UCONAIDN IS NULL THEN 1 ELSE 0 END,
    CASE WHEN NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') IS NULL THEN 1 ELSE 0 END,
    CASE WHEN h.IT5HDMDAIDN = demand.DemandId THEN 0 ELSE 1 END,
    h.IT5UCONAIDN DESC;";

            queries["GET_ELIA_DEMAND_ROUTING"] = $@"
SELECT TOP (1)
    d.IT5HDMDAIDN AS DemandId,
    d.IT5HDMDDCRE AS DemandCreationDate,
    NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') AS ProductCode,
    NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') AS ProcessCode,
    NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') AS ChannelCode,
    NULLIF(LTRIM(RTRIM(d.IT5HDMDCAPL)), '') AS ApplicationChannelCode,
    NULLIF(LTRIM(RTRIM(d.IT5HDMDCSTA)), '') AS DemandStatus,
    d.IT5HDMDAIDNDMDREF AS ReferenceDemandId,
    d.IT5HDMDAIDNDMDLIE AS LinkedDemandId,
    d.IT5HDMDNSEQAVE AS AddendumSequence,
    d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate
FROM {fj1}.[TB5HDMD] AS d WITH (NOLOCK)
WHERE d.IT5HDMDAIDN = @DemandId
ORDER BY
    CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') IS NULL THEN 1 ELSE 0 END,
    CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') IS NULL THEN 1 ELSE 0 END,
    CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') IS NULL THEN 1 ELSE 0 END,
    CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCAPL)), '') IS NULL THEN 1 ELSE 0 END,
    d.IT5HDMDDTISMAJDB2 DESC;";

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
FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN {fj1}.[TB5UPRP] AS p WITH (NOLOCK)
    ON p.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
ORDER BY
    CASE WHEN p.IT5UPRPUBRU IS NOT NULL AND p.IT5UPRPUBRU > 0 THEN 0 ELSE 1 END,
    p.IT5UAVENSEQ DESC,
    CASE WHEN p.IT5UPRPUNET IS NOT NULL THEN 0 ELSE 1 END,
    p.IT5UPRPUBRU DESC,
    p.IT5UPRPUNET DESC,
    p.IT5UCONAIDN DESC;";

            queries["GET_ELIA_PAYMENT_METHOD_BY_DEMAND"] = $@"
SELECT
    h.IT5HDMDAIDN AS DemandId,
    pm.IT5UCONAIDN AS EliaContractId,
    pm.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(pm.IT5UGARCGAR)), '') AS GuaranteeCode,
    NULLIF(LTRIM(RTRIM(pm.IT5UPMPCMDEPAI)), '') AS PaymentMethodCode,
    NULLIF(LTRIM(RTRIM(pm.IT5UPMPCCTYPGRPPRI)), '') AS PremiumGroupType,
    pm.IT5UPMPDDEB AS StartDate,
    pm.IT5UPMPDDMD AS MandateDate,
    NULLIF(LTRIM(RTRIM(pm.IT5UPMPCSTATMDA)), '') AS MandateStatus,
    pm.IT5UPMPBIUTLBE AS UsageIndicator,
    pm.IT5UPMPBPRIPRT AS PremiumIndicator
FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN {fj1}.[TB5UPMP] AS pm WITH (NOLOCK)
    ON pm.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
ORDER BY pm.IT5UAVENSEQ DESC, pm.IT5UGARCGAR;";

            queries["GET_ELIA_FUND_FEATURES_BY_DEMAND"] = $@"
SELECT DISTINCT
    h.IT5HDMDAIDN AS DemandId,
    ff.IT5UCONAIDN AS EliaContractId,
    ff.IT5UAVENSEQ AS AddendumSequence,
    NULLIF(LTRIM(RTRIM(ff.IT5UGARCGAR)), '') AS GuaranteeCode,
    ff.IT5UGFFNSEQ AS FundSequence,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') AS FundCode,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') AS SecondaryFundCode,
    ff.IT5UGFFDACF AS FundActivationDate,
    ff.IT5UGFFDIAC AS FundEndDate,
    CAST(NULL AS decimal(18, 6)) AS FundPercentage,
    NULLIF(LTRIM(RTRIM(ff.IT5UGFFBRAC)), '') AS FundIndicator
FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN {fj1}.[TB5UGFF] AS ff WITH (NOLOCK)
    ON ff.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
  AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
ORDER BY AddendumSequence DESC, GuaranteeCode, FundSequence;";

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
  AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
    OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
ORDER BY EliaContractId, AddendumSequence DESC, GuaranteeCode, FundSequence;";

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
    (
        SELECT COUNT_BIG(*)
        FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
        INNER JOIN {fj1}.[TB5UGFF] AS ff WITH (NOLOCK)
            ON ff.IT5UCONAIDN = h.IT5UCONAIDN
        WHERE h.IT5HDMDAIDN = @DemandId
          AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
            OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
    ) +
    (
        SELECT COUNT_BIG(*)
        FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
        INNER JOIN {fj1}.[TB5UPRF] AS prf WITH (NOLOCK)
            ON prf.IT5UCONAIDN = h.IT5UCONAIDN
        WHERE h.IT5HDMDAIDN = @DemandId
          AND NULLIF(LTRIM(RTRIM(prf.IT5UPRFCFND)), '') IS NOT NULL
    ) AS FundFeatureCount;";
        }

        private static void AddLegacyContextQueries(
            IDictionary<string, string> queries,
            string fj1)
        {
            queries["GET_INTERNAL_ID"] = @"
SELECT TOP (1)
    NO_CNT
FROM [LV].[SCNTT0] WITH (NOLOCK)
WHERE REPLACE(REPLACE(LTRIM(RTRIM(NO_CNT_EXTENDED)), '-', ''), ' ', '') =
      REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')
   OR (
          LEN(REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')) = 9
      AND SUBSTRING(REPLACE(REPLACE(LTRIM(RTRIM(NO_CNT_EXTENDED)), '-', ''), ' ', ''), 2, 9) =
          REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')
      )
ORDER BY NO_CNT;";

            queries["GET_ELIA_ID"] = $@"
SELECT TOP (1)
    IT5UCONAIDN,
    NULLIF(LTRIM(RTRIM(IT5UCONLREFEXN)), '') AS ContractNumber
FROM {fj1}.[TB5UCON] WITH (NOLOCK)
WHERE REPLACE(REPLACE(LTRIM(RTRIM(IT5UCONLREFEXN)), '-', ''), ' ', '') LIKE
      REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '') + '%'
   OR (
          LEN(REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')) = 9
      AND SUBSTRING(REPLACE(REPLACE(LTRIM(RTRIM(IT5UCONLREFEXN)), '-', ''), ' ', ''), 2, 9) =
          REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')
      )
ORDER BY IT5UCONAIDN DESC;";

            queries["GET_ELIA_DEMAND_IDS"] = $@"
SELECT DISTINCT
    IT5HDMDAIDN
FROM {fj1}.[TB5HELT] WITH (NOLOCK)
WHERE IT5UCONAIDN = @EliaId
ORDER BY IT5HDMDAIDN;";

            queries["GET_CONTRACT_BY_DEMAND"] = $@"
SELECT TOP (1)
    NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') AS IT5UCONLREFEXN
FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
INNER JOIN {fj1}.[TB5UCON] AS u WITH (NOLOCK)
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId
  AND NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') IS NOT NULL
ORDER BY
    u.IT5UCONAIDN DESC;";

            queries["GET_FULL_CONTEXT_BY_DEMAND"] = $@"
;WITH DemandData AS
(
    SELECT TOP (1)
        d.IT5HDMDAIDN AS DemandId,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') AS ProductCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') AS ProcessCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') AS ChannelCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCAPL)), '') AS ApplicationChannelCode,
        NULLIF(LTRIM(RTRIM(d.IT5HDMDCSTA)), '') AS DemandStatus,
        d.IT5HDMDAIDNDMDREF AS ReferenceDemandId,
        d.IT5HDMDAIDNDMDLIE AS LinkedDemandId,
        d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate
    FROM {fj1}.[TB5HDMD] AS d WITH (NOLOCK)
    WHERE d.IT5HDMDAIDN = @DemandId
    ORDER BY
        CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') IS NULL THEN 1 ELSE 0 END,
        CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') IS NULL THEN 1 ELSE 0 END,
        d.IT5HDMDDTISMAJDB2 DESC
),
ContractData AS
(
    SELECT TOP (1)
        demand.DemandId,
        h.IT5UCONAIDN AS UconId,
        NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') AS ContractNumber,
        demand.ProductCode,
        demand.ProcessCode,
        demand.ChannelCode,
        demand.ApplicationChannelCode,
        demand.DemandStatus,
        demand.LastEliaUpdate
    FROM DemandData AS demand
    LEFT JOIN {fj1}.[TB5HELT] AS h WITH (NOLOCK)
        ON (
               h.IT5HDMDAIDN = demand.DemandId
            OR (demand.ReferenceDemandId IS NOT NULL AND h.IT5HDMDAIDN = demand.ReferenceDemandId)
            OR (demand.LinkedDemandId IS NOT NULL AND h.IT5HDMDAIDN = demand.LinkedDemandId)
           )
    LEFT JOIN {fj1}.[TB5UCON] AS u WITH (NOLOCK)
        ON u.IT5UCONAIDN = h.IT5UCONAIDN
    ORDER BY
        CASE WHEN h.IT5UCONAIDN IS NULL THEN 1 ELSE 0 END,
        CASE WHEN NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') IS NULL THEN 1 ELSE 0 END,
        CASE WHEN h.IT5HDMDAIDN = demand.DemandId THEN 0 ELSE 1 END,
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
    ISNULL(fundInfo.FundFeatureCount, 0) AS FundFeatureCount,
    CASE
        WHEN contractData.DemandId IS NULL THEN 'DEMAND NOT FOUND'
        WHEN contractData.UconId IS NULL THEN 'DEMAND FOUND - ELIA CONTRACT NOT LINKED'
        WHEN contractData.ContractNumber IS NULL THEN 'ELIA FOUND - CONTRACT NUMBER NOT AVAILABLE'
        WHEN contractData.ProductCode IS NULL THEN 'ELIA FOUND - PRODUCT NOT FOUND'
        WHEN contractData.ProcessCode IS NULL THEN 'ELIA FOUND - PROCESS NOT FOUND'
        WHEN contractData.ChannelCode IS NULL THEN 'ELIA FOUND - CHANNEL NOT FOUND'
        WHEN lisa.InternalId IS NULL THEN 'ELIA FOUND - LISA NOT FOUND'
        WHEN premium.GrossPremium IS NULL THEN 'CONTRACT FOUND - PREMIUM NOT FOUND'
        ELSE 'OK'
    END AS DiagnosticStatus
FROM ContractData AS contractData
OUTER APPLY
(
    SELECT TOP (1)
        s.NO_CNT AS InternalId,
        LTRIM(RTRIM(s.NO_CNT_EXTENDED)) AS LisaContractNumber
    FROM [LV].[SCNTT0] AS s WITH (NOLOCK)
    WHERE REPLACE(LTRIM(RTRIM(s.NO_CNT_EXTENDED)), '-', '') =
          REPLACE(LTRIM(RTRIM(contractData.ContractNumber)), '-', '')
    ORDER BY s.NO_CNT
) AS lisa
OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium
    FROM {fj1}.[TB5UPRP] AS p WITH (NOLOCK)
    WHERE p.IT5UCONAIDN = contractData.UconId
    ORDER BY
        CASE WHEN p.IT5UPRPUBRU IS NOT NULL AND p.IT5UPRPUBRU > 0 THEN 0 ELSE 1 END,
        p.IT5UAVENSEQ DESC,
        CASE WHEN p.IT5UPRPUNET IS NOT NULL THEN 0 ELSE 1 END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium
OUTER APPLY
(
    SELECT
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UGFF] AS ff WITH (NOLOCK)
            WHERE ff.IT5UCONAIDN = contractData.UconId
              AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
        ) +
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UPRF] AS prf WITH (NOLOCK)
            WHERE prf.IT5UCONAIDN = contractData.UconId
              AND NULLIF(LTRIM(RTRIM(prf.IT5UPRFCFND)), '') IS NOT NULL
        ) AS FundFeatureCount
) AS fundInfo;";

            queries["GET_FULL_CONTEXT_BY_CONTRACT"] = $@"
;WITH ContractData AS
(
    SELECT TOP (1)
        demand.DemandId,
        u.IT5UCONAIDN AS UconId,
        NULLIF(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '') AS ContractNumber,
        demand.ProductCode,
        demand.ProcessCode,
        demand.ChannelCode,
        demand.ApplicationChannelCode,
        demand.DemandStatus
    FROM {fj1}.[TB5UCON] AS u WITH (NOLOCK)
    OUTER APPLY
    (
        SELECT TOP (1)
            h.IT5HDMDAIDN AS DemandId,
            NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') AS ProductCode,
            NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') AS ProcessCode,
            NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') AS ChannelCode,
            NULLIF(LTRIM(RTRIM(d.IT5HDMDCAPL)), '') AS ApplicationChannelCode,
            NULLIF(LTRIM(RTRIM(d.IT5HDMDCSTA)), '') AS DemandStatus,
            d.IT5HDMDDTISMAJDB2 AS LastEliaUpdate
        FROM {fj1}.[TB5HELT] AS h WITH (NOLOCK)
        LEFT JOIN {fj1}.[TB5HDMD] AS d WITH (NOLOCK)
            ON d.IT5HDMDAIDN = h.IT5HDMDAIDN
        WHERE h.IT5UCONAIDN = u.IT5UCONAIDN
        ORDER BY
            CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPRD)), '') IS NULL THEN 1 ELSE 0 END,
            CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCPCS)), '') IS NULL THEN 1 ELSE 0 END,
            CASE WHEN NULLIF(LTRIM(RTRIM(d.IT5HDMDCCDI)), '') IS NULL THEN 1 ELSE 0 END,
            d.IT5HDMDDTISMAJDB2 DESC,
            h.IT5HDMDAIDN DESC
    ) AS demand
    WHERE REPLACE(REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', ''), ' ', '') LIKE
          REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '') + '%'
       OR (
              LEN(REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')) = 9
          AND SUBSTRING(REPLACE(REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', ''), ' ', ''), 2, 9) =
              REPLACE(REPLACE(LTRIM(RTRIM(@ContractNumber)), '-', ''), ' ', '')
          )
    ORDER BY u.IT5UCONAIDN DESC
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
    ISNULL(fundInfo.FundFeatureCount, 0) AS FundFeatureCount,
    CASE
        WHEN contractData.ContractNumber IS NULL THEN 'CONTRACT NOT FOUND'
        WHEN contractData.ProductCode IS NULL THEN 'ELIA FOUND - PRODUCT NOT FOUND'
        WHEN contractData.ProcessCode IS NULL THEN 'ELIA FOUND - PROCESS NOT FOUND'
        WHEN contractData.ChannelCode IS NULL THEN 'ELIA FOUND - CHANNEL NOT FOUND'
        WHEN lisa.InternalId IS NULL THEN 'ELIA FOUND - LISA NOT FOUND'
        WHEN premium.GrossPremium IS NULL THEN 'CONTRACT FOUND - PREMIUM NOT FOUND'
        ELSE 'OK'
    END AS DiagnosticStatus
FROM ContractData AS contractData
OUTER APPLY
(
    SELECT TOP (1)
        s.NO_CNT AS InternalId,
        LTRIM(RTRIM(s.NO_CNT_EXTENDED)) AS LisaContractNumber
    FROM [LV].[SCNTT0] AS s WITH (NOLOCK)
    WHERE REPLACE(LTRIM(RTRIM(s.NO_CNT_EXTENDED)), '-', '') =
          REPLACE(LTRIM(RTRIM(contractData.ContractNumber)), '-', '')
    ORDER BY s.NO_CNT
) AS lisa
OUTER APPLY
(
    SELECT TOP (1)
        p.IT5UAVENSEQ AS PremiumSequence,
        p.IT5UPRPUBRU AS GrossPremium,
        p.IT5UPRPUNET AS NetPremium
    FROM {fj1}.[TB5UPRP] AS p WITH (NOLOCK)
    WHERE p.IT5UCONAIDN = contractData.UconId
    ORDER BY
        CASE WHEN p.IT5UPRPUBRU IS NOT NULL AND p.IT5UPRPUBRU > 0 THEN 0 ELSE 1 END,
        p.IT5UAVENSEQ DESC,
        CASE WHEN p.IT5UPRPUNET IS NOT NULL THEN 0 ELSE 1 END,
        p.IT5UPRPUBRU DESC,
        p.IT5UPRPUNET DESC
) AS premium
OUTER APPLY
(
    SELECT
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UGFF] AS ff WITH (NOLOCK)
            WHERE ff.IT5UCONAIDN = contractData.UconId
              AND (NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFND)), '') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ff.IT5UGFFCFNDDSI)), '') IS NOT NULL)
        ) +
        (
            SELECT COUNT_BIG(*)
            FROM {fj1}.[TB5UPRF] AS prf WITH (NOLOCK)
            WHERE prf.IT5UCONAIDN = contractData.UconId
              AND NULLIF(LTRIM(RTRIM(prf.IT5UPRFCFND)), '') IS NOT NULL
        ) AS FundFeatureCount
) AS fundInfo;";
        }

        private static void AddBranch23MovementQueries(IDictionary<string, string> queries)
        {
            queries["BR23_LAST_FUND_MOVEMENT"] = @"
SELECT
    ISNULL(MAX(movement.NO_ORD_MVT_FONDS), 0) AS LastMovementNumber
FROM [LV].[FMVGT0] AS movement WITH (NOLOCK)
WHERE movement.C_STE = @Company
  AND movement.NO_CNT = @Contract;";

            queries["BR23_NEW_FUND_MOVEMENT_FUNDS"] = @"
SELECT
    movement.NO_ORD_MVT_FONDS,
    movement.TY_MVT_FONDS,
    movement.C_ETAT_MVT_FONDS,
    NULLIF(LTRIM(RTRIM(movement.NM_JOB_DMOD)), '') AS LastJob,
    NULLIF(LTRIM(RTRIM(detail.C_FONDS)), '') AS FundCode
FROM [LV].[FMVGT0] AS movement WITH (NOLOCK)
INNER JOIN [LV].[FMVDT0] AS detail WITH (NOLOCK)
    ON detail.C_STE = movement.C_STE
   AND detail.NO_CNT = movement.NO_CNT
   AND detail.NO_ORD_MVT_FONDS = movement.NO_ORD_MVT_FONDS
WHERE movement.C_STE = @Company
  AND movement.NO_CNT = @Contract
  AND movement.NO_ORD_MVT_FONDS > @LastMovementBefore
  AND NULLIF(LTRIM(RTRIM(detail.C_FONDS)), '') IS NOT NULL
ORDER BY movement.NO_ORD_MVT_FONDS, detail.C_FONDS;";
        }
    }
}
