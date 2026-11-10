using AutoActivator.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AutoActivator.Services
{
    /// <summary>
    /// Resolves payment routing for single and batch activation plans.
    /// </summary>
    public interface IActivationRoutingService
    {
        ActivationRoutingResult Resolve(string? channelCode);

        bool TryResolve(
            string? channelCode,
            out ActivationRoutingResult result);

        ActivationRoutingResult Resolve(
            string? channelCode,
            string? bucpOverride,
            string? paymentCommandOverride,
            ActivationInputMode inputMode);

        ActivationRoutingResult ResolveFromPaymentPair(
            string? bucp,
            string? paymentCommand);

        IReadOnlyCollection<string> GetBankChannelCodes();

        IReadOnlyCollection<string> GetBrokerChannelCodes();
    }

    /// <summary>
    /// Immutable routing decision propagated to the activation plan, trace and report.
    /// </summary>
    public sealed record ActivationRoutingResult
    {
        public static ActivationRoutingResult Unresolved(
            string? channelCode,
            string diagnosticCode,
            string diagnosticMessage)
        {
            return new ActivationRoutingResult
            {
                ChannelCode = ActivationRoutingService.NormalizeChannelCode(channelCode),
                PaymentChannel = ActivationPaymentChannel.Unknown,
                ResolvedChannel = "UNKNOWN",
                Bucp = string.Empty,
                PaymentCommand = string.Empty,
                Rule = "NO_CHANNEL_RULE",
                Source = ActivationRoutingSource.None,
                Status = ActivationRoutingStatus.Unresolved,
                DiagnosticCode = diagnosticCode,
                DiagnosticMessage = diagnosticMessage,
                IsOverrideApplied = false
            };
        }

        public string ChannelCode { get; init; } = string.Empty;

        public ActivationPaymentChannel PaymentChannel { get; init; } =
            ActivationPaymentChannel.Unknown;

        public string ResolvedChannel { get; init; } = "UNKNOWN";

        /// <summary>
        /// Five-character JCL BUCP value, for example 00382 or 80819.
        /// </summary>
        public string Bucp { get; init; } = string.Empty;

        public string PaymentCommand { get; init; } = string.Empty;

        public string Rule { get; init; } = "NO_CHANNEL_RULE";

        public ActivationRoutingSource Source { get; init; } =
            ActivationRoutingSource.None;

        public ActivationRoutingStatus Status { get; init; } =
            ActivationRoutingStatus.Unresolved;

        public string DiagnosticCode { get; init; } = string.Empty;

        public string DiagnosticMessage { get; init; } = string.Empty;

        public bool IsOverrideApplied { get; init; }

        public string OriginalBucp { get; init; } = string.Empty;

        public string OriginalPaymentCommand { get; init; } = string.Empty;

        public bool IsResolved =>
            (Status is ActivationRoutingStatus.Resolved
                or ActivationRoutingStatus.ResolvedWithOverrides) &&
            PaymentChannel != ActivationPaymentChannel.Unknown &&
            !string.IsNullOrWhiteSpace(Bucp) &&
            !string.IsNullOrWhiteSpace(PaymentCommand);

        public string Summary =>
            $"ChannelCode={Display(ChannelCode)} | " +
            $"Channel={ResolvedChannel} | " +
            $"BUCP={Display(Bucp)} | " +
            $"CMDPMT={Display(PaymentCommand)} | " +
            $"Rule={Rule} | Source={Source} | Status={Status}";

        private static string Display(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    public enum ActivationRoutingStatus
    {
        Unresolved = 0,
        Resolved = 1,
        ResolvedWithOverrides = 2,
        BlockedInvalidOverride = 3
    }

    public enum ActivationRoutingSource
    {
        None = 0,
        EliaChannelCode = 1,
        ManualPaymentPair = 2,
        EliaChannelCodeWithOverrides = 3
    }

    /// <summary>
    /// Single source of truth for current payment routing.
    ///
    /// Bank channels:
    /// 01, 02, 04, 05, 13, 18, 22, 23, 24 -> BUCP 00382 / CMDPMT 8.
    ///
    /// Broker New Generation channels:
    /// 03, 06, 07, 08, 09, 10, 11, 12, 14, 15, 16, 17, 19, 20, 21
    /// -> BUCP 80819 / CMDPMT H.
    ///
    /// The historical 49797/X route is not selected automatically.
    /// </summary>
    public sealed class ActivationRoutingService : IActivationRoutingService
    {
        public const string BankBucp = "00382";
        public const string BankPaymentCommand = "8";
        public const string BrokerNewGenerationBucp = "80819";
        public const string BrokerNewGenerationPaymentCommand = "H";

        private static readonly HashSet<string> BankChannelCodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "01", "02", "04", "05", "13", "18", "22", "23", "24",
                "C02", "BANK"
            };

        private static readonly HashSet<string> BrokerChannelCodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "03", "06", "07", "08", "09", "10", "11", "12",
                "14", "15", "16", "17", "19", "20", "21",
                "C01", "C03", "BROKER_NEW_GEN"
            };

        private static readonly ReadOnlyCollection<string> ReadOnlyBankChannelCodes =
            Array.AsReadOnly(BankChannelCodes.OrderBy(code => code).ToArray());

        private static readonly ReadOnlyCollection<string> ReadOnlyBrokerChannelCodes =
            Array.AsReadOnly(BrokerChannelCodes.OrderBy(code => code).ToArray());

        static ActivationRoutingService()
        {
            string[] duplicateCodes = BankChannelCodes
                .Intersect(BrokerChannelCodes, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (duplicateCodes.Length > 0)
            {
                throw new InvalidOperationException(
                    "The activation routing matrix contains duplicate Bank/Broker " +
                    $"ChannelCodes: {string.Join(", ", duplicateCodes)}.");
            }
        }

        /// <summary>
        /// Resolves the canonical SQL-autocomplete route from ELIA ChannelCode.
        /// </summary>
        public ActivationRoutingResult Resolve(string? channelCode)
        {
            string code = NormalizeChannelCode(channelCode);

            if (string.IsNullOrWhiteSpace(code))
            {
                return ActivationRoutingResult.Unresolved(
                    code,
                    "CHANNEL_CODE_MISSING",
                    "ELIA ChannelCode is missing; payment routing cannot be resolved.");
            }

            if (BankChannelCodes.Contains(code))
            {
                return CreateResolvedResult(
                    code,
                    ActivationPaymentChannel.Bank,
                    "BANK",
                    BankBucp,
                    BankPaymentCommand,
                    $"CHANNEL_{code}_BANK_{BankBucp}_{BankPaymentCommand}",
                    ActivationRoutingSource.EliaChannelCode);
            }

            if (BrokerChannelCodes.Contains(code))
            {
                return CreateResolvedResult(
                    code,
                    ActivationPaymentChannel.BrokerNewGeneration,
                    "BROKER_NEW_GEN",
                    BrokerNewGenerationBucp,
                    BrokerNewGenerationPaymentCommand,
                    $"CHANNEL_{code}_BROKER_NEW_GEN_" +
                    $"{BrokerNewGenerationBucp}_{BrokerNewGenerationPaymentCommand}",
                    ActivationRoutingSource.EliaChannelCode);
            }

            return ActivationRoutingResult.Unresolved(
                code,
                "CHANNEL_CODE_NOT_MAPPED",
                $"ELIA ChannelCode '{code}' is not present in the confirmed " +
                "Bank/Broker New Generation routing matrix.");
        }

        public bool TryResolve(
            string? channelCode,
            out ActivationRoutingResult result)
        {
            result = Resolve(channelCode);
            return result.IsResolved;
        }

        /// <summary>
        /// Resolves SQL routing and optionally applies explicit manual overrides.
        /// Strict SQL mode ignores empty overrides and blocks non-empty overrides.
        /// </summary>
        public ActivationRoutingResult Resolve(
            string? channelCode,
            string? bucpOverride,
            string? paymentCommandOverride,
            ActivationInputMode inputMode)
        {
            inputMode.EnsureDefined();

            ActivationRoutingResult sqlResult = Resolve(channelCode);
            string normalizedBucpOverride = NormalizeBucp(bucpOverride);
            string normalizedCommandOverride = NormalizePaymentCommand(
                paymentCommandOverride);

            bool hasBucpOverride =
                !string.IsNullOrWhiteSpace(normalizedBucpOverride);
            bool hasCommandOverride =
                !string.IsNullOrWhiteSpace(normalizedCommandOverride);
            bool hasOverride = hasBucpOverride || hasCommandOverride;

            if (!hasOverride)
            {
                return sqlResult;
            }

            if (!inputMode.AllowsManualOverrides())
            {
                return sqlResult with
                {
                    Status = ActivationRoutingStatus.BlockedInvalidOverride,
                    DiagnosticCode = "OVERRIDE_NOT_ALLOWED",
                    DiagnosticMessage =
                        "Manual BUCP/CMDPMT overrides were supplied while strict " +
                        "SQL autocomplete mode is active."
                };
            }

            if (inputMode != ActivationInputMode.Manual && !sqlResult.IsResolved)
            {
                return sqlResult with
                {
                    Status = ActivationRoutingStatus.BlockedInvalidOverride,
                    DiagnosticCode = "SQL_ROUTING_UNRESOLVED",
                    DiagnosticMessage =
                        "SQL routing must resolve successfully before overrides can " +
                        "be applied in SQL autocomplete with overrides mode."
                };
            }

            string effectiveBucp = hasBucpOverride
                ? normalizedBucpOverride
                : sqlResult.Bucp;
            string effectiveCommand = hasCommandOverride
                ? normalizedCommandOverride
                : sqlResult.PaymentCommand;

            if (string.IsNullOrWhiteSpace(effectiveBucp) ||
                string.IsNullOrWhiteSpace(effectiveCommand))
            {
                return sqlResult with
                {
                    Bucp = effectiveBucp,
                    PaymentCommand = effectiveCommand,
                    Status = ActivationRoutingStatus.BlockedInvalidOverride,
                    DiagnosticCode = "INCOMPLETE_MANUAL_ROUTING",
                    DiagnosticMessage =
                        "Manual routing requires both BUCP and CMDPMT."
                };
            }

            ActivationPaymentChannel effectiveChannel = ResolveChannelFromPair(
                effectiveBucp,
                effectiveCommand);

            if (effectiveChannel == ActivationPaymentChannel.Unknown)
            {
                return sqlResult with
                {
                    Bucp = effectiveBucp,
                    PaymentCommand = effectiveCommand,
                    Status = ActivationRoutingStatus.BlockedInvalidOverride,
                    DiagnosticCode = "INVALID_BUCP_CMDPMT_COMBINATION",
                    DiagnosticMessage =
                        $"The override combination {effectiveBucp}/{effectiveCommand} " +
                        "is not a supported Bank or Broker New Generation route."
                };
            }

            string resolvedChannel = ToResolvedChannel(effectiveChannel);
            string originalBucp = sqlResult.Bucp;
            string originalCommand = sqlResult.PaymentCommand;

            return sqlResult with
            {
                PaymentChannel = effectiveChannel,
                ResolvedChannel = resolvedChannel,
                Bucp = effectiveBucp,
                PaymentCommand = effectiveCommand,
                Rule = inputMode == ActivationInputMode.Manual
                    ? $"MANUAL_{resolvedChannel}_{effectiveBucp}_{effectiveCommand}"
                    : sqlResult.Rule + "_WITH_OVERRIDE",
                Source = inputMode == ActivationInputMode.Manual
                    ? ActivationRoutingSource.ManualPaymentPair
                    : ActivationRoutingSource.EliaChannelCodeWithOverrides,
                Status = ActivationRoutingStatus.ResolvedWithOverrides,
                DiagnosticCode = "ROUTING_OVERRIDE_APPLIED",
                DiagnosticMessage =
                    inputMode == ActivationInputMode.Manual
                        ? $"Manual routing applied: {effectiveBucp}/" +
                          $"{effectiveCommand} ({resolvedChannel})."
                        : $"Manual routing override applied: {effectiveBucp}/" +
                          $"{effectiveCommand}. SQL route was " +
                          $"{originalBucp}/{originalCommand}.",
                IsOverrideApplied = true,
                OriginalBucp = originalBucp,
                OriginalPaymentCommand = originalCommand
            };
        }

        public ActivationRoutingResult ResolveFromPaymentPair(
            string? bucp,
            string? paymentCommand)
        {
            string normalizedBucp = NormalizeBucp(bucp);
            string normalizedCommand = NormalizePaymentCommand(paymentCommand);
            ActivationPaymentChannel channel = ResolveChannelFromPair(
                normalizedBucp,
                normalizedCommand);

            if (channel == ActivationPaymentChannel.Unknown)
            {
                return ActivationRoutingResult.Unresolved(
                    string.Empty,
                    "INVALID_BUCP_CMDPMT_COMBINATION",
                    $"BUCP/CMDPMT {normalizedBucp}/{normalizedCommand} is not a " +
                    "supported Bank or Broker New Generation route.");
            }

            string resolvedChannel = ToResolvedChannel(channel);
            return CreateResolvedResult(
                string.Empty,
                channel,
                resolvedChannel,
                normalizedBucp,
                normalizedCommand,
                $"PAYMENT_PAIR_{resolvedChannel}_{normalizedBucp}_{normalizedCommand}",
                ActivationRoutingSource.ManualPaymentPair);
        }

        public IReadOnlyCollection<string> GetBankChannelCodes() =>
            ReadOnlyBankChannelCodes;

        public IReadOnlyCollection<string> GetBrokerChannelCodes() =>
            ReadOnlyBrokerChannelCodes;

        public static ActivationPaymentChannel ResolveChannelFromPair(
            string? bucp,
            string? paymentCommand)
        {
            string normalizedBucp = NormalizeBucp(bucp);
            string normalizedCommand = NormalizePaymentCommand(paymentCommand);

            if ((string.Equals(normalizedBucp, BankBucp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedBucp, "00182", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(normalizedCommand, BankPaymentCommand, StringComparison.OrdinalIgnoreCase))
            {
                return ActivationPaymentChannel.Bank;
            }

            if (string.Equals(normalizedBucp, BrokerNewGenerationBucp, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(normalizedCommand, BrokerNewGenerationPaymentCommand, StringComparison.OrdinalIgnoreCase))
            {
                return ActivationPaymentChannel.BrokerNewGeneration;
            }

            if (string.Equals(normalizedBucp, "49797", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(normalizedCommand, "X", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedCommand, "H", StringComparison.OrdinalIgnoreCase)))
            {
                return ActivationPaymentChannel.BrokerNewGeneration;
            }

            return ActivationPaymentChannel.Unknown;
        }

        public static string NormalizeChannelCode(string? channelCode)
        {
            string value = (channelCode ?? string.Empty).Trim();
            return value.Length == 1 ? value.PadLeft(2, '0') : value;
        }

        public static string NormalizeBucp(string? bucp)
        {
            string value = (bucp ?? string.Empty).Trim();
            return value.Length == 0 ? string.Empty : value.PadLeft(5, '0');
        }

        public static string NormalizePaymentCommand(string? paymentCommand) =>
            (paymentCommand ?? string.Empty).Trim().ToUpperInvariant();

        private static string ToResolvedChannel(
            ActivationPaymentChannel paymentChannel)
        {
            return paymentChannel switch
            {
                ActivationPaymentChannel.Bank => "BANK",
                ActivationPaymentChannel.BrokerNewGeneration => "BROKER_NEW_GEN",
                _ => "UNKNOWN"
            };
        }

        private static ActivationRoutingResult CreateResolvedResult(
            string channelCode,
            ActivationPaymentChannel paymentChannel,
            string resolvedChannel,
            string bucp,
            string paymentCommand,
            string rule,
            ActivationRoutingSource source)
        {
            return new ActivationRoutingResult
            {
                ChannelCode = NormalizeChannelCode(channelCode),
                PaymentChannel = paymentChannel,
                ResolvedChannel = resolvedChannel,
                Bucp = NormalizeBucp(bucp),
                PaymentCommand = NormalizePaymentCommand(paymentCommand),
                Rule = rule,
                Source = source,
                Status = ActivationRoutingStatus.Resolved,
                DiagnosticCode = "ROUTING_RESOLVED",
                DiagnosticMessage = string.IsNullOrWhiteSpace(channelCode)
                    ? $"BUCP/CMDPMT resolved to {resolvedChannel} with " +
                      $"{NormalizeBucp(bucp)}/" +
                      $"{NormalizePaymentCommand(paymentCommand)}."
                    : $"ChannelCode {NormalizeChannelCode(channelCode)} resolved " +
                      $"to {resolvedChannel} with BUCP/CMDPMT " +
                      $"{NormalizeBucp(bucp)}/" +
                      $"{NormalizePaymentCommand(paymentCommand)}.",
                IsOverrideApplied = false
            };
        }
    }
}