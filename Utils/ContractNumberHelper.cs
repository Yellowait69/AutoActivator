using System;
using System.Globalization;
using System.Linq;

namespace AutoActivator.Utils
{
    /// <summary>
    /// Helpers centralisés sans I/O pour normaliser les numéros de contrat externes (3-7-2 / 12 chiffres)
    /// et en déduire mathématiquement le numéro de contrat interne LISA (NO_CNT / JCL sur 9 chiffres).
    /// </summary>
    public static class ContractNumberHelper
    {
        /// <summary>
        /// Extrait uniquement les chiffres ASCII d'une référence contrat après nettoyage des séparateurs.
        /// </summary>
        public static string ExtractDigits(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            Span<char> buffer = stackalloc char[rawValue.Length];
            int count = 0;
            foreach (char c in rawValue)
            {
                if (c >= '0' && c <= '9')
                {
                    buffer[count++] = c;
                }
            }

            return count == 0 ? string.Empty : new string(buffer.Slice(0, count));
        }

        /// <summary>
        /// Vérifie que la chaîne (hors tirets, espaces, points et BOM) ne contient que des chiffres ASCII.
        /// </summary>
        public static bool TryGetCleanNumericContract(string? rawContract, out string digits)
        {
            digits = string.Empty;
            if (string.IsNullOrWhiteSpace(rawContract))
            {
                return false;
            }

            string trimmed = rawContract
                .Replace("\u00A0", string.Empty)
                .Replace("\uFEFF", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Replace(".", string.Empty)
                .Trim();

            if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit))
            {
                return false;
            }

            digits = trimmed;
            return true;
        }

        /// <summary>
        /// Déduit mathématiquement NO_CNT (et la clé JCL 9 chiffres) depuis un numéro de contrat externe (12 chiffres -> pos 2..10)
        /// ou un numéro technique LISA (7 à 9 chiffres). Zéro requête SQL nécessaire.
        /// </summary>
        public static bool TryDeriveNoCnt(
            string? contractReference,
            out long noCnt,
            out string jclContract)
        {
            return TryDeriveNoCnt(
                contractReference,
                allowTechnicalShortContract: true,
                out noCnt,
                out jclContract);
        }

        /// <summary>
        /// Déduit mathématiquement NO_CNT (et la clé JCL 9 chiffres) depuis un numéro de contrat externe (12 chiffres -> pos 2..10)
        /// ou un numéro technique LISA (7 à 9 chiffres lorsque <paramref name="allowTechnicalShortContract"/> vaut true).
        /// Zéro requête SQL nécessaire.
        /// </summary>
        public static bool TryDeriveNoCnt(
            string? contractReference,
            bool allowTechnicalShortContract,
            out long noCnt,
            out string jclContract)
        {
            noCnt = 0;
            jclContract = string.Empty;

            if (!TryGetCleanNumericContract(contractReference, out string digits))
            {
                return false;
            }

            if (digits.Length == 12)
            {
                string middle9 = digits.Substring(1, 9);
                if (long.TryParse(middle9, NumberStyles.None, CultureInfo.InvariantCulture, out noCnt) && noCnt > 0)
                {
                    jclContract = middle9;
                    return true;
                }

                return false;
            }

            if (allowTechnicalShortContract && digits.Length is >= 7 and <= 9)
            {
                if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out noCnt) && noCnt > 0)
                {
                    jclContract = digits;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Retourne les variantes canoniques SARGables d'un contrat externe 12 chiffres :
        /// ex. "182-2867013-42" et "182286701342".
        /// </summary>
        public static bool TryGetCanonicalExternalVariants(
            string? rawContract,
            out string formatted372,
            out string compact12,
            out string middle9)
        {
            formatted372 = string.Empty;
            compact12 = string.Empty;
            middle9 = string.Empty;

            if (!TryGetCleanNumericContract(rawContract, out string digits))
            {
                return false;
            }

            if (digits.Length == 12)
            {
                compact12 = digits;
                formatted372 = $"{digits.Substring(0, 3)}-{digits.Substring(3, 7)}-{digits.Substring(10, 2)}";
                middle9 = digits.Substring(1, 9);
                return true;
            }

            if (digits.Length == 9)
            {
                middle9 = digits;
            }

            return false;
        }

        /// <summary>
        /// Formate une référence de contrat pour soumission JCL / clé technique NO_CNT (9 chiffres si 12 chiffres 3-7-2).
        /// </summary>
        public static string FormatForJcl(string? rawContract)
        {
            if (TryDeriveNoCnt(rawContract, allowTechnicalShortContract: true, out _, out string jclContract))
            {
                return jclContract;
            }

            if (TryGetCleanNumericContract(rawContract, out string digits) && digits.Length > 0)
            {
                return digits;
            }

            return (rawContract ?? string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();
        }

        /// <summary>
        /// Formate un contrat 12 chiffres au format d'affichage 3-7-2 (XXX-XXXXXXX-XX).
        /// </summary>
        public static string FormatDisplay372(string? contract)
        {
            if (string.IsNullOrWhiteSpace(contract))
            {
                return "-";
            }

            if (TryGetCleanNumericContract(contract, out string digits) && digits.Length == 12)
            {
                return $"{digits.Substring(0, 3)}-{digits.Substring(3, 7)}-{digits.Substring(10, 2)}";
            }

            return contract.Trim();
        }
    }
}
