using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoActivator.Config
{
    /// <summary>
    /// Centralise la configuration globale de l'application AutoActivator :
    /// - répertoires et fichiers (Settings.cs) ;
    /// - routage ELIA et autocomplétion SQL (Settings.cs) ;
    /// - traitements Branche 23 et chaîne LVDQ (Settings.Branch23.cs) ;
    /// - connexions et bases SQL Server (Settings.Database.cs).
    /// </summary>
    public static partial class Settings
    {
        // =============================================================
        // RÉPERTOIRES ET FICHIERS
        // =============================================================

        public static readonly string BaseDir =
            AppDomain.CurrentDomain.BaseDirectory;

        public static readonly string DataDir =
            Path.Combine(BaseDir, "data");

        public static readonly string InputDir =
            Path.Combine(DataDir, "input");

        public static readonly string OutputDir =
            Path.Combine(DataDir, "output");

        public static readonly string BaselineDir =
            Path.Combine(DataDir, "baseline");

        public static readonly string SnapshotDir =
            Path.Combine(DataDir, "snapshot");

        public static readonly string LogDir =
            Path.Combine(DataDir, "logs");

        public static readonly string JclNetworkFolder =
            @"\\Jafile02\elia\11 - Technical Architecture\11 - IS Tooling\01 - Tools\LVCHAIN\JCL";

        public static readonly string SourceFile =
            Path.Combine(InputDir, "contrats_sources.xlsx");

        public static readonly string ActivationOutputFile =
            Path.Combine(InputDir, "contrats_en_attente_activation.xlsx");

        public static readonly string InputFile =
            ActivationOutputFile;

        static Settings()
        {
            ActivationAutomation.ValidateRoutingMatrix();
            EnsureDirectoriesExist();
        }

        public static void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(InputDir);
            Directory.CreateDirectory(OutputDir);
            Directory.CreateDirectory(BaselineDir);
            Directory.CreateDirectory(SnapshotDir);
            Directory.CreateDirectory(LogDir);
        }

        public static bool IsJclNetworkFolderAvailable()
        {
            try
            {
                return Directory.Exists(JclNetworkFolder);
            }
            catch
            {
                return false;
            }
        }

        public static string NormalizeEnvironmentSuffix(string? envSuffix)
        {
            if (string.IsNullOrWhiteSpace(envSuffix))
            {
                return "Q000";
            }

            return DbConfig.NormalizeEnvironment(envSuffix);
        }

        public static string NormalizeShortEnvironment(string? envSuffix)
        {
            string normalized = NormalizeEnvironmentSuffix(envSuffix);

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

        public static string GetDefaultTestingExcelPath(
            string? channel,
            string? env)
        {
            string safeChannel = string.IsNullOrWhiteSpace(channel)
                ? "C01"
                : channel.Trim().ToUpperInvariant();

            string normalizedEnvironment = NormalizeEnvironmentSuffix(env);

            return
                $@"\\jafile01\Automated_Testing\IS_QCRUNS\00_GENERICS\KEY_{safeChannel}ComparisonsDB_URL_ELIA_LoginPage_{normalizedEnvironment}.xls";
        }

        // =============================================================
        // ROUTING PRÉACTIVATION ELIA
        // =============================================================

        public static class PreActivationRouting
        {
            public static readonly IReadOnlyList<string> SupportedEnvironments =
                Array.AsReadOnly(new[] { "Q000", "D000" });

            public static readonly IReadOnlySet<string> SupportedActivationProcesses =
                new HashSet<string>(
                    new[] { "C01", "C02", "C03", "C05" },
                    StringComparer.OrdinalIgnoreCase);

            public static readonly IReadOnlySet<string> KnownBranch23Products =
                new HashSet<string>(
                    new[]
                    {
                        "9500", "9502", "9503", "9506",
                        "D018", "D022", "O018", "F018",
                        "B018", "EB18"
                    },
                    StringComparer.OrdinalIgnoreCase);

            public static bool IsKnownBranch23Product(string? productCode)
            {
                return !string.IsNullOrWhiteSpace(productCode) &&
                       KnownBranch23Products.Contains(productCode.Trim());
            }

            public static bool IsSupportedActivationProcess(string? processCode)
            {
                return !string.IsNullOrWhiteSpace(processCode) &&
                       SupportedActivationProcesses.Contains(processCode.Trim());
            }
        }

        // =============================================================
        // ACTIVATION AUTOMATIQUE SQL & PRESETS BANK / BROKER
        // =============================================================

        public static class ActivationAutomation
        {
            public const string DefaultInputMode = "SqlAutocomplete";
            public const int SqlCommandTimeoutSeconds = 120;
            public const int BatchSqlConcurrency = 4;
            public const int MaximumBatchSqlConcurrency = 16;

            // Bank standard (ELIA ChannelCode -> 00382 / 8 ; P48LV -> 00182 / 8)
            public const string BankBucp = "00382";
            public const string BankLegacyBucp = "00182";
            public const string BankCmdpmt = "8";

            // Broker New Gen / 2P (ELIA ChannelCode -> 80819 / H)
            public const string BrokerNewGenerationBucp = "80819";
            public const string BrokerNewGenerationCmdpmt = "H";

            // Broker Classique AGF+ / AG Target+ (49797 / X ou H)
            public const string BrokerLegacyBucp = "49797";
            public const string BrokerLegacyCmdpmtX = "X";
            public const string BrokerLegacyCmdpmtH = "H";

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

            public static bool IsSupportedManualPair(string? bucp, string? cmdpmt)
            {
                string normalizedBucp = (bucp ?? string.Empty).Trim().PadLeft(5, '0');
                string normalizedCmd = (cmdpmt ?? string.Empty).Trim().ToUpperInvariant();

                return (normalizedBucp == BankBucp && normalizedCmd == BankCmdpmt) ||
                       (normalizedBucp == BankLegacyBucp && normalizedCmd == BankCmdpmt) ||
                       (normalizedBucp == BrokerNewGenerationBucp && normalizedCmd == BrokerNewGenerationCmdpmt) ||
                       (normalizedBucp == BrokerLegacyBucp &&
                        (normalizedCmd == BrokerLegacyCmdpmtX || normalizedCmd == BrokerLegacyCmdpmtH));
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
    }
}
