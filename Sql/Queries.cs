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
    /// Découpé en classes partielles :
    /// - Queries.cs : API publique, listes de tables et validation ;
    /// - Queries.PreActivation.cs : Routing préactivation, contexte full et mouvements BR23 ;
    /// - Queries.Tables.cs : Requêtes d'extraction LISA / ELIA par table.
    /// </summary>
    public static partial class SqlQueries
    {
        private static readonly ConcurrentDictionary<string, ReadOnlyDictionary<string, string>>
            QueriesByEnvironment = new(StringComparer.OrdinalIgnoreCase);

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

        public static ReadOnlyDictionary<string, string> Queries { get; } =
            GetQueries("Q000");

        public static ReadOnlyDictionary<string, string> GetQueries(string envSuffix)
        {
            string environment = Settings.DbConfig.NormalizeEnvironment(envSuffix);
            return QueriesByEnvironment.GetOrAdd(environment, BuildQueries);
        }

        public static string GetQuery(string key, string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("La clé SQL est obligatoire.", nameof(key));
            }

            string normalizedKey = key.Trim();
            ReadOnlyDictionary<string, string> queries = GetQueries(envSuffix);

            if (!queries.TryGetValue(normalizedKey, out string? query))
            {
                string environment = Settings.DbConfig.NormalizeEnvironment(envSuffix);
                throw new KeyNotFoundException(
                    $"Requête SQL introuvable : '{normalizedKey}' pour l'environnement '{environment}'.");
            }

            return query;
        }

        public static bool ContainsQuery(string key, string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return GetQueries(envSuffix).ContainsKey(key.Trim());
        }

        public static void ClearCache()
        {
            QueriesByEnvironment.Clear();
        }

        private static ReadOnlyDictionary<string, string> BuildQueries(string envSuffix)
        {
            string environment = Settings.DbConfig.NormalizeEnvironment(envSuffix);
            string lifeDatabase = Settings.DbConfig.GetLifeDatabase(environment);
            string fj1 = $"[{lifeDatabase}].[FJ1]";

            var queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AddPreActivationQueries(queries, fj1);
            AddLegacyContextQueries(queries, fj1);
            AddBranch23MovementQueries(queries);
            AddLisaContractQueries(queries);
            AddLisaPlanQueries(queries, fj1);
            AddDemandExtractionQueries(queries, fj1);
            AddEliaContractQueries(queries, fj1);
            AddFinancialQueries(queries, fj1);
            AddClauseAndReserveQueries(queries, fj1);

            ValidateRequiredQueries(queries);

            return new ReadOnlyDictionary<string, string>(queries);
        }

        private static void ValidateRequiredQueries(IReadOnlyDictionary<string, string> queries)
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

            string[] missingKeys = requiredKeys
                .Where(key => !queries.TryGetValue(key, out string? query) || string.IsNullOrWhiteSpace(query))
                .ToArray();

            if (missingKeys.Length > 0)
            {
                throw new InvalidOperationException(
                    "Requêtes SQL obligatoires absentes : " + string.Join(", ", missingKeys));
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
            if (!queries.TryGetValue(key, out string? query) || string.IsNullOrWhiteSpace(query))
            {
                throw new InvalidOperationException($"Requête SQL obligatoire absente : {key}.");
            }

            string[] missingFragments = requiredFragments
                .Where(fragment => query.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();

            if (missingFragments.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"La requête '{key}' ne contient pas les éléments obligatoires suivants : " +
                string.Join(", ", missingFragments));
        }
    }
}
