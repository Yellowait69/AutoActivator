using System;
using System.Collections.Generic;

namespace AutoActivator.Sql
{
    public static partial class SqlQueries
    {
        private static void AddLisaContractQueries(IDictionary<string, string> queries)
        {
            queries["LV.PCONT0"] =
                "SELECT * FROM [LV].[LV5P02TPCONT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.ELIAT0"] =
                "SELECT * FROM [LV].[ELIAT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.ELIHT0"] =
                "SELECT * FROM [LV].[ELIHT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.SCNTT0"] =
                "SELECT * FROM [LV].[SCNTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.SWBGT0"] =
                "SELECT * FROM [LV].[SWBGT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, C_PROP DESC;";

            queries["LV.SAVTT0"] =
                "SELECT * FROM [LV].[SAVTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, NO_CNT;";

            queries["LV.XRSTT0"] =
                "SELECT * FROM [LV].[XRSTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.SPERT0"] =
                "SELECT * FROM [LV].[SPERT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.ADMDT0"] =
                "SELECT * FROM [LV].[ADMDT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";
        }

        private static void AddLisaPlanQueries(IDictionary<string, string> queries, string fj1)
        {
            queries["FJ1.TB5LPPL"] =
                $"SELECT * FROM {fj1}.[TB5LPPL] WITH (NOLOCK) WHERE IT5LPPLNCON = @InternalId ORDER BY IT5LPPLNAVEREF, IT5LPPLLGAR, IT5LPPLNCON;";

            queries["FJ1.TB5LPPR"] =
                $"SELECT * FROM {fj1}.[TB5LPPR] WITH (NOLOCK) WHERE IT5LPPRNCON = @InternalId ORDER BY IT5LPPRNIDNPLNPRI DESC;";

            queries["FJ1.TB5LGDR"] =
                $"SELECT * FROM {fj1}.[TB5LGDR] WITH (NOLOCK) WHERE IT5LGDRNCON = @InternalId;";
        }

        private static void AddDemandExtractionQueries(IDictionary<string, string> queries, string fj1)
        {
            queries["FJ1.TB5HDMD"] = BuildDemandIdsQuery(fj1, "TB5HDMD");
            queries["FJ1.TB5HDGM"] = BuildDemandIdsQuery(fj1, "TB5HDGM");
            queries["FJ1.TB5HDGD"] = BuildDemandIdsQuery(fj1, "TB5HDGD");
            queries["FJ1.TB5HPRO"] = BuildDemandIdsQuery(fj1, "TB5HPRO");
            queries["FJ1.TB5HEPT"] = BuildDemandIdsQuery(fj1, "TB5HEPT");
            queries["FJ1.TB5HDIC"] = BuildDemandIdsQuery(fj1, "TB5HDIC");
        }

        private static void AddEliaContractQueries(IDictionary<string, string> queries, string fj1)
        {
            queries["FJ1.TB5HELT"] =
                $"SELECT * FROM {fj1}.[TB5HELT] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UCON"] =
                $"SELECT * FROM {fj1}.[TB5UCON] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UGAR"] =
                $"SELECT * FROM {fj1}.[TB5UGAR] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UGFF"] =
                $"SELECT * FROM {fj1}.[TB5UGFF] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId ORDER BY IT5UAVENSEQ DESC, IT5UGARCGAR, IT5UGFFNSEQ;";

            queries["FJ1.TB5UASU"] = $@"
SELECT *
FROM {fj1}.[TB5UASU] WITH (NOLOCK)
WHERE IT5UASUAIDN IN
(
    SELECT IT5UASUAIDN
    FROM {fj1}.[TB5UGAR] WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["FJ1.TB5UCCR"] =
                $"SELECT * FROM {fj1}.[TB5UCCR] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UAVE"] =
                $"SELECT * FROM {fj1}.[TB5UAVE] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UPNR"] = $@"
