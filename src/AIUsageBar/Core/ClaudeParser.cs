using System;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>Maps the api.anthropic.com/api/oauth/usage response to a ServiceUsage.</summary>
    internal static class ClaudeParser
    {
        public static ServiceUsage Parse(string json, string subscriptionType, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = new ServiceUsage
            {
                Service = "Claude",
                Tag = "CL",
                Status = ServiceStatus.Ok,
                Plan = string.IsNullOrEmpty(subscriptionType) ? null : subscriptionType.ToUpperInvariant(),
                LastSuccess = now,
            };
            AddWindow(u, root, "five_hour", "5H");
            AddWindow(u, root, "seven_day", "WK");

            if (Json.Get(root, "extra_usage.is_enabled") is bool on && on)
            {
                // Credits are reported in cents (decimal_places: 2).
                var used = Json.Num(Json.Get(root, "extra_usage.used_credits")) ?? 0;
                var limit = Json.Num(Json.Get(root, "extra_usage.monthly_limit")) ?? 0;
                u.AddDetail("EXTRA", Fmt.Money(used / 100) + "/" + Fmt.Money(limit / 100));
            }
            return u;
        }

        private static void AddWindow(ServiceUsage u, object root, string key, string label)
        {
            var used = Json.Num(Json.Get(root, key + ".utilization"));
            if (used == null) return;
            DateTimeOffset? resets = null;
            var s = Json.Str(Json.Get(root, key + ".resets_at"));
            if (s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r))
                resets = r;
            u.Windows.Add(new UsageWindow { Label = label, UsedPercent = used.Value, ResetsAt = resets });
        }
    }
}
