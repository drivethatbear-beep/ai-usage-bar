using System;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>Display formatting shared by parsers and UI. Output uses only pixel-font characters.</summary>
    internal static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"$20" for whole dollars, "$25.50" otherwise.</summary>
        public static string Money(double usd) =>
            "$" + (usd == Math.Floor(usd) ? usd.ToString("0", Inv) : usd.ToString("0.00", Inv));

        /// <summary>Always two decimals: "$3.00".</summary>
        public static string Usd2(double usd) => "$" + usd.ToString("0.00", Inv);

        /// <summary>Bar label: local "HH:mm" when the reset is within 24 h, else "M/d"; "" if already past.</summary>
        public static string ResetShort(DateTimeOffset reset, DateTimeOffset now)
        {
            if (reset <= now) return "";
            var local = reset.ToLocalTime();
            return reset - now < TimeSpan.FromDays(1) ? local.ToString("HH:mm", Inv) : local.ToString("M/d", Inv);
        }

        /// <summary>"오늘 20:09", "내일 01:59", "10월 7일 15:31" (local time).</summary>
        public static string ResetKo(DateTimeOffset reset, DateTimeOffset now)
        {
            var at = reset.ToLocalTime();
            var today = now.ToLocalTime().Date;
            var time = at.ToString("HH:mm", Inv);
            if (at.Date == today) return "오늘 " + time;
            if (at.Date == today.AddDays(1)) return "내일 " + time;
            return at.Month + "월 " + at.Day + "일 " + time;
        }

        /// <summary>"1분 이내", "41분", "2시간 41분", "3시간", "6일 23시간".</summary>
        public static string DurationKo(TimeSpan t)
        {
            if (t < TimeSpan.FromMinutes(1)) return "1분 이내";
            if (t < TimeSpan.FromHours(1)) return (int)t.TotalMinutes + "분";
            if (t < TimeSpan.FromDays(1)) return (int)t.TotalHours + "시간" + (t.Minutes > 0 ? " " + t.Minutes + "분" : "");
            return (int)t.TotalDays + "일 " + t.Hours + "시간";
        }

        /// <summary>Display form of a plan name: all-lower or all-upper words become Title Case; mixed case is kept.</summary>
        public static string PlanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw != raw.ToLowerInvariant() && raw != raw.ToUpperInvariant()) return raw;
            var words = raw.ToLowerInvariant().Split(' ');
            for (var i = 0; i < words.Length; i++)
                if (words[i].Length > 0) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        public static string Thousands(string number) =>
            long.TryParse(number, NumberStyles.Integer, Inv, out var n) ? n.ToString("#,0", Inv) : number;

        public static string Tokens(long n)
        {
            if (n < 1000) return n.ToString(Inv) + " TOK";
            if (n < 1000000) return (n / 1000.0).ToString("0.0", Inv) + "K TOK";
            return (n / 1000000.0).ToString("0.0", Inv) + "M TOK";
        }
    }
}
