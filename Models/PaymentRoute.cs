using System;
using System.Collections.Generic;

namespace AutoActivator.Models
{
    /// <summary>
    /// Source unique de vérité pour le routage de paiement LISA/ELIA (BUCP / CMDPMT).
    /// Centralise la matrice métier :
    /// - BANK (01, 02, 04, 05, 13, 18, 22, 23, 24 / C02) -> BUCP = 00382, CMDPMT = 8
    /// - BROKER_NEW_GEN (03, 06..12, 14..17, 19..21 / C01, C03) -> BUCP = 80819, CMDPMT = H
    /// </summary>
    public sealed record PaymentRoute(
        string ChannelCode,
        PreActivationBusinessChannel BusinessChannel,
        string BusinessChannelName,
        string Bucp,
        string Cmdpmt,
        string RuleDescription)
    {
        private static readonly HashSet<string> BankCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "01", "02", "04", "05", "13", "18", "22", "23", "24",
            "C02", "BANK", "BANQUE", "BNK", "00382", "382"
        };

        private static readonly HashSet<string> BrokerCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "03", "06", "07", "08", "09", "10", "11", "12",
            "14", "15", "16", "17", "19", "20", "21",
            "C01", "C03", "BROKER", "COURTIER", "BRK", "BROKER_NEW_GEN", "80819"
        };

        public static readonly PaymentRoute Bank = new(
            ChannelCode: "BANK",
            BusinessChannel: PreActivationBusinessChannel.Bank,
            BusinessChannelName: "BANK",
            Bucp: "00382",
            Cmdpmt: "8",
            RuleDescription: "BANK -> BUCP=00382, CMDPMT=8");

        public static readonly PaymentRoute BrokerNewGen = new(
            ChannelCode: "BROKER_NEW_GEN",
            BusinessChannel: PreActivationBusinessChannel.BrokerNewGeneration,
            BusinessChannelName: "BROKER_NEW_GEN",
            Bucp: "80819",
            Cmdpmt: "H",
            RuleDescription: "BROKER_NEW_GEN -> BUCP=80819, CMDPMT=H");

        public static bool TryFromChannel(string? channelCode, out PaymentRoute? route)
        {
            string normalized = (channelCode ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length == 1 && char.IsDigit(normalized[0]))
            {
                normalized = "0" + normalized;
            }

            if (BankCodes.Contains(normalized))
            {
                route = Bank with { ChannelCode = normalized };
                return true;
            }

            if (BrokerCodes.Contains(normalized))
            {
                route = BrokerNewGen with { ChannelCode = normalized };
                return true;
            }

            route = null;
            return false;
        }

        public static bool TryFromBucpAndCmd(string? bucp, string? cmdpmt, out PaymentRoute? route)
        {
            string normalizedBucp = (bucp ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(normalizedBucp))
            {
                normalizedBucp = normalizedBucp.PadLeft(5, '0');
            }

            string normalizedCmd = (cmdpmt ?? string.Empty).Trim().ToUpperInvariant();

            if (normalizedBucp == Bank.Bucp && normalizedCmd == Bank.Cmdpmt)
            {
                route = Bank;
                return true;
            }

            if (normalizedBucp == BrokerNewGen.Bucp && normalizedCmd == BrokerNewGen.Cmdpmt)
            {
                route = BrokerNewGen;
                return true;
            }

            route = null;
            return false;
        }
    }
}
