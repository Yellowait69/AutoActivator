using System;

namespace AutoActivator.Config
{
    public static partial class Settings
    {
        public sealed record ActivationEnvironmentSettings(
            string Name,
            string ShortName,
            string Ims,
            string Db2,
            string EliaDatabase,
            string PaymentDatabase,
            string ContractDatabase,
            string FundMovementDatabase,
            string FundValuationDatabase,
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
                    DbConfig.GetFundValuationDatabase(environment),
                    "I10.DB.CA.FIB.FASTCTRL"),

                "D000" => new ActivationEnvironmentSettings(
                    "D000", "D", "IM7T", "Q2T",
                    DbConfig.GetLifeDatabase(environment),
                    DbConfig.GetLisaPaymentDatabase(environment),
                    DbConfig.GetLisaContractDatabase(environment),
                    DbConfig.GetFundMovementDatabase(environment),
                    DbConfig.GetFundValuationDatabase(environment),
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

        public static class DbConfig
        {
            public const string Driver = "SQL Server";
            public const string TrustedConnection = "yes";
            public const int MaximumPoolSize = 150;
            public const int ConnectionTimeoutSeconds = 30;

            public static string Uid { get; set; } = "XA3894";
            public static string Pwd { get; set; } = string.Empty;

            public static string GetConnectionString(string envSuffix)
            {
                string environment = NormalizeEnvironment(envSuffix);
                string server = GetServerName(environment);
                string database = GetApplicationDatabase(environment);

                if (TrustedConnection.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildIntegratedConnectionString(server, database);
                }

                ValidateSqlCredentials();
                return BuildSqlAuthenticationConnectionString(server, database, Uid, Pwd);
            }

            public static string GetConnectionString(
                string envSuffix,
                string databaseName)
            {
                string environment = NormalizeEnvironment(envSuffix);
                string server = GetServerName(environment);
                string database = ValidateSqlIdentifier(databaseName, nameof(databaseName));

                if (TrustedConnection.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildIntegratedConnectionString(server, database);
                }

                ValidateSqlCredentials();
                return BuildSqlAuthenticationConnectionString(server, database, Uid, Pwd);
            }

            public static string GetServerName(string envSuffix)
            {
                string environment = NormalizeEnvironment(envSuffix);

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

            public static string GetApplicationDatabase(string envSuffix) =>
                $"FJ0AGDB_{NormalizeEnvironment(envSuffix)}";

            public static string GetLifeDatabase(string envSuffix) =>
                $"FJ5LIFE_{NormalizeEnvironment(envSuffix)}";

            /// <summary>
            /// Base LISA des réceptions et primes (LV.PRCTT0, LV.PRIST0, LV.PECHT0).
            /// </summary>
            public static string GetLisaPaymentDatabase(string envSuffix) =>
                $"LV000PD0_{NormalizeEnvironment(envSuffix)}";

            /// <summary>
            /// Base LISA des contrats (LV.SCNTT0).
            /// </summary>
            public static string GetLisaContractDatabase(string envSuffix) =>
                $"LV000SD0_{NormalizeEnvironment(envSuffix)}";

            /// <summary>
            /// Base LISA des mouvements de fonds Branche 23 (LV.FMVDT0, LV.FPLCT0).
            /// </summary>
            public static string GetFundMovementDatabase(string envSuffix) =>
                $"LV000FD0_{NormalizeEnvironment(envSuffix)}";

            /// <summary>
            /// Base LISA des cours et VNI des fonds Branche 23 (LV.GFCRT0 pour fonds ouverts, LV.GFEVT0 pour fonds fermés).
            /// </summary>
            public static string GetFundValuationDatabase(string envSuffix) =>
                $"LV000GD0_{NormalizeEnvironment(envSuffix)}";

            public static string GetLifeTable(
                string envSuffix,
                string tableName,
                string schemaName = "FJ1") =>
                BuildQualifiedTableName(GetLifeDatabase(envSuffix), schemaName, tableName);

            public static string GetApplicationTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV") =>
                BuildQualifiedTableName(GetApplicationDatabase(envSuffix), schemaName, tableName);

            public static string GetLisaPaymentTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV") =>
                BuildQualifiedTableName(GetLisaPaymentDatabase(envSuffix), schemaName, tableName);

            public static string GetLisaContractTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV") =>
                BuildQualifiedTableName(GetLisaContractDatabase(envSuffix), schemaName, tableName);

            public static string GetFundMovementTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV") =>
                BuildQualifiedTableName(GetFundMovementDatabase(envSuffix), schemaName, tableName);

            public static string GetFundValuationTable(
                string envSuffix,
                string tableName,
                string schemaName = "LV") =>
                BuildQualifiedTableName(GetFundValuationDatabase(envSuffix), schemaName, tableName);

            private static string BuildQualifiedTableName(
                string database,
                string schemaName,
                string tableName)
            {
                string validatedDatabase = ValidateSqlIdentifier(database, nameof(database));
                string validatedSchema = ValidateSqlIdentifier(schemaName, nameof(schemaName));
                string validatedTable = ValidateSqlIdentifier(tableName, nameof(tableName));

                return $"[{validatedDatabase}].[{validatedSchema}].[{validatedTable}]";
            }

            public static string NormalizeEnvironment(string? envSuffix)
            {
                if (string.IsNullOrWhiteSpace(envSuffix))
                {
                    throw new ArgumentException(
                        "L'environnement SQL est obligatoire.",
                        nameof(envSuffix));
                }

                string normalizedValue = envSuffix.Trim().ToUpperInvariant();

                return normalizedValue switch
                {
                    "D" or "D000" => "D000",
                    "Q" or "Q000" => "Q000",
                    _ => throw new ArgumentException(
                        $"Environnement SQL invalide : '{envSuffix}'. " +
                        "Les valeurs autorisées sont D, D000, Q et Q000.",
                        nameof(envSuffix))
                };
            }

            public static bool IsSupportedEnvironment(string? envSuffix)
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

            private static string BuildSqlAuthenticationConnectionString(
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
                        "L'identifiant SQL est obligatoire lorsque TrustedConnection vaut 'no'.");
                }

                if (string.IsNullOrWhiteSpace(Pwd))
                {
                    throw new InvalidOperationException(
                        "Le mot de passe SQL est obligatoire lorsque TrustedConnection vaut 'no'.");
                }
            }

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

                string identifier = value.Trim();

                foreach (char character in identifier)
                {
                    bool valid = char.IsLetterOrDigit(character) || character == '_';
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
