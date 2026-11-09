using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AutoActivator.Config
{
    /// <summary>
    /// This file defines the exclusion rules used by the comparison module.
    /// It specifies which columns should be ignored when comparing data between different environments (e.g., D000 vs Q000).
    /// By ignoring database-generated technical identifiers, timestamps, and anonymized personal data,
    /// it prevents false positives and ensures the comparison focuses only on relevant business data.
    /// Exclusions can be applied globally to all tables or targeted to specific tables.
    /// </summary>
    public static class Exclusions
    {
        private static readonly ConcurrentDictionary<string, HashSet<string>> _cachedExclusions =
            new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // GLOBAL LIST
        public static readonly IReadOnlyList<string> IgnoreColumns = new List<string>
        {
            // Major technical identifiers
            "NO_CNT", "NO_CNT_EXTENDED", "NO_AVT", "C_STE",

            // Technical creation and modification dates
            "D_CRT", "D_CRT_CNT", "TSTAMP_DMOD", "D_MOD", "D_JOB_DMOD", "D_GEST_DMOD",

            // Authors and Processes
            "NM_AUTEUR_CRT", "NM_AUTEUR_DMOD", "NM_AUTEUR", "NM_JOB_DMOD",
            "C_ID_GEST_DMOD", "C_ID_GEST", "TY_DMOD",

            // Fillers and empty fields
            "T_FILLER_11", "T_FILLER_20", "T_FILLER_30", "T_FILLER_31",
            "T_FILLER_36", "T_FILLER_84", "T_FILLER_85",

            // Identifiers
            "IT5HDMDAIDN", "IT5UCONAIDN", "IT5UASUAIDN", "IT5UPNRAIDN",
            "IT5UBENAIDN", "IT5UIVNAIDN",
            "IT5HDMDNREFEXNDOS", "IT5UPNRLCAEIDN", "IT5UCONLREFEXN",

            // DB2 Timestamps
            "IT5HELTDTISMAJDB2", "IT5UCONDTISMAJDB2", "IT5UGARDTISMAJDB2",
            "IT5UAVEDTISMAJDB2", "IT5UPRPDTISMAJDB2", "IT5UPRSDTISMAJDB2",
            "IT5UPMPDTISMAJDB2", "IT5HDMDDTISMAJDB2", "IT5HPRODTISMAJDB2",
            "IT5UIVNDTISMAJDB2", "IT5URPPDTISMAJDB2", "IT5UPRFDTISMAJDB2",
            "IT5UFMLDTISMAJDB2", "IT5UCRBDTISMAJDB2", "IT5UDCRDTISMAJDB2",
            "IT5UBENDTISMAJDB2",

            // Dates
            "IT5UCONDDEB", "IT5UGARDDEB", "IT5UAVEDDEB", "IT5UPMPDDEB", "IT5HPRODDEB",
            "IT5UIVNDDEB", "IT5UIVNDFIN",
            "IT5UCONDMAXEXSTRM", "IT5UCONDMAXSPPVSE", "IT5UGARDDEBTAR",
            "IT5UGARDFINTARGAR", "IT5UGARDFIN", "IT5UAVEDFIN", "IT5UAVEDCCL",
            "IT5UPRPDPMRPAI", "IT5HDMDDCRE", "IT5HDMDDDRNMOD",

            // Personal data / Other
            "IT5UPNRLNOM", "IT5UPNRLPRN", "IT5UPRSUPTT",

            "TSTAMP_CRT", "T_DON_LIE", "NO_REF_LOT"
        };

        // SPECIFIC EXCLUSIONS
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SpecificExclusions =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Contract Table (LISA)
            { "LV.PCONT0", new List<string> { "D_ECH", "D_EFF_CNT", "D_PDC_CNT", "D_PDC_GAR", "D_TRM_CNT", "D_TRM_GAR", "D_TRM_GAR_CLI" } },
            { "LV.ELIAT0", new List<string> { "D_PREV_CLT_EVN" } },
            { "LV.ELIHT0", new List<string> { "D_PREV_CLT_EVN" } },
            { "LV.SCNTT0", new List<string> { "NO_POLICE_PAPIER", "NO_BUR_INTRO", "NO_BUR_INT_GES", "D_DEB_CLI_TSFR_RES", "D_EFF_CNT", "D_ETAT_CNT", "D_ETAT_CNT_PIE", "D_EVOL_TRAIT", "D_LIM_CLI_TSFR_RES", "D_MAX_A_VE_LBR", "D_MAX_PROL_TRM", "D_PDC_AVT", "D_PDC_CNT", "D_RCP_POL_SIGN", "D_TRAIT_CNT", "D_TRM_CNT", "INSTA_MM_ANNIV_CNT", "M_CAP_VIE", "M_CAP_VIE_ORGN" } },
            { "LV.SWBGT0", new List<string> { "AGE_TRF_PDC", "D_ANT", "D_FIN_VALID_TRF", "D_PDC_GAR", "D_TRM_GAR", "D_TRM_GAR_CLI", "M_CAP_REF", "M_CAP_VIE" } },

            // Amendment / Endorsement Table (LISA)
            { "LV.SAVTT0", new List<string> { "NO_AVT_REF", "NO_AVT_CLS", "NO_AVT_T_LBR", "NO_AVT_ELT", "NO_AVT_PB", "NO_AVT_DCL", "D_EFF_AVT", "D_ETAT_AVT", "D_PDC_AVT", "D_PDC_CLI", "D_VE" } },

            // Beneficiaries / Clauses Table (LISA)
            { "LV.SCLST0", new List<string> { "NO_ORD_CLS" } },
            { "LV.SCLRT0", new List<string> { "NO_ORD_RNG", "NO_ORD_CLS" } },

            // Financial Tables (LISA)
            { "LV.PRIST0", new List<string> { "D_ECH" } },
            { "LV.PECHT0", new List<string> { "D_AF_QUITT", "D_ECH", "D_VALID_CPNT" } },
            { "LV.PMNTT0", new List<string> { "D_BISM_DCOR", "D_BISM_DVA", "D_ECH", "D_REF_PRM" } },
            { "LV.PRCTT0", new List<string> { "D_BISM_DCOR", "D_BISM_DVA", "D_REF_PRM" } },
            { "LV.BSPDT0", new List<string> { "NO_ORD_TRF_EPA", "NO_ORD_MVT_EPA", "NO_ORD_QUITT", "NO_ORD_MVT_ANNUL", "D_REF_MVT_EPA", "D_STA_IMPR", "C_STA_IMPR", "D_BISM_DCOR", "D_ECH", "D_ETAT_MVT_EPA", "M_CAP_VIE" } },
            { "LV.BSPGT0", new List<string> { "NO_ORD_TRF_EPA", "D_ETAT_TRF_EPA", "M_CAP_VIE" } },
            { "LV.BPBAT0", new List<string> { "D_DEB_DIST_PB", "D_FIN_DIST_PB" } },

            // ELIA Tables
            { "FJ1.TB5LGDR", new List<string> { "IT5LGDRDDEBVAL", "IT5LGDRDFINVAL", "IT5LGDRDTISCRE", "IT5LGDRDTISDRNMOD", "IT5LGDRDTISMAJDB2", "IT5LGDRNCON" } }
        };

        /// <summary>
        /// Normalizes a table name by stripping any "[TestId]" prefix if present.
        /// </summary>
        public static string NormalizeTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName)) return string.Empty;

            string clean = tableName.Trim();
            if (clean.StartsWith("["))
            {
                int closeBracketIdx = clean.IndexOf(']');
                if (closeBracketIdx != -1 && closeBracketIdx + 1 < clean.Length)
                {
                    clean = clean.Substring(closeBracketIdx + 1).Trim();
                }
            }
            return clean;
        }

        /// <summary>
        /// Utility method to retrieve all columns to exclude for a given table (cached and case-insensitive).
        /// </summary>
        public static HashSet<string> GetExclusionsForTable(string tableName)
        {
            string cleanName = NormalizeTableName(tableName);

            return _cachedExclusions.GetOrAdd(cleanName, key =>
            {
                var exclusions = new HashSet<string>(IgnoreColumns, StringComparer.OrdinalIgnoreCase);

                if (SpecificExclusions.TryGetValue(key, out var specificCols))
                {
                    foreach (var col in specificCols)
                    {
                        exclusions.Add(col);
                    }
                }

                return exclusions;
            });
        }
    }
}