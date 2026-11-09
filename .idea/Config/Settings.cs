using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoActivator.Config
{
    /// <summary>
    /// Centralise la configuration globale de l'application AutoActivator :
    /// - répertoires et fichiers ;
    /// - templates JCL ;
    /// - routage ELIA ;
    /// - traitements Branche 23 ;
    /// - connexions SQL Server.
    ///
    /// Environnements SQL pris en charge : D/D000 et Q/Q000.
    /// Les noms des serveurs et bases sont centralisés dans DbConfig afin d'éviter
    /// toute construction manuelle dans les services d'activation.
    /// </summary>
    public static class Settings
    {
        // =============================================================
        // RÉPERTOIRES ET FICHIERS
        // =============================================================

        /// <summary>
        /// Répertoire dans lequel l'application est exécutée.
        /// </summary>
        public static readonly string BaseDir =
            AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// Répertoire racine des données locales.
        /// </summary>
        public static readonly string DataDir =
            Path.Combine(
                BaseDir,
                "data");

        /// <summary>
        /// Répertoire contenant les fichiers d'entrée.
        /// </summary>
        public static readonly string InputDir =
            Path.Combine(
                DataDir,
                "input");

        /// <summary>
        /// Répertoire contenant les rapports et fichiers générés.
        /// </summary>
        public static readonly string OutputDir =
            Path.Combine(
                DataDir,
                "output");

        /// <summary>
        /// Répertoire contenant les fichiers de référence.
        /// </summary>
        public static readonly string BaselineDir =
            Path.Combine(
                DataDir,
                "baseline");

        /// <summary>
        /// Répertoire contenant les snapshots d'extraction.
        /// </summary>
        public static readonly string SnapshotDir =
            Path.Combine(
                DataDir,
                "snapshot");

        /// <summary>
        /// Répertoire contenant les journaux locaux éventuels.
        /// </summary>
        public static readonly string LogDir =
            Path.Combine(
                DataDir,
                "logs");

        /// <summary>
        /// Répertoire réseau contenant les modèles JCL Mainframe.
        /// </summary>
        public static readonly string JclNetworkFolder =
            @"\\Jafile02\elia\11 - Technical Architecture\11 - IS Tooling\01 - Tools\LVCHAIN\JCL";

        /// <summary>
        /// Fichier Excel source par défaut.
        /// </summary>
        public static readonly string SourceFile =
            Path.Combine(
                InputDir,
                "contrats_sources.xlsx");

        /// <summary>
        /// Fichier contenant les contrats en attente d'activation.
        /// </summary>
        public static readonly string ActivationOutputFile =
            Path.Combine(
                InputDir,
                "contrats_en_attente_activation.xlsx");

        /// <summary>
        /// Alias conservé pour la compatibilité avec le code existant.
        /// </summary>
        public static readonly string InputFile =
            ActivationOutputFile;

        /// <summary>
        /// Crée automatiquement les répertoires locaux utilisés
        /// par l'application.
        /// </summary>
        static Settings()
        {
            ActivationAutomation.ValidateRoutingMatrix();
            EnsureDirectoriesExist();
        }

        /// <summary>
        /// S'assure que les répertoires de travail locaux existent.
        ///
        /// Le répertoire JCL réseau n'est volontairement pas créé.
        /// </summary>
        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(InputDir);
            Directory.CreateDirectory(OutputDir);
            Directory.CreateDirectory(BaselineDir);
            Directory.CreateDirectory(SnapshotDir);
            Directory.CreateDirectory(LogDir);
        }

        /// <summary>
        /// Vérifie que le répertoire réseau JCL est accessible.
        /// </summary>
        public static bool IsJclNetworkFolderAvailable()
        {
            try
            {
                return Directory.Exists(
                    JclNetworkFolder);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Normalise un environnement SQL.
        ///
        /// Valeurs acceptées :
        /// - D ;
        /// - D000 ;
        /// - Q ;
        /// - Q000.
        ///
        /// Lorsque la valeur est vide, D000 est utilisé pour préserver
        /// le comportement historique de l'application.
        /// </summary>
        public static string NormalizeEnvironmentSuffix(
            string? envSuffix)
        {
            if (string.IsNullOrWhiteSpace(envSuffix))
            {
                return "Q000";
            }

            return DbConfig.NormalizeEnvironment(
                envSuffix);
        }

        /// <summary>
        /// Retourne la lettre courte d'un environnement.
        ///
        /// Exemples :
        /// - D000 devient D ;
        /// - Q000 devient Q.
        /// </summary>
        public static string NormalizeShortEnvironment(
            string? envSuffix)
        {
            string normalized =
                NormalizeEnvironmentSuffix(
                    envSuffix);

            return normalized switch
            {
                "D000" => "D",
                "Q000" => "Q",

                _ => throw new ArgumentOutOfRangeException(
                    nameof(envSuffix),
                    normalized,
                    "Environnement court non pris en charge.")
            };
        }

        /// <summary>
        /// Construit le chemin réseau par défaut du fichier Excel de
        /// résultats Testing pour un process et un environnement.
        /// </summary>
        public static string GetDefaultTestingExcelPath(
            string? channel,
            string? env)
        {
            string safeChannel =
                string.IsNullOrWhiteSpace(channel)
                    ? "C01"
                    : channel.Trim().ToUpperInvariant();

            string normalizedEnvironment =
                NormalizeEnvironmentSuffix(env);

            return
                $@"\\jafile01\Automated_Testing\IS_QCRUNS\00_GENERICS\KEY_{safeChannel}ComparisonsDB_URL_ELIA_LoginPage_{normalizedEnvironment}.xls";
        }

        // =============================================================
        // ROUTING PRÉACTIVATION ELIA
        // =============================================================

        /// <summary>
        /// Configuration du routing automatique avant activation.
        /// </summary>
        public static class PreActivationRouting
        {
            /// <summary>
            /// Environnements automatiquement interrogés lorsqu'aucun
            /// environnement n'est imposé.
            ///
            /// L'ordre est significatif, mais le resolver doit signaler
            /// une ambiguïté lorsque le Demand existe dans plusieurs
            /// environnements.
            /// </summary>
            public static readonly IReadOnlyList<string>
                SupportedEnvironments =
                    Array.AsReadOnly(
                        new[]
                        {
                            "Q000",
                            "D000"
                        });

            /// <summary>
            /// Processus ELIA actuellement pris en charge par le workflow
            /// automatique d'activation.
            /// </summary>
            public static readonly IReadOnlySet<string>
                SupportedActivationProcesses =
                    new HashSet<string>(
                        new[]
                        {
                            "C01",
                            "C03",
                            "C05"
                        },
                        StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Product Codes ELIA classifiés comme Branche 23.
            ///
            /// Cette liste sert de référentiel applicatif initial.
            /// Elle doit être remplacée ou complétée par un référentiel
            /// produit métier lorsqu'une source officielle exploitable
            /// sera disponible.
            /// </summary>
            public static readonly IReadOnlySet<string>
                KnownBranch23Products =
                    new HashSet<string>(
                        new[]
                        {
                            "9500",
                            "9502",
                            "9503",
                            "9506",
                            "D018",
                            "D022",
                            "O018",
                            "F018",
                            "B018",
                            "EB18"
                        },
                        StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Indique si un Product Code appartient au référentiel BR23.
            /// </summary>
            public static bool IsKnownBranch23Product(
                string? productCode)
            {
                return !string.IsNullOrWhiteSpace(productCode) &&
                       KnownBranch23Products.Contains(
                           productCode.Trim());
            }

            /// <summary>
            /// Indique si le process ELIA est pris en charge.
            /// </summary>
            public static bool IsSupportedActivationProcess(
                string? processCode)
            {
                return !string.IsNullOrWhiteSpace(processCode) &&
                       SupportedActivationProcesses.Contains(
                           processCode.Trim());
            }
        }

        // =============================================================
        // ACTIVATION AUTOMATIQUE SQL
        // =============================================================
        /// <summary>
        /// Source unique des règles d'autocomplétion utilisées par l'activation
        /// unitaire et le batch. Les valeurs métier Bank/Broker doivent rester
        /// identiques à celles d'ActivationRoutingService.
        /// </summary>
        public static class ActivationAutomation
        {
            public const string DefaultInputMode = "SqlAutocomplete";
            public const int SqlCommandTimeoutSeconds = 120;
            public const int BatchSqlConcurrency = 4;
            public const int MaximumBatchSqlConcurrency = 16;

            public const string BankBucp = "00382";
            public const string BankCmdpmt = "8";
            public const string BrokerNewGenerationBucp = "80819";
            public const string BrokerNewGenerationCmdpmt = "H";

            public static readonly IReadOnlySet<string> BankChannelCodes =
                new HashSet<string>(
                    new[]
                    {
                        "01", "02", "04", "05", "13",
                        "18", "22", "23", "24"
                    },
                    StringComparer.OrdinalIgnoreCase);

            public static readonly IReadOnlySet<string> BrokerNewGenerationChannelCodes =
                new HashSet<string>(
                    new[]
                    {
                        "03", "06", "07", "08", "09",
                        "10", "11", "12", "14", "15",
                        "16", "17", "19", "20", "21"
                    },
                    StringComparer.OrdinalIgnoreCase);

            public static string NormalizeChannelCode(string? channelCode)
            {
                string value = (channelCode ?? string.Empty).Trim();
                return value.Length == 1
                    ? value.PadLeft(2, '0')
                    : value;
            }

            public static bool IsBankChannel(string? channelCode)
            {
                return BankChannelCodes.Contains(
                    NormalizeChannelCode(channelCode));
            }

            public static bool IsBrokerNewGenerationChannel(string? channelCode)
            {
                return BrokerNewGenerationChannelCodes.Contains(
                    NormalizeChannelCode(channelCode));
            }

            public static void ValidateRoutingMatrix()
            {
                string[] duplicateCodes = BankChannelCodes
                    .Intersect(
                        BrokerNewGenerationChannelCodes,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (duplicateCodes.Length > 0)
                {
                    throw new InvalidOperationException(
                        "ChannelCode present in Bank and Broker routing: " +
                        string.Join(", ", duplicateCodes));
                }
            }
        }

        // =============================================================
        // CONFIGURATION BRANCHE 23
        // =============================================================

        /// <summary>
        /// Configuration des traitements spécifiques aux contrats
        /// Branche 23.
        ///
        /// Chaîne LVCHAIN concernée : LVDQ.
        /// </summary>
        /// <summary>
        /// Activation Branche 23 via PREMELIA (ADDPRCT -> LI1J04D2).
        /// La création SCNTT0 est asynchrone et n'est pas un contrôle final bloquant.
        /// N'affecte pas la Branche 21.
        /// </summary>
        public static class Branch23Activation
        {
            public const string Class = "I";
            public const string App = "LV";
            public const string Ste = "A";

            public const string BankBucp = ActivationAutomation.BankBucp;
            public const string BankCmdpmt = ActivationAutomation.BankCmdpmt;
            public const string BrokerNewGenerationBucp =
                ActivationAutomation.BrokerNewGenerationBucp;
            public const string BrokerNewGenerationCmdpmt =
                ActivationAutomation.BrokerNewGenerationCmdpmt;

            public const string ConclusionPremiumGroup = "01";
            public const string ConclusionPremiumType = "02";

            public const string ActiveContractState = "4";

            /// <summary>
            /// Politique lorsque le contrôle PE01 direct n'est pas
            /// disponible (localisation SQL de LV.ELIAT0 non confirmée).
            /// false = NOT_AVAILABLE bloque l'activation.
            /// </summary>
            public static bool AllowContinueWhenPe01NotAvailable { get; set; } = false;

            /// <summary>
            /// Branch23ActivationOnly s'arrête après LI1J04D2. Aucun fonds, VNI,
            /// LVDQFG03/01, LVD1PE01 ni seconde passe LI. SCNTT0 n'est pas interrogée
            /// immédiatement afin d'éviter un faux échec pendant la propagation LISA.
            /// </summary>
            public static bool Branch23ActivationOnly { get; set; } = true;
        }

        public static class Branch23
        {
            /// <summary>
            /// Nom de la chaîne LVCHAIN.
            /// </summary>
            public const string ChainName =
                "LVDQ";

            /// <summary>
            /// Application utilisée par les jobs LVDQ.
            /// </summary>
            public const string Application =
                "LV";

            /// <summary>
            /// STE utilisé dans les traitements LISA/LV.
            /// </summary>
            public const string Ste =
                "A";

            /// <summary>
            /// Nombre de jours ouvrables bancaires ajoutés à la date
            /// d'exécution pour déterminer la date de consolidation.
            ///
            /// Il s'agit d'une date métier transmise aux JCL.
            /// L'application n'attend pas réellement deux jours.
            /// </summary>
            public const int ConsolidationBankBusinessDays =
                2;

            /// <summary>
            /// Exemples connus de fonds fermés BR23.
            ///
            /// Cette liste n'est pas exhaustive. Lorsqu'un fonds est
            /// absent, Branch23FundService examine son historique dans :
            /// - LV.GFEVT0 pour un fonds fermé ;
            /// - LV.GFCRT0 pour un fonds ouvert.
            /// </summary>
            public static readonly IReadOnlySet<string>
                ClosedFunds =
                    new HashSet<string>(
                        new[]
                        {
                            "WSE001",
                            "CTO002",
                            "FICA",
                            "GDI001"
                        },
                        StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Incrément appliqué à M_UNT pour créer une cotation
            /// manquante de fonds fermé dans LV.GFEVT0.
            ///
            /// La propriété reste nullable afin de pouvoir désactiver
            /// la création automatique sans modifier Branch23FundService.
            /// </summary>
            public static decimal? ClosedFundUnitIncrement
            {
                get;
                set;
            } = 300m;

            /// <summary>
            /// Incrément appliqué à M_UNT_BANQ pour créer une VNI
            /// manquante de fonds ouvert dans LV.GFCRT0.
            ///
            /// La règle n'étant pas encore validée, la valeur reste null.
            /// Une VNI ouverte manquante sera signalée, mais ne sera pas
            /// créée automatiquement.
            /// </summary>
            public static decimal? OpenFundNavIncrement
            {
                get;
                set;
            }

            /// <summary>
            /// Indique si AutoActivator est autorisé à créer une VNI
            /// de fonds fermé.
            /// </summary>
            public static bool CanCreateClosedFundNav =>
                ClosedFundUnitIncrement.HasValue;

            /// <summary>
            /// Indique si AutoActivator est autorisé à créer une VNI
            /// de fonds ouvert.
            /// </summary>
            public static bool CanCreateOpenFundNav =>
                OpenFundNavIncrement.HasValue;

            /// <summary>
            /// Configuration de LVDQFG03.
            ///
            /// Séquence LVCHAIN : 0.
            /// Template JCL      : LVFG03U.
            /// </summary>
            public static class FundEvents
            {
                public const int Sequence =
                    0;

                public const string JobName =
                    "LVDQFG03";

                public const string JclName =
                    "LVFG03U";

                /// <summary>
                /// Dans LVCHAIN, DEVN reçoit la date d'exécution.
                /// </summary>
                public const bool EventDateUsesRunDate =
                    true;

                public const string Gbt01 = "PF01";
                public const string Gbt02 = "PF02";
                public const string Gbt03 = "LF01";
                public const string Gbt04 = "LF02";
                public const string Gbt05 = "GF01";
                public const string Gbt06 = "MF01";
                public const string Gbt07 = "MF02";
                public const string Gbt08 = "GF03";
                public const string Gbt09 = "LF03";
                public const string Gbt10 = "GF04";

                private static readonly string[]
                    OrderedGbtEvents =
                    {
                        Gbt01,
                        Gbt02,
                        Gbt03,
                        Gbt04,
                        Gbt05,
                        Gbt06,
                        Gbt07,
                        Gbt08,
                        Gbt09,
                        Gbt10
                    };

                /// <summary>
                /// Retourne une copie des événements GBT dans l'ordre
                /// attendu par LVFG03U.
                /// </summary>
                public static string[] GetGbtEvents()
                {
                    return OrderedGbtEvents.ToArray();
                }
            }

            /// <summary>
            /// Configuration de la consolidation BR23 standard.
            ///
            /// Séquence LVCHAIN : 1.
            /// Job              : LVDQFG01.
            /// Template JCL     : LVFG01U.
            ///
            /// La séquence 2 emploie également LVFG01U, mais avec STEP=0
            /// pour les mouvements 77/79. Le workflow standard BR23
            /// utilise uniquement la séquence 1.
            /// </summary>
            public static class Consolidation
            {
                public const int Sequence =
                    1;

                public const string JobName =
                    "LVDQFG01";

                public const string JclName =
                    "LVFG01U";

                public const string Restart =
                    "0";

                public const string Step =
                    "1";

                public const string Update =
                    "Y";
            }

            /// <summary>
            /// Configuration de LVD1PE01 pour l'événement FP01.
            ///
            /// Séquence LVCHAIN : 3.
            /// Template JCL     : LVPE01U.
            /// </summary>
            public static class Fp01
            {
                public const int Sequence =
                    3;

                public const string JobName =
                    "LVD1PE01";

                public const string JclName =
                    "LVPE01U";

                public const string CollectionCode =
                    "Y";

                public const string EventCode =
                    "FP01";

                public const string Rollback =
                    "Y";
            }

            /// <summary>
            /// Paramètres de la consolidation spéciale des mouvements
            /// 77 et 79.
            ///
            /// Cette séquence ne fait pas partie du workflow BR23 standard
            /// exécuté automatiquement.
            /// </summary>
            public static class Movement77And79Consolidation
            {
                public const int Sequence =
                    2;

                public const string JobName =
                    "LVDQFG01";

                public const string JclName =
                    "LVFG01U";

                public const string Restart =
                    "0";

                public const string Step =
                    "0";

                public const string Update =
                    "Y";
            }

            /// <summary>
            /// Retourne les templates JCL BR23 dans l'ordre d'exécution.
            /// </summary>
            public static IReadOnlyList<string> GetJclNames()
            {
                return new[]
                {
                    FundEvents.JclName,
                    Consolidation.JclName,
                    Fp01.JclName
                };
            }

            /// <summary>
            /// Retourne les noms logiques des jobs BR23 dans l'ordre
            /// d'exécution.
            /// </summary>
            public static IReadOnlyList<string> GetJobNames()
            {
                return new[]
                {
                    FundEvents.JobName,
                    Consolidation.JobName,
                    Fp01.JobName
                };
            }
        }

        public sealed record ActivationEnvironmentSettings(
            string Name,
            string ShortName,
            string Ims,
            string Db2,
            string EliaDatabase,
            string PaymentDatabase,
            string ContractDatabase,
            string FundMovementDatabase,
            string FastCtrl);

        public static ActivationEnvironmentSettings GetActivationEnvironment(
            string? envSuffix)
        {
            string environment = NormalizeEnvironmentSuffix(envSuffix);
            return environment switch
            {
                "Q000" => new ActivationEnvironmentSettings(
                    "Q000", "Q", "IM7C", "Q2C",
                    DbConfig.GetLifeDatabase(environment),
                    DbConfig.GetLisaPaymentDatabase(environment),
                    DbConfig.GetLisaContractDatabase(environment),
                    DbConfig.GetFundMovementDatabase(environment),
                    "I10.DB.CA.FIB.FASTCTRL"),

                "D000" => new ActivationEnvironmentSettings(
                    "D000", "D", "IM7T", "Q2T",
                    DbConfig.GetLifeDatabase(environment),
                    DbConfig.GetLisaPaymentDatabase(environment),
                    DbConfig.GetLisaContractDatabase(environment),
                    DbConfig.GetFundMovementDatabase(environment),
                    "I0T.DB.CA.FIB.FASTCTRL"),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(envSuffix),
                    environment,
                    "Environnement d'activation non pris en charge.")
            };
        }

        // =============================================================
        // CONFIGURATION SQL SERVER
        // =============================================================

        /// <summary>
        /// Configuration des connexions SQL Server.
        /// </summary>
        public static class DbConfig
        {
            public const string Driver =
                "SQL Server";

            /// <summary>
            /// Mode d'authentification SQL.
            ///
            /// "yes" utilise Integrated Security.
            /// "no" utilise Uid et Pwd.
            /// </summary>
            public const string TrustedConnection =
                "yes";

            /// <summary>
            /// Taille maximale du pool SQL.
            /// </summary>
            public const int MaximumPoolSize =
                150;

            /// <summary>
            /// Timeout de connexion SQL, en secondes.
            /// </summary>
            public const int ConnectionTimeoutSeconds =
                30;

            /// <summary>
            /// Identifiant SQL conservé uniquement pendant la session.
            /// Non utilisé lorsque TrustedConnection vaut "yes".
            /// </summary>
            public static string Uid { get; set; } =
                "XA3894";

            /// <summary>
            /// Mot de passe SQL conservé uniquement pendant la session.
            /// Non utilisé lorsque TrustedConnection vaut "yes".
            /// </summary>
            public static string Pwd { get; set; } =
                string.Empty;

            /// <summary>
            /// Génère la chaîne de connexion vers FJ0AGDB pour
            /// l'environnement demandé.
            /// </summary>
            public static string GetConnectionString(
                string envSuffix)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                string server =
                    GetServerName(
                        environment);

                string database =
                    GetApplicationDatabase(
                        environment);

                if (TrustedConnection.Equals(
                        "yes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return BuildIntegratedConnectionString(
                        server,
                        database);
                }

                ValidateSqlCredentials();

                return BuildSqlAuthenticationConnectionString(
                    server,
                    database,
                    Uid,
                    Pwd);
            }

            /// <summary>
            /// Génère une chaîne de connexion vers une base spécifique
            /// située sur le serveur de l'environnement demandé.
            ///
            /// Le nom de base doit être généré par l'application ou
            /// validé avant utilisation.
            /// </summary>
            public static string GetConnectionString(
                string envSuffix,
                string databaseName)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                string server =
                    GetServerName(
                        environment);

                string database =
                    ValidateSqlIdentifier(
                        databaseName,
                        nameof(databaseName));

                if (TrustedConnection.Equals(
                        "yes",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return BuildIntegratedConnectionString(
                        server,
                        database);
                }

                ValidateSqlCredentials();

                return BuildSqlAuthenticationConnectionString(
                    server,
                    database,
                    Uid,
                    Pwd);
            }

            /// <summary>
            /// Retourne le serveur SQL associé à l'environnement.
            /// </summary>
            public static string GetServerName(
                string envSuffix)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                return environment switch
                {
                    "D000" => "DBMFBDBD",
                    "Q000" => "DBMFBDBQ",

                    _ => throw new ArgumentOutOfRangeException(
                        nameof(envSuffix),
                        environment,
                        "Environnement SQL non pris en charge.")
                };
            }

            /// <summary>
            /// Retourne la base principale de l'application.
            /// </summary>
            public static string GetApplicationDatabase(
                string envSuffix)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                return
                    $"FJ0AGDB_{environment}";
            }

            /// <summary>
            /// Retourne la base LIFE contenant les tables ELIA/FJ1.
            /// </summary>
            public static string GetLifeDatabase(
                string envSuffix)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                return
                    $"FJ5LIFE_{environment}";
            }

            /// <summary>
            /// Retourne la base LISA des réceptions et primes
            /// (LV.PRCTT0, LV.PRIST0, LV.PECHT0).
            /// </summary>
            public static string GetLisaPaymentDatabase(
                string envSuffix)
            {
                return
                    $"LV000PD0_{NormalizeEnvironment(envSuffix)}";
            }

            /// <summary>
            /// Retourne la base LISA des contrats (LV.SCNTT0).
            /// </summary>
            public static string GetLisaContractDatabase(
                string envSuffix)
            {
                return
                    $"LV000SD0_{NormalizeEnvironment(envSuffix)}";
            }

            /// <summary>
            /// Retourne la base des mouvements de fonds LISA.
            /// </summary>
            public static string GetFundMovementDatabase(
                string envSuffix)
            {
                string environment =
                    NormalizeEnvironment(
                        envSuffix);

                return
                    $"LV000FD0_{environment}";
            }

            /// <summary>
            /// Construit le nom SQL complet d'une table LIFE.
            ///
            /// Exemple :
            /// GetLifeTable("Q", "TB5UCON")
            /// retourne [FJ5LIFE_Q000].[FJ1].[TB5UCON].
            /// </summary>
            public static string GetLifeTable(
                string envSuffix,
                string tableName,
                string schemaName = "FJ1")
            {
                string database =
                    GetLifeDatabase(
                        envSuffix);

                string validatedSchema =
                    ValidateSqlIdentifier(
                        schemaName,
                        nameof(schemaName));

                string validatedTable =
                    ValidateSqlIdentifier(
                        tableName,
                        nameof(tableName));

                return
                    $"[{database}]." +
                    $"[{validatedSchema}]." +
                    $"[{validatedTable}]";
            }

            /// <summary>
            /// Construit le nom SQL complet d'une table de la base
            /// applicative FJ0AGDB.
            /// </summary>
            public static string GetApplicationTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV")
            {
                string database =
                    GetApplicationDatabase(
                        envSuffix);

                string validatedSchema =
                    ValidateSqlIdentifier(
                        schemaName,
                        nameof(schemaName));

                string validatedTable =
                    ValidateSqlIdentifier(
                        tableName,
                        nameof(tableName));

                return
                    $"[{database}]." +
                    $"[{validatedSchema}]." +
                    $"[{validatedTable}]";
            }

            public static string GetLisaPaymentTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV")
            {
                return BuildQualifiedTableName(
                    GetLisaPaymentDatabase(envSuffix),
                    schemaName,
                    tableName);
            }

            public static string GetLisaContractTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV")
            {
                return BuildQualifiedTableName(
                    GetLisaContractDatabase(envSuffix),
                    schemaName,
                    tableName);
            }

            /// <summary>
            /// Construit le nom SQL complet d'une table de la base des
            /// mouvements de fonds.
            /// </summary>
            public static string GetFundMovementTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV")
            {
                string database =
                    GetFundMovementDatabase(
                        envSuffix);

                string validatedSchema =
                    ValidateSqlIdentifier(
                        schemaName,
                        nameof(schemaName));

                string validatedTable =
                    ValidateSqlIdentifier(
                        tableName,
                        nameof(tableName));

                return
                    $"[{database}]." +
                    $"[{validatedSchema}]." +
                    $"[{validatedTable}]";
            }

            private static string BuildQualifiedTableName(
                string database,
                string schemaName,
                string tableName)
            {
                string validatedDatabase = ValidateSqlIdentifier(
                    database,
                    nameof(database));
                string validatedSchema = ValidateSqlIdentifier(
                    schemaName,
                    nameof(schemaName));
                string validatedTable = ValidateSqlIdentifier(
                    tableName,
                    nameof(tableName));

                return
                    $"[{validatedDatabase}]." +
                    $"[{validatedSchema}]." +
                    $"[{validatedTable}]";
            }

            /// <summary>
            /// Normalise les représentations de l'environnement SQL.
            ///
            /// D    devient D000.
            /// D000 reste D000.
            /// Q    devient Q000.
            /// Q000 reste Q000.
            /// </summary>
            public static string NormalizeEnvironment(
                string? envSuffix)
            {
                if (string.IsNullOrWhiteSpace(envSuffix))
                {
                    throw new ArgumentException(
                        "L'environnement SQL est obligatoire.",
                        nameof(envSuffix));
                }

                string normalizedValue =
                    envSuffix
                        .Trim()
                        .ToUpperInvariant();

                return normalizedValue switch
                {
                    "D" => "D000",
                    "D000" => "D000",
                    "Q" => "Q000",
                    "Q000" => "Q000",

                    _ => throw new ArgumentException(
                        $"Environnement SQL invalide : " +
                        $"'{envSuffix}'. Les valeurs autorisées " +
                        "sont D, D000, Q et Q000.",
                        nameof(envSuffix))
                };
            }

            /// <summary>
            /// Retourne true lorsque la valeur représente un environnement
            /// SQL supporté.
            /// </summary>
            public static bool IsSupportedEnvironment(
                string? envSuffix)
            {
                if (string.IsNullOrWhiteSpace(envSuffix))
                {
                    return false;
                }

                try
                {
                    NormalizeEnvironment(envSuffix);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            private static string BuildIntegratedConnectionString(
                string server,
                string database)
            {
                return
                    $"Server={server};" +
                    $"Database={database};" +
                    "Integrated Security=True;" +
                    "TrustServerCertificate=True;" +
                    "Pooling=True;" +
                    $"Max Pool Size={MaximumPoolSize};" +
                    $"Connect Timeout={ConnectionTimeoutSeconds};";
            }

            private static string
                BuildSqlAuthenticationConnectionString(
                    string server,
                    string database,
                    string userId,
                    string password)
            {
                return
                    $"Server={server};" +
                    $"Database={database};" +
                    $"User Id={userId};" +
                    $"Password={password};" +
                    "TrustServerCertificate=True;" +
                    "Pooling=True;" +
                    $"Max Pool Size={MaximumPoolSize};" +
                    $"Connect Timeout={ConnectionTimeoutSeconds};";
            }

            private static void ValidateSqlCredentials()
            {
                if (string.IsNullOrWhiteSpace(Uid))
                {
                    throw new InvalidOperationException(
                        "L'identifiant SQL est obligatoire lorsque " +
                        "TrustedConnection vaut 'no'.");
                }

                if (string.IsNullOrWhiteSpace(Pwd))
                {
                    throw new InvalidOperationException(
                        "Le mot de passe SQL est obligatoire lorsque " +
                        "TrustedConnection vaut 'no'.");
                }
            }

            /// <summary>
            /// Vérifie qu'un identifiant SQL ne contient que des lettres,
            /// chiffres ou underscores.
            /// </summary>
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
                    bool valid =
                        char.IsLetterOrDigit(character) ||
                        character == '_';

                    if (!valid)
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
}