using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Queries chatgpt.com wham/usage with the Codex CLI token (read-only). Falls back to the
    /// last rate_limits event in the newest local session log when the API is unavailable.
    /// </summary>
    internal sealed class CodexProvider : IUsageProvider
    {
        private const string Url = "https://chatgpt.com/backend-api/wham/usage";
        private const int SessionFilesToScan = 5;
        private readonly string _authPath;
        private readonly string _sessionsDir;
        private readonly HttpClient _http;

        public CodexProvider(string homeDir = null, HttpMessageHandler handler = null)
        {
            var root = Path.Combine(homeDir ?? Io.DefaultHome, ".codex");
            _authPath = Path.Combine(root, "auth.json");
            _sessionsDir = Path.Combine(root, "sessions");
            _http = Io.Client(handler);
        }

        public string Tag => "CX";

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            string token = null, account = null;
            try
            {
                var auth = Json.Parse(Io.ReadShared(_authPath));
                token = Json.Str(Json.Get(auth, "tokens.access_token"));
                account = Json.Str(Json.Get(auth, "tokens.account_id"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
            }

            ServiceUsage apiResult = null;
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    var r = await Io.GetAsync(_http, Url, token, h =>
                    {
                        if (!string.IsNullOrEmpty(account)) h.Add("chatgpt-account-id", account);
                    }, ct).ConfigureAwait(false);
                    if (r.Status >= 200 && r.Status <= 299) return CodexParser.ParseApi(r.Body, DateTimeOffset.Now);
                    apiResult = Io.Failure("Codex", Tag, r.Status);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
                {
                    apiResult = Io.Error("Codex", Tag, ex);
                }
            }

            var local = FromSessions();
            if (local != null)
            {
                // Status stays Ok (the numbers are real) but the API failure travels along so the
                // scheduler can back off on 429 and prefer a newer value it already has.
                local.Error = apiResult == null ? null : apiResult.Status == ServiceStatus.NeedsLogin ? "LOGIN" : apiResult.Error;
                local.AddDetail("SOURCE", "LOCAL");
                return local;
            }
            return apiResult ?? new ServiceUsage { Service = "Codex", Tag = Tag, Status = ServiceStatus.NotInstalled };
        }

        private ServiceUsage FromSessions()
        {
            try
            {
                if (!Directory.Exists(_sessionsDir)) return null;
                var newest = new DirectoryInfo(_sessionsDir)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(SessionFilesToScan);
                foreach (var f in newest)
                {
                    var u = TailUsage(f);
                    if (u != null)
                    {
                        // Local data is only as fresh as the log line that produced it.
                        u.LastSuccess = new DateTimeOffset(f.LastWriteTimeUtc);
                        return u;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return null;
        }

        // Session logs reach hundreds of MB: read only the tail, widening once, and remember the
        // matching line per (path, mtime, length) so unchanged files are not re-read.
        private static readonly int[] TailWindows = { 256 * 1024, 4 * 1024 * 1024 };
        private readonly Dictionary<string, (DateTime mtime, long length, string line)> _tailCache =
            new Dictionary<string, (DateTime, long, string)>(StringComparer.OrdinalIgnoreCase);

        private ServiceUsage TailUsage(FileInfo f)
        {
            var now = DateTimeOffset.Now;
            if (_tailCache.TryGetValue(f.FullName, out var c) && c.mtime == f.LastWriteTimeUtc && c.length == f.Length)
                return c.line == null ? null : CodexParser.ParseSessionTail(new[] { c.line }, now);

            string found = null;
            foreach (var window in TailWindows)
            {
                var lines = Io.ReadTailLines(f.FullName, window);
                for (var i = lines.Length - 1; i >= 0 && found == null; i--)
                    if (CodexParser.ParseSessionTail(new[] { lines[i] }, now) != null) found = lines[i];
                if (found != null || f.Length <= window) break;
            }
            _tailCache[f.FullName] = (f.LastWriteTimeUtc, f.Length, found);
            return found == null ? null : CodexParser.ParseSessionTail(new[] { found }, now);
        }
    }
}
