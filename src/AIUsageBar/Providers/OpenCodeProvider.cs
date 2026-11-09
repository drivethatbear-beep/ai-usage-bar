using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// OpenCode Go: finds the Go API key OpenCode keeps for its "opencode-go" connection (read-only) and queries
    /// opencode.ai/zen/go/v1/usage. Key order: OPENCODE_API_KEY → opencode.db credential → auth.json.
    /// </summary>
    internal sealed class OpenCodeProvider : IUsageProvider
    {
        private const string Url = "https://opencode.ai/zen/go/v1/usage";
        private readonly HttpClient _http;
        private readonly Func<string, string> _env;
        private readonly Func<string, string, string> _dbValue;

        public OpenCodeProvider(string homeDir = null, HttpMessageHandler handler = null,
            Func<string, string> env = null, Func<string, string, string> dbValue = null)
        {
            _env = env ?? Environment.GetEnvironmentVariable;
            _dbValue = dbValue ?? SqliteRead.FirstValue;
            var xdg = _env("XDG_DATA_HOME");
            DataDir = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
                ? Path.Combine(xdg, "opencode")
                : Path.Combine(homeDir ?? Io.DefaultHome, ".local", "share", "opencode");
            _http = Io.Client(handler);
        }

        public string Tag => "OC";

        /// <summary>OpenCode's data folder (auth.json, opencode.db).</summary>
        public string DataDir { get; }

        public string ApiKey()
        {
            var k = _env("OPENCODE_API_KEY")?.Trim();
            if (!string.IsNullOrEmpty(k)) return k;

            try
            {
                var db = _env("OPENCODE_DB");
                if (string.IsNullOrEmpty(db)) db = Path.Combine(DataDir, "opencode.db");
                // "active" is NULL for connections that were never toggled, so only an explicit 0 is skipped.
                var raw = _dbValue(db, "SELECT value FROM credential WHERE integration_id = 'opencode-go' AND (active IS NULL OR active <> 0) ORDER BY time_updated DESC LIMIT 1");
                k = raw == null ? null : Json.Str(Json.Get(Json.Parse(raw), "key"));
                if (!string.IsNullOrEmpty(k)) return k;
            }
            catch (Exception ex) when (ex is FormatException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is IOException)
            {
            }

            try
            {
                var auth = Path.Combine(DataDir, "auth.json");
                if (File.Exists(auth))
                {
                    var root = Json.Parse(Io.ReadShared(auth));
                    foreach (var id in new[] { "opencode-go", "opencode" })
                    {
                        k = Json.Str(Json.Get(Json.Get(root, id), "key"));
                        if (!string.IsNullOrEmpty(k)) return k;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
            }
            return null;
        }

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            var key = ApiKey();
            if (string.IsNullOrEmpty(key))
                return new ServiceUsage { Service = "OpenCode Go", Tag = Tag, Status = ServiceStatus.NotInstalled };
            try
            {
                var r = await Io.GetAsync(_http, Url, key, h => h.Accept.ParseAdd("application/json"), ct).ConfigureAwait(false);
                if (r.Status < 200 || r.Status > 299) return Io.Failure("OpenCode Go", Tag, r.Status);
                return OpenCodeParser.Parse(r.Body, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
            {
                return Io.Error("OpenCode Go", Tag, ex);
            }
        }
    }
}
