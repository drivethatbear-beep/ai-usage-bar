using System;
using System.Globalization;
using System.Text;

namespace AIUsageBar.Core
{
    /// <summary>Maps cursor.com/api/usage-summary to a ServiceUsage (same numbers as Cursor → Settings → Usage).</summary>
    internal static class CursorParser
    {
        public static ServiceUsage Parse(string json, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = new ServiceUsage
            {
                Service = "Cursor",
                Tag = "CU",
                Status = ServiceStatus.Ok,
                Plan = Json.Str(Json.Get(root, "membershipType")),
                LastSuccess = now,
            };
            if (Json.Get(root, "isUnlimited") is bool unlimited && unlimited)
            {
                u.AddDetail("UNLIMITED", "무제한");
                return u;
            }

            DateTimeOffset? end = null;
            var endText = Json.Str(Json.Get(root, "billingCycleEnd"));
            if (endText != null && DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
                end = e;

            var total = Json.Num(Json.Get(root, "individualUsage.plan.totalPercentUsed"));
            if (total != null) u.Windows.Add(new UsageWindow { Label = "TOT", UsedPercent = total.Value, ResetsAt = end });
            var api = Json.Num(Json.Get(root, "individualUsage.plan.apiPercentUsed"));
            if (api != null) u.Windows.Add(new UsageWindow { Label = "API", UsedPercent = api.Value, ResetsAt = end });
            return u;
        }

        /// <summary>The user id Cursor's web session cookie expects: the part of the JWT "sub" after the last '|'.</summary>
        public static string UserIdFromJwt(string jwt)
        {
            var parts = jwt?.Split('.');
            if (parts == null || parts.Length < 2) return null;
            try
            {
                var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var sub = Json.Str(Json.Get(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))), "sub"));
                if (string.IsNullOrEmpty(sub)) return null;
                var i = sub.LastIndexOf('|');
                return i >= 0 ? sub.Substring(i + 1) : sub;
            }
            catch (Exception ex) when (ex is FormatException)
            {
                return null;
            }
        }
    }
}
