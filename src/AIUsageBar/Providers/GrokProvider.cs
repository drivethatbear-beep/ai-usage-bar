using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Grok: subscription limit usage from the resident Grok CLI (<c>_x.ai/billing</c>, the same figure as
    /// grok.com → Usage), plus API-equivalent cost and today's tokens from ~/.grok/sessions/**/usage.json.
    /// When the limit is unavailable, falls back to the user's weekly budget. On an X Subscription unified plan Grok,
    /// Grok Build and Cursor share that one weekly pool, so the item is shown as "Grok · Cursor" with Cursor's tier.
    /// </summary>
    internal sealed class GrokProvider : IUsageProvider, IDisposable
    {
        private static readonly TimeSpan MinBillingInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BillingStaleAfter = TimeSpan.FromMinutes(10);

        private readonly Func<double> _budget;
        private readonly IGrokRpc _rpc;
        private readonly Func<bool> _cliInstalled;
        private readonly Func<CancellationToken, Task<string>> _cursorTier;
        private readonly GrokAggregator _agg = new GrokAggregator(Io.ReadShared);
        private GrokBilling _billing;
        private DateTimeOffset _billingAt = DateTimeOffset.MinValue;
        private DateTimeOffset _billingOkAt = DateTimeOffset.MinValue;

        public GrokProvider(string homeDir, Func<double> weeklyBudgetUsd, IGrokRpc rpc = null, Func<bool> cliInstalled = null, Func<string> cursorTier = null)
        {
            // Like cliInstalled: with an injected rpc (tests) the real Cursor plan is not consulted.
            if (cursorTier != null) _cursorTier = _ => Task.FromResult(cursorTier());
            else if (rpc != null) _cursorTier = _ => Task.FromResult<string>(null);
            else _cursorTier = new CursorPlanSource().TierAsync;
            var root = Path.Combine(homeDir ?? Io.DefaultHome, ".grok");
            SessionsDir = Path.Combine(root, "sessions");
            var exe = Path.Combine(root, "bin", "grok.exe");
            _budget = weeklyBudgetUsd;
            _rpc = rpc ?? new GrokAgent(exe);
            // An injected rpc is available by definition; the real agent needs the CLI on disk.
            _cliInstalled = cliInstalled ?? (rpc != null ? (Func<bool>)(() => true) : () => File.Exists(exe));
        }

        public string SessionsDir { get; }

        public string Tag => "GK";

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.Now;
            await RefreshBillingAsync(now, ct).ConfigureAwait(false);
            var billing = _billing != null && now - _billingOkAt <= BillingStaleAfter ? _billing : null;

            var u = await Task.Run(() =>
            {
                lock (_agg)
                {
                    _agg.Update(ListFiles());
                    return _agg.Build(now, billing != null ? 0 : _budget());
                }
            }, ct).ConfigureAwait(false);
            var shared = await SharedTierAsync(ct).ConfigureAwait(false);
            if (billing == null) return Merge(u, shared);

            // The subscription limit replaces the budget gauge; the $ figure stays as information.
            var result = new ServiceUsage
            {
                Service = "Grok",
                Tag = Tag,
                Status = ServiceStatus.Ok,
                Plan = string.IsNullOrEmpty(billing.Tier) ? null : billing.Tier,
                LastSuccess = _billingOkAt,
            };
            result.Windows.Add(new UsageWindow { Label = billing.Label, UsedPercent = billing.UsedPercent, ResetsAt = billing.ResetsAt });
            var week = u.Detail("WEEK");
            if (week != null) result.AddDetail("API EQUIV WK", week);
            var today = u.Detail("TODAY");
            if (today != null) result.AddDetail("TODAY", today);
            return Merge(result, shared);
        }

        public const string SharedName = "Grok · Cursor";

        private async Task<string> SharedTierAsync(CancellationToken ct)
        {
            try { return await _cursorTier(ct).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !ct.IsCancellationRequested) { return null; }
        }

        /// <summary>Marks the item as the shared Grok · Grok Build · Cursor pool when Cursor is on a unified plan.</summary>
        private static ServiceUsage Merge(ServiceUsage u, string cursorTier)
        {
            if (string.IsNullOrEmpty(cursorTier)) return u;
            u.Service = SharedName;
            u.Plan = cursorTier;
            u.AddDetail("SHARED", "Grok · Grok Build · Cursor");
            return u;
        }

        private async Task RefreshBillingAsync(DateTimeOffset now, CancellationToken ct)
        {
            if (now - _billingAt < MinBillingInterval || !_cliInstalled()) return;
            _billingAt = now;
            try
            {
                var parsed = GrokBillingParser.Parse(await _rpc.CallAsync("_x.ai/billing", ct).ConfigureAwait(false));
                if (parsed == null) return;
                _billing = parsed;
                _billingOkAt = now;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Keep the last value until it goes stale; the budget fallback covers the rest.
            }
        }

        private IEnumerable<(string, DateTime)> ListFiles()
        {
            var list = new List<(string, DateTime)>();
            try
            {
                if (!Directory.Exists(SessionsDir)) return list;
                foreach (var f in new DirectoryInfo(SessionsDir).EnumerateFiles("usage.json", SearchOption.AllDirectories))
                    list.Add((f.FullName, f.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return list;
        }

        public void Dispose() => (_rpc as IDisposable)?.Dispose();
    }
}
