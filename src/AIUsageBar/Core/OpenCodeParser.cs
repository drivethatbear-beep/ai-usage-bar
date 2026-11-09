using System;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Maps the opencode.ai/zen/go/v1/usage response to a ServiceUsage: the rolling 5-hour window ("5H"), the week
    /// ("WK") and the month ("MO"). <c>percent</c> is already 0-100 (1 means 1 % used).
    /// </summary>
    internal static class OpenCodeParser
    {
        private static readonly (string key, string label)[] Windows = { ("rolling", "5H"), ("weekly", "WK"), ("monthly", "MO") };

        /// <summary>Throws FormatException when the body has no usable window.</summary>
        public static ServiceUsage Parse(string json, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = new ServiceUsage { Service = "OpenCode Go", Tag = "OC", Status = ServiceStatus.Ok, LastSuccess = now };
            foreach (var (key, label) in Windows)
            {
                var pct = Json.Num(Json.Get(root, "usage." + key + ".percent"));
                if (pct == null) continue;
                u.Windows.Add(new UsageWindow
                {
                    Label = label,
                    UsedPercent = Math.Max(0, Math.Min(100, pct.Value)),
                    ResetsAt = Time(Json.Get(root, "usage." + key + ".resetsAt")),
                });
                var status = Json.Str(Json.Get(root, "usage." + key + ".status"));
                if (!string.IsNullOrEmpty(status) && status != "ok") u.AddDetail("LIMIT " + label, status);
            }
            if (u.Windows.Count == 0) throw new FormatException("no OpenCode Go usage windows");
            return u;
        }

        private static DateTimeOffset? Time(object v)
        {
            var s = Json.Str(v);
            if (s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)) return r;
            return null;
        }
    }
}
