using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Maps Antigravity's GetUserStatus to a ServiceUsage. Each model carries a quota (remaining fraction and reset
    /// time); models share pools, so the Gemini models become one window ("GEM") and the other providers' models
    /// offered in Antigravity (Claude, GPT-OSS) another ("3P"), each showing its tightest model. The bar shows only
    /// the Gemini pool, the card both.
    /// </summary>
    internal static class GeminiParser
    {
        public static ServiceUsage Parse(string json, DateTimeOffset now)
        {
            var root = Json.Parse(json);
            var u = new ServiceUsage
            {
                Service = "Gemini",
                Tag = "GM",
                Status = ServiceStatus.Ok,
                Plan = Json.Str(Json.Get(root, "userStatus.userTier.name")) ?? Json.Str(Json.Get(root, "userStatus.planStatus.planInfo.planName")),
                LastSuccess = now,
            };

            UsageWindow gemini = null, other = null;
            if (Json.Get(root, "userStatus.cascadeModelConfigData.clientModelConfigs") is List<object> models)
                foreach (var m in models)
                {
                    if (!(Json.Get(m, "quotaInfo") is Dictionary<string, object> quota)) continue;
                    var w = WindowFrom(quota, "remainingFraction", "resetTime");
                    if (IsGemini(m)) gemini = Tighter(gemini, w, "GEM");
                    else other = Tighter(other, w, "3P");
                }
            if (gemini != null) u.Windows.Add(gemini);
            if (other != null) u.Windows.Add(other);
            return u;
        }

        /// <summary>Maps Antigravity's <c>RetrieveUserQuotaSummary</c> (5-hour + weekly buckets) to a ServiceUsage.</summary>
        public static ServiceUsage ParseSummary(string json, string plan, DateTimeOffset now)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(json) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            if (!(Json.Get(root, "response.groups") is List<object> groups)) return null;
            return ParseGroups(groups, plan, now, camelCase: true);
        }

        /// <summary>Maps <c>agy -p "/usage" --output-format json</c> output to a ServiceUsage when desktop Antigravity is closed.</summary>
        public static ServiceUsage ParseCli(string json, DateTimeOffset now)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(json) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            if (root == null || Json.Str(Json.Get(root, "status")) != "SUCCESS") return null;
            if (!(Json.Get(root, "command.data.groups") is List<object> groups)) return null;
            return ParseGroups(groups, null, now, camelCase: false);
        }

        private static ServiceUsage ParseGroups(List<object> groups, string plan, DateTimeOffset now, bool camelCase)
        {
            var nameKey = camelCase ? "displayName" : "name";
            var idKey = camelCase ? "bucketId" : "id";
            var fracKey = camelCase ? "remainingFraction" : "remaining_fraction";
            var resetKey = camelCase ? "resetTime" : "reset_time";

            UsageWindow gem5h = null, gemWk = null, gemOther = null, thirdParty = null;
            foreach (var g in groups)
            {
                var groupIsGemini = (Json.Str(Json.Get(g, nameKey)) ?? "").StartsWith("Gemini", StringComparison.OrdinalIgnoreCase);
                if (!(Json.Get(g, "buckets") is List<object> buckets)) continue;
                foreach (var b in buckets)
                {
                    if (!(b is Dictionary<string, object> bucket)) continue;
                    var w = WindowFrom(bucket, fracKey, resetKey);
                    var id = Json.Str(Json.Get(bucket, idKey)) ?? "";
                    var win = Json.Str(Json.Get(bucket, "window")) ?? "";
                    if (groupIsGemini || id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsFiveHour(win, id)) gem5h = Tighter(gem5h, w, "5H");
                        else if (IsWeekly(win, id)) gemWk = Tighter(gemWk, w, "WK");
                        else gemOther = Tighter(gemOther, w, "GEM");
                    }
                    else
                    {
                        thirdParty = Tighter(thirdParty, w, "3P");
                    }
                }
            }
            if (gem5h == null && gemWk == null && gemOther == null && thirdParty == null) return null;

            var u = new ServiceUsage
            {
                Service = "Gemini",
                Tag = "GM",
                Status = ServiceStatus.Ok,
                Plan = plan,
                LastSuccess = now,
            };
            if (gem5h != null) u.Windows.Add(gem5h);
            if (gemWk != null) u.Windows.Add(gemWk);
            if (gem5h == null && gemWk == null && gemOther != null) u.Windows.Add(gemOther);
            if (thirdParty != null) u.Windows.Add(thirdParty);
            return u;
        }

        private static bool IsFiveHour(string window, string id) =>
            window.Equals("5h", StringComparison.OrdinalIgnoreCase) || id.EndsWith("-5h", StringComparison.OrdinalIgnoreCase);

        private static bool IsWeekly(string window, string id) =>
            window.Equals("weekly", StringComparison.OrdinalIgnoreCase) || id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase);

        private static UsageWindow WindowFrom(Dictionary<string, object> quota, string fractionKey, string resetKey)
        {
            // Protobuf JSON omits zero values: no fraction next to a quota means none is left.
            var remaining = Math.Max(0, Math.Min(1, Json.Num(Json.Get(quota, fractionKey)) ?? 0));
            DateTimeOffset? reset = null;
            var text = Json.Str(Json.Get(quota, resetKey));
            if (text != null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r))
                reset = r;
            return new UsageWindow { UsedPercent = (1 - remaining) * 100, ResetsAt = reset };
        }

        private static bool IsGemini(object model)
        {
            var id = Json.Str(Json.Get(model, "modelId")) ?? "";
            var label = Json.Str(Json.Get(model, "label")) ?? "";
            return id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase);
        }

        private static UsageWindow Tighter(UsageWindow current, UsageWindow candidate, string label)
        {
            candidate.Label = label;
            return current == null || candidate.UsedPercent > current.UsedPercent ? candidate : current;
        }
    }
}
