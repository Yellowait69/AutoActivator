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

        private const string ExistingClosedNavRangeSql = @"
            SELECT DISTINCT CAST(D_CALC_EVOL_FONDS AS date) AS NavDate
            FROM [LV].[GFEVT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_CALC_EVOL_FONDS >= @firstDate
              AND D_CALC_EVOL_FONDS <= @lastDate;";

        private const string ExistingOpenNavRangeSql = @"
            SELECT DISTINCT CAST(D_COURS AS date) AS NavDate
            FROM [LV].[GFCRT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_COURS >= @firstDate
              AND D_COURS <= @lastDate;";

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
        /// Recherche les fonds associés à un contrat dans LISA (LV000FD0.LV.FMVDT0)
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
                using (token.Register(static s => TryCancelCommand((SqlCommand)s!), cmd))
                using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
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

        /// <summary>
        /// Traite l'ensemble de la fenêtre de dates pour un fonds donné de manière strictement séquentielle
        /// sur la connexion SQL fournie :
        /// 1. Classification unique du fonds (1 requête au lieu de N jours).
        /// 2. Lecture groupée des dates déjà cotées sur la fenêtre (1 requête au lieu de N jours).
        /// 3. Insertion séquentielle uniquement des dates réellement manquantes.
        /// </summary>
        private async Task<List<Branch23NavResult>> ProcessFundWindowSequentiallyAsync(
            SqlConnection connection,
            string fundCode,
            DateTime firstDate,
            DateTime lastDate,
            CancellationToken token)
        {
            int dayCount = (lastDate - firstDate).Days + 1;
            var results = new List<Branch23NavResult>(dayCount);

            (Branch23FundKind? kind, string kindSource, string classifyError) =
                await ClassifyFundOnceAsync(connection, fundCode, token).ConfigureAwait(false);

            if (!kind.HasValue)
            {
                for (int offset = 0; offset < dayCount; offset++)
                {
                    results.Add(new Branch23NavResult
                    {
                        FundCode = fundCode,
                        NavDate = firstDate.AddDays(offset),
                        KindSource = kindSource,
                        Error = string.IsNullOrWhiteSpace(classifyError)
                            ? "The fund type could not be determined."
                            : classifyError
                    });
                }
                return results;
            }

            bool isClosed = kind.Value == Branch23FundKind.Closed;
            HashSet<DateTime> existingDates;
            try
            {
                existingDates = await LoadExistingNavDatesOnConnectionAsync(
                        connection,
                        isClosed,
                        fundCode,
                        firstDate,
                        lastDate,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch (Exception ex)
            {
                string err = BuildErrorMessage(ex);
                for (int offset = 0; offset < dayCount; offset++)
                {
                    results.Add(new Branch23NavResult
                    {
                        FundCode = fundCode,
                        Kind = kind,
                        KindSource = kindSource,
                        NavDate = firstDate.AddDays(offset),
                        Error = err
                    });
                }
                return results;
            }

            decimal increment = isClosed
                ? Settings.Branch23.ClosedFundUnitIncrement ?? DefaultClosedFundUnitIncrement
                : Settings.Branch23.OpenFundNavIncrement ?? DefaultOpenFundNavIncrement;

            string existsSql = isClosed ? ClosedNavExistsSql : OpenNavExistsSql;
            string insertSql = isClosed ? InsertClosedNavSql : InsertOpenNavSql;

            for (int offset = 0; offset < dayCount; offset++)
            {
                token.ThrowIfCancellationRequested();

                DateTime navDate = firstDate.AddDays(offset);
                var item = new Branch23NavResult
                {
                    FundCode = fundCode,
                    Kind = kind,
                    KindSource = kindSource,
                    NavDate = navDate
                };
                results.Add(item);

                if (existingDates.Contains(navDate))
                {
                    item.AlreadyExisted = true;
                    continue;
                }

                try
                {
                    item.AppliedIncrement = increment;
                    item.SourceNavDate = await GetLatestSourceNavDateOnConnectionAsync(
                            connection,
                            isClosed,
                            fundCode,
                            navDate,
                            token)
                        .ConfigureAwait(false);

                    if (!item.SourceNavDate.HasValue)
                    {
                        item.Error =
                            $"No reference NAV was found for fund {fundCode} in " +
                            $"{ValuationDatabaseName}.{(isClosed ? "LV.GFEVT0" : "LV.GFCRT0")}. " +
                            "The NAV cannot be copied automatically.";
                        continue;
                    }

                    int insertedRows = await InsertNavOnConnectionAsync(
                            connection,
                            insertSql,
                            fundCode,
                            navDate,
                            increment,
                            token)
                        .ConfigureAwait(false);

                    item.InsertedRows = insertedRows;
                    if (insertedRows > 0)
                    {
                        item.Created = true;
                        existingDates.Add(navDate);
                        continue;
                    }

                    bool existsAfterInsert = await NavExistsOnConnectionAsync(
                            connection,
                            existsSql,
                            fundCode,
                            navDate,
                            token)
                        .ConfigureAwait(false);

                    if (existsAfterInsert)
                    {
                        item.CreatedConcurrently = true;
                        existingDates.Add(navDate);
                    }
                    else
                    {
                        item.Error =
                            $"No NAV row was inserted for fund {fundCode} in " +
                            $"{ValuationDatabaseName}.{(isClosed ? "LV.GFEVT0" : "LV.GFCRT0")}.";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                catch (Exception exception)
                {
                    item.Error = BuildErrorMessage(exception);
                }
            }

            return results;
        }

        private async Task<(Branch23FundKind? Kind, string KindSource, string Error)> ClassifyFundOnceAsync(
            SqlConnection connection,
            string fundCode,
            CancellationToken token)
        {
            if (Settings.Branch23.ClosedFunds.Contains(fundCode, StringComparer.OrdinalIgnoreCase))
            {
                return (Branch23FundKind.Closed, "Settings.Branch23.ClosedFunds", string.Empty);
            }

            try
            {
                using var command = new SqlCommand(FundHistorySql, connection)
                {
                    CommandTimeout = SqlCommandTimeoutSeconds
                };
                AddFundParameter(command, fundCode);

                long closedRows = 0;
                long openRows = 0;

                using (token.Register(static s => TryCancelCommand((SqlCommand)s!), command))
                using (SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        closedRows = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                        openRows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    }
                }

                if (closedRows > 0 && openRows == 0)
                {
                    return (Branch23FundKind.Closed, $"{ValuationDatabaseName}.LV.GFEVT0", string.Empty);
                }

                if (openRows > 0 && closedRows == 0)
                {
                    return (Branch23FundKind.Open, $"{ValuationDatabaseName}.LV.GFCRT0", string.Empty);
                }

                if (openRows > 0 && closedRows > 0)
                {
                    return (Branch23FundKind.Open, $"{ValuationDatabaseName}.LV.GFCRT0 (open priority)", string.Empty);
                }

                return (
                    null,
                    string.Empty,
                    $"Fund {fundCode} was not found in {ValuationDatabaseName}.LV.GFEVT0 or LV.GFCRT0.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            catch (Exception ex)
            {
                return (null, string.Empty, BuildErrorMessage(ex));
            }
        }

        private static async Task<HashSet<DateTime>> LoadExistingNavDatesOnConnectionAsync(
            SqlConnection connection,
            bool isClosed,
            string fund,
            DateTime firstDate,
            DateTime lastDate,
            CancellationToken token)
        {
            var dates = new HashSet<DateTime>();
            string sql = isClosed ? ExistingClosedNavRangeSql : ExistingOpenNavRangeSql;

            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };
            AddFundParameter(command, fund);
            command.Parameters.Add("@firstDate", SqlDbType.Date).Value = firstDate.Date;
            command.Parameters.Add("@lastDate", SqlDbType.Date).Value = lastDate.Date;

            using (token.Register(static s => TryCancelCommand((SqlCommand)s!), command))
            using (SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        dates.Add(Convert.ToDateTime(reader.GetValue(0), CultureInfo.InvariantCulture).Date);
                    }
                }
            }

            return dates;
        }

        private static async Task<bool> NavExistsOnConnectionAsync(
            SqlConnection connection,
            string sql,
            string fund,
            DateTime navDate,
            CancellationToken token)
        {
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, fund);
            AddDateParameter(command, navDate);

            object? scalar;
            using (token.Register(static s => TryCancelCommand((SqlCommand)s!), command))
            {
                scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            }

            return scalar != null &&
                   scalar != DBNull.Value &&
                   Convert.ToInt64(scalar, CultureInfo.InvariantCulture) > 0;
        }

        private static async Task<DateTime?> GetLatestSourceNavDateOnConnectionAsync(
            SqlConnection connection,
            bool isClosed,
            string fund,
            DateTime navDate,
            CancellationToken token)
        {
            string sql = isClosed ? LatestClosedNavDateSql : LatestOpenNavDateSql;

            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = SqlCommandTimeoutSeconds
            };

            AddFundParameter(command, fund);
            AddDateParameter(command, navDate);

            object? scalar;
            using (token.Register(static s => TryCancelCommand((SqlCommand)s!), command))
            {
                scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            }

            if (scalar == null || scalar == DBNull.Value)
            {
                return null;
            }

            return Convert.ToDateTime(scalar, CultureInfo.InvariantCulture).Date;
        }

        private static async Task<int> InsertNavOnConnectionAsync(
            SqlConnection connection,
            string sql,
            string fund,
            DateTime navDate,
            decimal increment,
            CancellationToken token)
        {
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
                object? scalar;
                using (token.Register(static s => TryCancelCommand((SqlCommand)s!), command))
                {
                    scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                }

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
            catch (Exception) when (token.IsCancellationRequested)
            {
                SafeRollback(transaction);
                throw new OperationCanceledException(token);
            }
            catch
            {
                SafeRollback(transaction);
                throw;
            }
        }

        private static void TryCancelCommand(SqlCommand command)
        {
            try
            {
                command.Cancel();
            }
            catch
            {
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
