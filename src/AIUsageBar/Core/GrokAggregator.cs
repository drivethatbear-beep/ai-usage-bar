using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIUsageBar.Core
{
    /// <summary>
    /// Sums Grok CLI per-turn usage (~/.grok/sessions/**/usage.json) into this week's cost and
    /// today's tokens. Files are re-parsed only when their modification time changes.
    /// </summary>
    internal sealed class GrokAggregator
    {
        private const double TicksPerUsd = 1e10;

        private struct Turn
        {
            public DateTimeOffset EndedAt;
            public double CostUsd;
            public long Tokens;
        }

        private sealed class Entry
        {
            public DateTime Mtime;
            public List<Turn> Turns;
        }

        private readonly Func<string, string> _readFile;
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        internal int ParseCount;

        public GrokAggregator(Func<string, string> readFile)
        {
            _readFile = readFile;
        }

        public void Update(IEnumerable<(string path, DateTime mtimeUtc)> files)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, mtime) in files)
            {
                seen.Add(path);
                if (_cache.TryGetValue(path, out var e) && e.Mtime == mtime) continue;
                var turns = TryParse(path);
                if (turns == null) { _cache.Remove(path); continue; } // retry on next Update
                _cache[path] = new Entry { Mtime = mtime, Turns = turns };
            }
            var stale = new List<string>();
            foreach (var k in _cache.Keys) if (!seen.Contains(k)) stale.Add(k);
            foreach (var k in stale) _cache.Remove(k);
            _anyFiles = seen.Count > 0;
        }

        private bool _anyFiles;

        private List<Turn> TryParse(string path)
        {
            try
            {
                ParseCount++;
                var root = Json.Parse(_readFile(path));
                var list = new List<Turn>();
                if (Json.Get(root, "turns") is List<object> turns)
                {
                    foreach (var t in turns)
                    {
                        var ended = Json.Str(Json.Get(t, "endedAt"));
                        if (ended == null || !DateTimeOffset.TryParse(ended, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                            continue;
                        list.Add(new Turn
                        {
                            EndedAt = at,
                            CostUsd = (Json.Num(Json.Get(t, "costUsdTicks")) ?? 0) / TicksPerUsd,
                            Tokens = (long)(Json.Num(Json.Get(t, "totalTokens")) ?? 0),
                        });
                    }
                }
                return list;
            }
            catch (Exception ex) when (ex is FormatException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public ServiceUsage Build(DateTimeOffset now, double weeklyBudgetUsd)
        {
            var u = new ServiceUsage { Service = "Grok", Tag = "GK", LastSuccess = now };
            if (!_anyFiles)
            {
                u.Status = ServiceStatus.NotInstalled;
                return u;
            }
            u.Status = ServiceStatus.Ok;

            var today = now.ToLocalTime().Date;
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
            var weekStartOffset = new DateTimeOffset(DateTime.SpecifyKind(weekStart, DateTimeKind.Local));
            var todayOffset = new DateTimeOffset(DateTime.SpecifyKind(today, DateTimeKind.Local));

            double weekCost = 0;
            long todayTokens = 0;
            foreach (var e in _cache.Values)
                foreach (var t in e.Turns)
                {
                    if (t.EndedAt >= weekStartOffset) weekCost += t.CostUsd;
                    if (t.EndedAt >= todayOffset) todayTokens += t.Tokens;
                }

            if (weeklyBudgetUsd > 0)
            {
                u.Windows.Add(new UsageWindow
                {
                    Label = "WK",
                    UsedPercent = weekCost / weeklyBudgetUsd * 100,
                    ResetsAt = new DateTimeOffset(DateTime.SpecifyKind(weekStart.AddDays(7), DateTimeKind.Local)),
                });
                u.AddDetail("WEEK", Fmt.Usd2(weekCost) + " / " + Fmt.Money(weeklyBudgetUsd));
                u.AddDetail("LEFT", Fmt.Usd2(Math.Max(0, weeklyBudgetUsd - weekCost)));
            }
            else
            {
                u.AddDetail("WEEK", Fmt.Usd2(weekCost));
            }
            u.AddDetail("TODAY", Fmt.Tokens(todayTokens));
            return u;
        }
    }
}
