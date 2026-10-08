using System;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>Subscription limit usage as reported by the Grok CLI (same numbers as grok.com → Usage).</summary>
    internal sealed class GrokBilling
    {
        public string Tier;
        public double UsedPercent;
        public string Label;
        public DateTimeOffset? ResetsAt;
    }

    internal static class GrokBillingParser
    {
        /// <summary>Parses a JSON-RPC response to <c>_x.ai/billing</c>; null on error or when no usage percent is present.</summary>
        public static GrokBilling Parse(string responseLine)
        {
            if (string.IsNullOrEmpty(responseLine)) return null;
            object root;
            try { root = Json.Parse(responseLine); }
            catch (FormatException) { return null; }

            // Zero values are omitted from the response (e.g. right after the weekly reset), so a known
            // billing period or tier with no percentage means 0 % used, not "no data".
            var used = Json.Num(Json.Get(root, "result.config.creditUsagePercent"));
            var hasPeriod = Json.Get(root, "result.config.currentPeriod") != null || Json.Get(root, "result.config.billingPeriodEnd") != null;
            if (used == null && !hasPeriod) return null;

            DateTimeOffset? end = null;
            var endText = Json.Str(Json.Get(root, "result.config.currentPeriod.end")) ?? Json.Str(Json.Get(root, "result.config.billingPeriodEnd"));
            if (endText != null && DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
                end = e;

            return new GrokBilling
            {
                Tier = Json.Str(Json.Get(root, "result.subscription_tier")),
                UsedPercent = used ?? 0,
                Label = LabelFor(Json.Str(Json.Get(root, "result.config.currentPeriod.type"))),
                ResetsAt = end,
            };
        }

        private static string LabelFor(string periodType)
        {
            switch (periodType)
            {
                case "USAGE_PERIOD_TYPE_MONTHLY": return "MO";
                case "USAGE_PERIOD_TYPE_DAILY": return "DY";
                default: return "WK";
            }
        }
    }
}
