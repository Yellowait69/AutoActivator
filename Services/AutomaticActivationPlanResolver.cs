using AutoActivator.Config;
using AutoActivator.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoActivator.Services
{
    /// <summary>
    /// Resolves complete activation plans for single and batch workflows.
    /// </summary>
    public interface IAutomaticActivationPlanResolver
    {
        Task<AutomaticActivationPlan> ResolveAsync(
            AutomaticActivationPlanRequest request,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<AutomaticActivationPlan>> ResolveBatchAsync(
            IEnumerable<AutomaticActivationPlanRequest> requests,
            int maxSqlConcurrency =
                Settings.ActivationAutomation.BatchSqlConcurrency,
            CancellationToken cancellationToken = default);

        Task<AutomaticActivationBatchResult> ResolveBatchDetailedAsync(
            IEnumerable<AutomaticActivationPlanRequest> requests,
            int maxSqlConcurrency =
                Settings.ActivationAutomation.BatchSqlConcurrency,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Input required to resolve one activation plan.
    /// </summary>
    public sealed record AutomaticActivationPlanRequest
    {
        public string InputValue { get; init; } = string.Empty;
        public bool InputIsDemandId { get; init; }
        public string Environment { get; init; } = "Q";
        public string Cus { get; init; } = string.Empty;
        public bool ForceBranch23 { get; init; }

        public ActivationInputMode InputMode { get; init; } =
            ActivationInputMode.SqlAutocomplete;

        public IReadOnlyDictionary<string, string> Overrides { get; init; } =
            EmptyOverrides();

        public string CorrelationId { get; init; } = string.Empty;
        public int SourceRowNumber { get; init; }

        private static IReadOnlyDictionary<string, string> EmptyOverrides()
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// One row returned by detailed batch resolution. A failed SQL lookup is isolated
    /// to its row instead of cancelling all other contracts.
    /// </summary>
    public sealed record AutomaticActivationBatchItemResult
    {
        public int Index { get; init; }
        public int SourceRowNumber { get; init; }
        public string CorrelationId { get; init; } = string.Empty;
        public string InputValue { get; init; } = string.Empty;
        public AutomaticActivationPlan? Plan { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public bool IsSuccess => Plan != null && string.IsNullOrWhiteSpace(ErrorMessage);
    }

    public sealed record AutomaticActivationBatchResult
    {
        public IReadOnlyList<AutomaticActivationBatchItemResult> Items { get; init; } =
            Array.Empty<AutomaticActivationBatchItemResult>();

        public int TotalCount => Items.Count;
        public int SuccessCount => Items.Count(item => item.IsSuccess);
        public int ErrorCount => TotalCount - SuccessCount;

        public IReadOnlyList<AutomaticActivationPlan> Plans =>
            Items
                .Where(item => item.Plan != null)
                .Select(item => item.Plan!)
                .ToArray();
    }

    /// <summary>
    /// Coordinates automatic activation-plan resolution through ActivationDataService.
    /// SQL queries and ELIA/LISA business logic remain centralized in that service.
    /// The resolver adds validation, cancellation and bounded batch concurrency.
    /// </summary>
    public sealed class AutomaticActivationPlanResolver :
        IAutomaticActivationPlanResolver
    {
        private readonly ActivationDataService _activationDataService;

        public AutomaticActivationPlanResolver()
            : this(new ActivationDataService())
        {
        }

        public AutomaticActivationPlanResolver(
            ActivationDataService activationDataService)
        {
            _activationDataService = activationDataService ??
                throw new ArgumentNullException(nameof(activationDataService));
        }

        public async Task<AutomaticActivationPlan> ResolveAsync(
            AutomaticActivationPlanRequest request,
            CancellationToken cancellationToken = default)
        {
            AutomaticActivationPlanRequest normalized = NormalizeRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            AutomaticActivationPlan plan =
                await _activationDataService.ResolveAutomaticActivationPlanAsync(
                        normalized.InputValue,
                        normalized.InputIsDemandId,
                        normalized.Environment,
                        normalized.Cus,
                        normalized.ForceBranch23,
                        normalized.InputMode,
                        normalized.Overrides,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (plan == null)
            {
                throw new InvalidOperationException(
                    "ActivationDataService returned no activation plan.");
            }

            return plan;
        }

        /// <summary>
        /// Backward-compatible batch API. Throws an AggregateException after every row
        /// has completed when at least one row failed. Use ResolveBatchDetailedAsync to
        /// receive row-level errors without throwing.
        /// </summary>
        public async Task<IReadOnlyList<AutomaticActivationPlan>> ResolveBatchAsync(
            IEnumerable<AutomaticActivationPlanRequest> requests,
            int maxSqlConcurrency =
                Settings.ActivationAutomation.BatchSqlConcurrency,
            CancellationToken cancellationToken = default)
        {
            AutomaticActivationBatchResult detailed =
                await ResolveBatchDetailedAsync(
                        requests,
                        maxSqlConcurrency,
                        cancellationToken)
                    .ConfigureAwait(false);

            AutomaticActivationBatchItemResult[] failed = detailed.Items
                .Where(item => !item.IsSuccess)
                .ToArray();

            if (failed.Length > 0)
            {
                throw new AggregateException(
                    $"{failed.Length} of {detailed.TotalCount} activation-plan " +
                    "resolutions failed.",
                    failed.Select(item =>
                        new InvalidOperationException(
                            $"Row {item.SourceRowNumber}, input " +
                            $"'{item.InputValue}': {item.ErrorCode} - " +
                            item.ErrorMessage)));
            }

            return detailed.Plans;
        }

        public async Task<AutomaticActivationBatchResult>
            ResolveBatchDetailedAsync(
                IEnumerable<AutomaticActivationPlanRequest> requests,
                int maxSqlConcurrency =
                    Settings.ActivationAutomation.BatchSqlConcurrency,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requests);
            ValidateConcurrency(maxSqlConcurrency);

            AutomaticActivationPlanRequest[] items = requests.ToArray();
            if (items.Length == 0)
            {
                return new AutomaticActivationBatchResult();
            }

            var results = new AutomaticActivationBatchItemResult[items.Length];
            using var gate = new SemaphoreSlim(
                maxSqlConcurrency,
                maxSqlConcurrency);

            Task[] tasks = items.Select(
                (request, index) => ResolveOneSafelyAsync(
                    request,
                    index,
                    results,
                    gate,
                    cancellationToken)).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return new AutomaticActivationBatchResult
            {
                Items = Array.AsReadOnly(results)
            };
        }

        private async Task ResolveOneSafelyAsync(
            AutomaticActivationPlanRequest request,
            int index,
            AutomaticActivationBatchItemResult[] results,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AutomaticActivationPlanRequest normalized = NormalizeRequest(request);

                try
                {
                    AutomaticActivationPlan plan = await ResolveAsync(
                            normalized,
                            cancellationToken)
                        .ConfigureAwait(false);

                    results[index] = CreateSuccessResult(
                        normalized,
                        index,
                        plan);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    results[index] = CreateFailureResult(
                        normalized,
                        index,
                        exception);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static AutomaticActivationPlanRequest NormalizeRequest(
            AutomaticActivationPlanRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.InputMode.EnsureDefined();

            string input = (request.InputValue ?? string.Empty).Trim();
            string cus = (request.Cus ?? string.Empty).Trim();
            string environment = Settings.NormalizeEnvironmentSuffix(
                request.Environment);

            if (input.Length == 0)
            {
                throw new ArgumentException(
                    "A contract number or Demand ID is required.",
                    nameof(request));
            }

            if (cus.Length == 0)
            {
                throw new ArgumentException(
                    "CUS is required.",
                    nameof(request));
            }

            IReadOnlyDictionary<string, string> overrides =
                NormalizeOverrides(request.Overrides, request.InputMode);

            return request with
            {
                InputValue = input,
                Environment = environment,
                Cus = cus,
                Overrides = overrides,
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId.Trim()
            };
        }

        private static IReadOnlyDictionary<string, string> NormalizeOverrides(
            IReadOnlyDictionary<string, string>? overrides,
            ActivationInputMode inputMode)
        {
            var values = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            if (overrides == null || !inputMode.AllowsManualOverrides())
            {
                return new ReadOnlyDictionary<string, string>(values);
            }

            foreach (KeyValuePair<string, string> item in overrides)
            {
                string key = (item.Key ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();
                string value = (item.Value ?? string.Empty).Trim();

                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                values[NormalizeOverrideKey(key)] = value;
            }

            return new ReadOnlyDictionary<string, string>(values);
        }

        private static string NormalizeOverrideKey(string key)
        {
            return key switch
            {
                "CMD" or "PAYMENTCOMMAND" => "CMDPMT",
                "RUNDATE" or "DATE" => "DRUN",
                "PREMIUM" or "GROSSPREMIUM" => "AMOUNT",
                "PROCESSCODE" => "PROCESS",
                "CHANNELCODE" => "CHANNEL",
                _ => key
            };
        }

        private static void ValidateConcurrency(int maxSqlConcurrency)
        {
            int maximum =
                Settings.ActivationAutomation.MaximumBatchSqlConcurrency;

            if (maxSqlConcurrency < 1 || maxSqlConcurrency > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxSqlConcurrency),
                    $"SQL concurrency must be between 1 and {maximum}.");
            }
        }

        private static AutomaticActivationBatchItemResult CreateSuccessResult(
            AutomaticActivationPlanRequest request,
            int index,
            AutomaticActivationPlan plan)
        {
            return new AutomaticActivationBatchItemResult
            {
                Index = index,
                SourceRowNumber = request.SourceRowNumber,
                CorrelationId = request.CorrelationId,
                InputValue = request.InputValue,
                Plan = plan
            };
        }

        private static AutomaticActivationBatchItemResult CreateFailureResult(
            AutomaticActivationPlanRequest request,
            int index,
            Exception exception)
        {
            return new AutomaticActivationBatchItemResult
            {
                Index = index,
                SourceRowNumber = request.SourceRowNumber,
                CorrelationId = request.CorrelationId,
                InputValue = request.InputValue,
                ErrorCode = exception.GetType().Name,
                ErrorMessage = exception.Message
            };
        }
    }
}