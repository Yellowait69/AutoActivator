using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoActivator.Config
{
    public static partial class Settings
    {
        // =============================================================
        // CONFIGURATION BRANCHE 23 (PREMELIA + LVDQ + VNI SQL LV000GD0)
        // =============================================================

        /// <summary>
        /// Paramètres d'activation Branche 23 (PREMELIA + LVDQ + SQL VNI LV000GD0).
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

            public static bool AllowContinueWhenPe01NotAvailable { get; set; } = true;

            /// <summary>
            /// Lorsque false (valeur par défaut), les contrats Branche 23 enchaînent :
            /// 1. PREMELIA (ADDPRCT -> LVPP06U -> LVPG22U -> LI1J04D0 -> LI1J04D2)
            /// 2. LVDQ Seq 0 : LVDQFG03 (LVFG03U) à la date de création (DRUN) -> Mouvement État 2
            /// 3. SQL VNI : LV000GD0.LV.GFCRT0 / GFEVT0 à la date N+2
            /// 4. LVDQ Seq 1 : LVDQFG01 (LVFG01U) à la date N+2 -> Consolidation Mouvement État 4
            /// Lorsque true, s'arrête après LI1J04D2 (ou selon les cases cochées par l'utilisateur).
            /// </summary>
            public static bool Branch23ActivationOnly { get; set; } = false;
        }

        public static class Branch23
        {
            public const string ChainName = "LVDQ";
            public const string Application = "LV";
            public const string Ste = "A";

            /// <summary>
            /// Nombre de jours ouvrables bancaires ajoutés à la date d'exécution (Date N)
            /// pour obtenir la date de consolidation (Date N+2).
            /// </summary>
            public const int ConsolidationBankBusinessDays = 2;

            /// <summary>
            /// Fonds fermés connus (cotés dans LV000GD0.LV.GFEVT0).
            /// Les fonds ouverts sont cotés dans LV000GD0.LV.GFCRT0.
            /// </summary>
            public static readonly IReadOnlySet<string> ClosedFunds =
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
            /// Incrément appliqué à M_UNT pour créer une cotation manquante
            /// de fonds fermé dans LV000GD0.LV.GFEVT0.
            /// </summary>
            public static decimal? ClosedFundUnitIncrement { get; set; } = 300m;

            /// <summary>
            /// Incrément appliqué à M_UNT_BANQ pour créer une VNI manquante
            /// de fonds ouvert dans LV000GD0.LV.GFCRT0 (0m = copie de la dernière VNI).
            /// </summary>
            public static decimal? OpenFundNavIncrement { get; set; } = 0m;

            public static bool CanCreateClosedFundNav =>
                ClosedFundUnitIncrement.HasValue;

            public static bool CanCreateOpenFundNav =>
                OpenFundNavIncrement.HasValue;

            /// <summary>
            /// LVDQ Séquence 0 : LVDQFG03 (Template JCL : LVFG03U) à Date N -> Mouvement État 2.
            /// </summary>
            public static class FundEvents
            {
                public const int Sequence = 0;
                public const string JobName = "LVDQFG03";
                public const string JclName = "LVFG03U";
                public const bool EventDateUsesRunDate = true;

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

                private static readonly string[] OrderedGbtEvents =
                {
                    Gbt01, Gbt02, Gbt03, Gbt04, Gbt05,
                    Gbt06, Gbt07, Gbt08, Gbt09, Gbt10
                };

                public static string[] GetGbtEvents() => OrderedGbtEvents.ToArray();
            }

            /// <summary>
            /// LVDQ Séquence 1 : LVDQFG01 (Template JCL : LVFG01U, RESTART=0, STEP=1, UPD=Y)
            /// à Date N+2 -> Consolidation Mouvement en État 4.
            /// </summary>
            public static class Consolidation
            {
                public const int Sequence = 1;
                public const string JobName = "LVDQFG01";
                public const string JclName = "LVFG01U";
                public const string Restart = "0";
                public const string Step = "1";
                public const string Update = "Y";
            }

            /// <summary>
            /// LVDQ Séquence 3 : LVD1PE01 (Template JCL : LVPE01U, GBT=FP01).
            /// </summary>
            public static class Fp01
            {
                public const int Sequence = 3;
                public const string JobName = "LVD1PE01";
                public const string JclName = "LVPE01U";
                public const string CollectionCode = "Y";
                public const string EventCode = "FP01";
                public const string Rollback = "Y";
            }

            public static class Movement77And79Consolidation
            {
                public const int Sequence = 2;
                public const string JobName = "LVDQFG01";
                public const string JclName = "LVFG01U";
                public const string Restart = "0";
                public const string Step = "0";
                public const string Update = "Y";
            }

            public static IReadOnlyList<string> GetJclNames() =>
                new[] { FundEvents.JclName, Consolidation.JclName, Fp01.JclName };

            public static IReadOnlyList<string> GetJobNames() =>
                new[] { FundEvents.JobName, Consolidation.JobName, Fp01.JobName };
        }
    }
}
