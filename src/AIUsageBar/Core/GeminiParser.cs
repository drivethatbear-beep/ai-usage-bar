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

        /// <summary>Maps <c>agy -p "/usage" --output-format json</c> output to a ServiceUsage when desktop Antigravity is closed.</summary>
        public static ServiceUsage ParseCli(string json, DateTimeOffset now)
        {
            Dictionary<string, object> root;
            try { root = Json.Parse(json) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            if (root == null || Json.Str(Json.Get(root, "status")) != "SUCCESS") return null;
            if (!(Json.Get(root, "command.data.groups") is List<object> groups)) return null;

            UsageWindow gemini = null, other = null;
            foreach (var g in groups)
            {
                var groupIsGemini = (Json.Str(Json.Get(g, "name")) ?? "").StartsWith("Gemini", StringComparison.OrdinalIgnoreCase);
                if (!(Json.Get(g, "buckets") is List<object> buckets)) continue;
                foreach (var b in buckets)
                {
                    if (!(b is Dictionary<string, object> bucket)) continue;
                    var w = WindowFrom(bucket, "remaining_fraction", "reset_time");
                    var id = Json.Str(Json.Get(bucket, "id")) ?? "";
                    if (groupIsGemini || id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                        gemini = Tighter(gemini, w, "GEM");
                    else
                        other = Tighter(other, w, "3P");
                }
            }
            if (gemini == null && other == null) return null;

            var u = new ServiceUsage
            {
                Service = "Gemini",
                Tag = "GM",
                Status = ServiceStatus.Ok,
                LastSuccess = now,
            };
            if (gemini != null) u.Windows.Add(gemini);
            if (other != null) u.Windows.Add(other);
            return u;
        }

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
