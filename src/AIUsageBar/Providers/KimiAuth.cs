using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// The bar's own Kimi Code login (OAuth device flow, same public client as Kimi Code). Its tokens are a separate
    /// login from Kimi Code's, stored DPAPI-encrypted under %APPDATA%\AIUsageBar, so refreshing them never touches
    /// Kimi Code's credentials and the usage stays live while Kimi Code is closed.
    /// </summary>
    internal sealed class KimiAuth
    {
        public const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
        private const string Host = "https://auth.kimi.ai";
        private static readonly TimeSpan RefreshSlack = TimeSpan.FromSeconds(60);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIUsageBar.KimiAuth");

        private readonly HttpClient _http;
        private readonly Func<DateTimeOffset> _now;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public KimiAuth(string path = null, HttpMessageHandler handler = null, Func<DateTimeOffset> now = null)
        {
            Path = path ?? DefaultPath;
            _http = Io.Client(handler);
            _now = now ?? (() => DateTimeOffset.Now);
        }

        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageBar", "kimi-auth.dat");

        public string Path { get; }

        public bool HasLogin => File.Exists(Path);

        public sealed class Tokens
        {
            public string Access;
            public string Refresh;
            public DateTimeOffset ExpiresAt;
        }

        public sealed class DeviceCode
        {
            public string Code;
            public string UserCode;
            public string VerificationUri;
            public TimeSpan Interval;
            public DateTimeOffset ExpiresAt;
        }

        /// <summary>The refresh token was rejected: the login is gone and has been removed.</summary>
        public sealed class RevokedException : Exception
        {
            public RevokedException(string message) : base(message) { }
        }

        public Tokens Load()
        {
            try
            {
                if (!File.Exists(Path)) return null;
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser));
                var d = Json.Parse(json);
                var access = Json.Str(Json.Get(d, "access"));
                var refresh = Json.Str(Json.Get(d, "refresh"));
                var exp = Json.Num(Json.Get(d, "expires"));
                if (string.IsNullOrEmpty(refresh) || exp == null) return null;
                return new Tokens { Access = access, Refresh = refresh, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds((long)exp.Value) };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException || ex is FormatException)
            {
                return null;
            }
        }

        public void Save(Tokens t)
        {
            var json = "{\"access\":" + Quote(t.Access) + ",\"refresh\":" + Quote(t.Refresh) +
                       ",\"expires\":" + t.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "}";
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            var tmp = Path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(Path)) File.Replace(tmp, Path, null);
            else File.Move(tmp, Path);
        }

        public void Clear()
        {
            try { File.Delete(Path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>
        /// A usable access token, refreshed when it expires within a minute; null when there is no login.
        /// Throws <see cref="RevokedException"/> (after removing the login) when Kimi rejects the refresh token,
        /// and HttpRequestException / TaskCanceledException when Kimi cannot be reached.
        /// </summary>
        public async Task<string> AccessTokenAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var t = Load();
                if (t == null) return null;
                if (!string.IsNullOrEmpty(t.Access) && t.ExpiresAt > _now() + RefreshSlack) return t.Access;

                var r = await Io.PostFormAsync(_http, Host + "/api/oauth/token", new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", t.Refresh),
                }, ct).ConfigureAwait(false);
                if (r.Status >= 200 && r.Status <= 299)
                {
                    var fresh = ParseTokens(r.Body);
                    Save(fresh);
                    return fresh.Access;
                }
                if (r.Status == 401 || r.Status == 403 || (r.Status == 400 && ErrorCode(r.Body) == "invalid_grant"))
                {
                    Clear();
                    throw new RevokedException("Kimi refresh rejected: HTTP " + r.Status);
                }
                throw new HttpRequestException("Kimi refresh failed: HTTP " + r.Status);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Starts a device login: the user approves <see cref="DeviceCode.VerificationUri"/> in a browser.</summary>
        public async Task<DeviceCode> StartLoginAsync(CancellationToken ct)
        {
            var r = await Io.PostFormAsync(_http, Host + "/api/oauth/device_authorization", new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
            }, ct).ConfigureAwait(false);
            if (r.Status < 200 || r.Status > 299) throw new HttpRequestException("Kimi login failed: HTTP " + r.Status);
            var d = Json.Parse(r.Body);
            var code = Json.Str(Json.Get(d, "device_code"));
            var user = Json.Str(Json.Get(d, "user_code"));
            var uri = Json.Str(Json.Get(d, "verification_uri_complete")) ?? Json.Str(Json.Get(d, "verification_uri"));
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(user) || !IsHttps(uri)) throw new FormatException("Kimi device authorization response");
            return new DeviceCode
            {
                Code = code,
                UserCode = user,
                VerificationUri = uri,
                Interval = TimeSpan.FromSeconds(Math.Max(1, Json.Num(Json.Get(d, "interval")) ?? 5)),
                ExpiresAt = _now() + TimeSpan.FromSeconds(Json.Num(Json.Get(d, "expires_in")) ?? 900),
            };
        }

        /// <summary>Polls until the user approves (saves the login, returns true) or the code expires / is denied (false).</summary>
        public async Task<bool> CompleteLoginAsync(DeviceCode dc, CancellationToken ct, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            delay = delay ?? Task.Delay;
            var interval = dc.Interval;
            while (_now() < dc.ExpiresAt)
            {
                await delay(interval, ct).ConfigureAwait(false);
                var r = await Io.PostFormAsync(_http, Host + "/api/oauth/token", new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", dc.Code),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                }, ct).ConfigureAwait(false);
                if (r.Status >= 200 && r.Status <= 299)
                {
                    Save(ParseTokens(r.Body));
                    return true;
                }
                if (r.Status >= 500) continue;
                var error = ErrorCode(r.Body);
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
                return false;
            }
            return false;
        }

        private Tokens ParseTokens(string body)
        {
            var d = Json.Parse(body);
            var access = Json.Str(Json.Get(d, "access_token"));
            var refresh = Json.Str(Json.Get(d, "refresh_token"));
            var expiresIn = Json.Num(Json.Get(d, "expires_in"));
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expiresIn == null || expiresIn <= 0)
                throw new FormatException("Kimi token response");
            return new Tokens { Access = access, Refresh = refresh, ExpiresAt = _now() + TimeSpan.FromSeconds(expiresIn.Value) };
        }

        private static string ErrorCode(string body)
        {
            try { return Json.Str(Json.Get(Json.Parse(body), "error")); }
            catch (FormatException) { return null; }
        }

        private static bool IsHttps(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
