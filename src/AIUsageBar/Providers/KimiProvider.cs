using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Queries the Kimi Code usage endpoint. With the bar's own login (<see cref="KimiAuth"/>) the token is refreshed
    /// by the bar and the usage stays live. Otherwise it reads Kimi Code's OAuth token (read-only, never refreshed):
    /// those live 15 minutes and only Kimi Code refreshes them, so an expired one is not sent and the last values
    /// stay on screen until Kimi Code runs again and writes a fresh one.
    /// </summary>
    internal sealed class KimiProvider : IUsageProvider
    {
        private const string Url = "https://api.kimi.ai/coding/v1/usages";
        private static readonly TimeSpan ExpirySlack = TimeSpan.FromSeconds(30);
        private readonly HttpClient _http;
        private readonly Func<DateTimeOffset> _now;

        private readonly KimiAuth _auth;

        /// <param name="auth">The bar's own Kimi login; when present it is used first and keeps the usage live.</param>
        public KimiProvider(string homeDir = null, HttpMessageHandler handler = null, Func<DateTimeOffset> now = null, KimiAuth auth = null)
        {
            CredentialsDir = Path.Combine(homeDir ?? Io.DefaultHome, ".kimi-code", "credentials");
            _http = Io.Client(handler);
            _now = now ?? (() => DateTimeOffset.Now);
            _auth = auth;
        }

        public string Tag => "KM";

        /// <summary>Kimi Code rewrites a file here on every token refresh.</summary>
        public string CredentialsDir { get; }

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            if (_auth != null && _auth.HasLogin)
            {
                try
                {
                    var own = await _auth.AccessTokenAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(own)) return await QueryAsync(own, ct).ConfigureAwait(false);
                }
                catch (KimiAuth.RevokedException)
                {
                    // Login removed; fall back to Kimi Code's token below.
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
                {
                    return Io.Error("Kimi", Tag, ex);
                }
            }

            string token = null;
            double? expiresAt = null;
            try
            {
                if (Directory.Exists(CredentialsDir))
                    foreach (var f in new DirectoryInfo(CredentialsDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
                    {
                        var creds = Json.Parse(Io.ReadShared(f.FullName));
                        token = Json.Str(Json.Get(creds, "access_token"));
                        expiresAt = Json.Num(Json.Get(creds, "expires_at"));
                        if (!string.IsNullOrEmpty(token)) break;
                    }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                token = null;
            }
            if (string.IsNullOrEmpty(token))
                return new ServiceUsage { Service = "Kimi", Tag = Tag, Status = ServiceStatus.NotInstalled };

            if (expiresAt.HasValue && DateTimeOffset.FromUnixTimeSeconds((long)expiresAt.Value) <= _now() + ExpirySlack)
                return new ServiceUsage { Service = "Kimi", Tag = Tag, Status = ServiceStatus.Error, Error = "NOT RUNNING" };

            return await QueryAsync(token, ct).ConfigureAwait(false);
        }

        private async Task<ServiceUsage> QueryAsync(string token, CancellationToken ct)
        {
            try
            {
                var r = await Io.GetAsync(_http, Url, token, null, ct).ConfigureAwait(false);
                if (r.Status < 200 || r.Status > 299) return Io.Failure("Kimi", Tag, r.Status);
                return KimiParser.Parse(r.Body, _now());
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
            {
                return Io.Error("Kimi", Tag, ex);
            }
        }
    }
}
