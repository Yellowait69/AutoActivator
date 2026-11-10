using System;
using System.Collections.Generic;

namespace AutoActivator.Models
{
    /// <summary>
    /// Sélection granulaire des jobs JCL et étapes SQL à exécuter lors d'une activation
    /// (mode "LVCHAIN à la carte" : un seul job, un sous-ensemble ou la chaîne complète).
    /// </summary>
    public sealed class ActivationJobSelection
    {
        // =============================================================
        // CHAÎNE PREMELIA (PRIME & ACTIVATION CONTRAT LISA)
        // =============================================================

        /// <summary>
        /// PremElia Ligne 1 : ADDPRCT (Ajout de la prime dans LV000PD0.LV.PRCTT0 via JCL).
        /// </summary>
        public bool RunAddPrct { get; set; } = true;

        /// <summary>
        /// Option alternative / complémentaire à ADDPRCT : insertion directe via SQL
        /// dans LV000PD0.LV.PRCTT0 (utile pour Broker ou lorsque la ligne 1 PremElia n'est pas utilisée).
        /// </summary>
        public bool UseSqlPrctInsert { get; set; } = false;

        /// <summary>
        /// PremElia Ligne 2 : LVPP06U (Validation comptable de la prime -> P41LV État 2).
        /// </summary>
        public bool RunLvpp06u { get; set; } = true;

        /// <summary>
        /// PremElia Ligne 3 : LVPG22U (Affectation financière et quittance + P15LV instance).
        /// </summary>
        public bool RunLvpg22u { get; set; } = true;

        /// <summary>
        /// PremElia Ligne 6 : LI1J04D0 (Activation contrat IMS Core).
        /// </summary>
        public bool RunLi1j04d0 { get; set; } = true;

        /// <summary>
        /// PremElia Ligne 7 : LI1J04D2 (Synchronisation finale IMS -> Contrat ACTIF État 4).
        /// </summary>
        public bool RunLi1j04d2 { get; set; } = true;

        // =============================================================
        // CHAÎNE LVDQ & SQL VNI (BRANCHE 23 : FONDS & CONSOLIDATION)
        // =============================================================

        /// <summary>
        /// LVDQ Ligne 0 : LVDQFG03 / LVFG03U à la date de création (DRUN = Date N).
        /// Crée le mouvement de fonds en LISA (F51LV) en État 2.
        /// </summary>
        public bool RunLvdqFg03 { get; set; } = true;

        /// <summary>
        /// SQL LV000GD0.LV.GFCRT0 (fonds ouverts) / LV.GFEVT0 (fonds fermés) :
        /// vérifie et insère la VNI (D_COURS / M_UNT_BANQ) de Date N jusqu'à Date N+2.
        /// </summary>
        public bool RunBranch23SqlVni { get; set; } = true;

        /// <summary>
        /// LVDQ Ligne 1 : LVDQFG01 / LVFG01U (RESTART=0, STEP=1, UPD=Y) à Date N+2.
        /// Consolide le mouvement de fonds en LISA (F51LV) de l'État 2 vers l'État 4.
        /// </summary>
        public bool RunLvdqFg01 { get; set; } = true;

        /// <summary>
        /// LVDQ Ligne 3 : LVD1PE01 / LVPE01U (Comptabilité GBT FP01 - optionnel).
        /// </summary>
        public bool RunLvd1Pe01 { get; set; } = false;

        // =============================================================
        // PROPRIÉTÉS CALCULÉES
        // =============================================================

        public bool HasAnyBillingJob =>
            RunAddPrct || UseSqlPrctInsert || RunLvpp06u || RunLvpg22u;

        public bool HasAnyImsJob =>
            RunLi1j04d0 || RunLi1j04d2;

        public bool HasAnyPremeliaJob =>
            HasAnyBillingJob || HasAnyImsJob;

        public bool RequiresPremelia =>
            HasAnyPremeliaJob;

        public bool HasAnyBranch23Step =>
            RunLvdqFg03 || RunBranch23SqlVni || RunLvdqFg01 || RunLvd1Pe01;

        public bool HasAnyStep =>
            HasAnyPremeliaJob || HasAnyBranch23Step;

        public bool IsBranch23OnlySelection =>
            !HasAnyPremeliaJob && HasAnyBranch23Step;

        public ActivationJobSelection Clone()
        {
            return new ActivationJobSelection
            {
                RunAddPrct = RunAddPrct,
                UseSqlPrctInsert = UseSqlPrctInsert,
                RunLvpp06u = RunLvpp06u,
                RunLvpg22u = RunLvpg22u,
                RunLi1j04d0 = RunLi1j04d0,
                RunLi1j04d2 = RunLi1j04d2,
                RunLvdqFg03 = RunLvdqFg03,
                RunBranch23SqlVni = RunBranch23SqlVni,
                RunLvdqFg01 = RunLvdqFg01,
                RunLvd1Pe01 = RunLvd1Pe01
            };
        }

