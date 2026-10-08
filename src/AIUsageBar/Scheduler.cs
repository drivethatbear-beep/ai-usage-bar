using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;

namespace AIUsageBar
{
    /// <summary>
    /// Polls each provider on its own loop. On failure the last good values are kept and only the
    /// status changes. A 429 doubles that provider's delay (max 600 s) until it succeeds again.
    /// </summary>
    internal sealed class Scheduler : IDisposable
    {
        public const int MaxBackoffSeconds = 600;
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

        private readonly List<IUsageProvider> _providers;
        private readonly Func<int> _refreshSeconds;
        private readonly Dictionary<string, ServiceUsage> _lastGood = new Dictionary<string, ServiceUsage>();
        private readonly Dictionary<string, int> _backoff = new Dictionary<string, int>();
        private readonly Dictionary<string, SemaphoreSlim> _gates = new Dictionary<string, SemaphoreSlim>();
        private readonly Dictionary<string, CancellationTokenSource> _wakers = new Dictionary<string, CancellationTokenSource>();
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly List<Timer> _debouncers = new List<Timer>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _lock = new object();

        public event Action<ServiceUsage> Updated;

        public Scheduler(IEnumerable<IUsageProvider> providers, Func<int> refreshSeconds)
        {
            _providers = providers.ToList();
            _refreshSeconds = refreshSeconds;
            foreach (var p in _providers)
            {
                _backoff[p.Tag] = 0;
                _gates[p.Tag] = new SemaphoreSlim(1, 1);
            }
        }

        public int NextDelaySeconds(string tag)
        {
            lock (_lock)
            {
                var b = _backoff.TryGetValue(tag, out var v) ? v : 0;
                return b > 0 ? b : Math.Max(Settings.MinRefreshSeconds, _refreshSeconds());
            }
        }

        public void Start()
        {
            foreach (var p in _providers) _ = LoopAsync(p);
        }

        public Task RefreshNowAsync() => Task.WhenAll(_providers.Select(RunOnceAsync));

        /// <summary>Re-polls one provider soon after files under <paramref name="dir"/> change.</summary>
        public void Watch(string dir, string filter, string tag)
        {
            if (!Directory.Exists(dir)) return;
            var p = _providers.FirstOrDefault(x => x.Tag == tag);
            if (p == null) return;
            var timer = new Timer(_ => _ = RunOnceAsync(p), null, Timeout.Infinite, Timeout.Infinite);
            var w = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler kick = (s, e) => timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            w.Changed += kick;
            w.Created += kick;
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
            _debouncers.Add(timer);
        }

        private async Task LoopAsync(IUsageProvider p)
        {
            while (!_cts.IsCancellationRequested)
            {
                await RunOnceAsync(p).ConfigureAwait(false);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(NextDelaySeconds(p.Tag)), _cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private async Task RunOnceAsync(IUsageProvider p)
        {
            var gate = _gates[p.Tag];
            if (!await gate.WaitAsync(0).ConfigureAwait(false)) return; // a fetch is already running
            try
            {
                ServiceUsage r;
                try
                {
                    r = await p.FetchAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    r = new ServiceUsage { Tag = p.Tag, Status = ServiceStatus.Error, Error = "ERROR" };
                }
                // Apply must run even with no subscribers: ?. would skip evaluating its argument.
                var merged = Apply(p.Tag, r);
                Updated?.Invoke(merged);
            }
            finally
            {
                gate.Release();
            }
        }

        private ServiceUsage Apply(string tag, ServiceUsage r)
        {
            lock (_lock)
            {
                // A fallback result can be Ok and still carry the API's 429.
                if (r.Error == "RATE LIMIT")
                {
                    var cur = _backoff[tag] > 0 ? _backoff[tag] : Math.Max(Settings.MinRefreshSeconds, _refreshSeconds());
                    _backoff[tag] = Math.Min(MaxBackoffSeconds, cur * 2);
                }
                else if (r.Status == ServiceStatus.Ok)
                {
                    _backoff[tag] = 0;
                }

                _lastGood.TryGetValue(tag, out var last);
                var olderThanLast = last != null && r.LastSuccess.HasValue && last.LastSuccess.HasValue && r.LastSuccess.Value < last.LastSuccess.Value;
                if (r.Status == ServiceStatus.Ok && !olderThanLast)
                {
                    _lastGood[tag] = r;
                    return r;
                }
                if (r.Status == ServiceStatus.Ok)
                {
                    // An older snapshot (e.g. a local log) must not replace a newer API value.
                    r = new ServiceUsage { Tag = tag, Status = ServiceStatus.Error, Error = r.Error ?? "STALE" };
                }
                if (r.Status == ServiceStatus.NotInstalled || last == null) return r;
                return new ServiceUsage
                {
                    Service = last.Service,
                    Tag = tag,
                    Status = r.Status,
                    Error = r.Error,
                    Plan = last.Plan,
                    Windows = last.Windows,
                    Details = last.Details,
                    LastSuccess = last.LastSuccess,
                };
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var w in _watchers) w.Dispose();
            foreach (var t in _debouncers) t.Dispose();
            foreach (var p in _providers) (p as IDisposable)?.Dispose(); // e.g. the resident grok agent
        }
    }
}
