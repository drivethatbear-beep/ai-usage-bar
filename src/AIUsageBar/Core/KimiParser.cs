using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Maps the Kimi Code api.kimi.ai/coding/v1/usages response to a ServiceUsage: the 5-hour window ("5H") and the
    /// monthly total ("MO"). Ratios are fractions used; when the 5-hour ratio is missing, the 300-minute entry
    /// of <c>limits</c> (used / limit) stands in.
    /// </summary>
    internal static class KimiParser
    {
        public static ServiceUsage Parse(string json, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = new ServiceUsage { Service = "Kimi", Tag = "KM", Status = ServiceStatus.Ok, LastSuccess = now };
            var five = Ratio(root, "usages.limit_5h", "5H") ?? FromLimits(root, 300, "5H");
            if (five != null) u.Windows.Add(five);
            var month = Ratio(root, "usages.limit_month_total", "MO");
            if (month != null) u.Windows.Add(month);
            return u;
        }

        private static UsageWindow Ratio(object root, string key, string label)
        {
            var used = Json.Num(Json.Get(root, key + ".used_ratio"));
            if (used == null) return null;
            return new UsageWindow { Label = label, UsedPercent = used.Value * 100, ResetsAt = Time(Json.Get(root, key + ".reset_time")) };
        }

        private static UsageWindow FromLimits(object root, int minutes, string label)
        {
            if (!(Json.Get(root, "limits") is List<object> limits)) return null;
            foreach (var l in limits)
            {
                var unit = Json.Str(Json.Get(l, "window.timeUnit")) ?? "";
                if (Json.Num(Json.Get(l, "window.duration")) != minutes || !unit.EndsWith("MINUTE", StringComparison.Ordinal)) continue;
                var limit = Json.Num(Json.Get(l, "detail.limit"));
                var used = Json.Num(Json.Get(l, "detail.used"));
                if (limit == null || used == null || limit.Value <= 0) continue;
                return new UsageWindow { Label = label, UsedPercent = used.Value / limit.Value * 100, ResetsAt = Time(Json.Get(l, "detail.resetTime")) };
            }
            return null;
        }

        private static DateTimeOffset? Time(object v)
        {
            var s = Json.Str(v);
            if (s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)) return r;
            return null;
        }
    }
}
