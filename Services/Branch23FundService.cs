using AutoActivator.Config;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Type de fonds Branche 23 déterminé à partir des tables de cotation (LV000GD0).
    /// </summary>
    public enum Branch23FundKind
    {
        /// <summary>
        /// Fonds fermé dont le prix est conservé dans LV000GD0.LV.GFEVT0 (D_CALC_EVOL_FONDS).
        /// </summary>
        Closed = 0,

        /// <summary>
        /// Fonds ouvert dont la VNI est conservée dans LV000GD0.LV.GFCRT0 (D_COURS).
        /// </summary>
        Open = 1
    }

    /// <summary>
    /// Résultat du contrôle ou de la préparation d'une VNI dans LV000GD0.
    /// </summary>
    public sealed class Branch23NavResult
    {
        public string FundCode { get; set; } = string.Empty;
        public Branch23FundKind? Kind { get; set; }
        public string KindSource { get; set; } = string.Empty;
        public DateTime NavDate { get; set; }
        public bool AlreadyExisted { get; set; }
        public bool Created { get; set; }
        public bool CreatedConcurrently { get; set; }
        public int InsertedRows { get; set; }
        public DateTime? SourceNavDate { get; set; }
        public decimal? AppliedIncrement { get; set; }
        public string Error { get; set; } = string.Empty;

        public bool IsReady =>
            AlreadyExisted ||
            Created ||
            CreatedConcurrently;

        public override string ToString()
        {
            string kind = Kind.HasValue ? Kind.Value.ToString() : "Unknown";
            string sourceDate = SourceNavDate.HasValue
                ? SourceNavDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "-";

            if (AlreadyExisted)
            {
                return $"{FundCode} ({kind}, {KindSource}): NAV already present on {NavDate:yyyy-MM-dd}";
            }

            if (CreatedConcurrently)
            {
                return $"{FundCode} ({kind}, {KindSource}): NAV created concurrently on {NavDate:yyyy-MM-dd}";
            }

            if (Created)
            {
                return $"{FundCode} ({kind}, {KindSource}): NAV created on {NavDate:yyyy-MM-dd} " +
                       $"from {sourceDate}, increment={AppliedIncrement?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
            }

            return $"{FundCode} ({kind}, {KindSource}): NOT READY - " +
                   $"{(string.IsNullOrWhiteSpace(Error) ? "Unknown error." : Error)}";
        }
    }

    /// <summary>
    /// Source des fonds associés à un contrat Branche 23.
    /// </summary>
    public interface IBranch23FundSource
    {
        Task<IReadOnlyList<string>> GetContractFundsAsync(
            string jclContract,
            string envSuffix,
            CancellationToken token);
    }

    /// <summary>
    /// Source de fonds explicitement configurée depuis l'interface ou le fichier batch.
    /// </summary>
    public sealed class ConfiguredBranch23FundSource : IBranch23FundSource
    {
        private readonly IReadOnlyList<string> _funds;

        public ConfiguredBranch23FundSource(IEnumerable<string>? funds)
        {
            _funds = NormalizeFunds(funds);
        }

        public static IReadOnlyList<string> Parse(string? rawFunds)
        {
            if (string.IsNullOrWhiteSpace(rawFunds))
            {
                return Array.Empty<string>();
            }

            string[] values = rawFunds.Split(
                new[] { ',', ';', '|', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            return NormalizeFunds(values);
        }

        public Task<IReadOnlyList<string>> GetContractFundsAsync(
            string jclContract,
            string envSuffix,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_funds);
        }

        private static IReadOnlyList<string> NormalizeFunds(IEnumerable<string>? funds)
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
    /// Contrôle et prépare les VNI des fonds d'un contrat Branche 23 dans LV000GD0
    /// (LV.GFCRT0 pour les fonds ouverts, LV.GFEVT0 pour les fonds fermés).
    /// </summary>
    public sealed partial class Branch23FundService
    {
        private const int SqlCommandTimeoutSeconds = 60;
        private const decimal DefaultClosedFundUnitIncrement = 300m;
        private const decimal DefaultOpenFundNavIncrement = 0m;

        private readonly string _environment;
        private readonly string _primaryValuationConnectionString;
        private readonly string _fallbackApplicationConnectionString;

        public Branch23FundService(string envSuffix)
        {
            if (string.IsNullOrWhiteSpace(envSuffix))
            {
                throw new ArgumentException(
                    "L'environnement est obligatoire.",
                    nameof(envSuffix));
            }

            _environment = Settings.NormalizeEnvironmentSuffix(envSuffix);

            string valuationDb = Settings.DbConfig.GetFundValuationDatabase(_environment);
            _primaryValuationConnectionString =
                Settings.DbConfig.GetConnectionString(_environment, valuationDb);
            _fallbackApplicationConnectionString =
                Settings.DbConfig.GetConnectionString(_environment);
        }

        public string ValuationDatabaseName =>
            Settings.DbConfig.GetFundValuationDatabase(_environment);

        /// <summary>
        /// Contrôle et prépare les VNI de tous les fonds à la date donnée (D_COURS / D_CALC_EVOL_FONDS).
        /// </summary>
        public async Task<List<Branch23NavResult>> EnsureNavsAsync(
            IEnumerable<string>? fundCodes,
            DateTime navDate,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            IReadOnlyList<string> normalizedFunds = NormalizeFunds(fundCodes);
            var results = new List<Branch23NavResult>(normalizedFunds.Count);
            DateTime normalizedNavDate = navDate.Date;

            foreach (string fund in normalizedFunds)
            {
                token.ThrowIfCancellationRequested();

                var result = new Branch23NavResult
                {
                    FundCode = fund,
                    NavDate = normalizedNavDate
                };

                results.Add(result);

                try
                {
                    await ProcessFundAsync(result, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result.Error = BuildErrorMessage(exception);
                }
            }

            return results;
        }

        /// <summary>
        /// Prépare toutes les VNI comprises entre la date d'activation (Date N) et la
        /// date de consolidation (Date N+2), bornes incluses.
        /// </summary>
        public async Task<List<Branch23NavResult>> EnsureNavWindowAsync(
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
                    "The consolidation date cannot be earlier than the activation date.",
                    nameof(consolidationDate));
            }

            int requiredDayCount = (lastDate - firstDate).Days + 1;
            var results = new List<Branch23NavResult>();

            for (int offset = 0; offset < requiredDayCount; offset++)
            {
                token.ThrowIfCancellationRequested();

                DateTime requiredDate = firstDate.AddDays(offset);
                List<Branch23NavResult> dateResults =
                    await EnsureNavsAsync(fundCodes, requiredDate, token)
                        .ConfigureAwait(false);

                results.AddRange(dateResults);
            }

            return results;
        }

        /// <summary>
        /// Prépare les VNI de Date N jusqu'à Date N+2 + jours tampons ouvrés
        /// (couvre le cas où LISA crée le mouvement à J+1 et consolide le cours à Dat.Cre + 2,
        /// comme observé en F51LV : RunDate=19/02, Dat.Cre=20/02, Dat.Cours=22/02).
        /// </summary>
        public Task<List<Branch23NavResult>> EnsureNavWindowWithBufferAsync(
            IEnumerable<string>? fundCodes,
            DateTime activationDate,
            DateTime consolidationDate,
            int extraCalendarDaysAfterConsolidation,
            CancellationToken token)
        {
            DateTime extendedEnd = consolidationDate.Date.AddDays(
                Math.Max(0, extraCalendarDaysAfterConsolidation));
            return EnsureNavWindowAsync(
                fundCodes,
                activationDate.Date,
                extendedEnd,
                token);
        }

        public Task<List<Branch23NavResult>> EnsureNavWindowAsync(
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
    }
}
