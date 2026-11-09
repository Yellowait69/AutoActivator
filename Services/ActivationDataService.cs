using AutoActivator.Services.Branch23Activation;
using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Sql;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Service principal de préparation et d'exécution d'une activation.
    ///
    /// Responsabilités :
    /// - résoudre les identifiants ELIA et LISA ;
    /// - analyser le contexte ELIA avant activation ;
    /// - déterminer automatiquement le produit, le process et la branche ;
    /// - préparer les paramètres JCL ;
    /// - déléguer l'exécution à ActivationOrchestrator ;
    /// - appliquer le workflow fonds uniquement aux contrats BR23 ;
    /// - produire les rapports détaillés.
    /// </summary>
    public sealed class ActivationDataService
    {
        private readonly PreActivationRoutingService
            _preActivationRoutingService;
        private readonly IActivationRoutingService
            _activationRoutingService;

        public ActivationDataService()
            : this(new PreActivationRoutingService(), new ActivationRoutingService())
        {
        }

        public ActivationDataService(
            PreActivationRoutingService preActivationRoutingService)
            : this(preActivationRoutingService, new ActivationRoutingService())
        {
        }

        public ActivationDataService(
            PreActivationRoutingService preActivationRoutingService,
            IActivationRoutingService activationRoutingService)
        {
            _preActivationRoutingService = preActivationRoutingService
                ?? throw new ArgumentNullException(nameof(preActivationRoutingService));
            _activationRoutingService = activationRoutingService
                ?? throw new ArgumentNullException(nameof(activationRoutingService));
        }

        // =============================================================
        // FORMATAGE CONTRAT
        // =============================================================

        /// <summary>
        /// Transforme un numéro de contrat étendu en identifiant contrat
        /// utilisé dans les JCL CNTBEG/CNTEND.
        /// </summary>
        public string FormatContractForJcl(string rawContract)
        {
            return FormatContractStatic(rawContract);
        }

        // =============================================================
        // ROUTING PRÉACTIVATION
        // =============================================================

        /// <summary>
        /// Résout automatiquement le contexte ELIA et l'environnement
        /// à partir d'un Demand ID.
        ///
        /// Cette méthode n'exécute aucun job et ne modifie aucune donnée.
        /// </summary>
        public Task<PreActivationRoutingInfo>
            ResolvePreActivationRoutingAsync(
                string demandId,
                CancellationToken token = default)
        {
            return _preActivationRoutingService
                .ResolveFromDemandAsync(
                    demandId,
                    token);
        }

        /// <summary>
        /// Résout le contexte ELIA dans un environnement explicitement
        /// sélectionné.
        /// </summary>
        public Task<PreActivationRoutingInfo>
            ResolvePreActivationRoutingInEnvironmentAsync(
                string demandId,
                string envSuffix,
                CancellationToken token = default)
        {
            return _preActivationRoutingService
                .ResolveFromDemandInEnvironmentAsync(
                    demandId,
                    envSuffix,
                    token);
        }

        // =============================================================
        // PLAN D'ACTIVATION AUTOMATIQUE
        // =============================================================
        /// <summary>
        /// Résout un plan complet sans soumettre de job et sans modifier les bases.
        /// Cette méthode est utilisée par l'activation unique et chaque ligne du batch.
        /// </summary>
        public async Task<AutomaticActivationPlan>
            ResolveAutomaticActivationPlanAsync(
                string rawInput,
                bool isDemandId,
                string envSuffix,
                string cus,
                bool forceBranch23,
                ActivationInputMode inputMode =
                    ActivationInputMode.SqlAutocomplete,
                IReadOnlyDictionary<string, string>? overrides = null,
                CancellationToken token = default)
        {
            inputMode.EnsureDefined();
            string input = CleanInput(rawInput);
            string normalizedCus = CleanInput(cus);
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException(
                    "A contract number or Demand ID is required.", nameof(rawInput));
            if (string.IsNullOrWhiteSpace(normalizedCus))
                throw new ArgumentException("CUS is required.", nameof(cus));

            string environment = Settings.NormalizeEnvironmentSuffix(envSuffix);
            var effectiveOverrides = new Dictionary<string, string>(
                overrides ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            ActivationLookupResult lookup =
                await ResolveContractForActivationAsync(
                    input, isDemandId, environment, token).ConfigureAwait(false);

            if (!lookup.FoundInElia || IsMissingValue(lookup.ContractNumber))
                return new AutomaticActivationPlan
                {
                    InputMode = inputMode,
                    InputValue = input,
                    InputIsDemandId = isDemandId,
                    ForceBranch23 = forceBranch23,
                    Environment = environment,
                    Cus = normalizedCus,
                    ResumeStrategy = ActivationResumeStrategy.Blocked,
                    Overrides = effectiveOverrides,
                    Diagnostic = "BLOCKED_CONTRACT_NOT_FOUND"
                };

            effectiveOverrides.TryGetValue("BUCP", out string? bucpOverride);
            effectiveOverrides.TryGetValue("CMDPMT", out string? cmdpmtOverride);
            string channelCode = ResolveOverride(
                lookup.ChannelCode, effectiveOverrides, inputMode, "CHANNEL");
            string processCode = ResolveOverride(
                lookup.ProcessCode, effectiveOverrides, inputMode, "PROCESS");

            ActivationRoutingResult paymentRouting =
                _activationRoutingService.Resolve(
                    channelCode,
                    bucpOverride,
                    cmdpmtOverride,
                    inputMode);

            decimal grossPremium = ParseDecimal(lookup.RawPremium);
            if (inputMode.AllowsManualOverrides() &&
                effectiveOverrides.TryGetValue("AMOUNT", out string? amountOverride) &&
                TryParsePositiveAmount(amountOverride, out decimal manualAmount))
                grossPremium = manualAmount;

            PreActivationRoutingInfo? planRouting = null;
            if (!IsMissingValue(lookup.DemandId))
            {
                try
                {
                    planRouting = await _preActivationRoutingService
                        .ResolveFromDemandInEnvironmentAsync(
                            lookup.DemandId,
                            environment,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        "[AutomaticActivationPlan] Business date context unavailable: " +
                        exception.Message);
                }
            }

            DateTime runDate = ResolvePlanRunDate(
                effectiveOverrides,
                inputMode,
                planRouting,
                out string runDateSource);
            bool alreadyActive = !IsMissingValue(lookup.LisaInternalId);

            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContractNumber"] = "SQL ELIA",
                ["EliaId"] = "SQL TB5UCON",
                ["DemandId"] = "SQL TB5HELT",
                ["ProductCode"] = "SQL ELIA",
                ["ProcessCode"] = IsOverride(effectiveOverrides, inputMode, "PROCESS")
                    ? "Manual override" : "SQL TB5HDMD",
                ["ChannelCode"] = IsOverride(effectiveOverrides, inputMode, "CHANNEL")
                    ? "Manual override" : "SQL TB5HDMD",
                ["GrossPremium"] = IsOverride(effectiveOverrides, inputMode, "AMOUNT")
                    ? "Manual override" : "SQL TB5UPRP",
                ["RunDate"] = runDateSource,
                ["BUCP"] = paymentRouting.IsOverrideApplied
                    ? "Manual override" : "Channel routing matrix",
                ["CMDPMT"] = paymentRouting.IsOverrideApplied
                    ? "Manual override" : "Channel routing matrix"
            };

            string diagnostic = alreadyActive
                ? "ALREADY_ACTIVATED"
                : !paymentRouting.IsResolved
                    ? paymentRouting.DiagnosticCode
                    : grossPremium <= 0m
                        ? "BLOCKED_NO_POSITIVE_PREMIUM"
                        : "READY_FOR_PRE_ACTIVATION";

            return new AutomaticActivationPlan
            {
                InputMode = inputMode,
                InputValue = input,
                InputIsDemandId = isDemandId,
                ForceBranch23 = forceBranch23,
                Environment = environment,
                Ims = NormalizeShortEnvironment(environment) == "D" ? "IM7T" : "IM7C",
                Db2 = NormalizeShortEnvironment(environment) == "D" ? "Q2T" : "Q2C",
                Cus = normalizedCus,
                FastCtrl = NormalizeShortEnvironment(environment) == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL" : "I10.DB.CA.FIB.FASTCTRL",
                EliaDatabase = Settings.DbConfig.GetLifeDatabase(environment),
                PaymentDatabase = Settings.DbConfig.GetLisaPaymentDatabase(environment),
                ContractDatabase = Settings.DbConfig.GetLisaContractDatabase(environment),
                ContractNumber = lookup.ContractNumber,
                MainframeContractNumber = lookup.JclContract,
                EliaId = lookup.EliaUconId,
                DemandId = lookup.DemandId,
                ProductCode = lookup.ProductCode,
                ProcessCode = processCode,
                ChannelCode = channelCode,
                ApplicationChannel = lookup.ApplicationChannelCode,
                DemandStatus = lookup.DemandStatus,
                EliaPaymentCode = lookup.PaymentMethodCode,
                DemandLinkCount = IsMissingValue(lookup.DemandId) ? 0 : 1,
                PremiumCandidateCount = grossPremium > 0m ? 1 : 0,
                PaymentCandidateCount =
                    !string.IsNullOrWhiteSpace(lookup.PaymentMethodCode) ||
                    paymentRouting.IsResolved
                        ? 1
                        : 0,
                GrossPremium = grossPremium,
                NetPremium = ParseNullableDecimal(lookup.NetPremium),
                RunDate = runDate,
                RunDateSource = sources["RunDate"],
                PaymentChannel = paymentRouting.PaymentChannel,
                Bucp = paymentRouting.Bucp,
                PaymentCommand = paymentRouting.PaymentCommand,
                RoutingRule = paymentRouting.Rule,
                RoutingStatus = paymentRouting.Status.ToString(),
                IsBranch23 = forceBranch23 ||
                    lookup.BranchClassification == ContractBranchClassification.Branch23,
                IsAlreadyActive = alreadyActive,
                ResumeStrategy = alreadyActive
                    ? ActivationResumeStrategy.AlreadyActive
                    : ActivationResumeStrategy.FullActivation,
                ValueSources = sources,
                Overrides = effectiveOverrides,
                Diagnostic = diagnostic
            };
        }

        private static string ResolveOverride(
            string sqlValue,
            IReadOnlyDictionary<string, string> overrides,
            ActivationInputMode inputMode,
            string key)
        {
            return IsOverride(overrides, inputMode, key)
                ? overrides[key].Trim().ToUpperInvariant()
                : NormalizeCode(sqlValue);
        }

        private static bool IsOverride(
            IReadOnlyDictionary<string, string> overrides,
            ActivationInputMode inputMode,
            string key)
        {
            return inputMode.AllowsManualOverrides() &&
                   overrides.TryGetValue(key, out string? value) &&
                   !string.IsNullOrWhiteSpace(value);
        }

        private static decimal ParseDecimal(string? value)
        {
            return decimal.TryParse(
                (value ?? string.Empty).Replace(',', '.'),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal parsed) ? parsed : 0m;
        }

        private static decimal? ParseNullableDecimal(string? value)
        {
            decimal parsed = ParseDecimal(value);
            return parsed > 0m ? parsed : null;
        }

        private static DateTime ResolvePlanRunDate(
            IReadOnlyDictionary<string, string> overrides,
            ActivationInputMode inputMode,
            PreActivationRoutingInfo? routing,
            out string source)
        {
            if (IsOverride(overrides, inputMode, "DRUN"))
            {
                string value = overrides["DRUN"].Trim();
                string[] formats =
                {
                    "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy"
                };
                if (DateTime.TryParseExact(
                    value,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
                {
                    source = "Manual override";
                    return parsed.Date;
                }

                throw new InvalidOperationException(
                    $"Invalid DRUN override: '{value}'.");
            }

            if (routing?.FirstPaymentDate is DateTime firstPaymentDate)
            {
                source = "TB5UPRP.IT5UPRPDPMRPAI";
                return firstPaymentDate.Date;
            }

            if (routing?.PaymentDate is DateTime paymentDate)
            {
                source = "TB5UPRP.IT5UPRPDVRS";
                return paymentDate.Date;
            }

            if (routing?.DemandCreationDate is DateTime demandCreationDate)
            {
                source = "TB5HDMD.IT5HDMDDCRE";
                return demandCreationDate.Date;
            }

            source = "Execution date fallback";
            return DateTime.Today;
        }

        // =============================================================
        // RÉSOLUTION DES IDENTIFIANTS
        // =============================================================

        /// <summary>
        /// Résout les identifiants contrat ELIA/LISA ainsi que les primes.
        ///
        /// Lorsqu'un Demand ID est fourni, le routing préactivation ELIA
        /// est utilisé en priorité. La résolution legacy reste disponible
        /// comme fallback.
        /// </summary>
        public async Task<ActivationLookupResult>
            ResolveContractForActivationAsync(
                string rawInput,
                bool isDemandId,
                string envSuffix,
                CancellationToken token = default)
        {
            var stopwatch = Stopwatch.StartNew();
            string cleanInput = CleanInput(rawInput);
            var result = new ActivationLookupResult
            {
                RawInput = cleanInput,
                IsDemandId = isDemandId,
                DemandId = isDemandId ? cleanInput : "N/A",
                DbDiagnosticStatus = isDemandId
                    ? "DEMAND NOT FOUND"
                    : "CONTRACT NOT FOUND"
            };

            if (string.IsNullOrWhiteSpace(cleanInput))
            {
                stopwatch.Stop();
                result.LookupDurationMs = stopwatch.ElapsedMilliseconds;
                result.DbError = "Input value is empty.";
                return result;
            }

            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(envSuffix);

            try
            {
                if (isDemandId)
                {
                    bool routed =
                        await TryResolveFromPreActivationRoutingAsync(
                                cleanInput,
                                normalizedEnvironment,
                                result,
                                token)
                            .ConfigureAwait(false);

                    // Lecture directe obligatoire de TB5HDMD. Elle garantit
                    // ProductCode/ProcessCode/ChannelCode même si le routing
                    // complet retourne un contexte partiel.
                    await TryCompleteDirectDemandRoutingAsync(
                            result,
                            normalizedEnvironment,
                            token)
                        .ConfigureAwait(false);

                    if (routed)
                    {
                        await TryCompleteLisaInformationAsync(
                                result,
                                normalizedEnvironment,
                                token)
                            .ConfigureAwait(false);

                        NormalizeLookupResult(result);
                        DebugLookupResult(
                            "Preactivation result",
                            result,
                            normalizedEnvironment);
                        return result;
                    }
                }

                await ResolveUsingLegacyCombinedQueryAsync(
                        cleanInput,
                        isDemandId,
                        normalizedEnvironment,
                        result,
                        token)
                    .ConfigureAwait(false);

                // A zero-row combined lookup is not an exception. Run the
                // direct TB5HELT/TB5UCON fallback explicitly for Demand IDs.
                if (isDemandId &&
                    IsMissingValue(result.ContractNumber))
                {
                    await ResolveUsingFallbackAsync(
                            cleanInput,
                            true,
                            normalizedEnvironment,
                            result,
                            token)
                        .ConfigureAwait(false);
                }

                await TryEnrichLookupFromResolvedDemandAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteDirectDemandRoutingAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                await TryCompleteLisaInformationAsync(
                        result,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                NormalizeLookupResult(result);
                DebugLookupResult(
                    "Legacy result",
                    result,
                    normalizedEnvironment);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendDatabaseError(result, "Primary lookup", exception);
                Debug.WriteLine(
                    "[ActivationLookup] Primary lookup failed | " +
                    $"Input={cleanInput} | " +
                    $"Environment={normalizedEnvironment} | " +
                    $"Error={exception}");

                try
                {
                    await ResolveUsingFallbackAsync(
                            cleanInput,
                            isDemandId,
                            normalizedEnvironment,
                            result,
                            token)
                        .ConfigureAwait(false);

                    await TryEnrichLookupFromResolvedDemandAsync(
                            result,
                            normalizedEnvironment,
                            token)
                        .ConfigureAwait(false);

                    await TryCompleteDirectDemandRoutingAsync(
                            result,
                            normalizedEnvironment,
                            token)
                        .ConfigureAwait(false);

                    await TryCompleteLisaInformationAsync(
                            result,
                            normalizedEnvironment,
                            token)
                        .ConfigureAwait(false);

                    NormalizeLookupResult(result);
                    DebugLookupResult(
                        "Fallback result",
                        result,
                        normalizedEnvironment);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception fallbackException)
                {
                    AppendDatabaseError(
                        result,
                        "Fallback lookup",
                        fallbackException);
                    Debug.WriteLine(
                        "[ActivationLookup] Fallback failed | " +
                        $"Input={cleanInput} | " +
                        $"Environment={normalizedEnvironment} | " +
                        $"Error={fallbackException}");
                }
            }
            finally
            {
                stopwatch.Stop();
                result.LookupDurationMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Utilise PreActivationRoutingService pour résoudre une demande
        /// avant que le contrat existe dans LISA.
        /// </summary>
        private async Task<bool>
    TryResolveFromPreActivationRoutingAsync(
        string demandId,
        string environment,
        ActivationLookupResult result,
        CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);

            PreActivationRoutingInfo routing =
                await _preActivationRoutingService
                    .ResolveFromDemandInEnvironmentAsync(
                        demandId,
                        environment,
                        token)
                    .ConfigureAwait(false);

            if (routing.RoutingStatus ==
                PreActivationRoutingStatus.DemandNotFound)
            {
                return false;
            }

            result.FoundInElia = true;

            // =============================================================
            // IDENTIFIANTS ET ENVIRONNEMENT
            // =============================================================

            result.ResolvedEnvironment =
                routing.Environment;

            result.DemandId =
                routing.DemandId.ToString(
                    CultureInfo.InvariantCulture);

            if (routing.EliaContractId.HasValue)
            {
                result.EliaUconId =
                    routing.EliaContractId.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(
                    routing.ContractNumber))
            {
                result.ContractNumber =
                    routing.ContractNumber.Trim();

                result.JclContract =
                    FormatContractForJcl(
                        result.ContractNumber);
            }

            // =============================================================
            // ROUTING ELIA
            // =============================================================

            result.ProductCode =
                routing.ProductCode;

            result.ProcessCode =
                routing.ProcessCode;

            result.ChannelCode =
                routing.ChannelCode;

            result.ApplicationChannelCode =
                routing.ApplicationChannelCode;

            result.DemandStatus =
                routing.DemandStatus;

            // =============================================================
            // PRIME
            // =============================================================

            if (routing.PremiumSequence.HasValue)
            {
                result.PremiumSequence =
                    routing.PremiumSequence.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                result.RawPremium =
                    routing.GrossPremium.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                result.NetPremium =
                    routing.NetPremium.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            // =============================================================
            // MODE DE PAIEMENT ELIA
            // =============================================================

            result.PaymentMethodCode =
                routing.PaymentMethodCode
                ?? string.Empty;

            result.PremiumGroupType =
                routing.PremiumGroupType
                ?? string.Empty;

            /*
             * PaymentMethodCode contient le code ELIA brut.
             * Il ne faut pas l'utiliser directement comme CMDPMT tant que
             * le mapping ELIA -> JCL n'est pas validé.
             */

            // =============================================================
            // FONDS ET CLASSIFICATION
            // =============================================================

            result.FundFeatureCount =
                routing.FundFeatureCount;

            result.BranchClassification =
                routing.BranchClassification;

            result.BranchDetectionSource =
                routing.BranchDetectionSource;

            result.BranchDetectionReason =
                routing.BranchDetectionReason;

            // =============================================================
            // STATUT ET DIAGNOSTIC DU ROUTING
            // =============================================================

            result.RoutingStatus =
                routing.RoutingStatus;

            result.RoutingDiagnostic =
                routing.RoutingDiagnostic;

            result.DbDiagnosticStatus =
                BuildLookupDiagnosticStatus(
                    routing);

            return true;
        }

        /// <summary>
        /// Enrichit un résultat legacy à partir du Demand ID réellement
        /// retrouvé via TB5HELT. Cette étape est notamment nécessaire
        /// en activation unitaire par numéro de contrat.
        /// </summary>
        private async Task TryEnrichLookupFromResolvedDemandAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);

            token.ThrowIfCancellationRequested();

            if (IsMissingValue(result.DemandId))
            {
                return;
            }

            bool routingAlreadyAvailable =
                !string.IsNullOrWhiteSpace(result.ProductCode) &&
                !string.IsNullOrWhiteSpace(result.ProcessCode) &&
                !string.IsNullOrWhiteSpace(result.ChannelCode);

            if (routingAlreadyAvailable)
            {
                return;
            }

            string resolvedDemandId = result.DemandId.Trim();

            PreActivationRoutingInfo routing =
                await _preActivationRoutingService
                    .ResolveFromDemandInEnvironmentAsync(
                        resolvedDemandId,
                        environment,
                        token)
                    .ConfigureAwait(false);

            if (routing.RoutingStatus ==
                PreActivationRoutingStatus.DemandNotFound)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Enrichment skipped. " +
                    $"Demand {resolvedDemandId} was not found in " +
                    $"{environment}.");

                return;
            }

            ApplyRoutingToLookupResult(result, routing);

            Debug.WriteLine(
                "[PreActivationRouting] Lookup enriched. " +
                $"Demand={result.DemandId}, " +
                $"Contract={result.ContractNumber}, " +
                $"Product={DisplayValue(result.ProductCode)}, " +
                $"Process={DisplayValue(result.ProcessCode)}, " +
                $"Channel={DisplayValue(result.ChannelCode)}, " +
                $"Branch={result.BranchClassification}, " +
                $"Status={result.RoutingStatus}.");
        }

        /// <summary>
        /// Complète directement les données de routing depuis TB5HDMD.
        /// Cette lecture est indépendante des jointures contrat, prime,
        /// paiement et fonds.
        /// </summary>
        private static async Task TryCompleteDirectDemandRoutingAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);
            token.ThrowIfCancellationRequested();

            if (IsMissingValue(result.DemandId))
            {
                return;
            }

            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(environment);
            string demandId = result.DemandId.Trim();

            try
            {
                var database = new DatabaseManager(normalizedEnvironment);
                string query = SqlQueries.GetQuery(
                    "GET_ELIA_DEMAND_ROUTING",
                    normalizedEnvironment);

                using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object>
                        {
                            ["@DemandId"] = demandId
                        },
                        token)
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                Debug.WriteLine(
                    "[DirectDemandRouting] Query completed | " +
                    $"Demand={demandId} | " +
                    $"Environment={normalizedEnvironment} | " +
                    $"Rows={table.Rows.Count} | " +
                    $"Columns={GetColumnNames(table)}");

                if (table.Rows.Count == 0)
                {
                    return;
                }

                DataRow row = table.AsEnumerable()
                    .OrderByDescending(r => HasColumnValue(r, "ProductCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ProcessCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ChannelCode"))
                    .ThenByDescending(r => HasColumnValue(r, "ApplicationChannelCode"))
                    .First();

                Debug.WriteLine(
                    "[DirectDemandRouting] Selected row | " +
                    FormatDataRow(row));

                AssignNormalizedIfMissing(
                    result.ProductCode,
                    GetString(row, "ProductCode"),
                    value => result.ProductCode = value);
                AssignNormalizedIfMissing(
                    result.ProcessCode,
                    GetString(row, "ProcessCode"),
                    value => result.ProcessCode = value);
                AssignNormalizedIfMissing(
                    result.ChannelCode,
                    GetString(row, "ChannelCode"),
                    value => result.ChannelCode = value);
                AssignNormalizedIfMissing(
                    result.ApplicationChannelCode,
                    GetString(row, "ApplicationChannelCode"),
                    value => result.ApplicationChannelCode = value);
                AssignNormalizedIfMissing(
                    result.DemandStatus,
                    GetString(row, "DemandStatus"),
                    value => result.DemandStatus = value);

                result.FoundInElia = true;

                if (!string.IsNullOrWhiteSpace(result.ProductCode) &&
                    (string.IsNullOrWhiteSpace(result.DbDiagnosticStatus) ||
                     result.DbDiagnosticStatus.Contains(
                         "PRODUCT",
                         StringComparison.OrdinalIgnoreCase) ||
                     result.DbDiagnosticStatus.Contains(
                         "DEMAND NOT FOUND",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    result.DbDiagnosticStatus =
                        "ELIA DEMAND ROUTING RESOLVED";
                }

                DebugLookupResult(
                    "Direct TB5HDMD routing completed",
                    result,
                    normalizedEnvironment);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppendDatabaseError(
                    result,
                    "Direct TB5HDMD routing",
                    exception);
                Debug.WriteLine(
                    "[DirectDemandRouting] Failed | " +
                    $"Demand={demandId} | " +
                    $"Environment={normalizedEnvironment} | " +
                    $"Error={exception}");
            }
        }

        private static void ApplyRoutingToLookupResult(
            ActivationLookupResult result,
            PreActivationRoutingInfo routing)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(routing);

            if (!string.IsNullOrWhiteSpace(routing.Environment))
            {
                result.ResolvedEnvironment = routing.Environment.Trim();
            }

            if (routing.DemandId > 0)
            {
                result.DemandId = routing.DemandId.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (routing.EliaContractId.HasValue)
            {
                result.EliaUconId = routing.EliaContractId.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                result.ContractNumber = routing.ContractNumber.Trim();
                result.JclContract = FormatContractStatic(
                    result.ContractNumber);
            }

            if (!string.IsNullOrWhiteSpace(routing.ProductCode))
            {
                result.ProductCode = routing.ProductCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routing.ProcessCode))
            {
                result.ProcessCode =
                    routing.ProcessCode.Trim().ToUpperInvariant();
            }

            if (!string.IsNullOrWhiteSpace(routing.ChannelCode))
            {
                result.ChannelCode = routing.ChannelCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(
                    routing.ApplicationChannelCode))
            {
                result.ApplicationChannelCode =
                    routing.ApplicationChannelCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routing.DemandStatus))
            {
                result.DemandStatus = routing.DemandStatus.Trim();
            }

            if (routing.PremiumSequence.HasValue)
            {
                result.PremiumSequence =
                    routing.PremiumSequence.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                result.RawPremium = routing.GrossPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                result.NetPremium = routing.NetPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            result.PaymentMethodCode =
                routing.PaymentMethodCode ?? string.Empty;
            result.PremiumGroupType =
                routing.PremiumGroupType ?? string.Empty;
            result.FundFeatureCount = routing.FundFeatureCount;
            result.BranchClassification = routing.BranchClassification;
            result.BranchDetectionSource = routing.BranchDetectionSource;
            result.BranchDetectionReason = routing.BranchDetectionReason;
            result.RoutingStatus = routing.RoutingStatus;
            result.RoutingDiagnostic = routing.RoutingDiagnostic;
            result.DbDiagnosticStatus = BuildLookupDiagnosticStatus(routing);
            result.FoundInElia = true;
        }

        private static string BuildLookupDiagnosticStatus(
            PreActivationRoutingInfo routing)
        {
            if (routing.RoutingStatus ==
                PreActivationRoutingStatus.Ready)
            {
                return routing.HasPremium
                    ? "OK"
                    : "ELIA FOUND - PREMIUM NOT FOUND";
            }

            return routing.RoutingStatus switch
            {
                PreActivationRoutingStatus.DemandNotFound =>
                    "DEMAND NOT FOUND",

                PreActivationRoutingStatus.EliaContractNotLinked =>
                    "DEMAND FOUND - ELIA CONTRACT NOT LINKED",

                PreActivationRoutingStatus.ContractNumberNotAvailable =>
                    "ELIA FOUND - CONTRACT NUMBER NOT AVAILABLE",

                PreActivationRoutingStatus.ProductNotResolved =>
                    "ELIA FOUND - PRODUCT NOT RESOLVED",

                PreActivationRoutingStatus.ProcessNotResolved =>
                    "ELIA FOUND - PROCESS NOT RESOLVED",

                PreActivationRoutingStatus.ChannelNotResolved =>
                    "ELIA FOUND - CHANNEL NOT RESOLVED",

                PreActivationRoutingStatus.BranchNotResolved =>
                    "ELIA FOUND - BRANCH NOT RESOLVED",

                PreActivationRoutingStatus.EnvironmentAmbiguous =>
                    "ELIA FOUND - ENVIRONMENT AMBIGUOUS",

                _ =>
                    routing.RoutingStatus.ToString()
            };
        }

        /// <summary>
        /// Complète les informations LISA lorsqu'elles existent déjà.
        ///
        /// L'absence d'informations LISA avant activation est normale.
        /// </summary>
        private static async Task TryCompleteLisaInformationAsync(
            ActivationLookupResult result,
            string environment,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(
                    result.ContractNumber))
            {
                return;
            }

            try
            {
                // SCNTT0 is hosted in the LISA contract database, not in
                // the generic application database. Using the explicit database
                // prevents false "LISA NOT FOUND" diagnostics before/after BR23.
                string lisaContractDatabase =
                    Settings.DbConfig.GetLisaContractDatabase(environment);
                var database =
                    new DatabaseManager(environment, lisaContractDatabase);

                string query =
                    SqlQueries.GetQuery(
                        "GET_INTERNAL_ID",
                        environment);

                using DataTable table =
                    await database.GetDataAsync(
                            query,
                            new Dictionary<string, object>
                            {
                                ["@ContractNumber"] =
                                    result.ContractNumber
                            },
                            token)
                        .ConfigureAwait(false);

                if (table.Rows.Count == 0)
                {
                    return;
                }

                DataRow row =
                    table.Rows[0];

                if (table.Columns.Contains("NO_CNT") &&
                    row["NO_CNT"] != DBNull.Value)
                {
                    result.LisaInternalId =
                        row["NO_CNT"]
                            ?.ToString()
                            ?.Trim()
                        ?? "N/A";
                }

                result.LisaContractNumber =
                    result.ContractNumber;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[DB Warning] LISA context could not be completed " +
                    $"for {result.ContractNumber}: " +
                    exception.Message);
            }
        }

        private static async Task ResolveUsingLegacyCombinedQueryAsync(
    string cleanInput,
    bool isDemandId,
    string environment,
    ActivationLookupResult result,
    CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(result);

            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(cleanInput))
            {
                throw new ArgumentException(
                    "The contract number or Demand ID is required.",
                    nameof(cleanInput));
            }

            if (string.IsNullOrWhiteSpace(environment))
            {
                throw new ArgumentException(
                    "The environment is required.",
                    nameof(environment));
            }

            var database =
                new DatabaseManager(environment);

            string queryKey =
                isDemandId
                    ? "GET_FULL_CONTEXT_BY_DEMAND"
                    : "GET_FULL_CONTEXT_BY_CONTRACT";

            string parameterName =
                isDemandId
                    ? "@DemandId"
                    : "@ContractNumber";

            string query =
                SqlQueries.GetQuery(
                    queryKey,
                    environment);

            using DataTable table =
                await database.GetDataAsync(
                        query,
                        new Dictionary<string, object>
                        {
                            [parameterName] = cleanInput
                        },
                        token)
                    .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (table.Rows.Count == 0)
            {
                result.FoundInElia = false;

                result.DbDiagnosticStatus =
                    isDemandId
                        ? "DEMAND NOT FOUND"
                        : "CONTRACT NOT FOUND";

                if (!isDemandId)
                {
                    result.ContractNumber =
                        cleanInput;

                    result.JclContract =
                        FormatContractStatic(
                            cleanInput);
                }

                Debug.WriteLine(
                    "[ActivationLookup] Legacy combined query returned " +
                    $"no row. Input={cleanInput}, " +
                    $"IsDemandId={isDemandId}, " +
                    $"Environment={environment}, " +
                    $"Query={queryKey}.");

                return;
            }

            DataRow row =
                table.Rows[0];

            result.FoundInElia = true;

            string? contractNumber =
                GetString(
                    row,
                    "ContractNumber");

            if (!string.IsNullOrWhiteSpace(contractNumber))
            {
                result.ContractNumber =
                    contractNumber.Trim();
            }
            else if (!isDemandId)
            {
                result.ContractNumber =
                    cleanInput;
            }

            if (!string.IsNullOrWhiteSpace(
                    result.ContractNumber))
            {
                result.JclContract =
                    FormatContractStatic(
                        result.ContractNumber);
            }

            // =============================================================
            // IDENTIFIANTS ELIA
            // =============================================================

            AssignIfAvailable(
                row,
                "UconId",
                value =>
                    result.EliaUconId =
                        value.Trim());

            AssignIfAvailable(
                row,
                "DemandId",
                value =>
                    result.DemandId =
                        value.Trim());

            // =============================================================
            // ROUTING ELIA
            // =============================================================

            AssignIfAvailable(
                row,
                "ProductCode",
                value =>
                    result.ProductCode =
                        value.Trim().ToUpperInvariant());

            AssignIfAvailable(
                row,
                "ProcessCode",
                value =>
                    result.ProcessCode =
                        value.Trim().ToUpperInvariant());

            AssignIfAvailable(
                row,
                "ChannelCode",
                value =>
                    result.ChannelCode =
                        value.Trim().ToUpperInvariant());

            AssignIfAvailable(
                row,
                "ApplicationChannelCode",
                value =>
                    result.ApplicationChannelCode =
                        value.Trim().ToUpperInvariant());

            AssignIfAvailable(
                row,
                "DemandStatus",
                value =>
                    result.DemandStatus =
                        value.Trim().ToUpperInvariant());

            /*
             * Le ProcessCode ELIA devient le process technique du lookup
             * uniquement lorsqu'il est pris en charge par AutoActivator.
             */
            if (PreActivationRoutingService
                .IsSupportedActivationProcess(
                    result.ProcessCode))
            {
                result.ProcessCode =
                    result.ProcessCode
                        .Trim()
                        .ToUpperInvariant();
            }

            // =============================================================
            // INFORMATIONS LISA
            // =============================================================

            AssignIfAvailable(
                row,
                "InternalId",
                value =>
                    result.LisaInternalId =
                        value.Trim());

            AssignIfAvailable(
                row,
                "LisaContractNumber",
                value =>
                    result.LisaContractNumber =
                        value.Trim());

            // =============================================================
            // PRIME
            // =============================================================

            AssignIfAvailable(
                row,
                "PremiumSequence",
                value =>
                    result.PremiumSequence =
                        value.Trim());

            AssignIfAvailable(
                row,
                "GrossPremium",
                value =>
                    result.RawPremium =
                        value.Trim());

            AssignIfAvailable(
                row,
                "NetPremium",
                value =>
                    result.NetPremium =
                        value.Trim());

            // =============================================================
            // DIAGNOSTIC SQL
            // =============================================================

            AssignIfAvailable(
                row,
                "DiagnosticStatus",
                value =>
                    result.DbDiagnosticStatus =
                        value.Trim());

            if (string.IsNullOrWhiteSpace(
                    result.DbDiagnosticStatus))
            {
                result.DbDiagnosticStatus =
                    !string.IsNullOrWhiteSpace(
                        result.ContractNumber)
                        ? "ELIA CONTEXT RESOLVED"
                        : isDemandId
                            ? "DEMAND NOT FOUND"
                            : "CONTRACT NOT FOUND";
            }

            Debug.WriteLine(
                "[ActivationLookup] Legacy context resolved | " +
                $"Input={cleanInput} | " +
                $"Environment={environment} | " +
                $"Demand={DisplayValue(result.DemandId)} | " +
                $"UCON={DisplayValue(result.EliaUconId)} | " +
                $"Contract={DisplayValue(result.ContractNumber)} | " +
                $"JCL={DisplayValue(result.JclContract)} | " +
                $"Product={DisplayValue(result.ProductCode)} | " +
                $"Process={DisplayValue(result.ProcessCode)} | " +
                $"Channel={DisplayValue(result.ChannelCode)} | " +
                $"AppChannel=" +
                $"{DisplayValue(result.ApplicationChannelCode)} | " +
                $"DemandStatus={DisplayValue(result.DemandStatus)} | " +
                $"Premium={DisplayValue(result.RawPremium)} | " +
                $"Diagnostic={result.DbDiagnosticStatus}.");
        }

        private async Task ResolveUsingFallbackAsync(
            string cleanInput,
            bool isDemandId,
            string environment,
            ActivationLookupResult result,
            CancellationToken token)
        {
            string? resolvedContract =
                cleanInput;

            if (isDemandId)
            {
                resolvedContract =
                    await GetContractFromDemandAsync(
                            cleanInput,
                            environment,
                            token)
                        .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(
                    resolvedContract))
            {
                return;
            }

            result.FoundInElia = true;
            result.ContractNumber = resolvedContract;

            result.JclContract =
                FormatContractForJcl(
                    resolvedContract);

            result.RawPremium =
                await FetchPremiumAsync(
                        resolvedContract,
                        environment,
                        token)
                    .ConfigureAwait(false);

            result.DbDiagnosticStatus =
                result.RawPremium != "0"
                    ? "OK (FALLBACK)"
                    : "CONTRACT FOUND - PREMIUM NOT FOUND";

            await TryCompleteDirectDemandRoutingAsync(
                    result,
                    environment,
                    token)
                .ConfigureAwait(false);
        }

        public async Task<string?> GetContractFromDemandAsync(
            string demandId,
            string envSuffix,
            CancellationToken token = default)
        {
            string cleanDemandId = CleanInput(demandId);

            if (string.IsNullOrWhiteSpace(cleanDemandId))
            {
                return null;
            }

            if (!decimal.TryParse(
                    cleanDemandId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out decimal numericDemandId))
            {
                throw new ArgumentException(
                    $"Invalid ELIA Demand ID: '{demandId}'.",
                    nameof(demandId));
            }

            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(envSuffix);

            var database = new DatabaseManager(normalizedEnvironment);
            string query = SqlQueries.GetQuery(
                "GET_CONTRACT_BY_DEMAND",
                normalizedEnvironment);

            try
            {
                using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object>
                        {
                            ["@DemandId"] = numericDemandId
                        },
                        token)
                    .ConfigureAwait(false);

                if (table.Rows.Count == 0)
                {
                    Debug.WriteLine(
                        "[Demand Resolution] No row returned | " +
                        $"Demand={numericDemandId} | " +
                        $"Environment={normalizedEnvironment}.");
                    return null;
                }

                DataRow row = table.Rows[0];
                string? contractNumber =
                    GetString(row, "ContractNumber")
                    ?? GetString(row, "IT5UCONLREFEXN");

                if (string.IsNullOrWhiteSpace(contractNumber))
                {
                    throw new InvalidOperationException(
                        "GET_CONTRACT_BY_DEMAND returned a row but no " +
                        "ContractNumber or IT5UCONLREFEXN value.");
                }

                Debug.WriteLine(
                    "[Demand Resolution] Contract resolved | " +
                    $"Demand={numericDemandId} | " +
                    $"Contract={contractNumber} | " +
                    $"Environment={normalizedEnvironment}.");

                return contractNumber.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Unable to resolve the ELIA contract for " +
                    $"Demand ID '{numericDemandId}' in environment " +
                    $"'{normalizedEnvironment}'. " +
                    $"SQL error: {exception.Message}",
                    exception);
            }
        }

        public async Task<string> FetchPremiumAsync(
            string contract,
            string envSuffix,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(contract))
            {
                return "0";
            }

            try
            {
                string normalizedEnvironment =
                    Settings.NormalizeEnvironmentSuffix(
                        envSuffix);

                var database =
                    new DatabaseManager(
                        normalizedEnvironment);

                ReadOnlyDictionary<string, string>
                    environmentQueries =
                        SqlQueries.GetQueries(
                            normalizedEnvironment);

                using DataTable eliaTable =
                    await database.GetDataAsync(
                            environmentQueries["GET_ELIA_ID"],
                            new Dictionary<string, object>
                            {
                                ["@ContractNumber"] =
                                    CleanInput(contract)
                            },
                            token)
                        .ConfigureAwait(false);

                if (eliaTable.Rows.Count == 0)
                {
                    return "0";
                }

                string? eliaUconId =
                    eliaTable.Rows[0]
                        ["IT5UCONAIDN"]
                    ?.ToString()
                    ?.Trim();

                if (string.IsNullOrWhiteSpace(
                        eliaUconId))
                {
                    return "0";
                }

                if (!environmentQueries.TryGetValue(
                        "FJ1.TB5UPRP",
                        out string? premiumQuery))
                {
                    return "0";
                }

                using DataTable premiumTable =
                    await database.GetDataAsync(
                            premiumQuery,
                            new Dictionary<string, object>
                            {
                                ["@EliaId"] =
                                    eliaUconId
                            },
                            token)
                        .ConfigureAwait(false);

                if (premiumTable.Rows.Count == 0 ||
                    !premiumTable.Columns.Contains(
                        "IT5UPRPUBRU"))
                {
                    return "0";
                }

                return premiumTable.Rows[0]
                           ["IT5UPRPUBRU"]
                       ?.ToString()
                       ?.Trim()
                   ?? "0";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[DB Error] FetchPremiumAsync failed " +
                    $"for {contract}: " +
                    exception.Message);

                return "0";
            }
        }

        // =============================================================
        // RÉSOLUTION LISA BR23
        // =============================================================

        /// <summary>
        /// Résout le NO_CNT LISA lorsqu'il existe déjà. Pour un nouveau
        /// contrat BR23, utiliser cette méthode uniquement après la chaîne
        /// BR23 complète.
        /// </summary>
        private async Task<string?>
            ResolveBranch23LisaInternalIdAsync(
                ContractActivationTrace? trace,
                string jclContract,
                string envSuffix,
                CancellationToken token)
        {
            var candidates =
                new List<(string Value, bool IsDemand)>();

            if (trace != null)
            {
                if (!IsMissingValue(
                        trace.ResolvedContract))
                {
                    candidates.Add(
                        (trace.ResolvedContract, false));
                }

                if (!IsMissingValue(
                        trace.RawInput))
                {
                    candidates.Add(
                        (trace.RawInput, trace.IsDemandId));
                }
            }

            if (!string.IsNullOrWhiteSpace(
                    jclContract))
            {
                candidates.Add(
                    (jclContract, false));
            }

            foreach (var candidate in
                     candidates.Distinct())
            {
                ActivationLookupResult lookup =
                    await ResolveContractForActivationAsync(
                            candidate.Value,
                            candidate.IsDemand,
                            envSuffix,
                            token)
                        .ConfigureAwait(false);

                if (!IsMissingValue(
                        lookup.LisaInternalId))
                {
                    if (trace != null)
                    {
                        trace.LisaInternalId =
                            lookup.LisaInternalId;
                    }

                    return lookup.LisaInternalId;
                }
            }

            return null;
        }

        /// <summary>
        /// Waits for final creation of the contract in LV.SCNTT0 after the
        /// complete standard and Branch 23 JCL chain.
        /// </summary>
        private async Task<string?> WaitForFinalLisaCreationAsync(
            ContractActivationTrace? trace,
            string jclContract,
            string envSuffix,
            Action<string>? onProgress,
            CancellationToken token)
        {
            const int maxAttempts = 60;
            const int delayMilliseconds = 5000;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                string? lisaInternalId =
                    await ResolveBranch23LisaInternalIdAsync(
                            trace,
                            jclContract,
                            envSuffix,
                            token)
                        .ConfigureAwait(false);

                if (!IsMissingValue(lisaInternalId))
                {
                    if (trace != null)
                    {
                        trace.LisaInternalId = lisaInternalId!;
                        if (!IsMissingValue(trace.ResolvedContract))
                        {
                            trace.LisaContractNumber =
                                trace.ResolvedContract;
                        }
                    }

                    onProgress?.Invoke(
                        "Final LISA validation succeeded. " +
                        $"NO_CNT={lisaInternalId}.");

                    return lisaInternalId;
                }

                onProgress?.Invoke(
                    "Waiting for final LISA creation in LV.SCNTT0 " +
                    $"({attempt}/{maxAttempts}).");

                if (attempt < maxAttempts)
                {
                    await Task.Delay(delayMilliseconds, token)
                        .ConfigureAwait(false);
                }
            }

            return null;
        }

        // =============================================================
        /// <summary>
        /// Resolves BR23 funds before the contract exists in LISA.
        /// ELIA Demand ID then exact external contract have priority.
        /// Each path checks TB5UGFF then TB5UPRF before manual fallback.
        /// Manual funds are used only after successful empty automatic lookups.
        /// No LISA contract or fund movement is required for this lookup.
        /// </summary>
        private static async Task<(IReadOnlyList<string> Funds, string Source)>
            ResolveBranch23PreActivationFundsAsync(
                ContractActivationTrace? trace,
                string jclContract,
                string envSuffix,
                IBranch23FundSource? fallbackSource,
                CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string environment = Settings.NormalizeEnvironmentSuffix(envSuffix);
            var database = new DatabaseManager(environment);
            string? demandId = !IsMissingValue(trace?.DemandId)
                ? trace!.DemandId.Trim()
                : trace != null && trace.IsDemandId && !IsMissingValue(trace.RawInput)
                    ? trace.RawInput.Trim() : null;
            string? external = !IsMissingValue(trace?.ResolvedContract)
                ? trace!.ResolvedContract.Trim() : null;
            decimal? selectedSequence = null;
            string stage = "initialization";

            try
            {
                if (!string.IsNullOrWhiteSpace(demandId))
                {
                    if (!decimal.TryParse(demandId, NumberStyles.None,
                            CultureInfo.InvariantCulture, out decimal numericDemand) || numericDemand <= 0)
                        throw new InvalidOperationException("Invalid ELIA Demand ID.");

                    // Demand routing sequence is not a fund addendum key.
                    // Resolve the actual contract attachment and require agreement
                    // between its contract/reference/new sequences.
                    stage = "TB5HELT fund attachment context";
                    var attachment = await ResolveBranch23FundAttachmentAsync(
                        database, environment, numericDemand, jclContract, token)
                        .ConfigureAwait(false);
                    selectedSequence = attachment.Sequence;
                    string attachmentDiagnostic =
                        "TB5HELT: ELIA=" + attachment.EliaContractId.ToString(CultureInfo.InvariantCulture) +
                        "; contract/reference/new sequence=" +
                        attachment.Sequence.ToString(CultureInfo.InvariantCulture);
                    Debug.WriteLine("[BR23 FUND CONTEXT] " + attachmentDiagnostic);

                    // Confirm that the demand belongs to the contract being activated.
                    stage = "GET_CONTRACT_BY_DEMAND";
                    using DataTable contracts = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    if (contracts.Rows.Count > 0)
                    {
                        string? linked = GetString(contracts.Rows[0], "ContractNumber")
                            ?? GetString(contracts.Rows[0], "IT5UCONLREFEXN");
                        if (!IsMissingValue(linked))
                        {
                            if (!string.Equals(FormatContractStatic(linked),
                                    FormatContractStatic(jclContract), StringComparison.Ordinal))
                                throw new InvalidOperationException(
                                    "Demand ID and activation contract do not match.");
                            external = linked;
                        }
                    }

                    stage = "GET_ELIA_FUND_FEATURES_BY_DEMAND";
                    using DataTable table = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    ValidateBranch23FundAttachment(table, attachment.EliaContractId);
                    var automatic = SelectAutomaticBranch23Funds(table, selectedSequence);
                    if (automatic.Count > 0)
                        return (automatic, "ELIA TB5UGFF by Demand ID " + demandId + "; " + attachmentDiagnostic);

                    // TB5UGFF can be empty while premium funds exist in TB5UPRF.
                    // Do not hide SQL errors: fallback is only for successful empty reads.
                    stage = "GET_ELIA_PREMIUM_FUNDS_BY_DEMAND";
                    using DataTable premiumFunds = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@DemandId"] = numericDemand }, token)
                        .ConfigureAwait(false);
                    ValidateBranch23FundAttachment(premiumFunds, attachment.EliaContractId);
                    var premiumAutomatic = SelectAutomaticBranch23PremiumFunds(
                        premiumFunds, selectedSequence);
                    if (premiumAutomatic.Count > 0)
                        return (premiumAutomatic, "ELIA TB5UPRF by Demand ID " + demandId + "; " + attachmentDiagnostic);
                }

                // A nine-digit technical number is not a twelve-digit external number.
                // Resolve it by exact transformation, never by prefix or guessed check digits.
                string compact = CompactBranch23External(external);
                if (compact.Length != 12)
                {
                    string supplied = CompactBranch23External(jclContract);
                    if (supplied.Length == 12)
                        external = jclContract;
                    else
                    {
                        string technical = FormatContractStatic(jclContract);
                        if (technical.Length != 9 || technical.Any(c => c < '0' || c > '9'))
                            throw new InvalidOperationException("Invalid technical contract number.");
                        string life = Settings.DbConfig.GetLifeDatabase(environment);
                        string qualifiedLife = "[" + life.Replace("]", "]]") + "]";
                        stage = "Resolve exact external ELIA contract";
                        string query = $@"
SELECT DISTINCT LTRIM(RTRIM(u.IT5UCONLREFEXN)) AS ContractNumber
FROM {qualifiedLife}.[FJ1].[TB5UCON] AS u
CROSS APPLY
(
    SELECT REPLACE(REPLACE(LTRIM(RTRIM(u.IT5UCONLREFEXN)), '-', ''), ' ', '') AS Compact
) AS normalized
WHERE LEN(normalized.Compact) = 12
  AND SUBSTRING(normalized.Compact, 2, 9) = @TechnicalContract;";
                        using DataTable matches = await database.GetDataAsync(query,
                            new Dictionary<string, object> { ["@TechnicalContract"] = technical }, token)
                            .ConfigureAwait(false);
                        if (matches.Rows.Count > 1)
                            throw new InvalidOperationException(
                                "Multiple external ELIA contracts match the technical number; provide the full contract.");
                        external = matches.Rows.Count == 1
                            ? GetString(matches.Rows[0], "ContractNumber") : null;
                    }
                }

                if (!IsMissingValue(external))
                {
                    if (!string.Equals(FormatContractStatic(external),
                            FormatContractStatic(jclContract), StringComparison.Ordinal))
                        throw new InvalidOperationException("Resolved external contract does not match activation target.");
                    stage = "GET_ELIA_FUND_FEATURES_BY_CONTRACT";
                    using DataTable table = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@ContractNumber"] = external! }, token)
                        .ConfigureAwait(false);
                    var automatic = SelectAutomaticBranch23Funds(table, selectedSequence);
                    if (automatic.Count > 0)
                        return (automatic, "ELIA TB5UGFF by contract " + external);

                    // TB5UGFF can be empty while premium funds exist in TB5UPRF.
                    // Do not hide SQL errors: fallback is only for successful empty reads.
                    stage = "GET_ELIA_PREMIUM_FUNDS_BY_CONTRACT";
                    using DataTable premiumFunds = await database.GetDataAsync(
                        SqlQueries.GetQuery(stage, environment),
                        new Dictionary<string, object> { ["@ContractNumber"] = external! }, token)
                        .ConfigureAwait(false);
                    var premiumAutomatic = SelectAutomaticBranch23PremiumFunds(
                        premiumFunds, selectedSequence);
                    if (premiumAutomatic.Count > 0)
                        return (premiumAutomatic, "ELIA TB5UPRF by contract " + external);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                Debug.WriteLine($"[BR23 FUND RESOLUTION] Stage={stage}; Environment={environment}; Error={exception}");
                // SQL errors, conflicting identities and ambiguous history must not
                // be hidden by manual fallback values.
                throw new InvalidOperationException(
                    $"BR23_FUND_LOOKUP_FAILED at {stage}: {exception.Message}", exception);
            }

            if (fallbackSource != null)
            {
                IReadOnlyList<string> configured = await fallbackSource.GetContractFundsAsync(
                    jclContract, environment, token).ConfigureAwait(false);
                var fallback = new List<string>();
                foreach (string code in configured) AddBranch23Fund(fallback, code);
                if (fallback.Count > 0)
                    return (fallback.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        "Configured fallback: no automatic ELIA fund resolved");
            }
            return (Array.Empty<string>(), "No fund resolved from ELIA or configured fallback");
        }

        /// <summary>
        /// Read-only attachment lookup using confirmed TB5HELT columns.
        /// No additional Queries.cs key is required for this correction.
        /// Differing sequences are rejected; no 0-to-1 substitution is performed.
        /// </summary>
        private static async Task<(decimal EliaContractId, decimal Sequence)>
            ResolveBranch23FundAttachmentAsync(
                DatabaseManager database,
                string environment,
                decimal demandId,
                string targetContract,
                CancellationToken token)
        {
            string life = Settings.DbConfig.GetLifeDatabase(environment);
            if (string.IsNullOrWhiteSpace(life))
                throw new InvalidOperationException("LIFE database is not configured.");
            string qualifiedLife = "[" + life.Replace("]", "]]") + "]";
            string query = $@"
SELECT DISTINCT
    h.IT5UCONAIDN AS EliaContractId,
    h.IT5HELTNSEQCON AS ContractSequence,
    h.IT5HELTNSEQAVEREF AS ReferenceAddendumSequence,
    h.IT5HELTNSEQAVENOU AS NewAddendumSequence,
    u.IT5UCONLREFEXN AS ContractNumber
FROM {qualifiedLife}.[FJ1].[TB5HELT] AS h
LEFT JOIN {qualifiedLife}.[FJ1].[TB5UCON] AS u
    ON u.IT5UCONAIDN = h.IT5UCONAIDN
WHERE h.IT5HDMDAIDN = @DemandId;";
            using DataTable context = await database.GetDataAsync(
                query,
                new Dictionary<string, object> { ["@DemandId"] = demandId },
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (context.Rows.Count != 1)
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_AMBIGUOUS: expected exactly one distinct " +
                    "contract attachment for this demand; found " + context.Rows.Count +
                    ". No historical addendum was selected.");

            DataRow row = context.Rows[0];
            string? linkedContract = GetString(row, "ContractNumber");
            string target = FormatContractStatic(targetContract);
            if (target.Length != 9 || target.Any(c => c < '0' || c > '9') ||
                IsMissingValue(linkedContract) ||
                !string.Equals(FormatContractStatic(linkedContract), target,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_CONTRACT_MISMATCH: the demand attachment " +
                    "does not confirm the activation target.");

            decimal id = ReadBranch23AttachmentInteger(row, "EliaContractId");
            decimal contractSequence = ReadBranch23AttachmentInteger(row, "ContractSequence");
            decimal referenceSequence = ReadBranch23AttachmentInteger(row, "ReferenceAddendumSequence");
            decimal newSequence = ReadBranch23AttachmentInteger(row, "NewAddendumSequence");
            if (id <= 0)
                throw new InvalidOperationException("Invalid ELIA attachment contract identifier.");
            if (contractSequence != referenceSequence || contractSequence != newSequence)
                throw new InvalidOperationException(
                    "BR23_ATTACHMENT_SEQUENCE_AMBIGUOUS: contract=" + contractSequence +
                    ", reference=" + referenceSequence + ", new=" + newSequence +
                    ". A process-specific mapping is required; no maximum sequence was selected.");

            Debug.WriteLine("[BR23 ATTACHMENT] " + FormatDataRow(row));
            return (id, contractSequence);
        }

        private static decimal ReadBranch23AttachmentInteger(DataRow row, string column)
        {
            string? text = GetString(row, column);
            if (!decimal.TryParse(text, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out decimal value) ||
                value < 0 || decimal.Truncate(value) != value)
                throw new InvalidOperationException(
                    "Invalid or missing attachment integer: " + column);
            return value;
        }

        private static void ValidateBranch23FundAttachment(DataTable table, decimal expectedId)
        {
            foreach (DataRow row in table.Rows)
            {
                if (ReadBranch23AttachmentInteger(row, "EliaContractId") != expectedId)
                    throw new InvalidOperationException(
                        "BR23_FUND_CONTRACT_MISMATCH: fund rows belong to a different " +
                        "ELIA contract than the verified demand attachment.");
            }
        }

        private static string CompactBranch23External(string? value)
        {
            return CleanInput(value).Replace("-", string.Empty).Replace(" ", string.Empty);
        }

        private static IReadOnlyList<string> SelectAutomaticBranch23Funds(
            DataTable table, decimal? requestedSequence)
        {
            if (table.Rows.Count == 0) return Array.Empty<string>();
            foreach (string column in new[] { "EliaContractId", "AddendumSequence", "FundCode", "SecondaryFundCode" })
                if (!table.Columns.Contains(column))
                    throw new InvalidOperationException("Missing ELIA fund result column: " + column);

            var rows = table.AsEnumerable().ToList();
            var identities = rows.Select(row => GetString(row, "EliaContractId"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (identities.Count != 1 || IsMissingValue(identities[0]))
                throw new InvalidOperationException("Ambiguous or missing ELIA contract identity in fund results.");

            var sequences = new Dictionary<decimal, List<DataRow>>();
            foreach (DataRow row in rows)
            {
                if (!decimal.TryParse(GetString(row, "AddendumSequence"), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out decimal sequence))
                    throw new InvalidOperationException("Missing or invalid fund addendum sequence.");
                if (!sequences.TryGetValue(sequence, out List<DataRow>? group))
                {
                    group = new List<DataRow>();
                    sequences.Add(sequence, group);
                }
                group.Add(row);
            }
            decimal selected;
            if (requestedSequence.HasValue)
            {
                selected = requestedSequence.Value;
                if (!sequences.ContainsKey(selected))
                    throw new InvalidOperationException(
                        "No fund row for the demand addendum " + selected.ToString(CultureInfo.InvariantCulture) +
                        ". Historical addenda are not substituted automatically.");
            }
            else
            {
                if (sequences.Count != 1)
                    throw new InvalidOperationException(
                        "Multiple fund addenda found. Use the Demand ID to identify the intended addendum.");
                selected = sequences.Keys.Single();
            }
            var funds = new List<string>();
            foreach (DataRow row in sequences[selected])
            {
                string? primary = GetString(row, "FundCode");
                string? secondary = GetString(row, "SecondaryFundCode");
                if (string.IsNullOrWhiteSpace(primary))
                    throw new InvalidOperationException(
                        "A fund row has only a secondary code. Its LISA mapping must be verified; " +
                        "the secondary code is not treated as an additional fund.");
                AddBranch23Fund(funds, primary);
                Debug.WriteLine("[BR23 FUND] " + FormatDataRow(row));
            }
            // Dates and percentages remain diagnostic values. This method does
            // not invent date/status semantics or rewrite allocations.
            return funds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Selects premium-fund codes for one confirmed ELIA contract/addendum/guarantee.
        /// Multiple premium groups may reference the same fund; only codes are deduplicated.
        /// Proportions, quantities and financial allocations are never summed or rewritten.
        /// </summary>
        private static IReadOnlyList<string> SelectAutomaticBranch23PremiumFunds(
            DataTable table,
            decimal? requestedSequence)
        {
            if (table.Rows.Count == 0)
                return Array.Empty<string>();

            foreach (string column in new[]
            {
                "EliaContractId", "AddendumSequence", "GuaranteeCode",
                "PremiumGroupType", "FundCode", "SecondaryFundCode"
            })
            {
                if (!table.Columns.Contains(column))
                    throw new InvalidOperationException(
                        "Missing TB5UPRF result column: " + column);
            }

            // Reuse the existing strict contract/addendum validation. It rejects
            // ambiguous contract identities and never substitutes a historical addendum.
            IReadOnlyList<string> funds = SelectAutomaticBranch23Funds(
                table, requestedSequence);

            decimal selectedSequence;
            if (requestedSequence.HasValue)
            {
                selectedSequence = requestedSequence.Value;
            }
            else
            {
                // SelectAutomaticBranch23Funds has already confirmed a single sequence.
                selectedSequence = decimal.Parse(
                    GetString(table.Rows[0], "AddendumSequence")!,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture);
            }

            var guarantees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in table.Rows)
            {
                decimal sequence = decimal.Parse(
                    GetString(row, "AddendumSequence")!,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture);
                if (sequence != selectedSequence)
                    continue;

                string? guarantee = GetString(row, "GuaranteeCode");
                if (IsMissingValue(guarantee))
                    throw new InvalidOperationException(
                        "TB5UPRF contains a missing guarantee code for the selected addendum.");
                guarantees.Add(guarantee!.Trim());
            }

            // No verified guarantee-selection rule is available in this service.
            // Reject multiple guarantees instead of silently merging their allocations.
            if (guarantees.Count != 1)
                throw new InvalidOperationException(
                    "TB5UPRF contains multiple guarantees for the selected addendum: " +
                    string.Join(", ", guarantees.OrderBy(value => value)) +
                    ". An explicit guarantee-selection rule is required.");

            return funds;
        }

        private static void AddBranch23Fund(
            ICollection<string> funds,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                funds.Add(value.Trim().ToUpperInvariant());
            }
        }

        // ROUTING AUTOMATIQUE À L'EXÉCUTION
        // =============================================================

        /// <summary>
        /// Essaie de résoudre le routing ELIA à partir de la trace.
        ///
        /// En cas d'erreur SQL ou de routing, le workflow manuel existant
        /// reste disponible.
        /// </summary>
        private async Task<PreActivationRoutingInfo?>
            TryResolveExecutionRoutingAsync(
                ContractActivationTrace? trace,
                string environment,
                CancellationToken token)
        {
            if (trace == null)
            {
                return null;
            }

            string? demandId = null;

            if (!IsMissingValue(trace.DemandId))
            {
                demandId = trace.DemandId.Trim();
            }
            else if (trace.IsDemandId &&
                     !IsMissingValue(trace.RawInput))
            {
                demandId = trace.RawInput.Trim();
            }

            if (string.IsNullOrWhiteSpace(demandId))
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Execution routing skipped " +
                    "because no valid Demand ID is available.");

                return null;
            }

            try
            {
                PreActivationRoutingInfo routing =
                    await _preActivationRoutingService
                        .ResolveFromDemandInEnvironmentAsync(
                            demandId,
                            environment,
                            token)
                        .ConfigureAwait(false);

                Debug.WriteLine(
                    "[PreActivationRouting] Execution routing | " +
                    $"Demand={demandId} | " +
                    $"Product={DisplayValue(routing.ProductCode)} | " +
                    $"Process={DisplayValue(routing.ProcessCode)} | " +
                    $"Channel={DisplayValue(routing.ChannelCode)} | " +
                    $"Contract={DisplayValue(routing.ContractNumber)} | " +
                    $"Status={routing.RoutingStatus}");

                if (routing.RoutingStatus ==
                    PreActivationRoutingStatus.DemandNotFound)
                {
                    Debug.WriteLine(
                        "[PreActivationRouting] Execution routing ignored. " +
                        $"Demand {demandId} was not found in {environment}.");

                    return null;
                }

                return routing;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "[PreActivationRouting] Unable to resolve " +
                    $"Demand ID '{demandId}' in environment " +
                    $"'{environment}'. " +
                    $"{exception.GetType().Name}: " +
                    exception.Message);

                return null;
            }
        }

        // =============================================================
        // EXÉCUTION DE L'ACTIVATION
        // =============================================================

        private static void ApplyRoutingToTrace(
            ContractActivationTrace trace,
            PreActivationRoutingInfo routing)
        {
            ArgumentNullException.ThrowIfNull(trace);
            ArgumentNullException.ThrowIfNull(routing);

            if (!string.IsNullOrWhiteSpace(routing.ProductCode))
            {
                trace.ProductCode = routing.ProductCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routing.ProcessCode))
            {
                trace.ProcessCode =
                    routing.ProcessCode.Trim().ToUpperInvariant();
            }

            if (!string.IsNullOrWhiteSpace(routing.ChannelCode))
            {
                trace.EliaChannelCode = routing.ChannelCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(
                    routing.ApplicationChannelCode))
            {
                trace.ApplicationChannelCode =
                    routing.ApplicationChannelCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routing.DemandStatus))
            {
                trace.DemandStatus = routing.DemandStatus.Trim();
            }

            if (routing.DemandId > 0)
            {
                trace.DemandId = routing.DemandId.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (routing.EliaContractId.HasValue)
            {
                trace.EliaUconId = routing.EliaContractId.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.ContractNumber))
            {
                trace.ResolvedContract = routing.ContractNumber.Trim();
            }

            if (routing.PremiumSequence.HasValue)
            {
                trace.PremiumSequence =
                    routing.PremiumSequence.Value.ToString(
                        CultureInfo.InvariantCulture);
            }

            if (routing.GrossPremium.HasValue)
            {
                trace.RawPremium = routing.GrossPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            if (routing.NetPremium.HasValue)
            {
                trace.NetPremium = routing.NetPremium.Value.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(routing.PaymentMethodCode))
            {
                trace.PaymentMethodCode = routing.PaymentMethodCode.Trim();
            }

            if (!string.IsNullOrWhiteSpace(routing.PremiumGroupType))
            {
                trace.PremiumGroupType = routing.PremiumGroupType.Trim();
            }

            trace.FundFeatureCount = routing.FundFeatureCount;
            trace.BranchClassification = routing.BranchClassification;
            trace.BranchDetectionSource = routing.BranchDetectionSource;
            trace.BranchDetectionReason = routing.BranchDetectionReason;
            trace.RoutingStatus = routing.RoutingStatus;

            if (!string.IsNullOrWhiteSpace(routing.RoutingDiagnostic))
            {
                trace.RoutingDiagnostic = routing.RoutingDiagnostic;
            }

            if (routing.BranchClassification ==
                ContractBranchClassification.Branch23)
            {
                trace.IsBranch23 = true;
                trace.Branch23Source =
                    $"{routing.BranchDetectionSource}: " +
                    routing.BranchDetectionReason;
            }
            else if (routing.BranchClassification ==
                     ContractBranchClassification.Standard)
            {
                trace.IsBranch23 = false;
                trace.Branch23Source =
                    $"{routing.BranchDetectionSource}: " +
                    routing.BranchDetectionReason;
            }

            Debug.WriteLine(
                "[PreActivationRouting] Trace updated | " +
                $"Demand={DisplayValue(trace.DemandId)} | " +
                $"Contract={DisplayValue(trace.ResolvedContract)} | " +
                $"Product={DisplayValue(trace.ProductCode)} | " +
                $"Process={DisplayValue(trace.ProcessCode)} | " +
                $"Channel={DisplayValue(trace.EliaChannelCode)} | " +
                $"Branch={trace.BranchClassification}");
        }

        /// <summary>
        /// Branch23ActivationOnly: ELIA plan → PRCTT0 → ADDPRCT (if needed) → PRCTT0 →
        /// LVPP06U → LVPG22U → checks → LI1J04D0 → LI1J04D2 → strict SCNTT0 → stop.
        /// LI1J04D1 and LI1J04D3 are not treated as mandatory ESCWA child jobs.
        /// Funds, NAV, LVDQFG03/01, LVD1PE01 and the second LI pass are never executed.
        /// </summary>
        private async Task ExecuteBranch23ActivationOnlyAsync(
            string contract,
            string normalizedEnvironment,
            string shortEnvironment,
            string q2,
            string envIms,
            string fastCtrl,
            string cus,
            string bucp,
            string cmdpmt,
            bool skipLogon,
            string username,
            string password,
            Action<string>? onProgress,
            ContractActivationTrace trace,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(trace);
            token.ThrowIfCancellationRequested();

            trace.IsBranch23 = true;
            trace.SkipPrime = false;
            trace.Channel = "C01";
            trace.Branch23FundsSource = "Not used (Branch23ActivationOnly)";
            trace.Branch23MovementState =
                "Branch23ActivationOnly: ADDPRCT/LVPP06U/LVPG22U/LI1J04D0/LI1J04D2";
            trace.Branch23Source = string.IsNullOrWhiteSpace(trace.Branch23Source)
                ? "Branch23ActivationOnly"
                : trace.Branch23Source + " | ExecutionMode=Branch23ActivationOnly";

            onProgress?.Invoke(
                "ExecutionMode=Branch23ActivationOnly; " +
                $"PaymentDatabase={Settings.DbConfig.GetLisaPaymentDatabase(normalizedEnvironment)}; " +
                $"ContractDatabase={Settings.DbConfig.GetLisaContractDatabase(normalizedEnvironment)}; " +
                "LI sequence=LI1J04D0 then LI1J04D2 (no mandatory D1/D3 child lookup).");

            onProgress?.Invoke(
                "LVPG22U variables confirmed from LVChain: " +
                "NREMB=20; CONTR-EX=Y; CONTR-RE=Y; CONTR-UN=Y; " +
                "NJJART72=5; JOBNAMI=LVPP06U$; CM=<blank>.");

            Branch23PaymentChannel paymentChannel =
                Branch23ActivationResolver.IdentifyChannel(bucp, cmdpmt);

            var verifier = new Branch23ActivationVerifier(
                new Branch23SqlReader(normalizedEnvironment));

            Branch23ActivationPlan plan;
            try
            {
                string? demandId = IsMissingValue(trace.DemandId) ? null : trace.DemandId;
                plan = await verifier.BuildPlanAsync(demandId, contract, paymentChannel, token)
                    .ConfigureAwait(false);
            }
            catch (Branch23ActivationException exception)
            {
                PublishBranch23Report(trace, new Branch23ActivationReport
                {
                    DemandId = trace.DemandId ?? "-",
                    ContractNumber372 = contract,
                    Channel = paymentChannel.ToString(),
                    FinalDiagnostic = Branch23FinalDiagnostic.BLOCKED,
                    FailureCode = exception.Code,
                    FinalReason = exception.Message + " Aucun job soumis; aucune connexion ESCWA ouverte."
                }, onProgress);
                return;
            }

            var baseVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENV"] = shortEnvironment,
                ["ENVIMS"] = envIms,
                ["CUS"] = cus ?? string.Empty,
                ["Q2"] = q2,

                // Confirmed PREMELIA/LVD4PG22 variables from LVChain.
                // CM is intentionally blank. The key must be CM, not CM.; the point
                // in %%CM. is a JCL concatenation delimiter.
                ["CM"] = string.Empty,
                ["NREMB"] = "20",
                ["CONTR-EX"] = "Y",
                ["CONTR-RE"] = "Y",
                ["CONTR-UN"] = "Y",
                ["NJJART72"] = "5",
                ["JOBNAMI"] = "LVPP06U$",
                ["FASTCTRL"] = fastCtrl,
                ["USERNAME"] = username ?? string.Empty
            };

            trace.Environment = shortEnvironment;
            trace.EnvIms = envIms;
            trace.Q2Region = q2;
            trace.FastCtrlDsn = fastCtrl;
            trace.Cus = cus ?? string.Empty;
            trace.Bucp = plan.Payment.Bucp;
            trace.Cmdpmt = plan.Payment.Cmdpmt;
            trace.JclContract = plan.StartEnd.Start;
            trace.FormattedAmount = plan.Premium.Amount;
            trace.Username = username ?? string.Empty;

            var orchestrator = new ActivationOrchestrator(Settings.JclNetworkFolder);
            await orchestrator.PrepareBranch23ActivationSessionAsync(
                    username ?? string.Empty, password ?? string.Empty, shortEnvironment,
                    skipLogon, onProgress, trace, token)
                .ConfigureAwait(false);

            var workflow = new Branch23ActivationWorkflow(
                verifier,
                orchestrator.CreateBranch23JobRunner(trace, onProgress));

            Branch23ActivationReport report = await workflow.RunAsync(plan, baseVariables, onProgress, token)
                .ConfigureAwait(false);
            PublishBranch23Report(trace, report, onProgress);
        }

        /// <summary>
        /// Copies the Branch23ActivationOnly evidence into the trace and maps the final
        /// diagnostic. Only ACTIVATED returns normally; every other diagnostic throws.
        /// </summary>
        private static void PublishBranch23Report(
            ContractActivationTrace trace,
            Branch23ActivationReport report,
            Action<string>? onProgress)
        {
            trace.Branch23ActivationReport = report.Render();
            trace.Branch23MovementState = "Branch23ActivationOnly: " + report.FinalDiagnostic;
            trace.Branch23NavSummary = report.Lines.ToList();
            trace.EndTime = DateTime.Now;

            onProgress?.Invoke(trace.Branch23ActivationReport);
            foreach (string warning in report.Warnings)
                onProgress?.Invoke("BR23 AVERTISSEMENT: " + warning);

            if (report.FinalDiagnostic == Branch23FinalDiagnostic.ACTIVATED)
            {
                trace.FinalStatus = "SUCCESS";
                return;
            }

            if (report.FinalDiagnostic == Branch23FinalDiagnostic.CANCELLED)
            {
                trace.FinalStatus = "CANCELLED";
                throw new OperationCanceledException(report.FinalReason);
            }

            trace.FinalStatus = "FAILED";
            trace.FailureStage = string.IsNullOrWhiteSpace(report.FailureCode)
                ? "BR23_" + report.FinalDiagnostic
                : report.FailureCode;
            trace.FailureReason = $"{report.FinalDiagnostic}: {report.FinalReason}";
            throw new InvalidOperationException(
                $"Branch 23 activation not confirmed: {report.FinalDiagnostic} ({trace.FailureStage}).");
        }

        /// <summary>
        /// True only for Branch 23 with the explicit Branch23ActivationOnly mode.
        /// Otherwise the legacy BR21/BR23 path is used unchanged.
        /// </summary>
        public static bool ShouldUseBranch23ActivationOnly(
            bool isBranch23,
            bool branch23ActivationOnly)
        {
            // Canonical rule: every BR23 activation uses the validated PREMELIA/LISA
            // activation-only workflow. The second parameter remains for compatibility.
            _ = branch23ActivationOnly;
            return isBranch23;
        }

        public async Task ExecuteActivationSequenceAsync(
            string contract,
            string amount,
            string envValue,
            string cus,
            string bucp,
            string cmdpmt,
            string channel,
            bool skipPrime,
            bool skipLogon,
            string username,
            string password,
            Action<string> onProgress,
            CancellationToken token = default,
            ContractActivationTrace? trace = null,
            bool isBranch23 = false,
            IBranch23FundSource? branch23FundSource = null,
            bool branch23ActivationOnly = false)
        {
            token.ThrowIfCancellationRequested();

            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(
                    envValue);

            PreActivationRoutingInfo? routing =
                await TryResolveExecutionRoutingAsync(
                        trace,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

            if (routing != null)
            {
                if (trace != null)
                {
                    ApplyRoutingToTrace(trace, routing);
                }

                onProgress?.Invoke(
                    "ELIA routing: " +
                    $"Demand={routing.DemandId}, " +
                    $"Contract={DisplayValue(routing.ContractNumber)}, " +
                    $"Product={DisplayValue(routing.ProductCode)}, " +
                    $"Process={DisplayValue(routing.ProcessCode)}, " +
                    $"Channel={DisplayValue(routing.ChannelCode)}, " +
                    $"Branch={routing.BranchClassification}, " +
                    $"Status={routing.RoutingStatus}.");

                if (routing.IsBranch23)
                {
                    isBranch23 = true;

                    if (trace != null)
                    {
                        trace.IsBranch23 = true;

                        trace.Branch23Source =
                            routing.BranchDetectionSource +
                            ": " +
                            routing.BranchDetectionReason;
                    }
                }

                if (PreActivationRoutingService
                    .IsSupportedActivationProcess(
                        routing.ProcessCode))
                {
                    channel =
                        routing.ProcessCode;
                }

                if (!TryParsePositiveAmount(
                        amount,
                        out _) &&
                    routing.GrossPremium.HasValue &&
                    routing.GrossPremium.Value > 0m)
                {
                    amount =
                        routing.GrossPremium.Value.ToString(
                            "0.00",
                            CultureInfo.InvariantCulture);

                    onProgress?.Invoke(
                        "Premium automatically resolved " +
                        $"from ELIA: {amount}.");
                }

                /*
                 * Le code de paiement ELIA ne doit pas être copié
                 * automatiquement dans CMDPMT sans mapping validé.
                 *
                 * Ne pas faire :
                 *
                 * cmdpmt = routing.PaymentMethodCode;
                 */
            }

            if (routing != null && !string.IsNullOrWhiteSpace(routing.ChannelCode))
            {
                ActivationRoutingResult automaticPaymentRouting =
                    _activationRoutingService.Resolve(routing.ChannelCode);
                if (!automaticPaymentRouting.IsResolved)
                    throw new InvalidOperationException(
                        automaticPaymentRouting.DiagnosticMessage);

                string suppliedBucp = AutomaticActivationPlan.NormalizeBucp(bucp);
                string suppliedCommand = (cmdpmt ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();
                bool suppliedRoutingMatches =
                    string.Equals(
                        suppliedBucp,
                        automaticPaymentRouting.Bucp,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        suppliedCommand,
                        automaticPaymentRouting.PaymentCommand,
                        StringComparison.OrdinalIgnoreCase);

                if (!suppliedRoutingMatches)
                {
                    onProgress?.Invoke(
                        "Replacing global/manual payment preset " +
                        $"{suppliedBucp}/{suppliedCommand} with ELIA ChannelCode " +
                        $"routing {automaticPaymentRouting.Bucp}/" +
                        automaticPaymentRouting.PaymentCommand + ".");
                }

                bucp = automaticPaymentRouting.Bucp;
                cmdpmt = automaticPaymentRouting.PaymentCommand;

                if (trace != null)
                {
                    trace.Bucp = (bucp ?? string.Empty).Trim().PadLeft(5, '0');
                    trace.Cmdpmt = cmdpmt ?? string.Empty;
                    trace.RoutingDiagnostic =
                        automaticPaymentRouting.Rule +
                        " | BusinessChannel=" +
                        automaticPaymentRouting.ResolvedChannel;
                }

                onProgress?.Invoke(
                    "Payment routing resolved from ELIA ChannelCode " +
                    $"{routing.ChannelCode}: {automaticPaymentRouting.ResolvedChannel}, " +
                    $"BUCP={bucp}, CMDPMT={cmdpmt}.");
            }

            bool useBranch23ActivationOnly =
                ShouldUseBranch23ActivationOnly(
                    isBranch23,
                    branch23ActivationOnly);

            if (useBranch23ActivationOnly)
            {
                branch23ActivationOnly = true;
                branch23FundSource = null;
                if (trace != null)
                {
                    trace.IsBranch23 = true;
                    trace.Branch23FundsSource = "Not used (Branch23ActivationOnly)";
                    trace.Branch23MovementState =
                        "Branch23ActivationOnly: LI1J04D0 -> LI1J04D2 -> SCNTT0";
                }
                onProgress?.Invoke(
                    "BR23 ROUTING CONFIRMED | Mode=Branch23ActivationOnly | " +
                    "Sequence=ADDPRCT,LVPP06U,LVPG22U,LI1J04D0,LI1J04D2,SCNTT0 | " +
                    "Funds/LVDQ=DISABLED");
            }

            string normalizedEnvValue =
                NormalizeShortEnvironment(
                    envValue);

            string q2 =
                normalizedEnvValue == "D"
                    ? "Q2T"
                    : "Q2C";

            string fastCtrl =
                normalizedEnvValue == "D"
                    ? "I0T.DB.CA.FIB.FASTCTRL"
                    : "I10.DB.CA.FIB.FASTCTRL";

            string envImsValue =
                normalizedEnvValue == "D"
                    ? "T"
                    : "C";

            if (useBranch23ActivationOnly)
            {
                ContractActivationTrace branch23Trace =
                    trace ?? new ContractActivationTrace
                    {
                        StartTime = DateTime.Now,
                        FinalStatus = "RUNNING",
                        IsBranch23 = true,
                        Branch23Source = "ExecutionMode=Branch23ActivationOnly"
                    };

                await ExecuteBranch23ActivationOnlyAsync(
                        contract,
                        normalizedEnvironment,
                        normalizedEnvValue,
                        q2,
                        envImsValue,
                        fastCtrl,
                        cus,
                        bucp,
                        cmdpmt,
                        skipLogon,
                        username,
                        password,
                        onProgress,
                        branch23Trace,
                        token)
                    .ConfigureAwait(false);

                // Critical: never fall through to the legacy BR23 funds/LVDQ workflow.
                return;
            }

            string paddedBucp =
                (bucp ?? string.Empty)
                    .Trim()
                    .PadLeft(5, '0');

            amount =
                ValidateAndFormatAmount(
                    amount,
                    skipPrime,
                    channel,
                    trace);

            string paddedAmount =
                amount.PadLeft(10, '0');

            if (trace != null)
            {
                trace.Environment =
                    normalizedEnvValue;

                trace.EnvIms =
                    envImsValue;

                trace.Q2Region =
                    q2;

                trace.FastCtrlDsn =
                    fastCtrl;

                trace.Channel =
                    channel;

                trace.Cus =
                    cus;

                trace.Bucp =
                    paddedBucp;

                trace.Cmdpmt =
                    cmdpmt;

                trace.SkipPrime =
                    skipPrime;

                trace.Username =
                    username;

                trace.JclContract =
                    contract;

                trace.FormattedAmount =
                    paddedAmount;

                trace.IsBranch23 =
                    isBranch23;
            }

            DateTime now =
                DateTime.Now;

            var generalVariables =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ENV"] = normalizedEnvValue,
                    ["ENVIMS"] = envImsValue,
                    ["CUS"] = cus ?? string.Empty,
                    ["YYMMDD"] = now.ToString("yyMMdd"),
                    ["YYYY"] = now.ToString("yyyy"),
                    ["MM"] = now.ToString("MM"),
                    ["DD"] = now.ToString("dd"),
                    ["CLASS"] = "A",
                    ["CNTBEG"] = contract,
                    ["CNTEND"] = contract,
                    ["MMDD"] = now.ToString("MMdd"),
                    ["CYMD"] = now.ToString("yyyyMMdd"),
                    ["STE"] = "A",
                    ["Q2"] = q2,
                    ["CM"] = string.Empty,
                    ["DRUN"] = now.ToString("yyyyMMdd"),
                    ["NREMB"] = "20",
                    ["CONTR-EX"] = "Y",
                    ["CONTR-RE"] = "Y",
                    ["CONTR-UN"] = "Y",
                    ["NJJART72"] = "5",
                    ["JOBNAMI"] = "LVPP06U$",
                    ["FASTCTRL"] = fastCtrl
                };

            var addprctVariables =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["CMDPMT"] =
                        cmdpmt ?? string.Empty,

                    ["AMOUNT"] =
                        paddedAmount,

                    ["BUCP"] =
                        paddedBucp,

                    ["USERNAME"] =
                        username ?? string.Empty
                };

            Dictionary<
                string,
                Dictionary<string, string>>?
                branch23JobVariables = null;

            Func<CancellationToken, Task>?
                prepareBranch23NavsAsync = null;

            Func<CancellationToken, Task>?
                beforeBranch23FundEventsAsync = null;

            Func<CancellationToken, Task>?
                afterBranch23FundEventsAsync = null;

            // =========================================================
            // BRANCHE 23 UNIQUEMENT
            // =========================================================

            if (isBranch23)
            {
                DateTime consolidationDate =
                    Branch23ParameterBuilder
                        .GetConsolidationDate(now);

                branch23JobVariables =
                    Branch23ParameterBuilder
                        .BuildJobVariables(
                            now,
                            consolidationDate);

                string navEnvironment =
                    normalizedEnvValue + "000";

                IReadOnlyList<string> funds =
                    Array.Empty<string>();

                if (trace != null)
                {
                    trace.IsBranch23 = true;
                    trace.Branch23ConsolidationDate = consolidationDate;

                    if (string.IsNullOrWhiteSpace(trace.Branch23Source))
                    {
                        trace.Branch23Source =
                            "Manual selection or legacy detection";
                    }
                }

                /*
                 * Correct BR23 order:
                 * funds -> NAV J/J+1/J+2 -> LVDQFG03 -> LVDQFG01 -> LVD1PE01.
                 * A new contract has no LISA NO_CNT yet. Never query SCNTT0,
                 * FMVGT0 or FMVDT0 as a prerequisite here.
                 */
                // Read-only preflight before the orchestrator can submit ADDPRCT.
                var fundResolution = await ResolveBranch23PreActivationFundsAsync(
                    trace, contract, navEnvironment, branch23FundSource, token)
                    .ConfigureAwait(false);
                funds = fundResolution.Funds;
                if (funds.Count == 0)
                    throw new Branch23FundResolutionException(
                        "BR23_PREACTIVATION_FUNDS_MISSING",
                        "No fund was resolved from ELIA by Demand ID or contract, " +
                        "and no fallback fund is configured. No mainframe job was submitted by this call.");
                if (trace != null)
                {
                    trace.Branch23Funds = funds.ToList();
                    trace.Branch23FundsSource = fundResolution.Source;
                }
                onProgress?.Invoke("BR23 funds resolved: " + string.Join(", ", funds) +
                    "; source: " + fundResolution.Source);

                beforeBranch23FundEventsAsync = async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    var fundService =
                        new Branch23FundService(navEnvironment);

                    DateTime firstNavDate = now.Date;
                    DateTime lastNavDate = consolidationDate.Date;

                    if (lastNavDate < firstNavDate)
                    {
                        throw new InvalidOperationException(
                            "The BR23 consolidation date cannot be earlier " +
                            "than the activation date.");
                    }

                    int requiredDayCount =
                        (lastNavDate - firstNavDate).Days + 1;

                    DateTime[] requiredDates =
                        Enumerable
                            .Range(0, requiredDayCount)
                            .Select(offset => firstNavDate.AddDays(offset))
                            .ToArray();

                    var navSummary = new List<string>();
                    var notReadySummary = new List<string>();

                    foreach (DateTime requiredDate in requiredDates)
                    {
                        var dateResults = await fundService.EnsureNavsAsync(
                                funds,
                                requiredDate,
                                ct)
                            .ConfigureAwait(false);

                        navSummary.AddRange(
                            dateResults.Select(result => result.ToString()));

                        notReadySummary.AddRange(
                            dateResults
                                .Where(result => !result.IsReady)
                                .Select(result => result.ToString()));
                    }

                    if (trace != null)
                    {
                        trace.Branch23Funds = funds.ToList();
                        trace.Branch23FundsSource =
                            fundResolution.Source;
                        trace.Branch23NavSummary = navSummary.ToList();
                        trace.Branch23LastMovementBefore = null;
                        trace.Branch23MovementNumber = null;
                        trace.Branch23MovementState =
                            "LISA movement validation deferred until the " +
                            "complete BR23 JCL chain has finished.";
                    }

                    if (notReadySummary.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "BR23 NAV preparation failed between the activation " +
                            "date and consolidation date: " +
                            string.Join(" | ", notReadySummary));
                    }

                    onProgress?.Invoke(
                        "BR23 funds and all required NAV dates are ready " +
                        "before LVDQFG03. " +
                        $"Funds={string.Join(", ", funds)}; " +
                        $"Dates={string.Join(", ", requiredDates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}.");
                };

                prepareBranch23NavsAsync = null;
                afterBranch23FundEventsAsync = null;
            }

            string jclFolder =
                Settings.JclNetworkFolder;

            var orchestrator =
                new ActivationOrchestrator(
                    jclFolder);

            await orchestrator
                .RunActivationSequenceAsync(
                    generalVariables,
                    addprctVariables,
                    username,
                    password,
                    channel,
                    skipPrime,
                    skipLogon,
                    onProgress,
                    token,
                    trace,
                    branch23JobVariables,
                    prepareBranch23NavsAsync,
                    beforeBranch23FundEventsAsync,
                    afterBranch23FundEventsAsync,
                    beforeInitialLiValidationAsync:
                        ct => ValidatePreLiBusinessPrerequisitesAsync(
                            normalizedEnvironment,
                            contract,
                            trace,
                            onProgress,
                            ct),
                    resolveGeneratedJobNumberAsync:
                        new MicroFocusApiService()
                            .ResolveGeneratedJobNumberAsync)
                .ConfigureAwait(false);

            if (isBranch23 &&
                trace != null &&
                string.Equals(
                    trace.FinalStatus,
                    "SUCCESS",
                    StringComparison.OrdinalIgnoreCase))
            {
                string? finalLisaInternalId =
                    await WaitForFinalLisaCreationAsync(
                            trace,
                            contract,
                            normalizedEnvironment,
                            onProgress,
                            token)
                        .ConfigureAwait(false);

                if (IsMissingValue(finalLisaInternalId))
                {
                    trace.FinalStatus = "FAILED";
                    trace.FailureStage = "LISA_FINALIZATION_TIMEOUT";
                    trace.FailureReason =
                        "All standard and Branch 23 JCL jobs completed " +
                        "with accepted return codes, but the contract " +
                        $"'{trace.ResolvedContract}' did not appear in " +
                        "LV.SCNTT0 within 5 minutes.";
                    trace.RootCauseAnalysis =
                        "A technical JCL RC=0000 does not prove that a " +
                        "business object was processed. Verify the " +
                        "BERPCTL counters for LVDQFG03 and LVDQFG01 and " +
                        "the final LVD1PE01 output.";
                    trace.RecommendedAction =
                        "Do not replay ADDPRCT. Inspect zero business " +
                        "counters and the final LISA creation step. Use " +
                        "Skip Prime for any controlled retry.";
                    trace.EndTime = DateTime.Now;

                    throw new InvalidOperationException(
                        trace.FailureReason);
                }
            }
        }

        // =============================================================
        // VALIDATION METIER AVANT LA PREMIERE PASSE LI
        // =============================================================
        private static async Task ValidatePreLiBusinessPrerequisitesAsync(
            string environment,
            string jclContract,
            ContractActivationTrace? trace,
            Action<string>? onProgress,
            CancellationToken token)
        {
            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(environment);
            string technicalContract = FormatContractStatic(jclContract);

            if (technicalContract.Length != 9 ||
                technicalContract.Any(character =>
                    character < '0' || character > '9') ||
                !decimal.TryParse(
                    technicalContract,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out decimal contractNumber) ||
                contractNumber <= 0)
            {
                throw new InvalidOperationException(
                    "PRE_LI_INVALID_CONTRACT: a valid nine-digit technical " +
                    "contract number is required.");
            }

            const int maximumAttempts = 10;
            const int delayMilliseconds = 1000;
            const string query = @"
SELECT
    (SELECT COUNT_BIG(*) FROM [LV].[PRIST0] AS p
      WHERE p.C_STE = @Company AND p.NO_CNT = @ContractNumber)
        AS PremiumCount,
    (SELECT COUNT_BIG(*) FROM [LV].[PECHT0] AS q
      WHERE q.C_STE = @Company AND q.NO_CNT = @ContractNumber)
        AS ReceiptCount,
    (SELECT COUNT_BIG(*) FROM [LV].[PECHT0] AS q
      WHERE q.C_STE = @Company AND q.NO_CNT = @ContractNumber
        AND q.D_AF_QUITT IS NOT NULL
        AND q.D_AF_QUITT > CONVERT(date, '0001-01-01'))
        AS AssignedReceiptCount,
    (SELECT COUNT_BIG(*) FROM [LV].[ELIAT0] AS e
      WHERE e.C_STE = @Company AND e.NO_CNT = @ContractNumber
        AND e.C_EVN_LIE IN ('PE01', 'PE03')
        AND e.D_PREV_CLT_EVN <= CONVERT(date, GETDATE()))
        AS EligibleEventCount;";

            var database = new DatabaseManager(normalizedEnvironment);
            long premiumCount = 0;
            long receiptCount = 0;
            long assignedReceiptCount = 0;
            long eligibleEventCount = 0;

            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using DataTable table = await database.GetDataAsync(
                    query,
                    new Dictionary<string, object>
                    {
                        ["@Company"] = "A",
                        ["@ContractNumber"] = contractNumber
                    },
                    token).ConfigureAwait(false);

                if (table.Rows.Count != 1)
                    throw new InvalidOperationException(
                        "PRE_LI_SQL_RESULT_INVALID: expected one summary row.");

                DataRow row = table.Rows[0];
                premiumCount = ReadRequiredInt64(row, "PremiumCount");
                receiptCount = ReadRequiredInt64(row, "ReceiptCount");
                assignedReceiptCount = ReadRequiredInt64(
                    row, "AssignedReceiptCount");
                eligibleEventCount = ReadRequiredInt64(
                    row, "EligibleEventCount");

                string diagnostic =
                    $"NO_CNT={technicalContract}; PRIST0={premiumCount}; " +
                    $"PECHT0={receiptCount}; assigned={assignedReceiptCount}; " +
                    $"eligible PE01/PE03={eligibleEventCount}.";
                Debug.WriteLine("[PRE-LI BUSINESS VALIDATION] " + diagnostic);

                if (premiumCount > 0 && receiptCount > 0 &&
                    assignedReceiptCount > 0 && eligibleEventCount > 0)
                {
                    onProgress?.Invoke(
                        "Pre-LI business prerequisites confirmed. " + diagnostic);
                    return;
                }

                if (attempt < maximumAttempts)
                {
                    onProgress?.Invoke(
                        $"Waiting for LVPG22U persistence ({attempt}/{maximumAttempts}). " +
                        diagnostic);
                    await Task.Delay(delayMilliseconds, token)
                        .ConfigureAwait(false);
                }
            }

            string reason =
                $"LVPG22U ended, but contract {technicalContract} lacks " +
                $"required persisted objects. PRIST0={premiumCount}; " +
                $"PECHT0={receiptCount}; assigned={assignedReceiptCount}; " +
                $"eligible PE01/PE03={eligibleEventCount}.";

            if (trace != null)
            {
                trace.FailureStage = "PRE_LI_BUSINESS_PREREQUISITE_MISSING";
                trace.FailureReason = reason;
                trace.RootCauseAnalysis =
                    "Technical RC=0000 does not prove business persistence.";
                trace.RecommendedAction =
                    "Inspect ADDPRCT, LVPP06U and LVPG22U, including " +
                    "the LVPP06U OI/AUTRE classification. Do not replay " +
                    "LI or billing automatically.";
            }

            throw new InvalidOperationException(reason);
        }

        private static long ReadRequiredInt64(
            DataRow row,
            string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) ||
                row[columnName] == DBNull.Value)
                throw new InvalidOperationException(
                    "PRE_LI_SQL_RESULT_INVALID: missing column " + columnName);

            return Convert.ToInt64(
                row[columnName], CultureInfo.InvariantCulture);
        }

        // =============================================================
        // VALIDATION DU MONTANT
        // =============================================================

        private static string ValidateAndFormatAmount(
            string amount,
            bool skipPrime,
            string channel,
            ContractActivationTrace? trace)
        {
            if (TryParsePositiveAmount(
                    amount,
                    out decimal parsedAmount))
            {
                return parsedAmount.ToString(
                    "0.00",
                    CultureInfo.InvariantCulture);
            }

            if (skipPrime ||
                string.Equals(
                    channel,
                    "C05",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "0.00";
            }

            if (trace != null)
            {
                trace.FinalStatus =
                    "FAILED";

                trace.FailureStage =
                    trace.FoundInEliaFlag()
                        ? "DB_PREMIUM_ZERO"
                        : "DB_CONTRACT_NOT_FOUND";

                trace.FailureReason =
                    trace.FoundInEliaFlag()
                        ? "The contract was found in ELIA, but " +
                          "the gross premium is missing or equal " +
                          "to 0.00."
                        : "The contract or Demand ID could not be " +
                          "resolved in ELIA.";

                trace.RootCauseAnalysis =
                    trace.FoundInEliaFlag()
                        ? "ADDPRCT requires a positive premium when " +
                          "Skip Prime is disabled."
                        : "The ELIA identifiers could not be resolved " +
                          "in the selected environment.";

                trace.RecommendedAction =
                    trace.FoundInEliaFlag()
                        ? "Verify FJ1.TB5UPRP, or use Skip Prime " +
                          "when no premium injection is required."
                        : "Verify the Demand ID, contract number and " +
                          "the selected environment.";
            }

            throw new InvalidOperationException(
                "Premium was not found or is equal to 0.00. " +
                "Activation was cancelled.");
        }

        private static bool TryParsePositiveAmount(
            string amount,
            out decimal parsedAmount)
        {
            string cleaned =
                (amount ?? "0")
                    .Replace(",", ".")
                    .Trim();

            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out parsedAmount) &&
                   parsedAmount > 0m;
        }

        // =============================================================
        // RAPPORTS
        // =============================================================

        public string FormatSingleActivationReport(
            ContractActivationTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(
                    nameof(trace));
            }

            var builder =
                new StringBuilder(8192);

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                "                     AUTO-ACTIVATOR - DETAILED SINGLE ACTIVATION REPORT");

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine(
                $"Generated At       : " +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            builder.AppendLine(
                $"Execution Window   : " +
                $"{trace.StartTime:HH:mm:ss.fff} -> " +
                $"{trace.EndTime:HH:mm:ss.fff} " +
                $"(Total: {trace.TotalDurationMs / 1000.0:F2}s / " +
                $"{trace.TotalDurationMs} ms)");

            builder.AppendLine(
                $"Final Status       : " +
                GetStatusBadgeText(
                    trace.FinalStatus));

            builder.AppendLine(
                "=========================================================================================================");

            builder.AppendLine();

            AppendContractTraceDetails(
                builder,
                trace,
                includeHeaderBanner: false);

            return builder.ToString();
        }

        public static void AppendContractTraceDetails(
            StringBuilder builder,
            ContractActivationTrace trace,
            bool includeHeaderBanner)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(
                    nameof(builder));
            }

            if (trace == null)
            {
                throw new ArgumentNullException(
                    nameof(trace));
            }

            if (includeHeaderBanner)
            {
                builder.AppendLine(
                    "---------------------------------------------------------------------------------------------------------");

                builder.AppendLine(
                    $"CONTRACT #{trace.RowNum,-4} | " +
                    $"INPUT: {trace.RawInput,-16} | " +
                    $"CONTRACT: {trace.ResolvedContract,-15} | " +
                    $"STATUS: " +
                    GetStatusBadgeText(
                        trace.FinalStatus));

                builder.AppendLine(
                    "---------------------------------------------------------------------------------------------------------");
            }

            string environmentSuffix =
                Settings.NormalizeEnvironmentSuffix(
                    trace.Environment);

            string sqlServer =
                Settings.DbConfig.GetServerName(
                    environmentSuffix);

            bool branch23ActivationOnly =
                trace.IsBranch23 &&
                (string.Equals(
                     trace.Branch23FundsSource,
                     "Not used (Branch23ActivationOnly)",
                     StringComparison.OrdinalIgnoreCase) ||
                 (trace.Branch23MovementState?.IndexOf(
                      "Branch23ActivationOnly",
                      StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

            string applicationDatabase =
                branch23ActivationOnly
                    ? Settings.DbConfig.GetLisaContractDatabase(environmentSuffix)
                    : Settings.DbConfig.GetApplicationDatabase(environmentSuffix);

            string paymentDatabase =
                Settings.DbConfig.GetLisaPaymentDatabase(environmentSuffix);

            string lifeDatabase =
                Settings.DbConfig.GetLifeDatabase(
                    environmentSuffix);

            builder.AppendLine(
                "[1. CONTRACT IDENTITY & DATABASE RESOLUTION]");

            builder.AppendLine(
                $"  • Input Provided       : " +
                $"{trace.RawInput} " +
                $"({(trace.IsDemandId
                    ? "Demand ID"
                    : "Contract Number")})");

            builder.AppendLine(
                $"  • SQL Target           : " +
                $"Server: {sqlServer} | " +
                $"LISA DB: {applicationDatabase} | " +
                $"LIFE DB: {lifeDatabase}");

            if (branch23ActivationOnly)
            {
                builder.AppendLine(
                    "  • Execution Mode       : Branch23ActivationOnly");
                builder.AppendLine(
                    $"  • Payment Database     : {paymentDatabase}");
                builder.AppendLine(
                    $"  • Contract Database    : {applicationDatabase}");
            }

            builder.AppendLine(
                $"  • DB Diagnostic Status : " +
                $"{trace.DbDiagnosticStatus}");

            builder.AppendLine(
                $"  • Resolved Contract    : " +
                $"{trace.ResolvedContract}");

            builder.AppendLine(
                $"  • LISA Extended        : " +
                $"{trace.LisaContractNumber}");

            builder.AppendLine(
                $"  • Mainframe JCL ID     : " +
                $"{trace.JclContract}");

            builder.AppendLine(
                $"  • ELIA UCON ID         : " +
                $"{trace.EliaUconId}");

            builder.AppendLine(
                $"  • ELIA Demand ID       : " +
                $"{trace.DemandId}");
            builder.AppendLine(
                $"  • ELIA ProductCode     : " +
                $"{DisplayValue(trace.ProductCode)}");
            builder.AppendLine(
                $"  • ELIA ProcessCode     : " +
                $"{DisplayValue(trace.ProcessCode)}");
            builder.AppendLine(
                $"  • ELIA ChannelCode     : " +
                $"{DisplayValue(trace.EliaChannelCode)}");
            builder.AppendLine(
                $"  • Business Channel     : " +
                $"{ResolveTraceBusinessChannel(trace.EliaChannelCode)}");
            builder.AppendLine(
                $"  • Payment Routing Rule : " +
                $"{DisplayValue(trace.RoutingDiagnostic)}");
            builder.AppendLine(
                $"  • ELIA App Channel     : " +
                $"{DisplayValue(trace.ApplicationChannelCode)}");
            builder.AppendLine(
                $"  • ELIA Demand Status   : " +
                $"{DisplayValue(trace.DemandStatus)}");
            builder.AppendLine(
                $"  • Branch Classification: " +
                $"{trace.BranchClassification}");

            builder.AppendLine(
                $"  • LISA Internal NO_CNT : " +
                $"{trace.LisaInternalId}");

            builder.AppendLine(
                $"  • DB Premiums          : " +
                $"Gross: {trace.RawPremium} | " +
                $"Net: {trace.NetPremium} | " +
                $"Seq: {trace.PremiumSequence} | " +
                $"JCL AMOUNT: '{trace.FormattedAmount}'");

            builder.AppendLine(
                $"  • SQL Lookup Time      : " +
                $"{trace.DbLookupDurationMs} ms");

            builder.AppendLine();

            builder.AppendLine(
                "[2. MAINFRAME ENVIRONMENT & JCL PARAMETERS]");

            builder.AppendLine(
                $"  • Target Environment   : " +
                $"{trace.Environment}000 | " +
                $"IMS: IM7{trace.EnvIms} | " +
                $"DB2: {trace.Q2Region}");

            builder.AppendLine(
                $"  • ESCWA Server Node    : " +
                $"{trace.ServerNode}");

            builder.AppendLine(
                $"  • Process              : " +
                $"{trace.Channel}");

            builder.AppendLine(
                $"  • Skip Prime           : " +
                $"{(trace.SkipPrime ? "YES" : "NO")}");

            builder.AppendLine(
                $"  • Payment Parameters   : " +
                $"CUS='{trace.Cus}' | " +
                $"BUCP='{trace.Bucp}' | " +
                $"CMDPMT='{trace.Cmdpmt}' | " +
                $"User='{trace.Username}'");
            builder.AppendLine(
                $"  • Routing Evidence     : " +
                $"ChannelCode={DisplayValue(trace.EliaChannelCode)} | " +
                $"Canal={ResolveTraceBusinessChannel(trace.EliaChannelCode)} | " +
                $"Rule={DisplayValue(trace.RoutingDiagnostic)}");

            builder.AppendLine(
                $"  • FASTCTRL Dataset     : " +
                $"{trace.FastCtrlDsn}");

            builder.AppendLine(
                trace.IsBranch23
                    ? branch23ActivationOnly
                        ? $"  • Branch 23            : YES | " +
                          $"Source: {trace.Branch23Source} | " +
                          "Mode: activation LISA uniquement"
                        : $"  • Branch 23            : YES | " +
                          $"Source: {trace.Branch23Source} | " +
                          $"Consolidation: " +
                          $"{trace.Branch23ConsolidationDate:yyyy-MM-dd}"
                    : "  • Branch 23            : NO | " +
                      "Standard workflow only");

            if (trace.IsBranch23 && !branch23ActivationOnly)
            {
                string funds =
                    trace.Branch23Funds != null &&
                    trace.Branch23Funds.Count > 0
                        ? string.Join(
                            ", ",
                            trace.Branch23Funds)
                        : "none resolved";

                builder.AppendLine(
                    $"  • BR23 Funds           : " +
                    $"{funds} | " +
                    $"Source: {trace.Branch23FundsSource}");

                builder.AppendLine(
                    $"  • BR23 Movement        : " +
                    $"Last before FG03: " +
                    $"{trace.Branch23LastMovementBefore
                        ?.ToString() ?? "N/A"} | " +
                    $"New: " +
                    $"{trace.Branch23MovementNumber
                        ?.ToString() ?? "N/A"} | " +
                    $"{trace.Branch23MovementState}");

                if (trace.Branch23NavSummary != null)
                {
                    foreach (string nav in
                             trace.Branch23NavSummary)
                    {
                        builder.AppendLine(
                            $"      - NAV {nav}");
                    }
                }
            }

            if (branch23ActivationOnly)
            {
                builder.AppendLine(
                    "  • BR23 Funds / LVDQ     : NOT USED");
                builder.AppendLine(
                    "  • LI Sequence           : LI1J04D0 -> LI1J04D2 -> SCNTT0");
                builder.AppendLine(
                    $"  • BR23 Result           : {trace.Branch23MovementState}");
            }

            builder.AppendLine();

            if (!string.Equals(
                    trace.FinalStatus,
                    "SUCCESS",
                    StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(
                    "[3. FAILURE DIAGNOSTICS & ROOT-CAUSE ANALYSIS]");

                builder.AppendLine(
                    $"  • Failure Stage        : " +
                    $"{trace.FailureStage}");

                if (!string.IsNullOrWhiteSpace(
                        trace.FailedJobName) &&
                    trace.FailedJobName != "-")
                {
                    builder.AppendLine(
                        $"  • Failed Job           : " +
                        $"{trace.FailedJobName} " +
                        $"({trace.FailedJobNum}) | " +
                        $"RC: {trace.FailedReturnCode}");
                }

                builder.AppendLine(
                    $"  • Failure Reason       : " +
                    $"{trace.FailureReason}");

                if (!string.IsNullOrWhiteSpace(
                        trace.RootCauseAnalysis))
                {
                    builder.AppendLine(
                        $"  • Root Cause           : " +
                        $"{trace.RootCauseAnalysis}");
                }

                if (!string.IsNullOrWhiteSpace(
                        trace.RecommendedAction))
                {
                    builder.AppendLine(
                        $"  • Recommended Action   : " +
                        $"{trace.RecommendedAction}");
                }

                builder.AppendLine();
            }

            builder.AppendLine(
                "[4. MAINFRAME EXECUTION TIMELINE]");

            if (trace.Steps == null ||
                trace.Steps.Count == 0)
            {
                builder.AppendLine(
                    "  No Mainframe job was submitted.");
            }
            else
            {
                builder.AppendLine(
                    "  Step | Job      | SubJob   | Job ID   | " +
                    "Status       | RC     | Duration | Polls | Notes");

                builder.AppendLine(
                    "  -----+----------+----------+----------+" +
                    "--------------+--------+----------+-------+" +
                    "--------------------------------");

                foreach (var step in trace.Steps)
                {
                    string duration =
                        step.DurationMs > 0
                            ? $"{step.DurationMs / 1000.0:F2}s"
                            : "-";

                    string polls =
                        step.PollCount > 0
                            ? step.PollCount.ToString(
                                CultureInfo.InvariantCulture)
                            : "-";

                    string note =
                        !string.IsNullOrWhiteSpace(
                            step.ErrorMessage)
                            ? step.ErrorMessage
                            : step.DiagnosticHint;

                    builder.AppendLine(
                        $"   #{step.StepOrder,-2} | " +
                        $"{step.JobName,-8} | " +
                        $"{step.SubJobName,-8} | " +
                        $"{step.JobNum,-8} | " +
                        $"{step.Status,-12} | " +
                        $"{step.ReturnCode,-6} | " +
                        $"{duration,8} | " +
                        $"{polls,5} | " +
                        $"{note}");
                }

                foreach (var step in
                         trace.Steps.Where(
                             item =>
                                 !string.IsNullOrWhiteSpace(
                                     item.SpoolExcerpt)))
                {
                    builder.AppendLine();

                    builder.AppendLine(
                        $"  --- SPOOL {step.JobName} " +
                        $"({step.JobNum}) | " +
                        $"Status={step.Status} | " +
                        $"RC={step.ReturnCode} ---");

                    foreach (string line in
                             step.SpoolExcerpt
                                 .Replace("\r", string.Empty)
                                 .Split('\n'))
                    {
                        builder.AppendLine(
                            $"  {line}");
                    }

                    builder.AppendLine(
                        "  --- END SPOOL ---");
                }
            }

            builder.AppendLine();
        }

        private static string GetStatusBadgeText(
            string status)
        {
            return status switch
            {
                "SUCCESS" =>
                    "SUCCESS (ACTIVATED)",

                "ALREADY_ACTIVE" =>
                    "ALREADY ACTIVE (ADDPRCT RC=0008)",

                "CANCELLED" =>
                    "CANCELLED BY USER",

                _ =>
                    "FAILED (NOT ACTIVATED)"
            };
        }

        // =============================================================
        // HELPERS
        // =============================================================

        private static bool HasColumnValue(
            DataRow row,
            string columnName)
        {
            return !string.IsNullOrWhiteSpace(
                GetString(row, columnName));
        }

        private static void AssignNormalizedIfMissing(
            string? currentValue,
            string? sourceValue,
            Action<string> assign)
        {
            ArgumentNullException.ThrowIfNull(assign);

            if (!string.IsNullOrWhiteSpace(currentValue) ||
                string.IsNullOrWhiteSpace(sourceValue))
            {
                return;
            }

            assign(sourceValue.Trim().ToUpperInvariant());
        }

        private static void NormalizeLookupResult(
            ActivationLookupResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            result.ProductCode = NormalizeCode(result.ProductCode);
            result.ProcessCode = NormalizeCode(result.ProcessCode);
            result.ChannelCode = NormalizeCode(result.ChannelCode);
            result.ApplicationChannelCode =
                NormalizeCode(result.ApplicationChannelCode);
            result.DemandStatus = NormalizeCode(result.DemandStatus);

            if (!IsMissingValue(result.ContractNumber))
            {
                result.ContractNumber = result.ContractNumber.Trim();
                if (IsMissingValue(result.JclContract))
                {
                    result.JclContract =
                        FormatContractStatic(result.ContractNumber);
                }
            }
        }

        private static string NormalizeCode(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        private static void AppendDatabaseError(
            ActivationLookupResult result,
            string stage,
            Exception exception)
        {
            string error =
                $"{stage}: {exception.GetType().Name}: {exception.Message}";
            result.DbError = string.IsNullOrWhiteSpace(result.DbError)
                ? error
                : result.DbError + " | " + error;
        }

        private static void DebugLookupResult(
            string stage,
            ActivationLookupResult result,
            string environment)
        {
            Debug.WriteLine(
                "[ActivationLookup] " + stage + " | " +
                $"Environment={environment} | " +
                $"Demand={DisplayValue(result.DemandId)} | " +
                $"UCON={DisplayValue(result.EliaUconId)} | " +
                $"Contract={DisplayValue(result.ContractNumber)} | " +
                $"Product={DisplayValue(result.ProductCode)} | " +
                $"Process={DisplayValue(result.ProcessCode)} | " +
                $"Channel={DisplayValue(result.ChannelCode)} | " +
                $"AppChannel={DisplayValue(result.ApplicationChannelCode)} | " +
                $"DemandStatus={DisplayValue(result.DemandStatus)} | " +
                $"Premium={DisplayValue(result.RawPremium)} | " +
                $"Diagnostic={DisplayValue(result.DbDiagnosticStatus)} | " +
                $"Error={DisplayValue(result.DbError)}");
        }

        private static string GetColumnNames(DataTable table)
        {
            return string.Join(
                ", ",
                table.Columns.Cast<DataColumn>()
                    .Select(column => column.ColumnName));
        }

        private static string FormatDataRow(DataRow row)
        {
            return string.Join(
                " | ",
                row.Table.Columns.Cast<DataColumn>()
                    .Select(column =>
                        $"{column.ColumnName}={FormatSqlValue(row[column])}"));
        }

        private static string FormatSqlValue(object? value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "<NULL>";
            }

            string text = value.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? "<EMPTY>"
                : text.Trim();
        }

        private static string CleanInput(
            string? value)
        {
            return (value ?? string.Empty)
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Trim();
        }

        private static string? GetString(
            DataRow row,
            string columnName)
        {
            if (!row.Table.Columns.Contains(
                    columnName) ||
                row[columnName] == DBNull.Value)
            {
                return null;
            }

            return row[columnName]
                ?.ToString()
                ?.Trim();
        }

        private static void AssignIfAvailable(
            DataRow row,
            string columnName,
            Action<string> assign)
        {
            string? value =
                GetString(
                    row,
                    columnName);

            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(value);
            }
        }

        private static string FormatContractStatic(
            string? rawContract)
        {
            if (string.IsNullOrWhiteSpace(
                    rawContract))
            {
                return string.Empty;
            }

            string cleaned = rawContract
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();

            return cleaned.Length == 12
                ? cleaned.Substring(1, 9)
                : cleaned;
        }

        private static string NormalizeShortEnvironment(
            string? environment)
        {
            string value =
                (environment ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();

            if (value.StartsWith(
                    "D",
                    StringComparison.Ordinal))
            {
                return "D";
            }

            if (value.StartsWith(
                    "Q",
                    StringComparison.Ordinal))
            {
                return "Q";
            }

            if (value.StartsWith(
                    "A",
                    StringComparison.Ordinal))
            {
                return "A";
            }

            if (value.StartsWith(
                    "P",
                    StringComparison.Ordinal))
            {
                return "P";
            }

            throw new ArgumentException(
                $"Unsupported environment: '{environment}'.",
                nameof(environment));
        }

        private static string ResolveTraceBusinessChannel(string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank => "BANK",
                PreActivationBusinessChannel.BrokerNewGeneration =>
                    "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }

        private static string DisplayValue(string? value)
        {
            return IsMissingValue(value)
                ? "-"
                : value!.Trim();
        }

        private static bool IsMissingValue(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(
                       value.Trim(),
                       "N/A",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       value.Trim(),
                       "Not found",
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class ContractActivationTraceExtensions
    {
        public static bool FoundInEliaFlag(
            this ContractActivationTrace? trace)
        {
            return trace != null &&
                   !string.IsNullOrWhiteSpace(
                       trace.EliaUconId) &&
                   !string.Equals(
                       trace.EliaUconId,
                       "N/A",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       trace.EliaUconId,
                       "Not found",
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}