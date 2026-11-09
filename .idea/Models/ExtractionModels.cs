using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Models
{
    // =========================================================================
    // ACTIVATION LOOKUP
    // =========================================================================

    /// <summary>
    /// Résultat de la résolution initiale d'un contrat ou d'un Demand ID.
    ///
    /// Ce modèle contient :
    /// - les identifiants ELIA et LISA ;
    /// - le numéro de contrat utilisable dans les JCL ;
    /// - les informations de prime disponibles ;
    /// - le contexte de routing ELIA lorsqu'il a pu être résolu.
    /// </summary>
    public sealed class ActivationLookupResult
    {
        // -----------------------------------------------------------------
        // ENTRÉE
        // -----------------------------------------------------------------

        public string RawInput { get; set; } =
            string.Empty;

        public bool IsDemandId { get; set; }

        // -----------------------------------------------------------------
        // RÉSOLUTION ELIA / LISA
        // -----------------------------------------------------------------

        public bool FoundInElia { get; set; }

        public string ContractNumber { get; set; } =
            string.Empty;

        public string JclContract { get; set; } =
            string.Empty;

        public string EliaUconId { get; set; } =
            "N/A";

        public string DemandId { get; set; } =
            "N/A";

        public string LisaInternalId { get; set; } =
            "N/A";

        public string LisaContractNumber { get; set; } =
            "N/A";

        // -----------------------------------------------------------------
        // PRIME
        // -----------------------------------------------------------------

        public string PremiumSequence { get; set; } =
            "N/A";

        public string RawPremium { get; set; } =
            "0";

        public string NetPremium { get; set; } =
            "0";

        // -----------------------------------------------------------------
        // ROUTING ELIA
        // -----------------------------------------------------------------

        /// <summary>
        /// Environnement court dans lequel la demande a été résolue.
        /// Valeurs attendues : D ou Q.
        /// </summary>
        public string ResolvedEnvironment { get; set; } =
            string.Empty;

        /// <summary>
        /// ProductCode provenant de TB5HDMD.IT5HDMDCPRD.
        /// </summary>
        public string ProductCode { get; set; } =
            string.Empty;

        /// <summary>
        /// ProcessCode provenant de TB5HDMD.IT5HDMDCPCS.
        /// Exemples : C01, C02, C03 et C05.
        /// </summary>
        public string ProcessCode { get; set; } =
            string.Empty;

        /// <summary>
        /// ChannelCode technique provenant de TB5HDMD.IT5HDMDCCDI.
        /// </summary>
        public string ChannelCode { get; set; } =
            string.Empty;

        /// <summary>
        /// ApplicationChannelCode provenant de TB5HDMD.IT5HDMDCAPL.
        /// </summary>
        public string ApplicationChannelCode { get; set; } =
            string.Empty;

        /// <summary>
        /// Statut brut de la demande ELIA.
        /// </summary>
        public string DemandStatus { get; set; } =
            string.Empty;

        /// <summary>
        /// Code brut de méthode de paiement ELIA.
        ///
        /// Cette valeur ne doit pas être injectée directement dans
        /// CMDPMT sans mapping métier validé.
        /// </summary>
        public string PaymentMethodCode { get; set; } =
            string.Empty;

        public ActivationPaymentChannel PaymentChannel { get; set; } =
            ActivationPaymentChannel.Unknown;
        public string BusinessChannel { get; set; } = string.Empty;
        public string Bucp { get; set; } = string.Empty;
        public string Cmdpmt { get; set; } = string.Empty;
        public string RoutingRule { get; set; } = string.Empty;
        public string RoutingSource { get; set; } = string.Empty;
        public string RunDate { get; set; } = string.Empty;
        public string RunDateSource { get; set; } = string.Empty;

        public string PremiumGroupType { get; set; } =
            string.Empty;

        public int FundFeatureCount { get; set; }

        public ContractBranchClassification BranchClassification
        {
            get;
            set;
        } = ContractBranchClassification.Unknown;

        public BranchDetectionSource BranchDetectionSource
        {
            get;
            set;
        } = BranchDetectionSource.Unknown;

        public string BranchDetectionReason { get; set; } =
            string.Empty;

        public PreActivationRoutingStatus RoutingStatus
        {
            get;
            set;
        } = PreActivationRoutingStatus.Unknown;

        public string RoutingDiagnostic { get; set; } =
            string.Empty;

        /// <summary>
        /// Propriété conservée pour compatibilité avec d'anciens appels.
        ///
        /// Le nouveau code doit préférer BranchClassification.
        /// </summary>
        public bool? IsBranch23
        {
            get
            {
                return BranchClassification switch
                {
                    ContractBranchClassification.Branch23 => true,
                    ContractBranchClassification.Standard => false,
                    _ => null
                };
            }

            set
            {
                if (!value.HasValue)
                {
                    return;
                }

                BranchClassification =
                    value.Value
                        ? ContractBranchClassification.Branch23
                        : ContractBranchClassification.Standard;

                if (BranchDetectionSource ==
                    BranchDetectionSource.Unknown)
                {
                    BranchDetectionSource =
                        BranchDetectionSource.ManualOverride;
                }
            }
        }

        // -----------------------------------------------------------------
        // DIAGNOSTIC
        // -----------------------------------------------------------------

        public string DbDiagnosticStatus { get; set; } =
            "N/A";

        public long LookupDurationMs { get; set; }

        public string DbError { get; set; } =
            string.Empty;

        // -----------------------------------------------------------------
        // PROPRIÉTÉS CALCULÉES
        // -----------------------------------------------------------------

        public bool HasContractNumber =>
            !IsMissingValue(ContractNumber);

        public bool HasJclContract =>
            !IsMissingValue(JclContract);

        public bool HasEliaContract =>
            FoundInElia &&
            !IsMissingValue(EliaUconId);

        public bool HasLisaContract =>
            !IsMissingValue(LisaInternalId);

        public bool HasPremium =>
            HasPositiveAmount(RawPremium) ||
            HasPositiveAmount(NetPremium);

        public bool HasFundFeatures =>
            FundFeatureCount > 0;
        public bool HasResolvedPaymentRouting =>
            PaymentChannel != ActivationPaymentChannel.Unknown &&
            !string.IsNullOrWhiteSpace(Bucp) &&
            !string.IsNullOrWhiteSpace(Cmdpmt);
        public string ResolvedBusinessChannel => PaymentChannel switch
        {
            ActivationPaymentChannel.Bank => "BANK",
            ActivationPaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
            _ => string.IsNullOrWhiteSpace(BusinessChannel)
                ? "UNKNOWN"
                : BusinessChannel.Trim().ToUpperInvariant()
        };

        public bool IsReadyForActivation =>
            HasContractNumber &&
            HasJclContract &&
            HasEliaContract;

        public string ToDiagnosticSummary()
        {
            return
                $"Input={Display(RawInput)}, " +
                $"IsDemand={IsDemandId}, " +
                $"Environment={Display(ResolvedEnvironment)}, " +
                $"Demand={Display(DemandId)}, " +
                $"UCON={Display(EliaUconId)}, " +
                $"Contract={Display(ContractNumber)}, " +
                $"JCL={Display(JclContract)}, " +
                $"LISA={Display(LisaInternalId)}, " +
                $"Product={Display(ProductCode)}, " +
                $"Process={Display(ProcessCode)}, " +
                $"Channel={Display(ChannelCode)}, " +
                $"BusinessChannel={ResolvedBusinessChannel}, " +
                $"BUCP/CMDPMT={Display(Bucp)}/{Display(Cmdpmt)}, " +
                $"RoutingRule={Display(RoutingRule)}, " +
                $"RunDate={Display(RunDate)}, " +
                $"RunDateSource={Display(RunDateSource)}, " +
                $"Premium={Display(RawPremium)}, " +
                $"PaymentMethod={Display(PaymentMethodCode)}, " +
                $"FundFeatures={FundFeatureCount}, " +
                $"Branch={BranchClassification}, " +
                $"Routing={RoutingStatus}, " +
                $"DBStatus={Display(DbDiagnosticStatus)}, " +
                $"Lookup={LookupDurationMs}ms";
        }

        public override string ToString()
        {
            return ToDiagnosticSummary();
        }

        private static bool HasPositiveAmount(
            string? value)
        {
            string normalized =
                (value ?? "0")
                    .Replace(",", ".")
                    .Trim();

            return decimal.TryParse(
                       normalized,
                       NumberStyles.Any,
                       CultureInfo.InvariantCulture,
                       out decimal amount) &&
                   amount > 0m;
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
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       value.Trim(),
                       "Not resolved",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string Display(
            string? value)
        {
            return IsMissingValue(value)
                ? "-"
                : value!.Trim();
        }
    }

    // =========================================================================
    // JOB EXECUTION
    // =========================================================================

    /// <summary>
    /// Représente l'exécution d'une étape Mainframe.
    /// </summary>
    public sealed class JobStepExecutionInfo
    {
        /// <summary>Generated sub-job number (LI1J04D1/D3) when confirmed.</summary>
        public string SubJobNum { get; set; } = string.Empty;

        /// <summary>Generated sub-job return code when confirmed.</summary>
        public string SubJobReturnCode { get; set; } = string.Empty;

        public int StepOrder { get; set; }

        /// <summary>
        /// Nom logique du job utilisé dans la séquence.
        ///
        /// Exemple BR23 : LVDQFG03.
        /// </summary>
        public string JobName { get; set; } =
            string.Empty;

        /// <summary>
        /// Nom d'un éventuel sous-job généré.
        ///
        /// Exemple : LI1J04D1.
        /// </summary>
        public string SubJobName { get; set; } =
            string.Empty;

        /// <summary>
        /// Nom du template JCL lorsque celui-ci diffère de JobName.
        ///
        /// Exemple :
        /// JobName = LVDQFG03
        /// JclName = LVFG03U
        /// </summary>
        public string JclName { get; set; } =
            string.Empty;

        public string PhaseDescription { get; set; } =
            string.Empty;

        public string JobNum { get; set; } =
            "-";

        public string ServerNode { get; set; } =
            "-";

        public string Status { get; set; } =
            "Pending";

        public string ReturnCode { get; set; } =
            "-";

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public long DurationMs { get; set; }

        public int PollCount { get; set; }

        public string ErrorMessage { get; set; } =
            string.Empty;

        public string SpoolExcerpt { get; set; } =
            string.Empty;

        public string DiagnosticHint { get; set; } =
            string.Empty;
        public string BusinessChannel { get; set; } = string.Empty;
        public string Bucp { get; set; } = string.Empty;
        public string Cmdpmt { get; set; } = string.Empty;
        public string ChannelCode { get; set; } = string.Empty;
        public string RoutingRule { get; set; } = string.Empty;

        public bool IsBranch23Step { get; set; }

        public bool IsSuccess =>
            string.Equals(
                Status,
                "Complete",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "Output",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "Done",
                StringComparison.OrdinalIgnoreCase);

        public bool IsSkipped =>
            string.Equals(
                Status,
                "Skipped",
                StringComparison.OrdinalIgnoreCase);

        public bool IsPending =>
            string.Equals(
                Status,
                "Pending",
                StringComparison.OrdinalIgnoreCase);

        public bool IsAborted =>
            string.Equals(
                Status,
                "Aborted",
                StringComparison.OrdinalIgnoreCase);

        public bool IsFailed =>
            string.Equals(
                Status,
                "Failed",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "Abend",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "JCLError",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "SubmitFailed",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "JCLPrepFailed",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "Timeout",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "NavNotReady",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "FundsNotResolved",
                StringComparison.OrdinalIgnoreCase);

        public string EffectiveTemplateName =>
            string.IsNullOrWhiteSpace(JclName)
                ? JobName
                : JclName;

        public string DurationDisplay =>
            DurationMs > 0
                ? $"{DurationMs / 1000.0:F2}s"
                : "-";

        public string ToDiagnosticSummary()
        {
            return
                $"Step={StepOrder}, " +
                $"Job={JobName}, " +
                $"Template={EffectiveTemplateName}, " +
                $"SubJob={SubJobName}, " +
                $"JobId={JobNum}, " +
                $"Status={Status}, " +
                $"RC={ReturnCode}, " +
                $"Channel={BusinessChannel}, " +
                $"BUCP/CMDPMT={Bucp}/{Cmdpmt}, " +
                $"Duration={DurationDisplay}, " +
                $"Polls={PollCount}, " +
                $"BR23={IsBranch23Step}";
        }

        public override string ToString()
        {
            return ToDiagnosticSummary();
        }
    }

    // =========================================================================
    // CONTRACT ACTIVATION TRACE
    // =========================================================================

    /// <summary>
    /// Trace complète de la résolution et de l'activation d'un contrat.
    ///
    /// Cette classe est utilisée par :
    /// - l'activation unitaire ;
    /// - le batch ;
    /// - ActivationOrchestrator ;
    /// - les rapports de diagnostic ;
    /// - l'historique de l'interface.
    /// </summary>
    public sealed class ContractActivationTrace
    {
        // -----------------------------------------------------------------
        // SOURCE
        // -----------------------------------------------------------------

        public int RowNum { get; set; } = 1;

        public string RawInput { get; set; } =
            string.Empty;

        public bool IsDemandId { get; set; }

        // -----------------------------------------------------------------
        // RÉSOLUTION ELIA / LISA
        // -----------------------------------------------------------------

        public string ResolvedContract { get; set; } =
            "Not resolved";

        public string JclContract { get; set; } =
            "N/A";

        public string EliaUconId { get; set; } =
            "N/A";

        public string DemandId { get; set; } =
            "N/A";

        public string LisaInternalId { get; set; } =
            "N/A";

        public string LisaContractNumber { get; set; } =
            "N/A";

        // -----------------------------------------------------------------
        // ROUTING ELIA
        // -----------------------------------------------------------------

        public string ProductCode { get; set; } =
            string.Empty;

        public string ProcessCode { get; set; } =
            string.Empty;

        public string EliaChannelCode { get; set; } =
            string.Empty;

        public string ApplicationChannelCode { get; set; } =
            string.Empty;

        public string DemandStatus { get; set; } =
            string.Empty;

        public string PaymentMethodCode { get; set; } =
            string.Empty;

        public string PremiumGroupType { get; set; } =
            string.Empty;

        public int FundFeatureCount { get; set; }

        public ContractBranchClassification BranchClassification
        {
            get;
            set;
        } = ContractBranchClassification.Unknown;

        public BranchDetectionSource BranchDetectionSource
        {
            get;
            set;
        } = BranchDetectionSource.Unknown;

        public string BranchDetectionReason { get; set; } =
            string.Empty;

        public PreActivationRoutingStatus RoutingStatus
        {
            get;
            set;
        } = PreActivationRoutingStatus.Unknown;

        public string RoutingDiagnostic { get; set; } =
            string.Empty;
        public ActivationPaymentChannel PaymentChannel { get; set; } =
            ActivationPaymentChannel.Unknown;
        public string BusinessChannel { get; set; } = string.Empty;
        public string RoutingRule { get; set; } = string.Empty;
        public string RoutingSource { get; set; } = string.Empty;
        public string RunDate { get; set; } = string.Empty;
        public string RunDateSource { get; set; } = string.Empty;

        // -----------------------------------------------------------------
        // PRIME
        // -----------------------------------------------------------------

        public string PremiumSequence { get; set; } =
            "N/A";

        public string RawPremium { get; set; } =
            "0";

        public string NetPremium { get; set; } =
            "0";

        public string FormattedAmount { get; set; } =
            "0000000.00";

        // -----------------------------------------------------------------
        // DIAGNOSTIC SQL
        // -----------------------------------------------------------------

        public string DbDiagnosticStatus { get; set; } =
            "N/A";

        public long DbLookupDurationMs { get; set; }

        public string DbError { get; set; } =
            string.Empty;

        // -----------------------------------------------------------------
        // CONTEXTE MAINFRAME
        // -----------------------------------------------------------------

        /// <summary>
        /// Environnement court D ou Q.
        /// </summary>
        public string Environment { get; set; } =
            "Q";

        /// <summary>
        /// Suffixe IMS utilisé pour former IM7T ou IM7C.
        ///
        /// Valeur attendue : T ou C.
        /// </summary>
        public string EnvIms { get; set; } =
            "C";

        public string Q2Region { get; set; } =
            "Q2C";

        public string FastCtrlDsn { get; set; } =
            string.Empty;

        public string ServerNode { get; set; } =
            "N/A";

        /// <summary>
        /// Process utilisé pour sélectionner la séquence JCL.
        ///
        /// Exemples : C01, C02, C03 ou C05.
        /// </summary>
        public string Channel { get; set; } =
            "C01";

        public string Cus { get; set; } =
            "MDEEM";

        public string Bucp { get; set; } =
            string.Empty;

        public string Cmdpmt { get; set; } =
            string.Empty;

        public bool SkipPrime { get; set; }

        public string Username { get; set; } =
            string.Empty;

        // -----------------------------------------------------------------
        // BRANCHE 23
        // -----------------------------------------------------------------

        public bool IsBranch23 { get; set; }

        public string Branch23Source { get; set; } =
            "N/A";

        public DateTime? Branch23ConsolidationDate { get; set; }

        public List<string> Branch23Funds { get; set; } =
            new List<string>();

        public List<string> Branch23NavSummary { get; set; } =
            new List<string>();

        public string Branch23FundsSource { get; set; } =
            "N/A";

        /// <summary>Rendered Branch23ActivationOnly evidence report.</summary>
        public string Branch23ActivationReport { get; set; } = string.Empty;

        public long? Branch23LastMovementBefore { get; set; }

        public long? Branch23MovementNumber { get; set; }

        public string Branch23MovementState { get; set; } =
            "N/A";

        // -----------------------------------------------------------------
        // ÉTAPES JCL
        // -----------------------------------------------------------------

        public List<JobStepExecutionInfo> Steps { get; set; } =
            new List<JobStepExecutionInfo>();

        // -----------------------------------------------------------------
        // RÉSULTAT
        // -----------------------------------------------------------------

        /// <summary>
        /// Valeurs principales :
        /// PENDING, RUNNING, SUCCESS, ALREADY_ACTIVE, FAILED, CANCELLED.
        /// </summary>
        public string FinalStatus { get; set; } =
            "PENDING";

        public string FailureStage { get; set; } =
            "NONE";

        public string FailedJobName { get; set; } =
            "-";

        public string FailedJobNum { get; set; } =
            "-";

        public string FailedReturnCode { get; set; } =
            "-";

        public string FailureReason { get; set; } =
            string.Empty;

        public string RootCauseAnalysis { get; set; } =
            string.Empty;

        public string RecommendedAction { get; set; } =
            string.Empty;

        // -----------------------------------------------------------------
        // DURÉES
        // -----------------------------------------------------------------

        public DateTime StartTime { get; set; } =
            DateTime.Now;

        public DateTime EndTime { get; set; }

        public long TotalDurationMs { get; set; }

        // -----------------------------------------------------------------
        // PROPRIÉTÉS CALCULÉES
        // -----------------------------------------------------------------

        public bool IsCompleted =>
            string.Equals(
                FinalStatus,
                "SUCCESS",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                FinalStatus,
                "ALREADY_ACTIVE",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                FinalStatus,
                "FAILED",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                FinalStatus,
                "CANCELLED",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                FinalStatus,
                "PENDING_ASYNC_LISA_CREATION",
                StringComparison.OrdinalIgnoreCase);

        public bool IsSuccessful =>
            string.Equals(
                FinalStatus,
                "SUCCESS",
                StringComparison.OrdinalIgnoreCase);

        public bool IsAlreadyActive =>
            string.Equals(
                FinalStatus,
                "ALREADY_ACTIVE",
                StringComparison.OrdinalIgnoreCase);

        public bool IsCancelled =>
            string.Equals(
                FinalStatus,
                "CANCELLED",
                StringComparison.OrdinalIgnoreCase);

        public bool IsFailed =>
            string.Equals(
                FinalStatus,
                "FAILED",
                StringComparison.OrdinalIgnoreCase);

        public int SuccessfulStepsCount =>
            Steps?.Count(step => step.IsSuccess) ?? 0;

        public int SkippedStepsCount =>
            Steps?.Count(step => step.IsSkipped) ?? 0;

        public int FailedStepsCount =>
            Steps?.Count(step => step.IsFailed) ?? 0;

        public int ExecutableStepsCount =>
            Steps?.Count(step => !step.IsSkipped) ?? 0;

        public string ElapsedDisplay =>
            TotalDurationMs > 0
                ? $"{TotalDurationMs / 1000.0:F2}s"
                : "-";

        public string StepsSummary
        {
            get
            {
                if (Steps == null ||
                    Steps.Count == 0)
                {
                    return "No JCL executed";
                }

                int successful =
                    Steps.Count(step => step.IsSuccess);

                int executable =
                    Steps.Count(step => !step.IsSkipped);

                int skipped =
                    Steps.Count(step => step.IsSkipped);

                JobStepExecutionInfo? lastStep =
                    Steps
                        .Where(
                            step =>
                                !step.IsPending &&
                                !step.IsSkipped)
                        .OrderByDescending(
                            step => step.StepOrder)
                        .FirstOrDefault();

                string lastInformation =
                    lastStep != null
                        ? $"{lastStep.JobName} " +
                          $"({lastStep.JobNum}: " +
                          $"RC={lastStep.ReturnCode}, " +
                          $"Status={lastStep.Status})"
                        : "None";

                return
                    $"{successful}/{executable} OK, " +
                    $"{skipped} skipped, " +
                    $"last: {lastInformation}";
            }
        }

        public string ToDiagnosticSummary()
        {
            return
                $"Row={RowNum}, " +
                $"Input={Display(RawInput)}, " +
                $"Environment={Display(Environment)}, " +
                $"Demand={Display(DemandId)}, " +
                $"UCON={Display(EliaUconId)}, " +
                $"Contract={Display(ResolvedContract)}, " +
                $"JCL={Display(JclContract)}, " +
                $"LISA={Display(LisaInternalId)}, " +
                $"Product={Display(ProductCode)}, " +
                $"Process={Display(Channel)}, " +
                $"EliaChannel={Display(EliaChannelCode)}, " +
                $"BusinessChannel={Display(BusinessChannel)}, " +
                $"BUCP/CMDPMT={Display(Bucp)}/{Display(Cmdpmt)}, " +
                $"RoutingRule={Display(RoutingRule)}, " +
                $"RunDate={Display(RunDate)}, " +
                $"RunDateSource={Display(RunDateSource)}, " +
                $"Premium={Display(RawPremium)}, " +
                $"Branch={BranchClassification}, " +
                $"BR23={IsBranch23}, " +
                $"Status={FinalStatus}, " +
                $"FailureStage={FailureStage}, " +
                $"Duration={ElapsedDisplay}";
        }

        public override string ToString()
        {
            return ToDiagnosticSummary();
        }

        private static string Display(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "-"
                : value.Trim();
        }
    }

    // =========================================================================
    // ACTIVATION HISTORY
    // =========================================================================

    /// <summary>
    /// Ligne synthétique affichée dans l'historique des activations.
    /// </summary>
    public sealed class ActivationHistoryItem
    {
        public string RowNum { get; set; } =
            "-";

        public string Time { get; set; } =
            string.Empty;

        public string Env { get; set; } =
            string.Empty;

        public string InputId { get; set; } =
            string.Empty;

        public string ContractId { get; set; } =
            string.Empty;

        public string JclContract { get; set; } =
            string.Empty;

        public string ProductCode { get; set; } =
            string.Empty;

        public string ProcessCode { get; set; } =
            string.Empty;
        public string ChannelCode { get; set; } = string.Empty;
        public string BusinessChannel { get; set; } = string.Empty;
        public string Bucp { get; set; } = string.Empty;
        public string Cmdpmt { get; set; } = string.Empty;
        public string RunDate { get; set; } = string.Empty;
        public string RoutingRule { get; set; } = string.Empty;

        public string Premium { get; set; } =
            "0.00";

        public bool IsBranch23 { get; set; }

        public string StepsInfo { get; set; } =
            string.Empty;

        public string Duration { get; set; } =
            "0.0s";

        public string Status { get; set; } =
            string.Empty;

        public string DiagnosticSummary { get; set; } =
            string.Empty;

        public ContractActivationTrace? FullTrace { get; set; }
    }

    // =========================================================================
    // EXTRACTION MODELS
    // =========================================================================

    public sealed class ExtractionResult
    {
        public string FilePath { get; set; } =
            string.Empty;

        public string StatusMessage { get; set; } =
            string.Empty;

        public string InternalId { get; set; } =
            "Not found";

        public string UconId { get; set; } =
            "Not found";

        public string DemandId { get; set; } =
            "Not found";

        public string LisaContent { get; set; } =
            string.Empty;

        public string EliaContent { get; set; } =
            string.Empty;

        public string Premium { get; set; } =
            "0";

        public string ContractReference { get; set; } =
            string.Empty;
    }

    public sealed class BatchProgressInfo
    {
        public int RowNum { get; set; }

        public string ContractId { get; set; } =
            string.Empty;

        public string InternalId { get; set; } =
            string.Empty;

        public string Product { get; set; } =
            string.Empty;

        public string Premium { get; set; } =
            "0";

        public string UconId { get; set; } =
            string.Empty;

        public string DemandId { get; set; } =
            string.Empty;

        public string ProcessCode { get; set; } =
            string.Empty;
        public string ChannelCode { get; set; } = string.Empty;
        public string BusinessChannel { get; set; } = string.Empty;
        public string Bucp { get; set; } = string.Empty;
        public string Cmdpmt { get; set; } = string.Empty;
        public string RunDate { get; set; } = string.Empty;

        public bool IsBranch23 { get; set; }

        public string Status { get; set; } =
            string.Empty;

        public int CurrentItem { get; set; }

        public int TotalItems { get; set; }

        public double ProgressPercentage =>
            TotalItems <= 0
                ? 0.0
                : Math.Round(
                    (double)CurrentItem /
                    TotalItems *
                    100.0,
                    1);
    }

    public sealed class ExtractionItem
    {
        public string RowNum { get; set; } =
            "-";

        public string ContractId { get; set; } =
            string.Empty;

        public string InternalId { get; set; } =
            string.Empty;

        public string Product { get; set; } =
            string.Empty;

        public string Premium { get; set; } =
            "0";

        public string Ucon { get; set; } =
            string.Empty;

        public string Hdmd { get; set; } =
            string.Empty;

        public string Time { get; set; } =
            string.Empty;

        public string Test { get; set; } =
            string.Empty;

        public string FilePath { get; set; } =
            string.Empty;
    }

    // =========================================================================
    // COMPARISON MODELS
    // =========================================================================

    public sealed class ComparisonMetrics
    {
        public int TotalRows { get; set; }

        public int ErrorRows { get; set; }

        public int SuccessfulRows =>
            Math.Max(
                0,
                TotalRows - ErrorRows);

        public double SuccessPercentage =>
            TotalRows == 0
                ? 100.0
                : Math.Round(
                    (double)SuccessfulRows /
                    TotalRows *
                    100.0,
                    2);
    }

    public sealed class ComparisonReport
    {
        public double GlobalSuccessPercentage { get; set; }

        public int TotalRowsCompared { get; set; }

        public int TotalDifferencesFound { get; set; }

        public int TotalBaseTests { get; set; }

        public int TotalTargetTests { get; set; }

        public int ComparedTestsCount { get; set; }

        public List<string> MissingInTarget { get; set; } =
            new List<string>();

        public List<string> MissingInBase { get; set; } =
            new List<string>();

        public Dictionary<string, ComparisonMetrics>
            TestMetrics
        { get; set; } =
                new Dictionary<string, ComparisonMetrics>(
                    StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ComparisonMetrics>
            ProductMetrics
        { get; set; } =
                new Dictionary<string, ComparisonMetrics>(
                    StringComparer.OrdinalIgnoreCase);

        public List<FileComparisonResult> FileResults { get; set; } =
            new List<FileComparisonResult>();
    }

    public sealed class FileComparisonResult
    {
        public string FileType { get; set; } =
            string.Empty;

        public string BaseFileName { get; set; } =
            string.Empty;

        public string TargetFileName { get; set; } =
            string.Empty;

        public string TableName { get; set; } =
            string.Empty;

        public string Status { get; set; } =
            string.Empty;

        public string ErrorDetails { get; set; } =
            string.Empty;

        public int TotalRows { get; set; }

        public int ErrorRows { get; set; }

        public bool IsMatch =>
            string.Equals(
                Status,
                "OK",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Status,
                "OK_EMPTY",
                StringComparison.OrdinalIgnoreCase);
    }
}