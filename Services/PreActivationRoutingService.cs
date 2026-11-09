using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Sql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Résout le contexte nécessaire à l'activation d'un contrat
    /// directement depuis les données ELIA.
    ///
    /// Ce service :
    /// - ne lance aucun JCL ;
    /// - ne modifie aucune donnée ;
    /// - ne dépend pas de l'existence préalable du contrat dans LISA ;
    /// - recherche automatiquement le Demand ID dans les environnements
    ///   configurés ;
    /// - récupère le produit, le process et les channels ELIA ;
    /// - résout automatiquement BANK (00382/8) ou BROKER_NEW_GEN (80819/H) ;
    /// - récupère le contrat ELIA et son numéro externe ;
    /// - récupère la prime et la méthode de paiement lorsqu'elles existent ;
    /// - récupère les éventuelles caractéristiques fonds de TB5UGFF ;
    /// - détermine la classification Standard, BR23 ou avec composante fonds.
    ///
    /// Les listes de produits BR23, de processus pris en charge et
    /// d'environnements sont centralisées dans
    /// Settings.PreActivationRouting.
    /// </summary>
    public sealed class PreActivationRoutingService
    {
        // =============================================================
        // API PUBLIQUE
        // =============================================================

        /// <summary>
        /// Résout automatiquement le contexte préactivation à partir
        /// d'un Demand ID textuel.
        ///
        /// Tous les environnements définis dans
        /// Settings.PreActivationRouting.SupportedEnvironments sont
        /// interrogés.
        /// </summary>
        public async Task<PreActivationRoutingInfo>
            ResolveFromDemandAsync(
                string rawDemandId,
                CancellationToken cancellationToken = default)
        {
            decimal demandId =
                ParseDemandId(rawDemandId);

            return await ResolveFromDemandAsync(
                    demandId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Résout automatiquement le contexte préactivation à partir
        /// d'un Demand ID numérique.
        ///
        /// Lorsque le Demand ID est trouvé dans plusieurs environnements,
        /// aucun choix arbitraire n'est effectué.
        /// </summary>
        public async Task<PreActivationRoutingInfo>
            ResolveFromDemandAsync(
                decimal demandId,
                CancellationToken cancellationToken = default)
        {
            ValidateDemandId(demandId);

            cancellationToken.ThrowIfCancellationRequested();

            EnvironmentSearchResult environmentSearch =
                await FindEnvironmentCandidatesAsync(
                        demandId,
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (environmentSearch.Candidates.Count == 0)
            {
                return BuildNotFoundResult(
                    demandId,
                    environmentSearch.LookupErrors);
            }

            if (environmentSearch.Candidates.Count > 1)
            {
                return BuildAmbiguousEnvironmentResult(
                    demandId,
                    environmentSearch.Candidates);
            }

            EnvironmentRoutingCandidate candidate =
                environmentSearch.Candidates[0];

            return await BuildRoutingInfoAsync(
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Résout le contexte dans un environnement explicitement fourni.
        /// </summary>
        public async Task<PreActivationRoutingInfo>
            ResolveFromDemandInEnvironmentAsync(
                string rawDemandId,
                string environment,
                CancellationToken cancellationToken = default)
        {
            decimal demandId =
                ParseDemandId(rawDemandId);

            return await ResolveFromDemandInEnvironmentAsync(
                    demandId,
                    environment,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Résout le contexte dans un environnement explicitement fourni.
        /// </summary>
        public async Task<PreActivationRoutingInfo>
            ResolveFromDemandInEnvironmentAsync(
                decimal demandId,
                string environment,
                CancellationToken cancellationToken = default)
        {
            ValidateDemandId(demandId);

            string normalizedEnvironment =
                Settings.DbConfig.NormalizeEnvironment(
                    environment);

            cancellationToken.ThrowIfCancellationRequested();

            EnvironmentRoutingCandidate? candidate =
                await TryLoadRoutingCandidateAsync(
                        demandId,
                        normalizedEnvironment,
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (candidate == null)
            {
                return BuildNotFoundInEnvironmentResult(
                    demandId,
                    normalizedEnvironment);
            }

            return await BuildRoutingInfoAsync(
                    candidate,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Retourne true lorsque le processus ELIA est pris en charge
        /// par le workflow automatique d'activation.
        /// </summary>
        public static bool IsSupportedActivationProcess(
            string? processCode)
        {
            return Settings.PreActivationRouting
                .IsSupportedActivationProcess(
                    processCode);
        }

        /// <summary>
        /// Retourne true lorsque le ProductCode fait partie du
        /// référentiel BR23 configuré.
        /// </summary>
        public static bool IsKnownBranch23Product(
            string? productCode)
        {
            return Settings.PreActivationRouting
                .IsKnownBranch23Product(
                    productCode);
        }

        // =============================================================
        // CONSTRUCTION DU RÉSULTAT
        // =============================================================

        private static async Task<PreActivationRoutingInfo>
            BuildRoutingInfoAsync(
                EnvironmentRoutingCandidate candidate,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            cancellationToken.ThrowIfCancellationRequested();

            FundFeatureLoadResult fundFeatureLoadResult =
                await TryGetFundFeaturesAsync(
                        candidate.DemandId,
                        candidate.Environment,
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            BranchResolution branchResolution =
                ResolveBranchClassification(
                    candidate.ProductCode,
                    fundFeatureLoadResult.Features);

            PreActivationRoutingStatus routingStatus =
                ResolveRoutingStatus(
                    candidate,
                    branchResolution);

            string routingDiagnostic =
                BuildRoutingDiagnostic(
                    candidate,
                    branchResolution,
                    routingStatus,
                    fundFeatureLoadResult);

            PreActivationRoutingInfo result =
                MapToRoutingInfo(
                    candidate,
                    fundFeatureLoadResult.Features,
                    branchResolution,
                    routingStatus,
                    routingDiagnostic);

            Debug.WriteLine(
                "[PreActivationRouting] Final routing result | " +
                result.ToDiagnosticSummary());

            return result;
        }

        private static PreActivationRoutingInfo MapToRoutingInfo(
            EnvironmentRoutingCandidate candidate,
            IReadOnlyList<EliaFundFeature> fundFeatures,
            BranchResolution branchResolution,
            PreActivationRoutingStatus routingStatus,
            string routingDiagnostic)
        {
            return new PreActivationRoutingInfo
            {
                Environment =
                    Settings.NormalizeShortEnvironment(
                        candidate.Environment),

                DemandId =
                    candidate.DemandId,

                DemandCreationDate =
                    candidate.DemandCreationDate,

                ProductCode =
                    NormalizeCode(
                        candidate.ProductCode),

                ProcessCode =
                    NormalizeCode(
                        candidate.ProcessCode),

                ChannelCode =
                    NormalizeCode(
                        candidate.ChannelCode),

                ApplicationChannelCode =
                    NormalizeCode(
                        candidate.ApplicationChannelCode),

                DemandStatus =
                    NormalizeCode(
                        candidate.DemandStatus),

                ReferenceDemandId =
                    candidate.ReferenceDemandId,

                LinkedDemandId =
                    candidate.LinkedDemandId,

                AddendumSequence =
                    candidate.AddendumSequence,

                LastEliaUpdate =
                    candidate.LastEliaUpdate,

                EliaContractId =
                    candidate.EliaContractId,

                ContractNumber =
                    NullIfWhiteSpace(
                        candidate.ContractNumber),

                PremiumSequence =
                    candidate.PremiumSequence,

                GrossPremium =
                    candidate.GrossPremium,

                NetPremium =
                    candidate.NetPremium,

                PaymentMethodCode =
                    NullIfWhiteSpace(
                        candidate.PaymentMethodCode),

                PremiumGroupType =
                    NullIfWhiteSpace(
                        candidate.PremiumGroupType),

                FundFeatures =
                    fundFeatures ?? Array.Empty<EliaFundFeature>(),

                BranchClassification =
                    branchResolution.Classification,

                BranchDetectionSource =
                    branchResolution.Source,

                BranchDetectionReason =
                    branchResolution.Reason,
                BusinessChannel =
                    PreActivationRoutingInfo.ResolveBusinessChannel(
                        candidate.ChannelCode),

                RoutingStatus =
                    routingStatus,

                RoutingDiagnostic =
                    routingDiagnostic
            };
        }

        // =============================================================
        // RECHERCHE DE L'ENVIRONNEMENT
        // =============================================================

        private async Task<EnvironmentSearchResult>
            FindEnvironmentCandidatesAsync(
                decimal demandId,
                CancellationToken cancellationToken)
        {
            Task<EnvironmentLookupResult>[] lookupTasks =
                Settings.PreActivationRouting
                    .SupportedEnvironments
                    .Select(
                        environment =>
                            TryLoadEnvironmentAsync(
                                demandId,
                                environment,
                                cancellationToken))
                    .ToArray();

            EnvironmentLookupResult[] results =
                await Task.WhenAll(lookupTasks)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            List<EnvironmentRoutingCandidate> candidates =
                results
                    .Where(result => result.Candidate != null)
                    .Select(result => result.Candidate!)
                    .ToList();

            List<string> errors =
                results
                    .Where(
                        result =>
                            !string.IsNullOrWhiteSpace(
                                result.Error))
                    .Select(
                        result =>
                            $"{result.Environment}: {result.Error}")
                    .ToList();

            foreach (string error in errors)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Environment lookup error | " +
                    error);
            }

            return new EnvironmentSearchResult
            {
                Candidates = candidates,
                LookupErrors = errors
            };
        }

        private async Task<EnvironmentLookupResult>
            TryLoadEnvironmentAsync(
                decimal demandId,
                string environment,
                CancellationToken cancellationToken)
        {
            try
            {
                EnvironmentRoutingCandidate? candidate =
                    await TryLoadRoutingCandidateAsync(
                            demandId,
                            environment,
                            cancellationToken)
                        .ConfigureAwait(false);

                return new EnvironmentLookupResult
                {
                    Environment = environment,
                    Candidate = candidate
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Environment lookup failed | " +
                    $"Demand={demandId} | " +
                    $"Environment={environment} | " +
                    $"Error={exception}");

                return new EnvironmentLookupResult
                {
                    Environment = environment,
                    Error =
                        $"{exception.GetType().Name}: " +
                        exception.Message
                };
            }
        }

        // =============================================================
        // CHARGEMENT DU CONTEXTE PRINCIPAL
        // =============================================================

        /// <summary>
        /// Charge le contexte complet depuis GET_PRE_ACTIVATION_ROUTING.
        ///
        /// Si le produit, le process, le channel, l'application channel
        /// ou le statut sont absents du résultat principal, ces valeurs
        /// sont complétées via GET_ELIA_DEMAND_ROUTING.
        /// </summary>
        private static async Task<EnvironmentRoutingCandidate?>
            TryLoadRoutingCandidateAsync(
                decimal demandId,
                string environment,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment =
                Settings.DbConfig.NormalizeEnvironment(
                    environment);

            var database =
                new DatabaseManager(
                    normalizedEnvironment);

            string query =
                SqlQueries.GetQuery(
                    "GET_PRE_ACTIVATION_ROUTING",
                    normalizedEnvironment);

            var parameters =
                new Dictionary<string, object>
                {
                    ["@DemandId"] = demandId
                };

            using DataTable table =
                await database.GetDataAsync(
                        query,
                        parameters,
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine(
                "[PreActivationRouting] Main SQL completed | " +
                $"Demand={demandId} | " +
                $"Environment={normalizedEnvironment} | " +
                $"Rows={table.Rows.Count} | " +
                $"Columns={GetColumnNames(table)}");

            if (table.Rows.Count == 0)
            {
                return null;
            }

            DataRow row =
                SelectBestRoutingRow(
                    table);

            Debug.WriteLine(
                "[PreActivationRouting] Main SQL selected row | " +
                FormatDataRow(row));

            var candidate =
                new EnvironmentRoutingCandidate
                {
                    Environment =
                        normalizedEnvironment,

                    DemandId =
                        GetDecimal(
                            row,
                            "DemandId")
                        ?? demandId,

                    DemandCreationDate =
                        GetDateTime(
                            row,
                            "DemandCreationDate"),

                    ProductCode =
                        GetString(
                            row,
                            "ProductCode"),

                    ProcessCode =
                        GetString(
                            row,
                            "ProcessCode"),

                    ChannelCode =
                        GetString(
                            row,
                            "ChannelCode"),

                    ApplicationChannelCode =
                        GetString(
                            row,
                            "ApplicationChannelCode"),

                    DemandStatus =
                        GetString(
                            row,
                            "DemandStatus"),

                    EliaContractId =
                        GetDecimal(
                            row,
                            "EliaContractId"),

                    ContractNumber =
                        GetString(
                            row,
                            "ContractNumber"),

                    PremiumSequence =
                        GetInt32(
                            row,
                            "PremiumSequence"),

                    GrossPremium =
                        GetDecimal(
                            row,
                            "GrossPremium"),

                    NetPremium =
                        GetDecimal(
                            row,
                            "NetPremium"),

                    PaymentMethodCode =
                        GetString(
                            row,
                            "PaymentMethodCode"),

                    PremiumGroupType =
                        GetString(
                            row,
                            "PremiumGroupType"),

                    ReferenceDemandId =
                        GetDecimal(
                            row,
                            "ReferenceDemandId"),

                    LinkedDemandId =
                        GetDecimal(
                            row,
                            "LinkedDemandId"),

                    AddendumSequence =
                        GetInt32(
                            row,
                            "AddendumSequence"),

                    LastEliaUpdate =
                        GetDateTime(
                            row,
                            "LastEliaUpdate"),

                    RawRoutingStatus =
                        GetString(
                            row,
                            "RoutingStatus"),

                    MainQuerySucceeded = true,
                    DemandRoutingFallbackUsed = false
                };

            Debug.WriteLine(
                "[PreActivationRouting] Main candidate mapped | " +
                BuildCandidateDebugSummary(candidate));

            EnvironmentRoutingCandidate completedCandidate =
                await CompleteMissingDemandRoutingAsync(
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);

            Debug.WriteLine(
                "[PreActivationRouting] Candidate completed | " +
                BuildCandidateDebugSummary(completedCandidate));

            return completedCandidate;
        }

        /// <summary>
        /// Complète les informations directement portées par TB5HDMD
        /// lorsque la requête principale retourne un contexte partiel.
        ///
        /// La requête GET_ELIA_DEMAND_ROUTING correspond au SELECT simple
        /// validé dans SQL Server Management Studio et retourne notamment :
        /// ProductCode, ProcessCode, ChannelCode,
        /// ApplicationChannelCode et DemandStatus.
        /// </summary>
        private static async Task<EnvironmentRoutingCandidate>
            CompleteMissingDemandRoutingAsync(
                EnvironmentRoutingCandidate candidate,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            bool hasProduct =
                !string.IsNullOrWhiteSpace(
                    candidate.ProductCode);

            bool hasProcess =
                !string.IsNullOrWhiteSpace(
                    candidate.ProcessCode);

            bool hasChannel =
                !string.IsNullOrWhiteSpace(
                    candidate.ChannelCode);

            bool hasApplicationChannel =
                !string.IsNullOrWhiteSpace(
                    candidate.ApplicationChannelCode);

            bool hasDemandStatus =
                !string.IsNullOrWhiteSpace(
                    candidate.DemandStatus);

            if (hasProduct &&
                hasProcess &&
                hasChannel &&
                hasApplicationChannel &&
                hasDemandStatus)
            {
                return candidate;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment =
                Settings.DbConfig.NormalizeEnvironment(
                    candidate.Environment);

            var database =
                new DatabaseManager(
                    normalizedEnvironment);

            string query =
                SqlQueries.GetQuery(
                    "GET_ELIA_DEMAND_ROUTING",
                    normalizedEnvironment);

            using DataTable table =
                await database.GetDataAsync(
                        query,
                        new Dictionary<string, object>
                        {
                            ["@DemandId"] =
                                candidate.DemandId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            Debug.WriteLine(
                "[PreActivationRouting] Demand routing fallback completed | " +
                $"Demand={candidate.DemandId} | " +
                $"Environment={normalizedEnvironment} | " +
                $"Rows={table.Rows.Count} | " +
                $"Columns={GetColumnNames(table)}");

            if (table.Rows.Count == 0)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Demand routing fallback " +
                    "returned no row.");

                return candidate;
            }

            DataRow bestRow =
                SelectBestDemandRoutingRow(
                    table);

            Debug.WriteLine(
                "[PreActivationRouting] Demand routing fallback row | " +
                FormatDataRow(bestRow));

            string fallbackProductCode =
                GetString(
                    bestRow,
                    "ProductCode");

            string fallbackProcessCode =
                GetString(
                    bestRow,
                    "ProcessCode");

            string fallbackChannelCode =
                GetString(
                    bestRow,
                    "ChannelCode");

            string fallbackApplicationChannelCode =
                GetString(
                    bestRow,
                    "ApplicationChannelCode");

            string fallbackDemandStatus =
                GetString(
                    bestRow,
                    "DemandStatus");

            var completedCandidate =
                new EnvironmentRoutingCandidate
                {
                    Environment =
                        candidate.Environment,

                    DemandId =
                        candidate.DemandId,

                    DemandCreationDate =
                        candidate.DemandCreationDate,

                    ProductCode =
                        FirstNotEmpty(
                            candidate.ProductCode,
                            fallbackProductCode),

                    ProcessCode =
                        FirstNotEmpty(
                            candidate.ProcessCode,
                            fallbackProcessCode),

                    ChannelCode =
                        FirstNotEmpty(
                            candidate.ChannelCode,
                            fallbackChannelCode),

                    ApplicationChannelCode =
                        FirstNotEmpty(
                            candidate.ApplicationChannelCode,
                            fallbackApplicationChannelCode),

                    DemandStatus =
                        FirstNotEmpty(
                            candidate.DemandStatus,
                            fallbackDemandStatus),

                    ReferenceDemandId =
                        candidate.ReferenceDemandId,

                    LinkedDemandId =
                        candidate.LinkedDemandId,

                    AddendumSequence =
                        candidate.AddendumSequence,

                    LastEliaUpdate =
                        candidate.LastEliaUpdate,

                    EliaContractId =
                        candidate.EliaContractId,

                    ContractNumber =
                        candidate.ContractNumber,

                    PremiumSequence =
                        candidate.PremiumSequence,

                    GrossPremium =
                        candidate.GrossPremium,

                    NetPremium =
                        candidate.NetPremium,

                    PaymentMethodCode =
                        candidate.PaymentMethodCode,

                    PremiumGroupType =
                        candidate.PremiumGroupType,

                    RawRoutingStatus =
                        candidate.RawRoutingStatus,

                    MainQuerySucceeded =
                        candidate.MainQuerySucceeded,

                    DemandRoutingFallbackUsed = true
                };

            Debug.WriteLine(
                "[PreActivationRouting] Missing routing completed | " +
                $"Demand={completedCandidate.DemandId} | " +
                $"Environment={completedCandidate.Environment} | " +
                $"Product={Display(completedCandidate.ProductCode)} | " +
                $"Process={Display(completedCandidate.ProcessCode)} | " +
                $"Channel={Display(completedCandidate.ChannelCode)} | " +
                $"ApplicationChannel=" +
                $"{Display(completedCandidate.ApplicationChannelCode)} | " +
                $"DemandStatus={Display(completedCandidate.DemandStatus)}");

            return completedCandidate;
        }

        /// <summary>
        /// Sélectionne la ligne principale la plus complète.
        /// </summary>
        private static DataRow SelectBestRoutingRow(
            DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            if (table.Rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "No routing row is available.");
            }

            return table
                .AsEnumerable()
                .OrderByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ProductCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ProcessCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ChannelCode")))
                .ThenByDescending(
                    row =>
                        GetDecimal(
                            row,
                            "EliaContractId")
                        .HasValue)
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ContractNumber")))
                .ThenByDescending(
                    row =>
                        GetInt32(
                            row,
                            "PremiumSequence")
                        ?? int.MinValue)
                .ThenByDescending(
                    row =>
                        GetDateTime(
                            row,
                            "LastEliaUpdate")
                        ?? DateTime.MinValue)
                .First();
        }

        /// <summary>
        /// Sélectionne la ligne TB5HDMD la plus complète.
        /// </summary>
        private static DataRow SelectBestDemandRoutingRow(
            DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            if (table.Rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "No Demand routing row is available.");
            }

            return table
                .AsEnumerable()
                .OrderByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ProductCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ProcessCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ChannelCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "ApplicationChannelCode")))
                .ThenByDescending(
                    row =>
                        !string.IsNullOrWhiteSpace(
                            GetString(
                                row,
                                "DemandStatus")))
                .First();
        }

        // =============================================================
        // CARACTÉRISTIQUES FONDS ELIA
        // =============================================================

        private static async Task<FundFeatureLoadResult>
            TryGetFundFeaturesAsync(
                decimal demandId,
                string environment,
                CancellationToken cancellationToken)
        {
            try
            {
                IReadOnlyList<EliaFundFeature> features =
                    await GetFundFeaturesAsync(
                            demandId,
                            environment,
                            cancellationToken)
                        .ConfigureAwait(false);

                return new FundFeatureLoadResult
                {
                    Features = features
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] " +
                    "Unable to load ELIA fund features | " +
                    $"Demand={demandId} | " +
                    $"Environment={environment} | " +
                    $"Error={exception}");

                return new FundFeatureLoadResult
                {
                    Features =
                        Array.Empty<EliaFundFeature>(),

                    Error =
                        $"{exception.GetType().Name}: " +
                        exception.Message
                };
            }
        }

        private static async Task<IReadOnlyList<EliaFundFeature>>
            GetFundFeaturesAsync(
                decimal demandId,
                string environment,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedEnvironment =
                Settings.DbConfig.NormalizeEnvironment(
                    environment);

            var database =
                new DatabaseManager(
                    normalizedEnvironment);

            string query =
                SqlQueries.GetQuery(
                    "GET_ELIA_FUND_FEATURES_BY_DEMAND",
                    normalizedEnvironment);

            var parameters =
                new Dictionary<string, object>
                {
                    ["@DemandId"] = demandId
                };

            using DataTable table =
                await database.GetDataAsync(
                        query,
                        parameters,
                        cancellationToken)
                    .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (table.Rows.Count == 0)
            {
                return Array.Empty<EliaFundFeature>();
            }

            var features =
                new List<EliaFundFeature>(
                    table.Rows.Count);

            foreach (DataRow row in table.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var feature =
                    new EliaFundFeature
                    {
                        AddendumSequence =
                            GetInt32(
                                row,
                                "AddendumSequence"),

                        GuaranteeCode =
                            NormalizeCode(
                                GetString(
                                    row,
                                    "GuaranteeCode")),

                        FundSequence =
                            GetInt32(
                                row,
                                "FundSequence"),

                        FundCode =
                            NormalizeNullableCode(
                                GetString(
                                    row,
                                    "FundCode")),

                        SecondaryFundCode =
                            NormalizeNullableCode(
                                GetString(
                                    row,
                                    "SecondaryFundCode")),

                        FundActivationDate =
                            GetDateTime(
                                row,
                                "FundActivationDate"),

                        FundEndDate =
                            GetDateTime(
                                row,
                                "FundEndDate"),

                        FundPercentage =
                            GetDecimal(
                                row,
                                "FundPercentage"),

                        FundIndicator =
                            NormalizeNullableCode(
                                GetString(
                                    row,
                                    "FundIndicator"))
                    };

                if (feature.HasFundCode)
                {
                    features.Add(feature);
                }
            }

            return features
                .GroupBy(
                    feature => new
                    {
                        feature.AddendumSequence,

                        GuaranteeCode =
                            NormalizeCode(
                                feature.GuaranteeCode),

                        feature.FundSequence,

                        FundCode =
                            NormalizeCode(
                                feature.FundCode),

                        SecondaryFundCode =
                            NormalizeCode(
                                feature.SecondaryFundCode)
                    })
                .Select(group => group.First())
                .OrderByDescending(
                    feature =>
                        feature.AddendumSequence
                        ?? int.MinValue)
                .ThenBy(
                    feature =>
                        feature.GuaranteeCode,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    feature =>
                        feature.FundSequence
                        ?? int.MinValue)
                .ToList();
        }

        // =============================================================
        // CLASSIFICATION STANDARD / BR23
        // =============================================================

        private static BranchResolution
            ResolveBranchClassification(
                string? productCode,
                IReadOnlyList<EliaFundFeature> fundFeatures)
        {
            string normalizedProduct =
                NormalizeCode(
                    productCode);

            IReadOnlyList<EliaFundFeature> safeFundFeatures =
                fundFeatures ??
                Array.Empty<EliaFundFeature>();

            if (Settings.PreActivationRouting
                .IsKnownBranch23Product(
                    normalizedProduct))
            {
                return new BranchResolution
                {
                    Classification =
                        ContractBranchClassification.Branch23,

                    Source =
                        BranchDetectionSource.EliaProductCode,

                    Reason =
                        $"ELIA ProductCode {normalizedProduct} " +
                        "is configured as Branch 23."
                };
            }

            if (safeFundFeatures.Count > 0)
            {
                return new BranchResolution
                {
                    Classification =
                        ContractBranchClassification
                            .CompositeWithFunds,

                    Source =
                        BranchDetectionSource.EliaFundFeatures,

                    Reason =
                        $"ELIA ProductCode " +
                        $"{Display(normalizedProduct)} is not in " +
                        "the confirmed Branch 23 product list, but " +
                        $"{safeFundFeatures.Count} fund feature(s) " +
                        "were found in TB5UGFF."
                };
            }

            /*
             * L'absence de TB5UGFF ne permet pas de prouver qu'un contrat
             * est standard.
             *
             * Il est interdit de classifier automatiquement Standard
             * uniquement parce que TB5UGFF est vide.
             *
             * Les produits standards doivent être confirmés par une règle
             * métier ou un référentiel produit explicite.
             */
            return new BranchResolution
            {
                Classification =
                    ContractBranchClassification.Unknown,

                Source =
                    BranchDetectionSource.Unknown,

                Reason =
                    "No confirmed branch classification exists for " +
                    $"ELIA ProductCode {Display(normalizedProduct)}, " +
                    "and no ELIA fund feature was found. " +
                    "The contract was not classified as Standard " +
                    "because an empty TB5UGFF result is not sufficient " +
                    "evidence."
            };
        }

        // =============================================================
        // DÉTERMINATION DU STATUT
        // =============================================================

        private static PreActivationRoutingStatus ResolveRoutingStatus(
            EnvironmentRoutingCandidate candidate,
            BranchResolution branchResolution)
        {
            if (!candidate.EliaContractId.HasValue)
            {
                return
                    PreActivationRoutingStatus.EliaContractNotLinked;
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.ContractNumber))
            {
                return
                    PreActivationRoutingStatus
                        .ContractNumberNotAvailable;
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.ProductCode))
            {
                return
                    PreActivationRoutingStatus.ProductNotResolved;
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.ProcessCode))
            {
                return
                    PreActivationRoutingStatus.ProcessNotResolved;
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.ChannelCode))
            {
                return
                    PreActivationRoutingStatus.ChannelNotResolved;
            }
            if (PreActivationRoutingInfo.ResolveBusinessChannel(
                    candidate.ChannelCode) ==
                PreActivationBusinessChannel.Unknown)
            {
                return PreActivationRoutingStatus.ChannelNotResolved;
            }

            if (branchResolution.Classification ==
                ContractBranchClassification.Unknown)
            {
                return
                    PreActivationRoutingStatus.BranchNotResolved;
            }

            return
                PreActivationRoutingStatus.Ready;
        }

        // =============================================================
        // DIAGNOSTIC
        // =============================================================

        private static string BuildRoutingDiagnostic(
            EnvironmentRoutingCandidate candidate,
            BranchResolution branchResolution,
            PreActivationRoutingStatus status,
            FundFeatureLoadResult fundFeatureLoadResult)
        {
            var messages =
                new List<string>
                {
                    $"Demand {candidate.DemandId} found in " +
                    $"{candidate.Environment}.",

                    $"Product={Display(candidate.ProductCode)}, " +
                    $"Process={Display(candidate.ProcessCode)}, " +
                    $"Channel={Display(candidate.ChannelCode)}, " +
                    $"ApplicationChannel=" +
                    $"{Display(candidate.ApplicationChannelCode)}.",

                    $"DemandStatus=" +
                    $"{Display(candidate.DemandStatus)}.",

                    $"EliaContractId=" +
                    $"{candidate.EliaContractId?.ToString(
                        CultureInfo.InvariantCulture) ?? "-"}, " +
                    $"ContractNumber=" +
                    $"{Display(candidate.ContractNumber)}.",

                    $"PremiumSequence=" +
                    $"{candidate.PremiumSequence?.ToString(
                        CultureInfo.InvariantCulture) ?? "-"}, " +
                    $"GrossPremium=" +
                    $"{candidate.GrossPremium?.ToString(
                        CultureInfo.InvariantCulture) ?? "-"}, " +
                    $"NetPremium=" +
                    $"{candidate.NetPremium?.ToString(
                        CultureInfo.InvariantCulture) ?? "-"}.",

                    $"PaymentMethod=" +
                    $"{Display(candidate.PaymentMethodCode)}, " +
                    $"PremiumGroupType=" +
                    $"{Display(candidate.PremiumGroupType)}, " +
                    $"BusinessChannel=" +
                    $"{ResolveBusinessChannelName(candidate.ChannelCode)}, " +
                    $"BUCP/CMDPMT=" +
                    $"{ResolvePaymentRoutingSummary(candidate.ChannelCode)}, " +
                    $"FundFeatures=" +
                    $"{fundFeatureLoadResult.Features.Count}.",

                    $"MainQuerySucceeded=" +
                    $"{candidate.MainQuerySucceeded}.",

                    $"DemandRoutingFallbackUsed=" +
                    $"{candidate.DemandRoutingFallbackUsed}.",

                    branchResolution.Reason,

                    $"RoutingStatus={status}."
                };

            if (!string.IsNullOrWhiteSpace(
                    candidate.RawRoutingStatus))
            {
                messages.Add(
                    $"SqlRoutingStatus=" +
                    $"{candidate.RawRoutingStatus.Trim()}.");
            }

            if (!string.IsNullOrWhiteSpace(
                    candidate.ProcessCode) &&
                !IsSupportedActivationProcess(
                    candidate.ProcessCode))
            {
                messages.Add(
                    $"Process {candidate.ProcessCode.Trim()} is not " +
                    "currently supported by the automatic activation " +
                    "workflow.");
            }

            if (!candidate.GrossPremium.HasValue &&
                !candidate.NetPremium.HasValue)
            {
                messages.Add(
                    "No premium was found in TB5UPRP for this " +
                    "ELIA contract.");
            }

            if (string.IsNullOrWhiteSpace(
                    candidate.PaymentMethodCode))
            {
                messages.Add(
                    "No payment method was found in TB5UPMP. " +
                    "This is informational because BUCP/CMDPMT are resolved " +
                    "from the confirmed ELIA ChannelCode mapping.");
            }
            else
            {
                messages.Add(
                    "The ELIA PaymentMethodCode is informational only. " +
                    "BUCP/CMDPMT are resolved from ChannelCode and the raw " +
                    "PaymentMethodCode is not copied to CMDPMT.");
            }

            if (!string.IsNullOrWhiteSpace(
                    fundFeatureLoadResult.Error))
            {
                messages.Add(
                    "Fund-feature loading failed: " +
                    fundFeatureLoadResult.Error);
            }

            return string.Join(
                " ",
                messages);
        }

        private static string BuildCandidateDebugSummary(
            EnvironmentRoutingCandidate candidate)
        {
            return
                $"Demand={candidate.DemandId} | " +
                $"Environment={candidate.Environment} | " +
                $"Product={Display(candidate.ProductCode)} | " +
                $"Process={Display(candidate.ProcessCode)} | " +
                $"Channel={Display(candidate.ChannelCode)} | " +
                $"BusinessChannel={ResolveBusinessChannelName(candidate.ChannelCode)} | " +
                $"BUCP/CMDPMT={ResolvePaymentRoutingSummary(candidate.ChannelCode)} | " +
                $"ApplicationChannel=" +
                $"{Display(candidate.ApplicationChannelCode)} | " +
                $"DemandStatus={Display(candidate.DemandStatus)} | " +
                $"UCON=" +
                $"{candidate.EliaContractId?.ToString(
                    CultureInfo.InvariantCulture) ?? "-"} | " +
                $"Contract={Display(candidate.ContractNumber)} | " +
                $"GrossPremium=" +
                $"{candidate.GrossPremium?.ToString(
                    CultureInfo.InvariantCulture) ?? "-"} | " +
                $"MainQuerySucceeded={candidate.MainQuerySucceeded} | " +
                $"DemandFallbackUsed=" +
                $"{candidate.DemandRoutingFallbackUsed}";
        }

        // =============================================================
        // RÉSULTATS SPÉCIAUX
        // =============================================================

        private static PreActivationRoutingInfo
            BuildNotFoundResult(
                decimal demandId,
                IReadOnlyList<string> lookupErrors)
        {
            string environments =
                string.Join(
                    ", ",
                    Settings.PreActivationRouting
                        .SupportedEnvironments);

            string diagnostic =
                $"Demand {demandId} was not found in the supported " +
                $"environments: {environments}.";

            if (lookupErrors.Count > 0)
            {
                diagnostic +=
                    " One or more environment lookups failed: " +
                    string.Join(
                        " | ",
                        lookupErrors);
            }

            return new PreActivationRoutingInfo
            {
                DemandId =
                    demandId,

                RoutingStatus =
                    PreActivationRoutingStatus.DemandNotFound,

                BranchClassification =
                    ContractBranchClassification.Unknown,

                BranchDetectionSource =
                    BranchDetectionSource.Unknown,

                BranchDetectionReason =
                    "The Demand ID was not found in any supported " +
                    "environment.",

                RoutingDiagnostic =
                    diagnostic
            };
        }

        private static PreActivationRoutingInfo
            BuildNotFoundInEnvironmentResult(
                decimal demandId,
                string environment)
        {
            return new PreActivationRoutingInfo
            {
                Environment =
                    Settings.NormalizeShortEnvironment(
                        environment),

                DemandId =
                    demandId,

                RoutingStatus =
                    PreActivationRoutingStatus.DemandNotFound,

                BranchClassification =
                    ContractBranchClassification.Unknown,

                BranchDetectionSource =
                    BranchDetectionSource.Unknown,

                BranchDetectionReason =
                    $"Demand {demandId} was not found in " +
                    $"{environment}.",

                RoutingDiagnostic =
                    $"Demand {demandId} was not found in " +
                    $"{environment}."
            };
        }

        private static PreActivationRoutingInfo
            BuildAmbiguousEnvironmentResult(
                decimal demandId,
                IReadOnlyList<EnvironmentRoutingCandidate> candidates)
        {
            string environments =
                string.Join(
                    ", ",
                    candidates
                        .Select(
                            candidate =>
                                candidate.Environment)
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .OrderBy(
                            environment =>
                                environment,
                            StringComparer.OrdinalIgnoreCase));

            string candidateDetails =
                string.Join(
                    " | ",
                    candidates.Select(
                        candidate =>
                            $"{candidate.Environment}: " +
                            $"Product={Display(candidate.ProductCode)}, " +
                            $"Process={Display(candidate.ProcessCode)}, " +
                            $"Contract={Display(candidate.ContractNumber)}"));

            return new PreActivationRoutingInfo
            {
                DemandId =
                    demandId,

                RoutingStatus =
                    PreActivationRoutingStatus.EnvironmentAmbiguous,

                BranchClassification =
                    ContractBranchClassification.Unknown,

                BranchDetectionSource =
                    BranchDetectionSource.Unknown,

                BranchDetectionReason =
                    "The branch was not resolved because the same " +
                    "Demand ID exists in multiple environments.",

                RoutingDiagnostic =
                    $"Demand {demandId} exists in multiple " +
                    $"environments: {environments}. No automatic " +
                    $"environment selection was made. " +
                    $"Candidates: {candidateDetails}."
            };
        }

        // =============================================================
        // VALIDATION DU DEMAND ID
        // =============================================================

        private static decimal ParseDemandId(
            string rawDemandId)
        {
            if (string.IsNullOrWhiteSpace(rawDemandId))
            {
                throw new ArgumentException(
                    "Le Demand ID est obligatoire.",
                    nameof(rawDemandId));
            }

            string cleaned =
                rawDemandId
                    .Replace("\u00A0", string.Empty)
                    .Replace("\uFEFF", string.Empty)
                    .Replace(" ", string.Empty)
                    .Trim();

            if (!decimal.TryParse(
                    cleaned,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out decimal demandId))
            {
                throw new ArgumentException(
                    $"Demand ID invalide : '{rawDemandId}'.",
                    nameof(rawDemandId));
            }

            ValidateDemandId(demandId);

            return demandId;
        }

        private static void ValidateDemandId(
            decimal demandId)
        {
            if (demandId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(demandId),
                    demandId,
                    "Le Demand ID doit être strictement positif.");
            }

            if (decimal.Truncate(demandId) != demandId)
            {
                throw new ArgumentException(
                    "Le Demand ID ne peut pas contenir de décimales.",
                    nameof(demandId));
            }

            if (demandId > 999999999999999999m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(demandId),
                    demandId,
                    "Le Demand ID dépasse la précision SQL " +
                    "decimal(18, 0).");
            }
        }

        // =============================================================
        // CONVERSION DES DONNÉES SQL
        // =============================================================

        private static string GetString(
            DataRow row,
            string columnName)
        {
            ValidateDataRowAndColumnName(
                row,
                columnName);

            if (!row.Table.Columns.Contains(
                    columnName) ||
                row[columnName] == DBNull.Value)
            {
                return string.Empty;
            }

            string value =
                row[columnName]
                    ?.ToString()
                    ?.Trim()
                ?? string.Empty;

            return value;
        }

        private static decimal? GetDecimal(
            DataRow row,
            string columnName)
        {
            ValidateDataRowAndColumnName(
                row,
                columnName);

            if (!row.Table.Columns.Contains(
                    columnName) ||
                row[columnName] == DBNull.Value)
            {
                return null;
            }

            object value =
                row[columnName];

            switch (value)
            {
                case decimal decimalValue:
                    return decimalValue;

                case byte byteValue:
                    return byteValue;

                case short shortValue:
                    return shortValue;

                case int intValue:
                    return intValue;

                case long longValue:
                    return longValue;

                case float floatValue:
                    return Convert.ToDecimal(
                        floatValue,
                        CultureInfo.InvariantCulture);

                case double doubleValue:
                    return Convert.ToDecimal(
                        doubleValue,
                        CultureInfo.InvariantCulture);
            }

            string text =
                value.ToString()?.Trim()
                ?? string.Empty;

            if (decimal.TryParse(
                    text,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out decimal invariantResult))
            {
                return invariantResult;
            }

            if (decimal.TryParse(
                    text,
                    NumberStyles.Any,
                    CultureInfo.CurrentCulture,
                    out decimal currentCultureResult))
            {
                return currentCultureResult;
            }

            return null;
        }

        private static int? GetInt32(
            DataRow row,
            string columnName)
        {
            decimal? decimalValue =
                GetDecimal(
                    row,
                    columnName);

            if (!decimalValue.HasValue)
            {
                return null;
            }

            if (decimalValue.Value < int.MinValue ||
                decimalValue.Value > int.MaxValue)
            {
                return null;
            }

            if (decimal.Truncate(
                    decimalValue.Value) !=
                decimalValue.Value)
            {
                return null;
            }

            return decimal.ToInt32(
                decimalValue.Value);
        }

        private static DateTime? GetDateTime(
            DataRow row,
            string columnName)
        {
            ValidateDataRowAndColumnName(
                row,
                columnName);

            if (!row.Table.Columns.Contains(
                    columnName) ||
                row[columnName] == DBNull.Value)
            {
                return null;
            }

            object value =
                row[columnName];

            if (value is DateTime dateTime)
            {
                return dateTime;
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.DateTime;
            }

            string text =
                value.ToString()?.Trim()
                ?? string.Empty;

            if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTime invariantResult))
            {
                return invariantResult;
            }

            if (DateTime.TryParse(
                    text,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTime currentCultureResult))
            {
                return currentCultureResult;
            }

            return null;
        }

        private static void ValidateDataRowAndColumnName(
            DataRow row,
            string columnName)
        {
            ArgumentNullException.ThrowIfNull(row);

            if (string.IsNullOrWhiteSpace(
                    columnName))
            {
                throw new ArgumentException(
                    "Le nom de colonne est obligatoire.",
                    nameof(columnName));
            }
        }

        // =============================================================
        // DIAGNOSTIC DES RÉSULTATS SQL
        // =============================================================

        private static string GetColumnNames(
            DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            return string.Join(
                ", ",
                table.Columns
                    .Cast<DataColumn>()
                    .Select(
                        column =>
                            column.ColumnName));
        }

        private static string FormatDataRow(
            DataRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            return string.Join(
                " | ",
                row.Table.Columns
                    .Cast<DataColumn>()
                    .Select(
                        column =>
                            $"{column.ColumnName}=" +
                            $"{FormatSqlValue(row[column])}"));
        }

        private static string FormatSqlValue(
            object? value)
        {
            if (value == null ||
                value == DBNull.Value)
            {
                return "<NULL>";
            }

            string text =
                value.ToString()
                ?? string.Empty;

            return string.IsNullOrWhiteSpace(text)
                ? "<EMPTY>"
                : text.Trim();
        }

        /// <summary>
        /// Returns the confirmed payment route for one ELIA ChannelCode.
        /// </summary>
        public static (string BusinessChannel, string Bucp, string Cmdpmt)
            ResolveConfirmedPaymentRouting(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank =>
                    ("BANK", "00382", "8"),
                PreActivationBusinessChannel.BrokerNewGeneration =>
                    ("BROKER_NEW_GEN", "80819", "H"),
                _ => ("UNKNOWN", string.Empty, string.Empty)
            };
        }

        private static string ResolveBusinessChannelName(string? channelCode) =>
            ResolveConfirmedPaymentRouting(channelCode).BusinessChannel;

        private static string ResolvePaymentRoutingSummary(string? channelCode)
        {
            (string businessChannel, string bucp, string cmdpmt) =
                ResolveConfirmedPaymentRouting(channelCode);
            return businessChannel == "UNKNOWN"
                ? "-/-"
                : $"{bucp}/{cmdpmt}";
        }

        // =============================================================
        // HELPERS TEXTUELS
        // =============================================================

        private static string FirstNotEmpty(
            string? primary,
            string? fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary.Trim();
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? string.Empty
                : fallback.Trim();
        }

        private static string NormalizeCode(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        private static string? NormalizeNullableCode(
            string? value)
        {
            string normalized =
                NormalizeCode(value);

            return normalized.Length == 0
                ? null
                : normalized;
        }

        private static string? NullIfWhiteSpace(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static string Display(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "-"
                : value.Trim();
        }

        // =============================================================
        // TYPES INTERNES
        // =============================================================

        private sealed class EnvironmentSearchResult
        {
            public IReadOnlyList<EnvironmentRoutingCandidate>
                Candidates
            { get; init; } =
                Array.Empty<EnvironmentRoutingCandidate>();

            public IReadOnlyList<string>
                LookupErrors
            { get; init; } =
                Array.Empty<string>();
        }

        private sealed class EnvironmentLookupResult
        {
            public string Environment { get; init; } =
                string.Empty;

            public EnvironmentRoutingCandidate?
                Candidate
            { get; init; }

            public string? Error { get; init; }
        }

        private sealed class FundFeatureLoadResult
        {
            public IReadOnlyList<EliaFundFeature>
                Features
            { get; init; } =
                Array.Empty<EliaFundFeature>();

            public string? Error { get; init; }
        }

        private sealed class EnvironmentRoutingCandidate
        {
            public string Environment { get; init; } =
                string.Empty;

            public decimal DemandId { get; init; }

            public DateTime? DemandCreationDate { get; init; }

            public string ProductCode { get; init; } =
                string.Empty;

            public string ProcessCode { get; init; } =
                string.Empty;

            public string ChannelCode { get; init; } =
                string.Empty;

            public string ApplicationChannelCode { get; init; } =
                string.Empty;

            public string DemandStatus { get; init; } =
                string.Empty;

            public decimal? ReferenceDemandId { get; init; }

            public decimal? LinkedDemandId { get; init; }

            public int? AddendumSequence { get; init; }

            public DateTime? LastEliaUpdate { get; init; }

            public decimal? EliaContractId { get; init; }

            public string ContractNumber { get; init; } =
                string.Empty;

            public int? PremiumSequence { get; init; }

            public decimal? GrossPremium { get; init; }

            public decimal? NetPremium { get; init; }

            public string PaymentMethodCode { get; init; } =
                string.Empty;

            public string PremiumGroupType { get; init; } =
                string.Empty;

            public string RawRoutingStatus { get; init; } =
                string.Empty;

            public bool MainQuerySucceeded { get; init; }

            public bool DemandRoutingFallbackUsed { get; init; }
        }

        private sealed class BranchResolution
        {
            public ContractBranchClassification Classification
            {
                get;
                init;
            } = ContractBranchClassification.Unknown;

            public BranchDetectionSource Source
            {
                get;
                init;
            } = BranchDetectionSource.Unknown;

            public string Reason { get; init; } =
                string.Empty;
        }
    }
}