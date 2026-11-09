using AutoActivator.Config;
using AutoActivator.Models;
using AutoActivator.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Services
{
    /// <summary>
    /// Construit les variables JCL spécifiques au workflow Branche 23.
    ///
    /// Ces variables complètent les variables générales préparées par
    /// ActivationDataService.
    ///
    /// Les variables générales suivantes ne sont donc pas dupliquées ici :
    /// - ENV ;
    /// - ENVIMS ;
    /// - Q2 ;
    /// - CUS ;
    /// - STE ;
    /// - CNTBEG ;
    /// - CNTEND ;
    /// - CLASS ;
    /// - USERNAME ;
    /// - FASTCTRL.
    ///
    /// Les dictionnaires retournés sont indexés par nom de template JCL :
    /// - LVFG03U pour LVDQFG03 ;
    /// - LVFG01U pour LVDQFG01 ;
    /// - LVPE01U pour LVD1PE01.
    /// </summary>
    public static class Branch23ParameterBuilder
    {
        private const string JclDateFormat =
            "yyyyMMdd";

        private const string JclMonthYearFormat =
            "MM.yyyy";

        private const int ExpectedGbtEventCount =
            10;

        public const string BankBucp = "00382";
        public const string BankPaymentCommand = "8";
        public const string BrokerNewGenerationBucp = "80819";
        public const string BrokerNewGenerationPaymentCommand = "H";

        /// <summary>
        /// Calcule la date métier de consolidation Branche 23.
        ///
        /// La date correspond à la date d'exécution augmentée du nombre
        /// de jours ouvrables bancaires défini dans :
        ///
        /// Settings.Branch23.ConsolidationBankBusinessDays.
        ///
        /// Cette méthode calcule uniquement une date métier.
        /// Elle ne provoque aucune attente applicative.
        /// </summary>
        /// <param name="runDate">
        /// Date d'exécution du workflow.
        /// </param>
        /// <returns>
        /// Date de consolidation sans composante horaire.
        /// </returns>
        public static Dictionary<string, string> BuildPaymentRoutingVariables(
            string? channelCode)
        {
            return PreActivationRoutingInfo.ResolveBusinessChannel(channelCode) switch
            {
                PreActivationBusinessChannel.Bank =>
                    CreatePaymentVariables(
                        BankBucp, BankPaymentCommand, "BANK", channelCode),
                PreActivationBusinessChannel.BrokerNewGeneration =>
                    CreatePaymentVariables(
                        BrokerNewGenerationBucp,
                        BrokerNewGenerationPaymentCommand,
                        "BROKER_NEW_GEN",
                        channelCode),
                _ => throw new InvalidOperationException(
                    $"ELIA ChannelCode '{NormalizeChannelCode(channelCode)}' " +
                    "is not mapped to a supported Branch 23 route.")
            };
        }

        public static string ValidatePaymentRouting(
            string? bucp,
            string? paymentCommand)
        {
            string normalizedBucp = NormalizeBucp(bucp);
            string command = (paymentCommand ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (normalizedBucp == BankBucp && command == BankPaymentCommand)
                return "BANK";

            if (normalizedBucp == BrokerNewGenerationBucp &&
                command == BrokerNewGenerationPaymentCommand)
                return "BROKER_NEW_GEN";

            throw new InvalidOperationException(
                $"Unsupported Branch 23 BUCP/CMDPMT pair " +
                $"'{normalizedBucp}/{command}'. Supported routes are " +
                $"BANK {BankBucp}/{BankPaymentCommand} and BROKER_NEW_GEN " +
                $"{BrokerNewGenerationBucp}/{BrokerNewGenerationPaymentCommand}.");
        }

        public static Dictionary<string, string> MergePaymentRoutingVariables(
            IReadOnlyDictionary<string, string> commonVariables,
            string? channelCode)
        {
            ArgumentNullException.ThrowIfNull(commonVariables);
            var result = new Dictionary<string, string>(
                commonVariables,
                StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> item in
                     BuildPaymentRoutingVariables(channelCode))
            {
                result[item.Key] = item.Value;
            }

            return result;
        }

        public static DateTime GetConsolidationDate(
            DateTime runDate)
        {
            DateTime normalizedRunDate =
                NormalizeDate(
                    runDate,
                    nameof(runDate));

            int businessDays =
                Settings.Branch23
                    .ConsolidationBankBusinessDays;

            if (businessDays < 0)
            {
                throw new InvalidOperationException(
                    "Settings.Branch23." +
                    "ConsolidationBankBusinessDays ne peut pas " +
                    "être négatif.");
            }

            DateTime consolidationDate =
                BankingCalendar.AddBankBusinessDays(
                    normalizedRunDate,
                    businessDays);

            return consolidationDate.Date;
        }

        /// <summary>
        /// Construit les variables spécifiques de tous les jobs BR23.
        ///
        /// Le dictionnaire principal est indexé par nom de template JCL,
        /// et non par nom logique de job.
        ///
        /// Clés retournées :
        /// - Settings.Branch23.FundEvents.JclName ;
        /// - Settings.Branch23.Consolidation.JclName ;
        /// - Settings.Branch23.Fp01.JclName.
        /// </summary>
        /// <param name="runDate">
        /// Date d'exécution du workflow.
        /// </param>
        /// <param name="consolidationDate">
        /// Date métier utilisée pour la consolidation et FP01.
        /// </param>
        public static Dictionary<
            string,
            Dictionary<string, string>> BuildJobVariables(
                DateTime runDate,
                DateTime consolidationDate)
        {
            DateTime normalizedRunDate =
                NormalizeDate(
                    runDate,
                    nameof(runDate));

            DateTime normalizedConsolidationDate =
                NormalizeDate(
                    consolidationDate,
                    nameof(consolidationDate));

            ValidateConfiguration();

            var variables =
                new Dictionary<
                    string,
                    Dictionary<string, string>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [Settings.Branch23.FundEvents.JclName] =
                        BuildFundEventsVariables(
                            normalizedRunDate),

                    [Settings.Branch23.Consolidation.JclName] =
                        BuildConsolidationVariables(
                            normalizedConsolidationDate),

                    [Settings.Branch23.Fp01.JclName] =
                        BuildFp01Variables(
                            normalizedConsolidationDate)
                };

            ValidateGeneratedVariables(
                variables);

            return variables;
        }

        /// <summary>
        /// Construit uniquement les variables du template LVFG03U.
        ///
        /// Le job logique correspondant est LVDQFG03.
        /// DEVN reçoit la date d'exécution, conformément au comportement
        /// de la chaîne LVCHAIN.
        /// </summary>
        public static Dictionary<string, string>
            BuildFundEventsVariables(
                DateTime runDate)
        {
            DateTime normalizedRunDate =
                NormalizeDate(
                    runDate,
                    nameof(runDate));

            string formattedRunDate =
                FormatJclDate(
                    normalizedRunDate);

            string[] gbtEvents =
                Settings.Branch23
                    .FundEvents
                    .GetGbtEvents();

            ValidateGbtEvents(
                gbtEvents);

            var variables =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["DRUN"] =
                        formattedRunDate,

                    ["DEVN"] =
                        Settings.Branch23
                            .FundEvents
                            .EventDateUsesRunDate
                            ? formattedRunDate
                            : FormatJclDate(DateTime.Today)
                };

            for (int index = 0;
                 index < gbtEvents.Length;
                 index++)
            {
                string variableName =
                    $"GBT{index + 1:00}";

                variables[variableName] =
                    NormalizeRequiredValue(
                        gbtEvents[index],
                        variableName);
            }

            return variables;
        }

        /// <summary>
        /// Construit uniquement les variables du template LVFG01U.
        ///
        /// Le job logique correspondant est LVDQFG01, séquence 1.
        ///
        /// La consolidation standard utilise :
        /// - RESTART=0 ;
        /// - STEP=1 ;
        /// - UPD=Y.
        ///
        /// La séquence 2, utilisée pour les mouvements 77 et 79 avec
        /// STEP=0, n'est jamais construite par cette méthode.
        /// </summary>
        public static Dictionary<string, string>
            BuildConsolidationVariables(
                DateTime consolidationDate)
        {
            DateTime normalizedConsolidationDate =
                NormalizeDate(
                    consolidationDate,
                    nameof(consolidationDate));

            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["DRUN"] =
                    FormatJclDate(
                        normalizedConsolidationDate),

                ["RESTART"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Consolidation
                            .Restart,
                        "RESTART"),

                ["STEP"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Consolidation
                            .Step,
                        "STEP"),

                ["UPD"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Consolidation
                            .Update,
                        "UPD")
            };
        }

        /// <summary>
        /// Construit uniquement les variables du template LVPE01U.
        ///
        /// Le job logique correspondant est LVD1PE01 pour l'événement
        /// FP01.
        ///
        /// Les dates du template sont construites sous la forme :
        /// - D-ECH = 01.%%N1MCY ;
        /// - D-PIV = 01.%%N2MCY.
        ///
        /// N1MCY correspond au mois de traitement.
        /// N2MCY correspond au mois suivant.
        /// </summary>
        public static Dictionary<string, string>
            BuildFp01Variables(
                DateTime consolidationDate)
        {
            DateTime normalizedConsolidationDate =
                NormalizeDate(
                    consolidationDate,
                    nameof(consolidationDate));

            (
                DateTime dueMonth,
                DateTime pivotMonth
            ) = GetDueAndPivotMonths(
                normalizedConsolidationDate);

            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["DRUN"] =
                    FormatJclDate(
                        normalizedConsolidationDate),

                ["N1MCY"] =
                    FormatMonthYear(
                        dueMonth),

                ["N2MCY"] =
                    FormatMonthYear(
                        pivotMonth),

                ["CINC"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Fp01
                            .CollectionCode,
                        "CINC"),

                ["ROLLB"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Fp01
                            .Rollback,
                        "ROLLB"),

                ["GBT"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Fp01
                            .EventCode,
                        "GBT")
            };
        }

        /// <summary>
        /// Calcule les mois d'échéance et de pivot utilisés par LVPE01U.
        ///
        /// DueMonth correspond au premier jour du mois de traitement.
        /// PivotMonth correspond au premier jour du mois suivant.
        /// </summary>
        public static (
            DateTime DueMonth,
            DateTime PivotMonth
        ) GetDueAndPivotMonths(
            DateTime processingDate)
        {
            DateTime normalizedProcessingDate =
                NormalizeDate(
                    processingDate,
                    nameof(processingDate));

            var dueMonth =
                new DateTime(
                    normalizedProcessingDate.Year,
                    normalizedProcessingDate.Month,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Unspecified);

            DateTime pivotMonth =
                dueMonth.AddMonths(1);

            return (
                dueMonth,
                pivotMonth
            );
        }

        /// <summary>
        /// Construit les variables de la consolidation spéciale des
        /// mouvements 77 et 79.
        ///
        /// Cette méthode est exposée pour un usage explicite, mais elle
        /// n'est pas utilisée par le workflow BR23 standard.
        /// </summary>
        public static Dictionary<string, string>
            BuildMovement77And79Variables(
                DateTime consolidationDate)
        {
            DateTime normalizedConsolidationDate =
                NormalizeDate(
                    consolidationDate,
                    nameof(consolidationDate));

            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["DRUN"] =
                    FormatJclDate(
                        normalizedConsolidationDate),

                ["RESTART"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Movement77And79Consolidation
                            .Restart,
                        "RESTART"),

                ["STEP"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Movement77And79Consolidation
                            .Step,
                        "STEP"),

                ["UPD"] =
                    NormalizeRequiredValue(
                        Settings.Branch23
                            .Movement77And79Consolidation
                            .Update,
                        "UPD")
            };
        }

        /// <summary>
        /// Vérifie la cohérence de la configuration BR23 statique.
        /// </summary>
        public static void ValidateConfiguration()
        {
            ValidatePaymentRouting(BankBucp, BankPaymentCommand);
            ValidatePaymentRouting(
                BrokerNewGenerationBucp,
                BrokerNewGenerationPaymentCommand);

            ValidateJobConfiguration(
                Settings.Branch23.FundEvents.JobName,
                Settings.Branch23.FundEvents.JclName,
                nameof(Settings.Branch23.FundEvents));

            ValidateJobConfiguration(
                Settings.Branch23.Consolidation.JobName,
                Settings.Branch23.Consolidation.JclName,
                nameof(Settings.Branch23.Consolidation));

            ValidateJobConfiguration(
                Settings.Branch23.Fp01.JobName,
                Settings.Branch23.Fp01.JclName,
                nameof(Settings.Branch23.Fp01));

            if (Settings.Branch23
                    .Consolidation
                    .Sequence != 1)
            {
                throw new InvalidOperationException(
                    "La consolidation BR23 standard doit utiliser " +
                    "la séquence LVCHAIN 1.");
            }

            if (!string.Equals(
                    Settings.Branch23
                        .Consolidation
                        .Step,
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "La consolidation BR23 standard doit utiliser " +
                    "STEP=1.");
            }

            if (Settings.Branch23
                    .Movement77And79Consolidation
                    .Sequence != 2)
            {
                throw new InvalidOperationException(
                    "La consolidation spéciale MVT 77/79 doit " +
                    "utiliser la séquence LVCHAIN 2.");
            }

            if (!string.Equals(
                    Settings.Branch23
                        .Movement77And79Consolidation
                        .Step,
                    "0",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "La consolidation spéciale MVT 77/79 doit " +
                    "utiliser STEP=0.");
            }

            if (Settings.Branch23
                    .Fp01
                    .Sequence != 3)
            {
                throw new InvalidOperationException(
                    "Le traitement FP01 doit utiliser la séquence " +
                    "LVCHAIN 3.");
            }

            ValidateGbtEvents(
                Settings.Branch23
                    .FundEvents
                    .GetGbtEvents());
        }

        /// <summary>
        /// Vérifie que les dictionnaires générés contiennent les trois
        /// templates attendus et leurs variables obligatoires.
        /// </summary>
        private static void ValidateGeneratedVariables(
            IReadOnlyDictionary<
                string,
                Dictionary<string, string>> variables)
        {
            ArgumentNullException.ThrowIfNull(
                variables);

            ValidateTemplateVariables(
                variables,
                Settings.Branch23.FundEvents.JclName,
                new[]
                {
                    "DRUN",
                    "DEVN",
                    "GBT01",
                    "GBT02",
                    "GBT03",
                    "GBT04",
                    "GBT05",
                    "GBT06",
                    "GBT07",
                    "GBT08",
                    "GBT09",
                    "GBT10"
                });

            ValidateTemplateVariables(
                variables,
                Settings.Branch23.Consolidation.JclName,
                new[]
                {
                    "DRUN",
                    "RESTART",
                    "STEP",
                    "UPD"
                });

            ValidateTemplateVariables(
                variables,
                Settings.Branch23.Fp01.JclName,
                new[]
                {
                    "DRUN",
                    "N1MCY",
                    "N2MCY",
                    "CINC",
                    "ROLLB",
                    "GBT"
                });
        }

        private static void ValidateTemplateVariables(
            IReadOnlyDictionary<
                string,
                Dictionary<string, string>> allVariables,
            string templateName,
            IEnumerable<string> requiredVariables)
        {
            if (!allVariables.TryGetValue(
                    templateName,
                    out Dictionary<string, string>? templateVariables))
            {
                throw new InvalidOperationException(
                    $"Aucun dictionnaire de variables n'a été " +
                    $"généré pour le template JCL " +
                    $"'{templateName}'.");
            }

            string[] missingVariables =
                requiredVariables
                    .Where(
                        variable =>
                            !templateVariables.TryGetValue(
                                variable,
                                out string? value) ||
                            string.IsNullOrWhiteSpace(value))
                    .ToArray();

            if (missingVariables.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Variables obligatoires manquantes pour " +
                $"{templateName} : " +
                string.Join(
                    ", ",
                    missingVariables.Select(
                        variable =>
                            "%%" + variable)));
        }

        private static void ValidateGbtEvents(
            IReadOnlyList<string>? events)
        {
            if (events == null)
            {
                throw new InvalidOperationException(
                    "La configuration des événements GBT est absente.");
            }

            if (events.Count != ExpectedGbtEventCount)
            {
                throw new InvalidOperationException(
                    $"La configuration LVFG03U doit contenir " +
                    $"{ExpectedGbtEventCount} événements GBT. " +
                    $"Nombre reçu : {events.Count}.");
            }

            for (int index = 0;
                 index < events.Count;
                 index++)
            {
                if (string.IsNullOrWhiteSpace(events[index]))
                {
                    throw new InvalidOperationException(
                        $"L'événement GBT{index + 1:00} est vide.");
                }
            }

            string[] duplicates =
                events
                    .Select(
                        value =>
                            value.Trim().ToUpperInvariant())
                    .GroupBy(
                        value =>
                            value,
                        StringComparer.OrdinalIgnoreCase)
                    .Where(
                        group =>
                            group.Count() > 1)
                    .Select(
                        group =>
                            group.Key)
                    .ToArray();

            if (duplicates.Length > 0)
            {
                throw new InvalidOperationException(
                    "La configuration contient des événements GBT " +
                    "dupliqués : " +
                    string.Join(
                        ", ",
                        duplicates));
            }
        }

        private static void ValidateJobConfiguration(
            string jobName,
            string jclName,
            string configurationName)
        {
            if (string.IsNullOrWhiteSpace(jobName))
            {
                throw new InvalidOperationException(
                    $"Le nom du job est absent dans " +
                    $"{configurationName}.");
            }

            if (string.IsNullOrWhiteSpace(jclName))
            {
                throw new InvalidOperationException(
                    $"Le nom du template JCL est absent dans " +
                    $"{configurationName}.");
            }
        }

        private static Dictionary<string, string> CreatePaymentVariables(
            string bucp,
            string paymentCommand,
            string businessChannel,
            string? channelCode)
        {
            string channel = NormalizeChannelCode(channelCode);
            string normalizedBucp = NormalizeBucp(bucp);
            string command = NormalizeRequiredValue(
                paymentCommand,
                "CMDPMT");

            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["BUCP"] = normalizedBucp,
                ["CMDPMT"] = command,
                ["CHANNEL_CODE"] = channel,
                ["BUSINESS_CHANNEL"] = businessChannel,
                ["ROUTING_RULE"] =
                    $"CHANNEL_{channel}_{businessChannel}_" +
                    $"{normalizedBucp}_{command}"
            };
        }

        private static string NormalizeChannelCode(string? value)
        {
            string result = (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            return result.Length == 1 ? result.PadLeft(2, '0') : result;
        }

        private static string NormalizeBucp(string? value)
        {
            string result = (value ?? string.Empty).Trim();
            return result.Length == 0 ? string.Empty : result.PadLeft(5, '0');
        }

        private static DateTime NormalizeDate(
            DateTime value,
            string parameterName)
        {
            if (value == DateTime.MinValue ||
                value == DateTime.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "La date fournie n'est pas exploitable.");
            }

            return value.Date;
        }

        private static string FormatJclDate(
            DateTime value)
        {
            return value.ToString(
                JclDateFormat,
                CultureInfo.InvariantCulture);
        }

        private static string FormatMonthYear(
            DateTime value)
        {
            return value.ToString(
                JclMonthYearFormat,
                CultureInfo.InvariantCulture);
        }

        private static string NormalizeRequiredValue(
            string? value,
            string variableName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"La variable JCL '{variableName}' ne peut pas " +
                    "être vide.");
            }

            return value
                .Trim()
                .ToUpperInvariant();
        }
    }
}