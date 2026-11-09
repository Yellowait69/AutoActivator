using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace AutoActivator.Models
{
    /// <summary>
    /// Defines how AutoActivator obtains activation variables for single and batch
    /// activations. SQL autocomplete is the safe default.
    /// </summary>
    public enum ActivationInputMode
    {
        /// <summary>
        /// SQL resolves the contract, process, ChannelCode, business channel,
        /// BUCP, CMDPMT, premium, business date and JCL variables.
        /// Manual business-variable overrides are ignored.
        /// </summary>
        [Description("Autocomplete based on SQL info")]
        SqlAutocomplete = 0,

        /// <summary>
        /// SQL resolves the complete plan first. Explicit UI or Excel values may then
        /// replace selected values, and every replacement must be logged.
        /// </summary>
        [Description("SQL autocomplete with manual overrides")]
        SqlAutocompleteWithOverrides = 1,

        /// <summary>
        /// Uses values entered in the UI or supplied by the Excel row without SQL
        /// business-variable resolution. This mode remains available for controlled
        /// exceptions and legacy troubleshooting.
        /// </summary>
        [Description("Manual variables")]
        Manual = 2
    }

    /// <summary>
    /// Centralized behavior, display and parsing helpers for ActivationInputMode.
    /// Keeping these rules here ensures that the UI, single activation and batch
    /// activation interpret each mode identically.
    /// </summary>
    public static class ActivationInputModeExtensions
    {
        private static readonly IReadOnlyDictionary<string, ActivationInputMode>
            ParsedValues = CreateParsedValues();

        /// <summary>
        /// True when SQL resolution must run before activation or preflight.
        /// </summary>
        public static bool UsesSqlAutocomplete(this ActivationInputMode mode)
        {
            mode.EnsureDefined();
            return mode is ActivationInputMode.SqlAutocomplete
                or ActivationInputMode.SqlAutocompleteWithOverrides;
        }

        /// <summary>
        /// True when explicit UI or Excel overrides may replace resolved values.
        /// </summary>
        public static bool AllowsManualOverrides(this ActivationInputMode mode)
        {
            mode.EnsureDefined();
            return mode is ActivationInputMode.SqlAutocompleteWithOverrides
                or ActivationInputMode.Manual;
        }

        /// <summary>
        /// True when manual values are the only source of activation variables.
        /// </summary>
        public static bool IsManualOnly(this ActivationInputMode mode)
        {
            mode.EnsureDefined();
            return mode == ActivationInputMode.Manual;
        }

        /// <summary>
        /// True when SQL owns all business variables and no override may be applied.
        /// </summary>
        public static bool IsStrictSqlMode(this ActivationInputMode mode)
        {
            mode.EnsureDefined();
            return mode == ActivationInputMode.SqlAutocomplete;
        }

        /// <summary>
        /// Returns a stable value for configuration, Excel exports and logs.
        /// </summary>
        public static string ToConfigurationValue(this ActivationInputMode mode)
        {
            return mode.EnsureDefined() switch
            {
                ActivationInputMode.SqlAutocomplete => "sql-autocomplete",
                ActivationInputMode.SqlAutocompleteWithOverrides =>
                    "sql-autocomplete-with-overrides",
                ActivationInputMode.Manual => "manual",
                _ => throw new InvalidEnumArgumentException(
                    nameof(mode),
                    (int)mode,
                    typeof(ActivationInputMode))
            };
        }

        /// <summary>
        /// Returns the user-facing label for the activation screen.
        /// </summary>
        public static string ToDisplayName(this ActivationInputMode mode)
        {
            return mode.EnsureDefined() switch
            {
                ActivationInputMode.SqlAutocomplete =>
                    "Autocomplete based on SQL info",
                ActivationInputMode.SqlAutocompleteWithOverrides =>
                    "SQL autocomplete with manual overrides",
                ActivationInputMode.Manual =>
                    "Manual variables",
                _ => "Unknown input mode"
            };
        }

        /// <summary>
        /// Returns a concise tooltip/report description.
        /// </summary>
        public static string ToDescription(this ActivationInputMode mode)
        {
            return mode.EnsureDefined() switch
            {
                ActivationInputMode.SqlAutocomplete =>
                    "SQL resolves contract data, ChannelCode, BANK or " +
                    "BROKER_NEW_GEN routing, BUCP, CMDPMT, premium, business " +
                    "date and JCL variables. Manual business overrides are ignored.",

                ActivationInputMode.SqlAutocompleteWithOverrides =>
                    "SQL resolves the complete activation plan first. Explicit UI " +
                    "or Excel overrides are then validated, applied and recorded.",

                ActivationInputMode.Manual =>
                    "The activation uses UI or Excel values. SQL business-variable " +
                    "resolution is not used. Payment routing still has to match a " +
                    "supported pair: BANK 00382/8 or BROKER_NEW_GEN 80819/H.",

                _ => "Unsupported activation input mode."
            };
        }

        /// <summary>
        /// Parses enum names, configuration values, UI labels and common aliases.
        /// Empty or unknown values return false and leave SQL autocomplete as the
        /// output value, allowing callers to apply an explicit safe default.
        /// </summary>
        public static bool TryParse(
            string? value,
            out ActivationInputMode mode)
        {
            mode = ActivationInputMode.SqlAutocomplete;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = Normalize(value);
            if (ParsedValues.TryGetValue(normalized, out mode))
            {
                return true;
            }

            if (!Enum.TryParse(value.Trim(), ignoreCase: true, out mode))
            {
                mode = ActivationInputMode.SqlAutocomplete;
                return false;
            }

            if (!Enum.IsDefined(typeof(ActivationInputMode), mode))
            {
                mode = ActivationInputMode.SqlAutocomplete;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses a value and falls back to SQL autocomplete, or to the explicitly
        /// supplied default, when the value is missing or invalid.
        /// </summary>
        public static ActivationInputMode ParseOrDefault(
            string? value,
            ActivationInputMode defaultMode =
                ActivationInputMode.SqlAutocomplete)
        {
            defaultMode.EnsureDefined();

            return TryParse(value, out ActivationInputMode mode)
                ? mode
                : defaultMode;
        }

        /// <summary>
        /// Parses a value and throws when it does not represent a supported mode.
        /// Useful for strict configuration loading.
        /// </summary>
        public static ActivationInputMode ParseRequired(string value)
        {
            if (TryParse(value, out ActivationInputMode mode))
            {
                return mode;
            }

            throw new ArgumentException(
                $"Unsupported activation input mode '{value}'. Expected " +
                "sql-autocomplete, sql-autocomplete-with-overrides or manual.",
                nameof(value));
        }

        /// <summary>
        /// Validates that the enum value is defined before use by the UI, batch
        /// coordinator or activation services.
        /// </summary>
        public static ActivationInputMode EnsureDefined(
            this ActivationInputMode mode)
        {
            if (!Enum.IsDefined(typeof(ActivationInputMode), mode))
            {
                throw new InvalidEnumArgumentException(
                    nameof(mode),
                    (int)mode,
                    typeof(ActivationInputMode));
            }

            return mode;
        }

        private static IReadOnlyDictionary<string, ActivationInputMode>
            CreateParsedValues()
        {
            return new Dictionary<string, ActivationInputMode>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["sqlautocomplete"] = ActivationInputMode.SqlAutocomplete,
                ["autocomplete"] = ActivationInputMode.SqlAutocomplete,
                ["autocompletebasedonsqlinfo"] =
                    ActivationInputMode.SqlAutocomplete,
                ["automatic"] = ActivationInputMode.SqlAutocomplete,
                ["auto"] = ActivationInputMode.SqlAutocomplete,
                ["sql"] = ActivationInputMode.SqlAutocomplete,
                ["strictsql"] = ActivationInputMode.SqlAutocomplete,

                ["sqlautocompletewithoverrides"] =
                    ActivationInputMode.SqlAutocompleteWithOverrides,
                ["sqlautocompletewithmanualoverrides"] =
                    ActivationInputMode.SqlAutocompleteWithOverrides,
                ["autocompletewithoverrides"] =
                    ActivationInputMode.SqlAutocompleteWithOverrides,
                ["automaticwithoverrides"] =
                    ActivationInputMode.SqlAutocompleteWithOverrides,
                ["hybrid"] =
                    ActivationInputMode.SqlAutocompleteWithOverrides,

                ["manual"] = ActivationInputMode.Manual,
                ["manualvariables"] = ActivationInputMode.Manual,
                ["manualonly"] = ActivationInputMode.Manual
            };
        }

        private static string Normalize(string value)
        {
            return value
                .Trim()
                .ToLower(CultureInfo.InvariantCulture)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);
        }
    }
}