        public bool IsJobSelected(string? jobName)
        {
            if (string.IsNullOrWhiteSpace(jobName))
            {
                return false;
            }

            return jobName.Trim().ToUpperInvariant() switch
            {
                "ADDPRCT" => RunAddPrct,
                "SQL_PRCTT0" => UseSqlPrctInsert,
                "LVPP06U" => RunLvpp06u,
                "LVPG22U" => RunLvpg22u,
                "LI1J04D0" => RunLi1j04d0,
                "LI1J04D2" => RunLi1j04d2,
                "LVDQFG03" or "LVFG03U" => RunLvdqFg03,
                "SQL_VNI" or "GFCRT0" or "GFEVT0" => RunBranch23SqlVni,
                "LVDQFG01" or "LVFG01U" => RunLvdqFg01,
                "LVD1PE01" or "LVPE01U" => RunLvd1Pe01,
                _ => true
            };
        }

        public string ToShortSummary(bool includeBranch23 = true)
        {
            var parts = new List<string>();
            if (RunAddPrct) parts.Add("ADDPRCT");
            if (UseSqlPrctInsert) parts.Add("SQL:PRCTT0");
            if (RunLvpp06u) parts.Add("LVPP06U");
            if (RunLvpg22u) parts.Add("LVPG22U");
            if (RunLi1j04d0) parts.Add("LI1J04D0");
            if (RunLi1j04d2) parts.Add("LI1J04D2");

            if (includeBranch23)
            {
                if (RunLvdqFg03) parts.Add("LVDQFG03(N)");
                if (RunBranch23SqlVni) parts.Add("SQL:GFCRT0(N+2)");
                if (RunLvdqFg01) parts.Add("LVDQFG01(N+2)");
                if (RunLvd1Pe01) parts.Add("LVD1PE01");
            }

            return parts.Count == 0
                ? "Aucun job sélectionné"
                : string.Join(" -> ", parts);
        }

        public string FormatSummary(bool includeBranch23 = true) =>
            ToShortSummary(includeBranch23);

        public static ActivationJobSelection FullChain() => new();

        public static ActivationJobSelection PremeliaOnly() => new()
        {
            RunAddPrct = true,
            UseSqlPrctInsert = false,
            RunLvpp06u = true,
            RunLvpg22u = true,
            RunLi1j04d0 = true,
            RunLi1j04d2 = true,
            RunLvdqFg03 = false,
            RunBranch23SqlVni = false,
            RunLvdqFg01 = false,
            RunLvd1Pe01 = false
        };

        public static ActivationJobSelection SkipAddPrct(bool includeBranch23 = true) => new()
        {
            RunAddPrct = false,
            UseSqlPrctInsert = false,
            RunLvpp06u = true,
            RunLvpg22u = true,
            RunLi1j04d0 = true,
            RunLi1j04d2 = true,
            RunLvdqFg03 = includeBranch23,
            RunBranch23SqlVni = includeBranch23,
            RunLvdqFg01 = includeBranch23,
            RunLvd1Pe01 = false
        };

        public static ActivationJobSelection ImsOnly(bool includeBranch23 = false) => new()
        {
            RunAddPrct = false,
            UseSqlPrctInsert = false,
            RunLvpp06u = false,
            RunLvpg22u = false,
            RunLi1j04d0 = true,
            RunLi1j04d2 = true,
            RunLvdqFg03 = includeBranch23,
            RunBranch23SqlVni = includeBranch23,
            RunLvdqFg01 = includeBranch23,
            RunLvd1Pe01 = false
        };

        public static ActivationJobSelection Branch23FundsAndConsolidationOnly() => new()
        {
            RunAddPrct = false,
            UseSqlPrctInsert = false,
            RunLvpp06u = false,
            RunLvpg22u = false,
            RunLi1j04d0 = false,
            RunLi1j04d2 = false,
            RunLvdqFg03 = true,
            RunBranch23SqlVni = true,
            RunLvdqFg01 = true,
            RunLvd1Pe01 = false
        };

        public static ActivationJobSelection Branch23VniAndConsolidationOnly() => new()
        {
            RunAddPrct = false,
            UseSqlPrctInsert = false,
            RunLvpp06u = false,
            RunLvpg22u = false,
            RunLi1j04d0 = false,
            RunLi1j04d2 = false,
            RunLvdqFg03 = false,
            RunBranch23SqlVni = true,
            RunLvdqFg01 = true,
            RunLvd1Pe01 = false
        };
    }
}
