using System;
using System.Collections.Generic;

namespace AIUsageBar.Core
{
    internal enum ServiceStatus { Ok, NotInstalled, NeedsLogin, Error }

    internal enum Severity { Normal, Low, Critical }

    /// <summary>One rate-limit window (e.g. 5-hour or weekly). Stores used %, exposes remaining %.</summary>
    internal sealed class UsageWindow
    {
        public string Label;
        public double UsedPercent;
        public DateTimeOffset? ResetsAt;

        public double RemainingAt(DateTimeOffset now)
        {
            if (ResetsAt.HasValue && ResetsAt.Value <= now) return 100;
            return Math.Max(0, Math.Min(100, 100 - UsedPercent));
        }

        public Severity SeverityAt(DateTimeOffset now)
        {
            var r = RemainingAt(now);
            if (r < 5) return Severity.Critical;
            if (r <= 20) return Severity.Low;
            return Severity.Normal;
        }

        public int FilledDots(DateTimeOffset now) =>
            (int)Math.Round(RemainingAt(now) / 10, MidpointRounding.AwayFromZero);
    }

    internal sealed class ServiceUsage
    {
        public string Service;
        public string Tag;
        public ServiceStatus Status;
        public string Plan;
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public List<KeyValuePair<string, string>> Details = new List<KeyValuePair<string, string>>();
        public DateTimeOffset? LastSuccess;
        public string Error;

        public void AddDetail(string key, string value) =>
            Details.Add(new KeyValuePair<string, string>(key, value));

        public string Detail(string key)
        {
            foreach (var kv in Details) if (kv.Key == key) return kv.Value;
            return null;
        }
    }
}
