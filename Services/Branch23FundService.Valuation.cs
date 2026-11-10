using AutoActivator.Config;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    public sealed partial class Branch23FundService
    {
        private const string FundHistorySql = @"
            SELECT
                (
                    SELECT COUNT_BIG(1)
                    FROM [LV].[GFEVT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                ) AS ClosedRows,
                (
                    SELECT COUNT_BIG(1)
                    FROM [LV].[GFCRT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                ) AS OpenRows;";

        private const string ClosedNavExistsSql = @"
            SELECT COUNT_BIG(1)
            FROM [LV].[GFEVT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_CALC_EVOL_FONDS = @navDate;";

        private const string OpenNavExistsSql = @"
            SELECT COUNT_BIG(1)
            FROM [LV].[GFCRT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_COURS = @navDate;";

        private const string LatestClosedNavDateSql = @"
            SELECT COALESCE(
                (
                    SELECT MAX(D_CALC_EVOL_FONDS)
                    FROM [LV].[GFEVT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                      AND D_CALC_EVOL_FONDS < @navDate
                ),
                (
                    SELECT MIN(D_CALC_EVOL_FONDS)
                    FROM [LV].[GFEVT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                )
            );";

        private const string LatestOpenNavDateSql = @"
            SELECT COALESCE(
                (
                    SELECT MAX(D_COURS)
                    FROM [LV].[GFCRT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                      AND D_COURS < @navDate
                ),
                (
                    SELECT MIN(D_COURS)
                    FROM [LV].[GFCRT0] WITH (NOLOCK)
                    WHERE C_FONDS = @fund
                )
            );";

        private const string InsertClosedNavSql = @"
            DECLARE @InsertedRows int = 0;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [LV].[GFEVT0] WITH (UPDLOCK, HOLDLOCK)
                WHERE C_FONDS = @fund
                  AND D_CALC_EVOL_FONDS = @navDate
            )
            BEGIN
                INSERT INTO [LV].[GFEVT0]
                SELECT TOP (1)
                       @fund,
                       @navDate,
                       source.M_UNT + @increment,
                       source.Q_UNT,
                       source.Q_UNT_LQD,
                       source.PC_ACCR_BEL20,
                       source.TX_ACCR_BUT,
                       source.TX_BUT,
                       source.C_FILLER_0,
                       source.M_FILLER_0,
                       source.D_FILLER_0,
                       source.C_DEV,
                       source.T_FILLER,
                       source.PC_FILLER_0,
                       source.TSTAMP_DMOD,
                       source.NM_AUTEUR_CRT,
                       source.D_CRT,
                       source.TY_DMOD,
                       source.C_ID_GEST_DMOD,
                       source.D_GEST_DMOD,
                       source.NM_JOB_DMOD,
                       source.D_JOB_DMOD
                FROM [LV].[GFEVT0] AS source
                WHERE source.C_FONDS = @fund
                ORDER BY
                    CASE WHEN source.D_CALC_EVOL_FONDS < @navDate THEN 0 ELSE 1 END,
                    CASE WHEN source.D_CALC_EVOL_FONDS < @navDate THEN source.D_CALC_EVOL_FONDS END DESC,
                    source.D_CALC_EVOL_FONDS ASC;

                SET @InsertedRows = @@ROWCOUNT;
            END;

            SELECT @InsertedRows;";

        private const string InsertOpenNavSql = @"
            DECLARE @InsertedRows int = 0;

            IF NOT EXISTS
            (
                SELECT 1
                FROM [LV].[GFCRT0] WITH (UPDLOCK, HOLDLOCK)
                WHERE C_FONDS = @fund
                  AND D_COURS = @navDate
            )
            BEGIN
                INSERT INTO [LV].[GFCRT0]
                SELECT TOP (1)
                       @fund,
                       @navDate,
                       source.C_VALID_COURS,
                       source.M_UNT_BANQ + @increment,
                       source.D_INTRO_COURS,
                       source.C_DEV,
                       source.M_MIN_UNT_BANQ,
                       source.C_FILLER_0,
                       source.M_FILLER_0,
                       source.D_FILLER_0,
                       source.PC_FILLER_0,
                       source.TSTAMP_DMOD,
                       source.NM_AUTEUR_CRT,
                       source.D_CRT,
                       source.TY_DMOD,
                       source.C_ID_GEST_DMOD,
                       source.D_GEST_DMOD,
                       source.NM_JOB_DMOD,
                       source.D_JOB_DMOD
                FROM [LV].[GFCRT0] AS source
                WHERE source.C_FONDS = @fund
                ORDER BY
                    CASE WHEN source.D_COURS < @navDate THEN 0 ELSE 1 END,
                    CASE WHEN source.D_COURS < @navDate THEN source.D_COURS END DESC,
                    source.D_COURS ASC;

                SET @InsertedRows = @@ROWCOUNT;
            END;

            SELECT @InsertedRows;";

        /// <summary>
        /// Ouvre une connexion SQL vers LV000GD0_&lt;ENV&gt; (base officielle de LV.GFCRT0 / LV.GFEVT0),
        /// avec repli automatique vers FJ0AGDB_&lt;ENV&gt; si des synonymes y sont exposés.
        /// </summary>
        private async Task<SqlConnection> OpenValuationConnectionAsync(CancellationToken token)
        {
            var primary = new SqlConnection(_primaryValuationConnectionString);
            try
            {
                await primary.OpenAsync(token).ConfigureAwait(false);
                return primary;
            }
            catch (OperationCanceledException)
            {
                primary.Dispose();
                throw;
            }
            catch
            {
                primary.Dispose();
                var fallback = new SqlConnection(_fallbackApplicationConnectionString);
                await fallback.OpenAsync(token).ConfigureAwait(false);
                return fallback;
            }
        }

        /// <summary>
        /// Recherche les fonds associés à un contrat dans LISA (LV000FD0.LV.FMVDT0 / LV.FPLCT0)
        /// après exécution de LVDQFG03 ou lors d'une reprise sur contrat déjà créé en LISA.
        /// </summary>
        public async Task<IReadOnlyList<string>> DiscoverContractFundsFromLisaAsync(
            string lisaNoCnt,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(lisaNoCnt) ||
                !decimal.TryParse(lisaNoCnt.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal noCnt))
            {
                return Array.Empty<string>();
            }

            var discovered = new List<string>();
            string movementDb = Settings.DbConfig.GetFundMovementDatabase(_environment);
            string connString = Settings.DbConfig.GetConnectionString(_environment, movementDb);

            const string sql = @"
                SELECT DISTINCT LTRIM(RTRIM(C_FONDS)) AS FundCode
                FROM [LV].[FMVDT0] WITH (NOLOCK)
                WHERE C_STE = 'A'
                  AND NO_CNT = @NoCnt
                  AND C_FONDS IS NOT NULL
                  AND LTRIM(RTRIM(C_FONDS)) <> '';";

            try
            {
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(token).ConfigureAwait(false);
                using var cmd = new SqlCommand(sql, conn)
                {
                    CommandTimeout = SqlCommandTimeoutSeconds
                };
                cmd.Parameters.AddWithValue("@NoCnt", noCnt);
                using SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        string code = reader.GetString(0).Trim().ToUpperInvariant();
                        if (code.Length is > 0 and <= 6)
                        {
                            discovered.Add(code);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Non bloquant : utilisé uniquement en complément de la résolution ELIA.
            }

            return discovered
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task ProcessFundAsync(
            Branch23NavResult result,
            CancellationToken token)
        {
            await ClassifyAsync(result, token).ConfigureAwait(false);

            if (!result.Kind.HasValue)
            {
                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    result.Error = "The fund type could not be determined.";
                }

                return;
            }

            bool isClosed = result.Kind.Value == Branch23FundKind.Closed;
            string existsSql = isClosed ? ClosedNavExistsSql : OpenNavExistsSql;

            bool exists = await NavExistsAsync(
                    existsSql,
                    result.FundCode,
                    result.NavDate,
                    token)
                .ConfigureAwait(false);

            if (exists)
            {
                result.AlreadyExisted = true;
                return;
            }

            decimal increment = isClosed
                ? Settings.Branch23.ClosedFundUnitIncrement ?? DefaultClosedFundUnitIncrement
                : Settings.Branch23.OpenFundNavIncrement ?? DefaultOpenFundNavIncrement;

            result.AppliedIncrement = increment;

            result.SourceNavDate = await GetLatestSourceNavDateAsync(
                    isClosed,
                    result.FundCode,
                    result.NavDate,
                    token)
                .ConfigureAwait(false);

            if (!result.SourceNavDate.HasValue)
            {
                result.Error =
                    $"No reference NAV was found for fund {result.FundCode} in " +
                    $"{ValuationDatabaseName}.{(isClosed ? "LV.GFEVT0" : "LV.GFCRT0")}. " +
                    "The NAV cannot be copied automatically.";
                return;
            }

            int insertedRows = await InsertNavAsync(
                    isClosed ? InsertClosedNavSql : InsertOpenNavSql,
                    result.FundCode,
                    result.NavDate,
                    increment,
                    token)
                .ConfigureAwait(false);

            result.InsertedRows = insertedRows;

            if (insertedRows > 0)
            {
                result.Created = true;
                return;
            }

            bool existsAfterInsert = await NavExistsAsync(
                    existsSql,
                    result.FundCode,
                    result.NavDate,
                    token)
                .ConfigureAwait(false);

            if (existsAfterInsert)
            {
                result.CreatedConcurrently = true;
                return;
            }

            result.Error =
                $"No NAV row was inserted for fund {result.FundCode} in " +
                $"{ValuationDatabaseName}.{(isClosed ? "LV.GFEVT0" : "LV.GFCRT0")}.";
        }

        private async Task ClassifyAsync(
            Branch23NavResult result,
            CancellationToken token)
        {
            if (Settings.Branch23.ClosedFunds.Contains(
                    result.FundCode,
                    StringComparer.OrdinalIgnoreCase))
            {
                result.Kind = Branch23FundKind.Closed;
                result.KindSource = "Settings.Branch23.ClosedFunds";
                return;
            }

            using SqlConnection connection =
                await OpenValuationConnectionAsync(token).ConfigureAwait(false);

            using var command = new SqlCommand(FundHistorySql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, result.FundCode);

            using SqlDataReader reader =
                await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            long closedRows = 0;
            long openRows = 0;

            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                closedRows = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                openRows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }

            if (closedRows > 0 && openRows == 0)
            {
                result.Kind = Branch23FundKind.Closed;
                result.KindSource = $"{ValuationDatabaseName}.LV.GFEVT0";
                return;
            }

            if (openRows > 0 && closedRows == 0)
            {
                result.Kind = Branch23FundKind.Open;
                result.KindSource = $"{ValuationDatabaseName}.LV.GFCRT0";
                return;
            }

            if (openRows > 0 && closedRows > 0)
            {
                // Si présent dans les deux et non référencé comme fermé, privilégier GFCRT0 (fonds ouvert).
                result.Kind = Branch23FundKind.Open;
                result.KindSource = $"{ValuationDatabaseName}.LV.GFCRT0 (open priority)";
                return;
            }

            result.Error =
                $"Fund {result.FundCode} was not found in {ValuationDatabaseName}.LV.GFEVT0 or LV.GFCRT0.";
        }

        private async Task<bool> NavExistsAsync(
            string sql,
            string fund,
            DateTime navDate,
            CancellationToken token)
        {
            using SqlConnection connection =
                await OpenValuationConnectionAsync(token).ConfigureAwait(false);

            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, fund);
            AddDateParameter(command, navDate);

            object? scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

            return scalar != null &&
                   scalar != DBNull.Value &&
                   Convert.ToInt64(scalar, CultureInfo.InvariantCulture) > 0;
        }

        private async Task<DateTime?> GetLatestSourceNavDateAsync(
            bool isClosed,
            string fund,
            DateTime navDate,
            CancellationToken token)
        {
            string sql = isClosed ? LatestClosedNavDateSql : LatestOpenNavDateSql;

            using SqlConnection connection =
                await OpenValuationConnectionAsync(token).ConfigureAwait(false);

            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, fund);
            AddDateParameter(command, navDate);

            object? scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

            if (scalar == null || scalar == DBNull.Value)
            {
                return null;
            }

            return Convert.ToDateTime(scalar, CultureInfo.InvariantCulture).Date;
        }

        private async Task<int> InsertNavAsync(
            string sql,
            string fund,
            DateTime navDate,
            decimal increment,
            CancellationToken token)
        {
            using SqlConnection connection =
                await OpenValuationConnectionAsync(token).ConfigureAwait(false);

            using SqlTransaction transaction =
                connection.BeginTransaction(IsolationLevel.Serializable);

            using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, fund);
            AddDateParameter(command, navDate);

            SqlParameter incrementParameter =
                command.Parameters.Add("@increment", SqlDbType.Decimal);
            incrementParameter.Precision = 18;
            incrementParameter.Scale = 6;
            incrementParameter.Value = increment;

            try
            {
                object? scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                int insertedRows = scalar == null || scalar == DBNull.Value
                    ? 0
                    : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);

                transaction.Commit();
                return insertedRows;
            }
            catch (OperationCanceledException)
            {
                SafeRollback(transaction);
                throw;
            }
            catch
            {
                SafeRollback(transaction);
                throw;
            }
        }

        private static void AddFundParameter(SqlCommand command, string fund)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (string.IsNullOrWhiteSpace(fund))
            {
                throw new ArgumentException("The fund code is required.", nameof(fund));
            }

            string normalizedFund = fund.Trim().ToUpperInvariant();
            if (normalizedFund.Length > 6)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fund),
                    normalizedFund,
                    "The fund code cannot exceed 6 characters.");
            }

            SqlParameter parameter =
                command.Parameters.Add("@fund", SqlDbType.VarChar, 6);
            parameter.Value = normalizedFund;
        }

        private static void AddDateParameter(SqlCommand command, DateTime navDate)
        {
            ArgumentNullException.ThrowIfNull(command);

            SqlParameter parameter =
                command.Parameters.Add("@navDate", SqlDbType.Date);
            parameter.Value = navDate.Date;
        }

        private static IReadOnlyList<string> NormalizeFunds(IEnumerable<string>? fundCodes)
        {
            List<string> normalizedFunds = (fundCodes ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToUpperInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? invalidFund = normalizedFunds.FirstOrDefault(value => value.Length > 6);
            if (invalidFund != null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fundCodes),
                    invalidFund,
                    "A BR23 fund code cannot exceed 6 characters.");
            }

            return normalizedFunds;
        }

        private static string BuildErrorMessage(Exception exception)
        {
            if (exception is SqlException sqlException)
            {
                return $"SQL error {sqlException.Number}: {sqlException.Message}";
            }

            return $"{exception.GetType().Name}: {exception.Message}";
        }

        private static void SafeRollback(SqlTransaction transaction)
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
            }
        }
    }
}
