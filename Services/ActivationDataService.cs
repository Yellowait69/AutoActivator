using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Services.Branch23Activation;
using AutoActivator.Sql;
using AutoActivator.Utils;
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
    /// Service principal de préparation et d'exécution d'une activation.
    /// Découpé en classes partielles :
    /// - ActivationDataService.cs : Planification et orchestration JCL ;
    /// - ActivationDataService.Lookup.cs : Résolution SQL ELIA/LISA et classification Branche 21/23 ;
    /// - ActivationDataService.Branch23.cs : Résolution de fonds et exécution Branche 23 ;
    /// - ActivationDataService.Reporting.cs : Formatage des rapports détaillés.
    /// </summary>
    public sealed partial class ActivationDataService
    {
        private readonly PreActivationRoutingService _preActivationRoutingService;
        private readonly IActivationRoutingService _activationRoutingService;

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

        public string FormatContractForJcl(string rawContract)
        {
            return FormatContractStatic(rawContract);
        }

        public Task<PreActivationRoutingInfo> ResolvePreActivationRoutingAsync(
            string demandId,
            CancellationToken token = default)
        {
            return _preActivationRoutingService.ResolveFromDemandAsync(demandId, token);
        }

        public Task<PreActivationRoutingInfo> ResolvePreActivationRoutingInEnvironmentAsync(
            string demandId,
            string envSuffix,
            CancellationToken token = default)
        {
            return _preActivationRoutingService.ResolveFromDemandInEnvironmentAsync(
                demandId,
                envSuffix,
                token);
        }

        public async Task<AutomaticActivationPlan> ResolveAutomaticActivationPlanAsync(
            string rawInput,
            bool isDemandId,
            string envSuffix,
            string cus,
            bool forceBranch23,
            ActivationInputMode inputMode = ActivationInputMode.SqlAutocomplete,
            IReadOnlyDictionary<string, string>? overrides = null,
            CancellationToken token = default,
            ActivationLookupResult? existingLookup = null,
            PreActivationRoutingInfo? existingRouting = null)
        {
            inputMode.EnsureDefined();
            string input = CleanInput(rawInput);
            string normalizedCus = CleanInput(cus);
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("A contract number or Demand ID is required.", nameof(rawInput));
            if (string.IsNullOrWhiteSpace(normalizedCus))
                throw new ArgumentException("CUS is required.", nameof(cus));

            string environment = Settings.NormalizeEnvironmentSuffix(envSuffix);
            var effectiveOverrides = new Dictionary<string, string>(
                overrides ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            ActivationLookupResult lookup = existingLookup ??
                await ResolveContractForActivationAsync(
                    input, isDemandId, environment, token).ConfigureAwait(false);

            if (!lookup.FoundInElia && inputMode == ActivationInputMode.Manual && !isDemandId && !string.IsNullOrWhiteSpace(input))
            {
                lookup.ContractNumber = input;
                lookup.JclContract = FormatContractStatic(input);
                lookup.FoundInElia = true;
            }

            PreActivationRoutingInfo? planRouting = existingRouting;
            bool lookupAlreadyHasDates =
                Branch23ActivationResolver.IsValidBusinessDate(lookup.PaymentDate) ||
                Branch23ActivationResolver.IsValidBusinessDate(lookup.FirstPaymentDate) ||
                Branch23ActivationResolver.IsValidBusinessDate(lookup.ContractStartDate) ||
                Branch23ActivationResolver.IsValidBusinessDate(lookup.DemandCreationDate);

            if (planRouting == null && !lookupAlreadyHasDates && !IsMissingValue(lookup.DemandId))
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

            if (planRouting != null && planRouting.RoutingStatus != PreActivationRoutingStatus.DemandNotFound)
            {
                lookup.FoundInElia = true;
                if (IsMissingValue(lookup.ContractNumber) && !string.IsNullOrWhiteSpace(planRouting.ContractNumber))
                {
                    lookup.ContractNumber = planRouting.ContractNumber.Trim();
                    lookup.JclContract = FormatContractStatic(lookup.ContractNumber);
                }
                if (IsMissingValue(lookup.EliaUconId) && planRouting.EliaContractId.HasValue)
                {
                    lookup.EliaUconId = planRouting.EliaContractId.Value.ToString(CultureInfo.InvariantCulture);
                }
                if (IsMissingValue(lookup.ProductCode) && !string.IsNullOrWhiteSpace(planRouting.ProductCode))
                {
                    lookup.ProductCode = planRouting.ProductCode.Trim();
                }
                if (IsMissingValue(lookup.ProcessCode) && !string.IsNullOrWhiteSpace(planRouting.ProcessCode))
                {
                    lookup.ProcessCode = planRouting.ProcessCode.Trim();
                }
                if (IsMissingValue(lookup.ChannelCode) && !string.IsNullOrWhiteSpace(planRouting.ChannelCode))
                {
                    lookup.ChannelCode = planRouting.ChannelCode.Trim();
                }
            }

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
            if (grossPremium <= 0m && planRouting?.GrossPremium.HasValue == true)
            {
                grossPremium = planRouting.GrossPremium.Value;
            }
            if (inputMode.AllowsManualOverrides() &&
                effectiveOverrides.TryGetValue("AMOUNT", out string? amountOverride) &&
                TryParsePositiveAmount(amountOverride, out decimal manualAmount))
            {
                grossPremium = manualAmount;
            }

            DateTime runDate = ResolvePlanRunDate(
                effectiveOverrides,
                inputMode,
                planRouting,
                lookup,
                out string runDateSource);
            bool alreadyActive = lookup.IsActiveInLisa;

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

            bool effectiveIsBranch23 =
                forceBranch23 ||
                lookup.IsBranch23 == true ||
                planRouting?.IsBranch23 == true ||
                Settings.PreActivationRouting.IsKnownBranch23Product(lookup.ProductCode) ||
                lookup.FundFeatureCount > 0 ||
                (planRouting?.FundFeatureCount ?? 0) > 0;

            var resolvedFundsList = new List<string>();
            string fundsSourceText = string.Empty;

            if (planRouting?.DistinctFundCodes != null && planRouting.DistinctFundCodes.Count > 0)
            {
                foreach (string f in planRouting.DistinctFundCodes)
                {
                    if (!string.IsNullOrWhiteSpace(f))
                    {
                        resolvedFundsList.Add(f.Trim().ToUpperInvariant());
                    }
                }
                fundsSourceText = "ELIA TB5UGFF/TB5UPRF";
            }
            else if (lookup.ResolvedFunds != null && lookup.ResolvedFunds.Count > 0)
            {
                foreach (string f in lookup.ResolvedFunds)
                {
                    if (!string.IsNullOrWhiteSpace(f))
                    {
                        resolvedFundsList.Add(f.Trim().ToUpperInvariant());
                    }
                }
                fundsSourceText = string.IsNullOrWhiteSpace(lookup.FundsSource)
                    ? "ELIA TB5UGFF/TB5UPRF"
                    : lookup.FundsSource;
            }

            if (effectiveIsBranch23 && resolvedFundsList.Count == 0 && !IsMissingValue(lookup.JclContract))
            {
                try
                {
                    var probeTrace = new ContractActivationTrace
                    {
                        DemandId = lookup.DemandId,
                        EliaUconId = lookup.EliaUconId,
                        ResolvedContract = lookup.ContractNumber,
                        JclContract = lookup.JclContract
                    };
                    var preFunds = await ResolveBranch23PreActivationFundsAsync(
                            probeTrace,
                            lookup.JclContract,
                            environment,
                            fallbackSource: null,
                            token)
                        .ConfigureAwait(false);
                    foreach (string f in preFunds.Funds)
                    {
                        if (!string.IsNullOrWhiteSpace(f))
                        {
                            resolvedFundsList.Add(f.Trim().ToUpperInvariant());
                        }
                    }
                    fundsSourceText = preFunds.Source;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AutomaticActivationPlan] Fund pre-resolution note: {ex.Message}");
                }
            }

            var distinctResolvedFunds = resolvedFundsList
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int effectiveFundCount = Math.Max(
                Math.Max(lookup.FundFeatureCount, planRouting?.FundFeatureCount ?? 0),
                distinctResolvedFunds.Count);

            if (effectiveFundCount > 0)
            {
                effectiveIsBranch23 = true;
            }

            if (!lookup.FoundInElia || IsMissingValue(lookup.ContractNumber))
            {
                string notFoundDetail = lookup.FoundInElia
                    ? $"BLOCKED_CONTRACT_NOT_FOUND: Demand ID '{input}' trouvé dans ELIA ({environment}, UCON={DisplayValue(lookup.EliaUconId)}, {(effectiveIsBranch23 ? "Branche 23" : "Branche 21")}) mais aucun N° de contrat externe (IT5UCONLREFEXN) n'est attribué dans TB5UCON [Statut: {lookup.DbDiagnosticStatus}]"
                    : $"BLOCKED_CONTRACT_NOT_FOUND: '{input}' introuvable dans ELIA ({environment}) [Statut: {lookup.DbDiagnosticStatus}]";

                if (!string.IsNullOrWhiteSpace(lookup.DbError))
                {
                    notFoundDetail += $" | SQL Error: {lookup.DbError}";
                }
                else
                {
                    string otherEnv = string.Equals(environment, "Q000", StringComparison.OrdinalIgnoreCase)
                        ? "D000"
                        : "Q000";
                    try
                    {
                        ActivationLookupResult otherLookup = await ResolveContractForActivationAsync(
                            input, isDemandId, otherEnv, token).ConfigureAwait(false);
                        if (otherLookup.FoundInElia && !IsMissingValue(otherLookup.ContractNumber))
                        {
                            notFoundDetail += $" | ATTENTION : '{input}' existe dans {otherEnv} (Contrat {otherLookup.ContractNumber}) ! Basculez l'environnement cible sur {otherEnv[0]}.";
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Debug.WriteLine($"[AutomaticActivationPlan] Cross-environment check note: {ex.Message}");
                    }
                }

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
                    LisaInternalId = IsMissingValue(lookup.LisaInternalId) ? string.Empty : lookup.LisaInternalId,
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
                    FundFeatureCount = effectiveFundCount,
                    ResolvedFunds = distinctResolvedFunds,
                    FundsSource = fundsSourceText,
                    BranchClassification = effectiveIsBranch23
                        ? ContractBranchClassification.Branch23
                        : lookup.BranchClassification != ContractBranchClassification.Unknown
                            ? lookup.BranchClassification
                            : ContractBranchClassification.Standard,
                    BranchDetectionSource = lookup.BranchDetectionSource,
                    BranchDetectionReason = lookup.BranchDetectionReason,
                    IsBranch23 = effectiveIsBranch23,
                    IsAlreadyActive = false,
                    ResumeStrategy = ActivationResumeStrategy.Blocked,
                    ValueSources = sources,
                    Overrides = effectiveOverrides,
                    Diagnostic = notFoundDetail
                };
            }

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
                LisaInternalId = IsMissingValue(lookup.LisaInternalId) ? string.Empty : lookup.LisaInternalId,
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
                FundFeatureCount = effectiveFundCount,
                ResolvedFunds = distinctResolvedFunds,
                FundsSource = fundsSourceText,
                BranchClassification = effectiveIsBranch23
                    ? ContractBranchClassification.Branch23
                    : lookup.BranchClassification != ContractBranchClassification.Unknown
                        ? lookup.BranchClassification
                        : ContractBranchClassification.Standard,
                BranchDetectionSource = lookup.BranchDetectionSource,
                BranchDetectionReason = lookup.BranchDetectionReason,
                IsBranch23 = effectiveIsBranch23,
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
            ActivationLookupResult? lookup,
            out string source)
        {
            string? drunOverride = null;
            if (IsOverride(overrides, inputMode, "DRUN"))
            {
                drunOverride = overrides["DRUN"].Trim();
            }
            else if (IsOverride(overrides, inputMode, "RUNDATE"))
            {
                drunOverride = overrides["RUNDATE"].Trim();
            }

            if (!string.IsNullOrWhiteSpace(drunOverride))
            {
                string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy" };
                if (DateTime.TryParseExact(
                    drunOverride,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
                {
                    source = "Manual override";
                    return parsed.Date;
                }

                throw new InvalidOperationException($"Invalid DRUN/RUNDATE override: '{drunOverride}'.");
            }

            DateTime? paymentDate = routing?.PaymentDate ?? lookup?.PaymentDate;
            DateTime? firstPaymentDate = routing?.FirstPaymentDate ?? lookup?.FirstPaymentDate;
            DateTime? contractStartDate = routing?.ContractStartDate ?? lookup?.ContractStartDate;
            DateTime? demandCreationDate = routing?.DemandCreationDate ?? lookup?.DemandCreationDate;

            if (Branch23ActivationResolver.IsValidBusinessDate(paymentDate) ||
                Branch23ActivationResolver.IsValidBusinessDate(firstPaymentDate) ||
                Branch23ActivationResolver.IsValidBusinessDate(contractStartDate))
            {
                Branch23RunDate br23Date = Branch23ActivationResolver.ResolveRunDate(
                    paymentDate,
                    firstPaymentDate,
                    contractStartDate,
                    demandCreationDate);
                source = br23Date.Source switch
                {
                    "DateVersement" => "TB5UPRP.IT5UPRPDVRS (DateVersement)",
                    "DatePremierePrime" => "TB5UPRP.IT5UPRPDPMRPAI (DatePremierePrime)",
                    "DateDebutContrat" => "TB5UCON.IT5UCONDDEB (DateDebutContrat)",
                    _ => br23Date.Source
                };
                return br23Date.RunDate.Date;
            }

            if (demandCreationDate is DateTime validDemandDate &&
                Branch23ActivationResolver.IsValidBusinessDate(validDemandDate))
            {
                source = "TB5HDMD.IT5HDMDDCRE";
                return validDemandDate.Date;
            }

            source = "Execution date fallback";
            return DateTime.Today;
        }

        public static bool ShouldUseBranch23ActivationOnly(
            bool isBranch23,
            bool branch23ActivationOnly)
        {
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
            bool branch23ActivationOnly = false,
            ActivationJobSelection? jobSelection = null,
            bool manualPaymentOverride = false)
        {
            token.ThrowIfCancellationRequested();

            string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(envValue);
            string normalizedEnvValue = NormalizeShortEnvironment(envValue);
            string q2 = normalizedEnvValue == "D" ? "Q2T" : "Q2C";
            string fastCtrl = normalizedEnvValue == "D"
                ? "I0T.DB.CA.FIB.FASTCTRL"
                : "I10.DB.CA.FIB.FASTCTRL";
            string envImsValue = normalizedEnvValue == "D" ? "T" : "C";

            bool traceAlreadyResolved =
                trace != null &&
                !IsMissingValue(trace.ResolvedContract) &&
                !IsMissingValue(trace.RunDate) &&
                (!IsMissingValue(trace.EliaChannelCode) || manualPaymentOverride);

            PreActivationRoutingInfo? routing = null;
            if (!traceAlreadyResolved)
            {
                routing = await TryResolveExecutionRoutingAsync(trace, normalizedEnvironment, token)
                    .ConfigureAwait(false);
            }

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
                    $"Branch={routing.BranchDisplay} ({routing.BranchClassification}), " +
                    $"Status={routing.RoutingStatus}.");

                if (routing.IsBranch23)
                {
                    isBranch23 = true;

                    if (trace != null)
                    {
                        trace.IsBranch23 = true;
                        trace.Branch23Source =
                            routing.BranchDetectionSource + ": " + routing.BranchDetectionReason;
                    }
                }

                if (PreActivationRoutingService.IsSupportedActivationProcess(routing.ProcessCode))
                {
                    channel = routing.ProcessCode;
                }

                if (!TryParsePositiveAmount(amount, out _) &&
                    routing.GrossPremium.HasValue &&
                    routing.GrossPremium.Value > 0m)
                {
                    amount = routing.GrossPremium.Value.ToString("0.00", CultureInfo.InvariantCulture);
                    onProgress?.Invoke($"Premium automatically resolved from ELIA: {amount}.");
                }
            }

            if (trace != null &&
                (trace.IsBranch23 ||
                 Settings.PreActivationRouting.IsKnownBranch23Product(trace.ProductCode) ||
                 trace.FundFeatureCount > 0))
            {
                isBranch23 = true;
                trace.IsBranch23 = true;
            }

            string manualBucpCandidate = AutomaticActivationPlan.NormalizeBucp(bucp);
            string manualCmdCandidate = (cmdpmt ?? string.Empty).Trim().ToUpperInvariant();
            bool hasValidManualOverride =
                manualPaymentOverride &&
                Settings.ActivationAutomation.IsSupportedManualPair(manualBucpCandidate, manualCmdCandidate);

            if (hasValidManualOverride)
            {
                bucp = manualBucpCandidate;
                cmdpmt = manualCmdCandidate;
                if (trace != null)
                {
                    trace.Bucp = bucp;
                    trace.Cmdpmt = cmdpmt;
                    trace.RoutingDiagnostic = $"Manual payment override preserved ({bucp}/{cmdpmt})";
                }
                onProgress?.Invoke($"Manual payment preset preserved: BUCP={bucp}, CMDPMT={cmdpmt}.");
            }
            else if (routing != null && !string.IsNullOrWhiteSpace(routing.ChannelCode))
            {
                ActivationRoutingResult automaticPaymentRouting =
                    _activationRoutingService.Resolve(routing.ChannelCode);
                if (!automaticPaymentRouting.IsResolved)
                    throw new InvalidOperationException(automaticPaymentRouting.DiagnosticMessage);

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

            string normalizedBucpInput = (bucp ?? string.Empty).Trim();
            if (normalizedBucpInput == "00000")
            {
                normalizedBucpInput = string.Empty;
            }

            string normalizedCmdpmtInput = (cmdpmt ?? string.Empty).Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(normalizedBucpInput) || string.IsNullOrWhiteSpace(normalizedCmdpmtInput))
            {
                if (!string.IsNullOrWhiteSpace(trace?.Bucp) && trace.Bucp.Trim() != "00000")
                {
                    normalizedBucpInput = trace.Bucp.Trim();
                }

                if (!string.IsNullOrWhiteSpace(trace?.Cmdpmt))
                {
                    normalizedCmdpmtInput = trace.Cmdpmt.Trim().ToUpperInvariant();
                }
            }

            if (string.IsNullOrWhiteSpace(normalizedBucpInput) || string.IsNullOrWhiteSpace(normalizedCmdpmtInput))
            {
                string? candidateChannel = !IsMissingValue(trace?.EliaChannelCode)
                    ? trace!.EliaChannelCode
                    : !IsMissingValue(trace?.BusinessChannel)
                        ? trace!.BusinessChannel
                        : channel;

                var confirmedRouting = PreActivationRoutingService.ResolveConfirmedPaymentRouting(candidateChannel);
                if (!string.IsNullOrWhiteSpace(confirmedRouting.Bucp) && !string.IsNullOrWhiteSpace(confirmedRouting.Cmdpmt))
                {
                    if (string.IsNullOrWhiteSpace(normalizedBucpInput))
                    {
                        normalizedBucpInput = confirmedRouting.Bucp;
                    }

                    if (string.IsNullOrWhiteSpace(normalizedCmdpmtInput))
                    {
                        normalizedCmdpmtInput = confirmedRouting.Cmdpmt;
                    }
                }
            }

            bool bypassBilling =
                skipPrime ||
                string.Equals(channel, "C05", StringComparison.OrdinalIgnoreCase);

            if (!bypassBilling &&
                (string.IsNullOrWhiteSpace(normalizedBucpInput) || string.IsNullOrWhiteSpace(normalizedCmdpmtInput)))
            {
                ActivationLookupResult fallbackLookup = await ResolveContractForActivationAsync(
                        contract,
                        isDemandId: false,
                        normalizedEnvironment,
                        token)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(fallbackLookup.Bucp) && fallbackLookup.Bucp.Trim() != "00000")
                {
                    normalizedBucpInput = fallbackLookup.Bucp.Trim();
                }

                if (!string.IsNullOrWhiteSpace(fallbackLookup.Cmdpmt))
                {
                    normalizedCmdpmtInput = fallbackLookup.Cmdpmt.Trim().ToUpperInvariant();
                }

                if (trace != null && IsMissingValue(trace.EliaChannelCode) && !IsMissingValue(fallbackLookup.ChannelCode))
                {
                    trace.EliaChannelCode = fallbackLookup.ChannelCode;
                }
            }

            string paddedBucp = string.IsNullOrWhiteSpace(normalizedBucpInput)
                ? string.Empty
                : normalizedBucpInput.PadLeft(5, '0');
            cmdpmt = normalizedCmdpmtInput;

            ContractActivationTrace effectiveTrace =
                trace ?? new ContractActivationTrace
                {
                    StartTime = DateTime.Now,
                    FinalStatus = "RUNNING",
                    IsBranch23 = isBranch23,
                    Branch23Source = isBranch23 ? "UnifiedWorkflow(Branch23)" : "UnifiedWorkflow(Branch21)"
                };

            await ExecuteUnifiedActivationWorkflowAsync(
                    contract,
                    normalizedEnvironment,
                    normalizedEnvValue,
                    q2,
                    envImsValue,
                    fastCtrl,
                    cus ?? string.Empty,
                    paddedBucp,
                    cmdpmt,
                    channel,
                    skipPrime,
                    skipLogon,
                    username ?? string.Empty,
                    password ?? string.Empty,
                    isBranch23,
                    onProgress,
                    effectiveTrace,
                    token,
                    branch23FundSource,
                    jobSelection,
                    branch23ActivationOnly,
                    manualPaymentOverride,
                    amount,
                    routing)
                .ConfigureAwait(false);
        }

        private static async Task ValidatePreLiBusinessPrerequisitesAsync(
            string environment,
            string jclContract,
            ContractActivationTrace? trace,
            Action<string>? onProgress,
            CancellationToken token)
        {
            string normalizedEnvironment = Settings.NormalizeEnvironmentSuffix(environment);
            string technicalContract = FormatContractStatic(jclContract);

            if (technicalContract.Length is < 7 or > 9 ||
                technicalContract.Any(character => character < '0' || character > '9') ||
                !decimal.TryParse(
                    technicalContract,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out decimal contractNumber) ||
                contractNumber <= 0)
            {
                throw new InvalidOperationException(
                    "PRE_LI_INVALID_CONTRACT: a valid 7-to-9 digit technical contract number (NO_CNT) is required.");
            }

            const int maximumAttempts = 6;
            const int delayMilliseconds = 1000;
            const string query = @"
SELECT
    (SELECT COUNT_BIG(*) FROM [LV].[PRIST0] AS p WITH (NOLOCK)
      WHERE p.C_STE = @Company AND p.NO_CNT = @ContractNumber)
        AS PremiumCount,
    (SELECT COUNT_BIG(*) FROM [LV].[PECHT0] AS q WITH (NOLOCK)
      WHERE q.C_STE = @Company AND q.NO_CNT = @ContractNumber)
        AS ReceiptCount;";

            string paymentDatabase = Settings.DbConfig.GetLisaPaymentDatabase(normalizedEnvironment);
            var database = new DatabaseManager(normalizedEnvironment, paymentDatabase);
            long premiumCount = 0;
            long receiptCount = 0;

            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using DataTable table = await database.GetDataAsync(
                        query,
                        new Dictionary<string, object>
                        {
                            ["@Company"] = "A",
                            ["@ContractNumber"] = contractNumber
                        },
                        token).ConfigureAwait(false);

                    if (table.Rows.Count == 1)
                    {
                        DataRow row = table.Rows[0];
                        premiumCount = ReadRequiredInt64(row, "PremiumCount");
                        receiptCount = ReadRequiredInt64(row, "ReceiptCount");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[PRE-LI BUSINESS VALIDATION] SQL check warning on {paymentDatabase}: {ex.Message}");
                    onProgress?.Invoke($"[PRE-LI] SQL check warning ({paymentDatabase}): {ex.Message}. Proceeding to LI1J04D0.");
                    return;
                }

                string diagnostic =
                    $"Database={paymentDatabase}; NO_CNT={technicalContract}; PRIST0={premiumCount}; PECHT0={receiptCount}.";
                Debug.WriteLine("[PRE-LI BUSINESS VALIDATION] " + diagnostic);

                if (premiumCount > 0 && receiptCount > 0)
                {
                    onProgress?.Invoke("Pre-LI business prerequisites confirmed in LISA payment DB. " + diagnostic);
                    return;
                }

                if (attempt < maximumAttempts)
                {
                    onProgress?.Invoke(
                        $"Waiting for LVPG22U persistence in {paymentDatabase} ({attempt}/{maximumAttempts}). " + diagnostic);
                    await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
                }
            }

            onProgress?.Invoke(
                $"[PRE-LI WARNING] PRIST0={premiumCount}, PECHT0={receiptCount} in {paymentDatabase} after {maximumAttempts}s; continuing to LI1J04D0.");
            _ = trace;
        }

        private static long ReadRequiredInt64(DataRow row, string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
                throw new InvalidOperationException("PRE_LI_SQL_RESULT_INVALID: missing column " + columnName);

            return Convert.ToInt64(row[columnName], CultureInfo.InvariantCulture);
        }

        private static string ValidateAndFormatAmount(
            string amount,
            bool skipPrime,
            string channel,
            ContractActivationTrace? trace)
        {
            if (TryParsePositiveAmount(amount, out decimal parsedAmount))
            {
                return parsedAmount.ToString("0.00", CultureInfo.InvariantCulture);
            }

            if (skipPrime || string.Equals(channel, "C05", StringComparison.OrdinalIgnoreCase))
            {
                return "0.00";
            }

            if (trace != null)
            {
                trace.FinalStatus = "FAILED";
                trace.FailureStage = trace.FoundInEliaFlag()
                    ? "DB_PREMIUM_ZERO"
                    : "DB_CONTRACT_NOT_FOUND";
                trace.FailureReason = trace.FoundInEliaFlag()
                    ? "The contract was found in ELIA, but the gross premium is missing or equal to 0.00."
                    : "The contract or Demand ID could not be resolved in ELIA.";
                trace.RootCauseAnalysis = trace.FoundInEliaFlag()
                    ? "ADDPRCT requires a positive premium when Skip Prime is disabled."
                    : "The ELIA identifiers could not be resolved in the selected environment.";
                trace.RecommendedAction = trace.FoundInEliaFlag()
                    ? "Verify FJ1.TB5UPRP, or use Skip Prime when no premium injection is required."
                    : "Verify the Demand ID, contract number and the selected environment.";
            }

            throw new InvalidOperationException(
                "Premium was not found or is equal to 0.00. Activation was cancelled.");
        }

        private static bool TryParsePositiveAmount(string amount, out decimal parsedAmount)
        {
            string cleaned = (amount ?? "0").Replace(",", ".").Trim();
            return decimal.TryParse(
                       cleaned,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out parsedAmount) &&
                   parsedAmount > 0m;
        }
    }

    internal static class ContractActivationTraceExtensions
    {
        public static bool FoundInEliaFlag(this ContractActivationTrace? trace)
        {
            return trace != null &&
                   !string.IsNullOrWhiteSpace(trace.EliaUconId) &&
                   !string.Equals(trace.EliaUconId, "N/A", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(trace.EliaUconId, "Not found", StringComparison.OrdinalIgnoreCase);
        }
    }
}
