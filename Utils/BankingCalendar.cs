using System;

namespace AutoActivator.Utils
{
    /// <summary>
    /// Banking calendar used to compute business dates (e.g. Branch 23 consolidation date at J+2 bank business days).
    /// Closing days follow the TARGET2 calendar: weekends, 1 January, Good Friday, Easter Monday, 1 May, 25 and 26 December.
    /// </summary>
    public static class BankingCalendar
    {
        public static DateTime AddBankBusinessDays(DateTime start, int businessDays)
        {
            if (businessDays < 0) throw new ArgumentOutOfRangeException(nameof(businessDays));

            DateTime current = start.Date;
            int added = 0;

            while (added < businessDays)
            {
                current = current.AddDays(1);
                if (IsBankBusinessDay(current)) added++;
            }

            return current;
        }

        public static bool IsBankBusinessDay(DateTime date)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) return false;
            return !IsBankHoliday(date.Date);
        }

        private static bool IsBankHoliday(DateTime date)
        {
            if (date.Month == 1 && date.Day == 1) return true;
            if (date.Month == 5 && date.Day == 1) return true;
            if (date.Month == 12 && (date.Day == 25 || date.Day == 26)) return true;

            DateTime easter = GetEasterSunday(date.Year);
            return date == easter.AddDays(-2) || date == easter.AddDays(1);
        }

        /// <summary>
        /// Gregorian Easter Sunday (Meeus/Jones/Butcher algorithm).
        /// </summary>
        private static DateTime GetEasterSunday(int year)
        {
            int a = year % 19;
            int b = year / 100;
            int c = year % 100;
            int d = b / 4;
            int e = b % 4;
            int f = (b + 8) / 25;
            int g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4;
            int k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31;
            int day = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(year, month, day);
        }
    }
}