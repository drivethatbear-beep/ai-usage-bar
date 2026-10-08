using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Reads the Claude Code OAuth token (read-only, never refreshed) and queries the usage endpoint.
    /// </summary>
    internal sealed class ClaudeProvider : IUsageProvider
    {
        private const string Url = "https://api.anthropic.com/api/oauth/usage";
        private readonly string _credPath;
        private readonly HttpClient _http;

        public ClaudeProvider(string homeDir = null, HttpMessageHandler handler = null)
        {
            _credPath = Path.Combine(homeDir ?? Io.DefaultHome, ".claude", ".credentials.json");
            _http = Io.Client(handler);
        }

        public string Tag => "CL";

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            string token, plan;
            try
            {
                var creds = Json.Parse(Io.ReadShared(_credPath));
                token = Json.Str(Json.Get(creds, "claudeAiOauth.accessToken"));
                plan = Json.Str(Json.Get(creds, "claudeAiOauth.subscriptionType"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                token = null;
                plan = null;
            }
            if (string.IsNullOrEmpty(token))
                return new ServiceUsage { Service = "Claude", Tag = Tag, Status = ServiceStatus.NotInstalled };

            try
            {
                var r = await Io.GetAsync(_http, Url, token, h => h.Add("anthropic-beta", "oauth-2025-04-20"), ct).ConfigureAwait(false);
                if (r.Status < 200 || r.Status > 299) return Io.Failure("Claude", Tag, r.Status);
                return ClaudeParser.Parse(r.Body, plan, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
            {
                return Io.Error("Claude", Tag, ex);
            }
        }
    }
}
