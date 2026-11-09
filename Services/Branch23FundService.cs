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
    /// <summary>
    /// Type de fonds Branche 23 déterminé à partir des tables de cotation.
    /// </summary>
    public enum Branch23FundKind
    {
        /// <summary>
        /// Fonds fermé dont le prix mensuel est conservé dans LV.GFEVT0.
        /// La date de cotation correspond à D_CALC_EVOL_FONDS.
        /// </summary>
        Closed = 0,

        /// <summary>
        /// Fonds ouvert dont la VNI est conservée dans LV.GFCRT0.
        /// La date de cotation correspond à D_COURS.
        /// </summary>
        Open = 1
    }

    /// <summary>
    /// Résultat du contrôle ou de la préparation d'une VNI.
    /// </summary>
    public sealed class Branch23NavResult
    {
        /// <summary>
        /// Code du fonds contrôlé.
        /// </summary>
        public string FundCode { get; set; } = string.Empty;

        /// <summary>
        /// Type du fonds lorsque celui-ci a pu être déterminé.
        /// </summary>
        public Branch23FundKind? Kind { get; set; }

        /// <summary>
        /// Source utilisée pour classifier le fonds.
        /// </summary>
        public string KindSource { get; set; } = string.Empty;

        /// <summary>
        /// Date de VNI attendue.
        /// </summary>
        public DateTime NavDate { get; set; }

        /// <summary>
        /// Indique qu'une VNI existait déjà à la date demandée.
        /// </summary>
        public bool AlreadyExisted { get; set; }

        /// <summary>
        /// Indique qu'une VNI a été créée par AutoActivator.
        /// </summary>
        public bool Created { get; set; }

        /// <summary>
        /// Indique qu'une autre exécution concurrente a créé la VNI
        /// entre le contrôle initial et l'insertion.
        /// </summary>
        public bool CreatedConcurrently { get; set; }

        /// <summary>
        /// Nombre de lignes insérées.
        /// </summary>
        public int InsertedRows { get; set; }

        /// <summary>
        /// Dernière date de VNI trouvée avant la date demandée.
        /// </summary>
        public DateTime? SourceNavDate { get; set; }

        /// <summary>
        /// Valeur d'incrément utilisée pour créer la nouvelle VNI.
        /// </summary>
        public decimal? AppliedIncrement { get; set; }

        /// <summary>
        /// Description de l'erreur lorsque la VNI n'est pas prête.
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// Indique que le fonds est prêt avant le démarrage de LVDQFG03.
        /// </summary>
        public bool IsReady =>
            AlreadyExisted ||
            Created ||
            CreatedConcurrently;

        public override string ToString()
        {
            string kind =
                Kind.HasValue
                    ? Kind.Value.ToString()
                    : "Unknown";

            string sourceDate =
                SourceNavDate.HasValue
                    ? SourceNavDate.Value.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)
                    : "-";

            if (AlreadyExisted)
            {
                return
                    $"{FundCode} ({kind}, {KindSource}): " +
                    $"NAV already present on " +
                    $"{NavDate:yyyy-MM-dd}";
            }

            if (CreatedConcurrently)
            {
                return
                    $"{FundCode} ({kind}, {KindSource}): " +
                    $"NAV created concurrently on " +
                    $"{NavDate:yyyy-MM-dd}";
            }

            if (Created)
            {
                return
                    $"{FundCode} ({kind}, {KindSource}): " +
                    $"NAV created on {NavDate:yyyy-MM-dd} " +
                    $"from {sourceDate}, increment=" +
                    $"{AppliedIncrement?.ToString(
                        CultureInfo.InvariantCulture) ?? "-"}";
            }

            return
                $"{FundCode} ({kind}, {KindSource}): " +
                $"NOT READY - " +
                $"{(string.IsNullOrWhiteSpace(Error)
                    ? "Unknown error."
                    : Error)}";
        }
    }

    /// <summary>
    /// Source des fonds associés à un contrat Branche 23.
    /// </summary>
    public interface IBranch23FundSource
    {
        /// <summary>
        /// Retourne les fonds associés au contrat.
        /// </summary>
        Task<IReadOnlyList<string>> GetContractFundsAsync(
            string jclContract,
            string envSuffix,
            CancellationToken token);
    }

    /// <summary>
    /// Source de fonds explicitement configurée depuis l'interface
    /// ou le fichier de batch.
    ///
    /// Cette source représente les fonds métier explicitement confirmés
    /// dans l'interface ou le fichier batch. Elle est prioritaire pour une
    /// nouvelle activation BR23, car le contrat ne possède pas encore de
    /// NO_CNT LISA et aucun mouvement FMVDT0 fiable n'existe à ce stade.
    /// </summary>
    public sealed class ConfiguredBranch23FundSource
        : IBranch23FundSource
    {
        private readonly IReadOnlyList<string> _funds;

        public ConfiguredBranch23FundSource(
            IEnumerable<string>? funds)
        {
            _funds = NormalizeFunds(funds);
        }

        /// <summary>
        /// Parse une liste textuelle de fonds.
        ///
        /// Séparateurs acceptés :
        /// virgule, point-virgule, pipe, espace, tabulation et retour
        /// à la ligne.
        /// </summary>
        public static IReadOnlyList<string> Parse(
            string? rawFunds)
        {
            if (string.IsNullOrWhiteSpace(rawFunds))
            {
                return Array.Empty<string>();
            }

            string[] values =
                rawFunds.Split(
                    new[]
                    {
                        ',',
                        ';',
                        '|',
                        ' ',
                        '\t',
                        '\r',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries);

            return NormalizeFunds(values);
        }

        public Task<IReadOnlyList<string>>
            GetContractFundsAsync(
                string jclContract,
                string envSuffix,
                CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            return Task.FromResult(_funds);
        }

        private static IReadOnlyList<string> NormalizeFunds(
            IEnumerable<string>? funds)
        {
            return (funds ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToUpperInvariant())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Contrôle et prépare les VNI des fonds appartenant à un contrat
    /// Branche 23 avant l'exécution de LVDQFG03.
    ///
    /// Ce service ne doit jamais être appelé pour un contrat standard.
    ///
    /// Règles :
    /// - fonds fermé : cotation dans LV.GFEVT0 ;
    /// - fonds ouvert : cotation dans LV.GFCRT0 ;
    /// - la VNI existante n'est jamais modifiée ;
    /// - fonds fermé : incrément par défaut de 300 sur M_UNT ;
    /// - fonds ouvert : copie M_UNT_BANQ sans incrément par défaut ;
    /// - les valeurs configurées dans Settings.Branch23 remplacent ces
    ///   valeurs par défaut lorsqu'elles sont présentes ;
    /// - l'insertion copie la dernière cotation strictement antérieure
    ///   à la date demandée, même si des cotations futures existent ;
    /// - l'insertion est protégée contre les créations concurrentes.
    /// </summary>
    public sealed class Branch23FundService
    {
        private const int SqlCommandTimeoutSeconds = 60;
        private const decimal DefaultClosedFundUnitIncrement = 300m;
        private const decimal DefaultOpenFundNavIncrement = 0m;

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
            SELECT MAX(D_CALC_EVOL_FONDS)
            FROM [LV].[GFEVT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_CALC_EVOL_FONDS < @navDate;";

        private const string LatestOpenNavDateSql = @"
            SELECT MAX(D_COURS)
            FROM [LV].[GFCRT0] WITH (NOLOCK)
            WHERE C_FONDS = @fund
              AND D_COURS < @navDate;";

        /*
         * La structure des INSERT SELECT est volontairement conservée
         * conformément aux colonnes actuellement utilisées.
         *
         * Une migration future devrait idéalement utiliser une liste
         * explicite des colonnes INSERT dès que le schéma complet et
         * stable des tables est confirmé.
         */
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
                  AND source.D_CALC_EVOL_FONDS < @navDate
                ORDER BY source.D_CALC_EVOL_FONDS DESC;

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
                  AND source.D_COURS < @navDate
                ORDER BY source.D_COURS DESC;

                SET @InsertedRows = @@ROWCOUNT;
            END;

            SELECT @InsertedRows;";

        private readonly string _connectionString;

        public Branch23FundService(
            string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(envSuffix))
            {
                throw new ArgumentException(
                    "L'environnement est obligatoire.",
                    nameof(envSuffix));
            }

            string normalizedEnvironment =
                Settings.NormalizeEnvironmentSuffix(envSuffix);

            _connectionString =
                Settings.DbConfig.GetConnectionString(
                    normalizedEnvironment);
        }

        /// <summary>
        /// Contrôle les VNI de tous les fonds à la date donnée.
        ///
        /// Les fonds dupliqués ou vides sont automatiquement supprimés
        /// avant le traitement.
        /// </summary>
        public async Task<List<Branch23NavResult>>
            EnsureNavsAsync(
                IEnumerable<string>? fundCodes,
                DateTime navDate,
                CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            IReadOnlyList<string> normalizedFunds =
                NormalizeFunds(fundCodes);

            var results =
                new List<Branch23NavResult>(
                    normalizedFunds.Count);

            DateTime normalizedNavDate =
                navDate.Date;

            foreach (string fund in normalizedFunds)
            {
                token.ThrowIfCancellationRequested();

                var result =
                    new Branch23NavResult
                    {
                        FundCode = fund,
                        NavDate = normalizedNavDate
                    };

                results.Add(result);

                try
                {
                    await ProcessFundAsync(
                            result,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result.Error =
                        BuildErrorMessage(exception);
                }
            }

            return results;
        }

        /// <summary>
        /// Prépare toutes les VNI comprises entre la date d'activation et la
        /// date de consolidation, bornes incluses.
        /// Cette méthode doit être appelée avant LVDQFG03.
        /// </summary>
        public async Task<List<Branch23NavResult>>
            EnsureNavWindowAsync(
                IEnumerable<string>? fundCodes,
                DateTime activationDate,
                DateTime consolidationDate,
                CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            DateTime firstDate = activationDate.Date;
            DateTime lastDate = consolidationDate.Date;

            if (lastDate < firstDate)
            {
                throw new ArgumentException(
                    "The consolidation date cannot be earlier than the " +
                    "activation date.",
                    nameof(consolidationDate));
            }

            int requiredDayCount =
                (lastDate - firstDate).Days + 1;

            var results = new List<Branch23NavResult>();

            for (int offset = 0;
                 offset < requiredDayCount;
                 offset++)
            {
                token.ThrowIfCancellationRequested();

                DateTime requiredDate =
                    firstDate.AddDays(offset);

                List<Branch23NavResult> dateResults =
                    await EnsureNavsAsync(
                            fundCodes,
                            requiredDate,
                            token)
                        .ConfigureAwait(false);

                results.AddRange(dateResults);
            }

            return results;
        }

        /// <summary>
        /// Compatibility overload. Prepares J, J+1 and J+2.
        /// Prefer the overload receiving the consolidation date.
        /// </summary>
        public Task<List<Branch23NavResult>>
            EnsureNavWindowAsync(
                IEnumerable<string>? fundCodes,
                DateTime activationDate,
                CancellationToken token)
        {
            return EnsureNavWindowAsync(
                fundCodes,
                activationDate,
                activationDate.Date.AddDays(2),
                token);
        }

        /// <summary>
        /// Traite un fonds unique.
        /// </summary>
        private async Task ProcessFundAsync(
            Branch23NavResult result,
            CancellationToken token)
        {
            await ClassifyAsync(
                    result,
                    token)
                .ConfigureAwait(false);

            if (!result.Kind.HasValue)
            {
                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    result.Error =
                        "The fund type could not be determined.";
                }

                return;
            }

            bool isClosed =
                result.Kind.Value ==
                Branch23FundKind.Closed;

            string existsSql =
                isClosed
                    ? ClosedNavExistsSql
                    : OpenNavExistsSql;

            bool exists =
                await NavExistsAsync(
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

            decimal increment =
                isClosed
                    ? Settings.Branch23.ClosedFundUnitIncrement
                      ?? DefaultClosedFundUnitIncrement
                    : Settings.Branch23.OpenFundNavIncrement
                      ?? DefaultOpenFundNavIncrement;

            result.AppliedIncrement = increment;

            result.SourceNavDate =
                await GetLatestSourceNavDateAsync(
                        isClosed,
                        result.FundCode,
                        result.NavDate,
                        token)
                    .ConfigureAwait(false);

            if (!result.SourceNavDate.HasValue)
            {
                result.Error =
                    $"No previous NAV was found for fund " +
                    $"{result.FundCode} in " +
                    $"{(isClosed
                        ? "LV.GFEVT0"
                        : "LV.GFCRT0")} before " +
                    $"{result.NavDate:yyyy-MM-dd}. " +
                    "The NAV cannot be copied automatically.";

                return;
            }

            int insertedRows =
                await InsertNavAsync(
                        isClosed
                            ? InsertClosedNavSql
                            : InsertOpenNavSql,
                        result.FundCode,
                        result.NavDate,
                        increment,
                        token)
                    .ConfigureAwait(false);

            result.InsertedRows =
                insertedRows;

            if (insertedRows > 0)
            {
                result.Created = true;
                return;
            }

            /*
             * Une insertion à zéro peut signifier :
             * - une autre exécution a inséré la ligne entre-temps ;
             * - aucune ligne source n'a été trouvée.
             *
             * On recontrôle donc l'existence avant de déclarer l'échec.
             */
            bool existsAfterInsert =
                await NavExistsAsync(
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
                $"No NAV row was inserted for fund " +
                $"{result.FundCode}. " +
                $"No suitable source row was available in " +
                $"{(isClosed
                    ? "LV.GFEVT0"
                    : "LV.GFCRT0")}.";
        }

        /// <summary>
        /// Classification :
        /// 1. référentiel Settings.Branch23.ClosedFunds ;
        /// 2. historique exclusivement dans GFEVT0 ;
        /// 3. historique exclusivement dans GFCRT0 ;
        /// 4. erreur si absent ou présent dans les deux tables.
        /// </summary>
        private async Task ClassifyAsync(
            Branch23NavResult result,
            CancellationToken token)
        {
            if (Settings.Branch23.ClosedFunds.Contains(
                    result.FundCode,
                    StringComparer.OrdinalIgnoreCase))
            {
                result.Kind =
                    Branch23FundKind.Closed;

                result.KindSource =
                    "Settings.Branch23.ClosedFunds";

                return;
            }

            using var connection =
                new SqlConnection(
                    _connectionString);

            using var command =
                new SqlCommand(
                    FundHistorySql,
                    connection)
                {
                    CommandTimeout =
                        SqlCommandTimeoutSeconds
                };

            AddFundParameter(
                command,
                result.FundCode);

            await connection.OpenAsync(token)
                .ConfigureAwait(false);

            using SqlDataReader reader =
                await command.ExecuteReaderAsync(token)
                    .ConfigureAwait(false);

            long closedRows = 0;
            long openRows = 0;

            if (await reader.ReadAsync(token)
                    .ConfigureAwait(false))
            {
                closedRows =
                    reader.IsDBNull(0)
                        ? 0
                        : reader.GetInt64(0);

                openRows =
                    reader.IsDBNull(1)
                        ? 0
                        : reader.GetInt64(1);
            }

            if (closedRows > 0 &&
                openRows == 0)
            {
                result.Kind =
                    Branch23FundKind.Closed;

                result.KindSource =
                    "Historical prices in LV.GFEVT0";

                return;
            }

            if (openRows > 0 &&
                closedRows == 0)
            {
                result.Kind =
                    Branch23FundKind.Open;

                result.KindSource =
                    "Historical prices in LV.GFCRT0";

                return;
            }

            if (closedRows == 0 &&
                openRows == 0)
            {
                result.Error =
                    $"Fund {result.FundCode} was not found in " +
                    "LV.GFEVT0 or LV.GFCRT0. " +
                    "The open/closed classification cannot be " +
                    "determined.";

                return;
            }

            result.Error =
                $"Fund {result.FundCode} exists in both " +
                $"LV.GFEVT0 ({closedRows} row(s)) and " +
                $"LV.GFCRT0 ({openRows} row(s)). " +
                "The open/closed classification is ambiguous.";
        }

        private async Task<bool> NavExistsAsync(
            string sql,
            string fund,
            DateTime navDate,
            CancellationToken token)
        {
            using var connection =
                new SqlConnection(
                    _connectionString);

            using var command =
                new SqlCommand(
                    sql,
                    connection)
                {
                    CommandTimeout =
                        SqlCommandTimeoutSeconds
                };

            AddFundParameter(
                command,
                fund);

            AddDateParameter(
                command,
                navDate);

            await connection.OpenAsync(token)
                .ConfigureAwait(false);

            object? scalar =
                await command.ExecuteScalarAsync(token)
                    .ConfigureAwait(false);

            return scalar != null &&
                   scalar != DBNull.Value &&
                   Convert.ToInt64(
                       scalar,
                       CultureInfo.InvariantCulture) > 0;
        }

        private async Task<DateTime?>
            GetLatestSourceNavDateAsync(
                bool isClosed,
                string fund,
                DateTime navDate,
                CancellationToken token)
        {
            string sql =
                isClosed
                    ? LatestClosedNavDateSql
                    : LatestOpenNavDateSql;

            using var connection =
                new SqlConnection(
                    _connectionString);

            using var command =
                new SqlCommand(
                    sql,
                    connection)
                {
                    CommandTimeout =
                        SqlCommandTimeoutSeconds
                };

            AddFundParameter(
                command,
                fund);

            AddDateParameter(
                command,
                navDate);

            await connection.OpenAsync(token)
                .ConfigureAwait(false);

            object? scalar =
                await command.ExecuteScalarAsync(token)
                    .ConfigureAwait(false);

            if (scalar == null ||
                scalar == DBNull.Value)
            {
                return null;
            }

            return Convert.ToDateTime(
                scalar,
                CultureInfo.InvariantCulture).Date;
        }

        private async Task<int> InsertNavAsync(
            string sql,
            string fund,
            DateTime navDate,
            decimal increment,
            CancellationToken token)
        {
            using var connection =
                new SqlConnection(
                    _connectionString);

            await connection.OpenAsync(token)
                .ConfigureAwait(false);

            using SqlTransaction transaction =
                connection.BeginTransaction(
                    IsolationLevel.Serializable);

            using var command =
                new SqlCommand(
                    sql,
                    connection,
                    transaction)
                {
                    CommandTimeout =
                        SqlCommandTimeoutSeconds
                };

            AddFundParameter(
                command,
                fund);

            AddDateParameter(
                command,
                navDate);

            SqlParameter incrementParameter =
                command.Parameters.Add(
                    "@increment",
                    SqlDbType.Decimal);

            incrementParameter.Precision = 18;
            incrementParameter.Scale = 6;
            incrementParameter.Value = increment;

            try
            {
                object? scalar =
                    await command.ExecuteScalarAsync(token)
                        .ConfigureAwait(false);

                int insertedRows =
                    scalar == null || scalar == DBNull.Value
                        ? 0
                        : Convert.ToInt32(
                            scalar,
                            CultureInfo.InvariantCulture);

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

        private static void AddFundParameter(
            SqlCommand command,
            string fund)
        {
            if (command == null)
            {
                throw new ArgumentNullException(
                    nameof(command));
            }

            if (string.IsNullOrWhiteSpace(fund))
            {
                throw new ArgumentException(
                    "The fund code is required.",
                    nameof(fund));
            }

            string normalizedFund =
                fund.Trim().ToUpperInvariant();

            if (normalizedFund.Length > 6)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fund),
                    normalizedFund,
                    "The fund code cannot exceed 6 characters.");
            }

            SqlParameter parameter =
                command.Parameters.Add(
                    "@fund",
                    SqlDbType.VarChar,
                    6);

            parameter.Value =
                normalizedFund;
        }

        private static void AddDateParameter(
            SqlCommand command,
            DateTime navDate)
        {
            if (command == null)
            {
                throw new ArgumentNullException(
                    nameof(command));
            }

            SqlParameter parameter =
                command.Parameters.Add(
                    "@navDate",
                    SqlDbType.Date);

            parameter.Value =
                navDate.Date;
        }

        private static IReadOnlyList<string> NormalizeFunds(
            IEnumerable<string>? fundCodes)
        {
            List<string> normalizedFunds =
                (fundCodes ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToUpperInvariant())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string? invalidFund =
                normalizedFunds.FirstOrDefault(value => value.Length > 6);

            if (invalidFund != null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fundCodes),
                    invalidFund,
                    "A BR23 fund code cannot exceed 6 characters.");
            }

            return normalizedFunds;
        }

        private static string BuildErrorMessage(
            Exception exception)
        {
            if (exception is SqlException sqlException)
            {
                return
                    $"SQL error {sqlException.Number}: " +
                    sqlException.Message;
            }

            return
                $"{exception.GetType().Name}: " +
                exception.Message;
        }

        private static void SafeRollback(
            SqlTransaction transaction)
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                /*
                 * Une erreur de rollback ne doit pas masquer
                 * l'exception SQL d'origine.
                 */
            }
        }
    }
}