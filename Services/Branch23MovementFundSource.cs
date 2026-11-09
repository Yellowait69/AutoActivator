using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoActivator.Config;
using AutoActivator.Sql;

namespace AutoActivator.Services
{
    /// <summary>
    /// Raised when Branch 23 data cannot be resolved safely.
    /// </summary>
    public sealed class Branch23FundResolutionException : Exception
    {
        public Branch23FundResolutionException(
            string failureStage,
            string message)
            : base(message)
        {
            FailureStage = NormalizeRequiredValue(
                failureStage,
                nameof(failureStage),
                "The BR23 failure stage is required.");
        }

        public string FailureStage { get; }

        private static string NormalizeRequiredValue(
            string value,
            string parameterName,
            string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    errorMessage,
                    parameterName);
            }

            return value.Trim();
        }
    }

    /// <summary>
    /// Describes one LISA Branch 23 movement and the fund codes stored in
    /// LV.FMVDT0 for that movement.
    /// </summary>
    public sealed class Branch23MovementFunds
    {
        public long MovementNumber { get; init; }

        public string MovementType { get; init; } =
            string.Empty;

        public string MovementState { get; init; } =
            string.Empty;

        public string LastJob { get; init; } =
            string.Empty;

        public IReadOnlyList<string> Funds { get; init; } =
            Array.Empty<string>();

        public override string ToString()
        {
            string funds =
                Funds.Count == 0
                    ? "none"
                    : string.Join(", ", Funds);

            return
                $"Movement={MovementNumber}; " +
                $"Type={Display(MovementType)}; " +
                $"State={Display(MovementState)}; " +
                $"LastJob={Display(LastJob)}; " +
                $"Funds={funds}";
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "N/A"
                : value.Trim();
        }
    }

    /// <summary>
    /// Reads Branch 23 fund movements after the contract exists in LISA.
    ///
    /// Important workflow rule:
    /// - this source requires a valid LISA NO_CNT;
    /// - it is never a prerequisite for LVDQFG03 on a new contract;
    /// - before LVDQFG03, confirmed UI funds have priority and ELIA TB5UGFF
    ///   is only a secondary source;
    /// - this source is reserved for post-activation validation, diagnostics
    ///   and controlled resume scenarios.
    /// </summary>
    public sealed class Branch23MovementFundSource
    {
        private readonly DatabaseManager _database;
        private readonly string _environmentSuffix;

        public Branch23MovementFundSource(
            string environmentSuffix)
        {
            if (string.IsNullOrWhiteSpace(environmentSuffix))
            {
                throw new ArgumentException(
                    "The environment suffix is required.",
                    nameof(environmentSuffix));
            }

            _environmentSuffix =
                Settings.NormalizeEnvironmentSuffix(
                    environmentSuffix);

            _database =
                new DatabaseManager(
                    _environmentSuffix);
        }

        /// <summary>
        /// Returns the latest existing LISA fund-movement number.
        /// Returns zero when the contract has no movement yet.
        /// </summary>
        public async Task<long> GetLastMovementNumberAsync(
            string lisaInternalId,
            CancellationToken token)
        {
            string normalizedInternalId =
                ValidateLisaInternalId(
                    lisaInternalId);

            token.ThrowIfCancellationRequested();

            using DataTable table =
                await _database.GetDataAsync(
                        SqlQueries.GetQuery(
                            "BR23_LAST_FUND_MOVEMENT",
                            _environmentSuffix),
                        BuildParameters(
                            normalizedInternalId,
                            lastMovementBefore: null),
                        token)
                    .ConfigureAwait(false);

            if (table.Rows.Count == 0)
            {
                return 0L;
            }

            return ReadInt64(
                       table.Rows[0],
                       "LastMovementNumber")
                   ?? 0L;
        }

        /// <summary>
        /// Returns the unique movement created after the supplied movement
        /// number, including its FMVDT0 fund codes.
        ///
        /// Returns null when no newer movement exists. Throws when several
        /// movements exist because the movement created by one execution can
        /// no longer be identified safely.
        /// </summary>
        public async Task<Branch23MovementFunds?>
            GetNewMovementFundsAsync(
                string lisaInternalId,
                long lastMovementBefore,
                CancellationToken token)
        {
            string normalizedInternalId =
                ValidateLisaInternalId(
                    lisaInternalId);

            if (lastMovementBefore < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastMovementBefore),
                    lastMovementBefore,
                    "The previous movement number cannot be negative.");
            }

            token.ThrowIfCancellationRequested();

            using DataTable table =
                await _database.GetDataAsync(
                        SqlQueries.GetQuery(
                            "BR23_NEW_FUND_MOVEMENT_FUNDS",
                            _environmentSuffix),
                        BuildParameters(
                            normalizedInternalId,
                            lastMovementBefore),
                        token)
                    .ConfigureAwait(false);

            if (table.Rows.Count == 0)
            {
                return null;
            }

            List<IGrouping<long, DataRow>> movements =
                table.Rows
                    .Cast<DataRow>()
                    .Select(row => new
                    {
                        Row = row,
                        MovementNumber = ReadRequiredMovementNumber(row)
                    })
                    .GroupBy(item => item.MovementNumber, item => item.Row)
                    .OrderBy(group => group.Key)
                    .ToList();

            if (movements.Count > 1)
            {
                throw new Branch23FundResolutionException(
                    "BR23_MOVEMENT_AMBIGUOUS",
                    "Several new Branch 23 fund movements were found " +
                    $"after NO_ORD_MVT_FONDS {lastMovementBefore}: " +
                    string.Join(
                        ", ",
                        movements.Select(group => group.Key)) +
                    ". The movement created by this execution cannot " +
                    "be identified safely.");
            }

            IGrouping<long, DataRow> movement =
                movements[0];

            DataRow firstRow =
                movement.First();

            IReadOnlyList<string> funds =
                movement
                    .Select(row => ReadString(row, "FundCode"))
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Select(value =>
                        value!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        value => value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (funds.Count == 0)
            {
                throw new Branch23FundResolutionException(
                    "BR23_MOVEMENT_FUNDS_MISSING",
                    $"Movement {movement.Key} was found for LISA " +
                    $"contract {normalizedInternalId}, but no fund code " +
                    "was present in LV.FMVDT0.C_FONDS.");
            }

            return new Branch23MovementFunds
            {
                MovementNumber = movement.Key,
                MovementType =
                    ReadString(firstRow, "TY_MVT_FONDS") ??
                    string.Empty,
                MovementState =
                    ReadString(firstRow, "C_ETAT_MVT_FONDS") ??
                    string.Empty,
                LastJob =
                    ReadString(firstRow, "LastJob") ??
                    string.Empty,
                Funds = funds
            };
        }

        /// <summary>
        /// Returns all movements created after the supplied movement number.
        /// This method is diagnostic only and does not require the result to
        /// contain exactly one movement.
        /// </summary>
        public async Task<IReadOnlyList<Branch23MovementFunds>>
            GetMovementsAfterAsync(
                string lisaInternalId,
                long lastMovementBefore,
                CancellationToken token)
        {
            string normalizedInternalId =
                ValidateLisaInternalId(
                    lisaInternalId);

            if (lastMovementBefore < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastMovementBefore),
                    lastMovementBefore,
                    "The previous movement number cannot be negative.");
            }

            token.ThrowIfCancellationRequested();

            using DataTable table =
                await _database.GetDataAsync(
                        SqlQueries.GetQuery(
                            "BR23_NEW_FUND_MOVEMENT_FUNDS",
                            _environmentSuffix),
                        BuildParameters(
                            normalizedInternalId,
                            lastMovementBefore),
                        token)
                    .ConfigureAwait(false);

            if (table.Rows.Count == 0)
            {
                return Array.Empty<Branch23MovementFunds>();
            }

            return table.Rows
                .Cast<DataRow>()
                .Select(row => new
                {
                    Row = row,
                    MovementNumber = ReadRequiredMovementNumber(row)
                })
                .GroupBy(item => item.MovementNumber, item => item.Row)
                .OrderBy(group => group.Key)
                .Select(MapMovement)
                .ToList();
        }

        private static Branch23MovementFunds MapMovement(
            IGrouping<long, DataRow> movement)
        {
            DataRow firstRow =
                movement.First();

            IReadOnlyList<string> funds =
                movement
                    .Select(row => ReadString(row, "FundCode"))
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Select(value =>
                        value!.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        value => value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return new Branch23MovementFunds
            {
                MovementNumber = movement.Key,
                MovementType =
                    ReadString(firstRow, "TY_MVT_FONDS") ??
                    string.Empty,
                MovementState =
                    ReadString(firstRow, "C_ETAT_MVT_FONDS") ??
                    string.Empty,
                LastJob =
                    ReadString(firstRow, "LastJob") ??
                    string.Empty,
                Funds = funds
            };
        }

        private static Dictionary<string, object> BuildParameters(
            string lisaInternalId,
            long? lastMovementBefore)
        {
            var parameters =
                new Dictionary<string, object>
                {
                    ["@Company"] =
                        Settings.Branch23.Ste,
                    ["@Contract"] =
                        lisaInternalId
                };

            if (lastMovementBefore.HasValue)
            {
                parameters["@LastMovementBefore"] =
                    lastMovementBefore.Value;
            }

            return parameters;
        }

        private static string ValidateLisaInternalId(
            string lisaInternalId)
        {
            if (string.IsNullOrWhiteSpace(lisaInternalId))
            {
                throw new ArgumentException(
                    "The LISA internal NO_CNT is required. " +
                    "Branch23MovementFundSource can only be used after " +
                    "the contract exists in LISA.",
                    nameof(lisaInternalId));
            }

            return lisaInternalId.Trim();
        }

        private static long ReadRequiredMovementNumber(
            DataRow row)
        {
            long? movementNumber =
                ReadInt64(
                    row,
                    "NO_ORD_MVT_FONDS");

            if (!movementNumber.HasValue)
            {
                throw new Branch23FundResolutionException(
                    "BR23_MOVEMENT_NUMBER_MISSING",
                    "The SQL result does not contain a valid " +
                    "NO_ORD_MVT_FONDS value.");
            }

            return movementNumber.Value;
        }

        private static long? ReadInt64(
            DataRow row,
            string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) ||
                row.IsNull(columnName))
            {
                return null;
            }

            object value =
                row[columnName];

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is decimal decimalValue)
            {
                return decimal.ToInt64(decimalValue);
            }

            string? text =
                Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture);

            return long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsed)
                    ? parsed
                    : null;
        }

        private static string? ReadString(
            DataRow row,
            string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) ||
                row.IsNull(columnName))
            {
                return null;
            }

            string? value =
                Convert.ToString(
                    row[columnName],
                    CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }
    }
}