SELECT *
FROM {fj1}.[TB5UPNR] WITH (NOLOCK)
WHERE IT5UPNRAIDN IN
(
    SELECT IT5UPNRAIDN
    FROM {fj1}.[TB5UAVE] WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["FJ1.TB5UPRP"] = $@"
SELECT *
FROM {fj1}.[TB5UPRP] WITH (NOLOCK)
WHERE IT5UCONAIDN = @EliaId
ORDER BY
    CASE WHEN IT5UPRPUBRU IS NOT NULL AND IT5UPRPUBRU > 0 THEN 0 ELSE 1 END,
    IT5UAVENSEQ DESC,
    CASE WHEN IT5UPRPUNET IS NOT NULL THEN 0 ELSE 1 END,
    IT5UPRPUBRU DESC,
    IT5UPRPUNET DESC;";

            queries["FJ1.TB5UPRS"] =
                $"SELECT * FROM {fj1}.[TB5UPRS] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UPMP"] =
                $"SELECT * FROM {fj1}.[TB5UPMP] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId ORDER BY IT5UAVENSEQ DESC;";

            queries["FJ1.TB5URPP"] =
                $"SELECT * FROM {fj1}.[TB5URPP] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UIVN"] =
                $"SELECT * FROM {fj1}.[TB5UIVN] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";
        }

        private static void AddFinancialQueries(IDictionary<string, string> queries, string fj1)
        {
            queries["LV.PRIST0"] =
                "SELECT * FROM [LV].[PRIST0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, D_ECH;";

            queries["LV.PECHT0"] =
                "SELECT * FROM [LV].[PECHT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, D_ECH, NO_ORD_QUITT;";

            queries["LV.PFIET0"] =
                "SELECT * FROM [LV].[PFIET0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY D_CRT_INST, NO_ORD_INST;";

            queries["LV.PMNTT0"] =
                "SELECT * FROM [LV].[PMNTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, NO_ORD_QUITT;";

            queries["LV.PRCTT0"] =
                "SELECT * FROM [LV].[PRCTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.PSUMT0"] =
                "SELECT * FROM [LV].[PSUMT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.SELTT0"] =
                "SELECT * FROM [LV].[SELTT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["FJ1.TB5LPPF"] =
                $"SELECT * FROM {fj1}.[TB5LPPF] WITH (NOLOCK) WHERE IT5LPPFNCON = @InternalId ORDER BY IT5LPPFCFDS;";

            queries["FJ1.TB5UPRF"] =
                $"SELECT * FROM {fj1}.[TB5UPRF] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UFML"] =
                $"SELECT * FROM {fj1}.[TB5UFML] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["LV.FMVGT0"] =
                "SELECT * FROM [LV].[FMVGT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY TSTAMP_DMOD;";

            queries["LV.FMVDT0"] =
                "SELECT * FROM [LV].[FMVDT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY TSTAMP_DMOD;";

            queries["LV.SFTS"] =
                "SELECT * FROM [LV].[LV5S18TSFTST0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.PINCT0"] =
                "SELECT * FROM [LV].[PINCT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";
        }

        private static void AddClauseAndReserveQueries(IDictionary<string, string> queries, string fj1)
        {
            queries["LV.SCLST0"] =
                "SELECT * FROM [LV].[SCLST0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_AVT, NO_ORD_CLS;";

            queries["LV.SCLRT0"] =
                "SELECT * FROM [LV].[LV5S16TSCLRT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.SCLDT0"] =
                "SELECT * FROM [LV].[LV5S17TSCLDT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["FJ1.TB5UCRB"] =
                $"SELECT * FROM {fj1}.[TB5UCRB] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UDCR"] =
                $"SELECT * FROM {fj1}.[TB5UDCR] WITH (NOLOCK) WHERE IT5UCONAIDN = @EliaId;";

            queries["FJ1.TB5UBEN"] = $@"
SELECT *
FROM {fj1}.[TB5UBEN] WITH (NOLOCK)
WHERE IT5UBENAIDN IN
(
    SELECT IT5UBENAIDN
    FROM {fj1}.[TB5UDCR] WITH (NOLOCK)
    WHERE IT5UCONAIDN = @EliaId
);";

            queries["LV.BSPDT0"] =
                "SELECT * FROM [LV].[BSPDT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.BSPGT0"] =
                "SELECT * FROM [LV].[BSPGT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.BPBAT0"] =
                "SELECT * FROM [LV].[BPBAT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.BPPAT0"] =
                "SELECT * FROM [LV].[BPPAT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId;";

            queries["LV.MWBGT0"] =
                "SELECT * FROM [LV].[MWBGT0] WITH (NOLOCK) WHERE NO_CNT = @InternalId ORDER BY NO_PRJ, C_PROP;";
        }

        private static string BuildDemandIdsQuery(string fj1, string tableName)
        {
            if (string.IsNullOrWhiteSpace(fj1))
            {
                throw new ArgumentException("Le nom qualifié du schéma FJ1 est obligatoire.", nameof(fj1));
            }

            string validatedTableName = ValidateSqlIdentifier(tableName, nameof(tableName));

            return $@"
SELECT *
FROM {fj1}.[{validatedTableName}] WITH (NOLOCK)
WHERE IT5HDMDAIDN IN
(
    SELECT TRY_CONVERT(decimal(18, 0), LTRIM(RTRIM(value)))
    FROM STRING_SPLIT(@DemandIds, ',')
    WHERE TRY_CONVERT(decimal(18, 0), LTRIM(RTRIM(value))) IS NOT NULL
);";
        }

        private static string ValidateSqlIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("L'identifiant SQL est obligatoire.", parameterName);
            }

            string identifier = value.Trim();

            foreach (char character in identifier)
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    throw new ArgumentException($"Identifiant SQL invalide : '{value}'.", parameterName);
                }
            }

            return identifier;
        }
    }
}
