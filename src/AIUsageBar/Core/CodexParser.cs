using System;
using System.Collections.Generic;
using System.Linq;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Maps Codex rate limits to a ServiceUsage, from either the chatgpt.com wham/usage API
    /// or the last "rate_limits" event in a local Codex session log.
    /// </summary>
    internal static class CodexParser
    {
        private const double FiveHourSeconds = 18000;

        public static ServiceUsage ParseApi(string json, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = New(Json.Str(Json.Get(root, "plan_type")), now);
            foreach (var key in new[] { "rate_limit.primary_window", "rate_limit.secondary_window" })
            {
                var used = Json.Num(Json.Get(root, key + ".used_percent"));
                if (used == null) continue;
                var seconds = Json.Num(Json.Get(root, key + ".limit_window_seconds")) ?? double.MaxValue;
                u.Windows.Add(new UsageWindow
                {
                    Label = seconds <= FiveHourSeconds ? "5H" : "WK",
                    UsedPercent = used.Value,
                    ResetsAt = Unix(Json.Num(Json.Get(root, key + ".reset_at"))),
                });
            }
            Finish(u, Json.Str(Json.Get(root, "credits.balance")));
            return u;
        }

        /// <summary>
        /// Uses the newest line carrying a usable rate_limits event, skipping truncated lines and
        /// "rate_limits": null. Returns null when there is none.
        /// </summary>
        public static ServiceUsage ParseSessionTail(IEnumerable<string> lines, DateTimeOffset now)
        {
            foreach (var line in lines.Reverse())
            {
                if (line == null || !line.Contains("\"rate_limits\"")) continue;
                var u = ParseRateLimitsLine(line, now);
                if (u != null && u.Windows.Count > 0) return u;
            }
            return null;
        }

        private static ServiceUsage ParseRateLimitsLine(string line, DateTimeOffset now)
        {
            object rl;
            try { rl = Json.Get(Json.Parse(line), "payload.rate_limits"); }
            catch (FormatException) { return null; }
            if (rl == null) return null;

            var u = New(Json.Str(Json.Get(rl, "plan_type")), now);
            foreach (var key in new[] { "primary", "secondary" })
            {
                var used = Json.Num(Json.Get(rl, key + ".used_percent"));
                if (used == null) continue;
                var minutes = Json.Num(Json.Get(rl, key + ".window_minutes")) ?? double.MaxValue;
                u.Windows.Add(new UsageWindow
                {
                    Label = minutes * 60 <= FiveHourSeconds ? "5H" : "WK",
                    UsedPercent = used.Value,
                    ResetsAt = Unix(Json.Num(Json.Get(rl, key + ".resets_at"))),
                });
            }
            Finish(u, Json.Str(Json.Get(rl, "credits.balance")));
            return u;
        }

        private static ServiceUsage New(string plan, DateTimeOffset now) => new ServiceUsage
        {
            Service = "Codex",
            Tag = "CX",
            Status = ServiceStatus.Ok,
            Plan = string.IsNullOrEmpty(plan) ? null : plan.ToUpperInvariant(),
            LastSuccess = now,
        };

        private static void Finish(ServiceUsage u, string credits)
        {
            u.Windows.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label)); // "5H" before "WK"
            if (!string.IsNullOrEmpty(credits)) u.AddDetail("CREDITS", credits);
        }

        private static DateTimeOffset? Unix(double? value)
        {
            if (!value.HasValue || value.Value <= 0) return null;
            var seconds = value.Value > 1e11 ? value.Value / 1000 : value.Value; // tolerate milliseconds
            if (seconds > 253402300799) return null;
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
    }
}
