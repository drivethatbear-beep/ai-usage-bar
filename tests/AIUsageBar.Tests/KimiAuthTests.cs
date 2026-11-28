using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using Xunit;

namespace AIUsageBar.Tests
{
    public class KimiAuthTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
        private const string TokenUrl = "https://auth.kimi.ai/api/oauth/token";
        private const string UsageUrl = "https://api.kimi.ai/coding/v1/usages";

        private static KimiAuth Auth(TempDir d, HttpMessageHandler h) => new KimiAuth(Path.Combine(d.Path, "kimi-auth.dat"), h, () => Now);

        private static string Body(HttpRequestMessage r) => r.Content == null ? "" : r.Content.ReadAsStringAsync().Result;

        /// <summary>Routes by URL; records request bodies (read before the content is disposed).</summary>
        private sealed class Router : HttpMessageHandler
        {
            public readonly List<(string Url, string Auth, string Body)> Calls = new List<(string, string, string)>();
            private readonly Func<string, string, (HttpStatusCode, string)> _f;
            public Router(Func<string, string, (HttpStatusCode, string)> f) { _f = f; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri.ToString();
                var body = Body(request);
                Calls.Add((url, request.Headers.Authorization?.ToString(), body));
                var (code, resp) = _f(url, body);
                return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(resp) });
            }
        }

        private static string TokenJson(string access, string refresh) =>
            "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expires_in\":900,\"token_type\":\"Bearer\"}";

        [Fact]
        public void Save_Load_Round_Trip_Is_Encrypted()
        {
            using (var d = new TempDir())
            {
                var a = Auth(d, FakeHandler.Status(HttpStatusCode.OK));
                Assert.False(a.HasLogin);
                Assert.Null(a.Load());
                a.Save(new KimiAuth.Tokens { Access = "acc\"1", Refresh = "ref-secret", ExpiresAt = Now.AddMinutes(10) });
                Assert.True(a.HasLogin);
                var raw = File.ReadAllBytes(a.Path);
                Assert.DoesNotContain("ref-secret", Encoding.UTF8.GetString(raw));
                var t = a.Load();
                Assert.Equal("acc\"1", t.Access);
                Assert.Equal("ref-secret", t.Refresh);
                Assert.Equal(Now.AddMinutes(10).ToUnixTimeSeconds(), t.ExpiresAt.ToUnixTimeSeconds());
                a.Clear();
                Assert.False(a.HasLogin);
            }
        }

        [Fact]
        public async Task Valid_Own_Token_Is_Used_Without_Refresh_And_Kimi_Code_Untouched()
        {
            using (var d = new TempDir())
            {
                var h = new Router((url, body) => (HttpStatusCode.OK, Samples.Read("kimi_usages.json")));
                var a = Auth(d, h);
                a.Save(new KimiAuth.Tokens { Access = "own", Refresh = "r1", ExpiresAt = Now.AddMinutes(10) });
                var u = await new KimiProvider(d.Path, h, () => Now, a).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                var call = Assert.Single(h.Calls);
                Assert.Equal(UsageUrl, call.Url);
                Assert.Equal("Bearer own", call.Auth);
                Assert.False(Directory.Exists(Path.Combine(d.Path, ".kimi-code")));
            }
        }

        [Fact]
        public async Task Expired_Own_Token_Is_Refreshed_Saved_And_Used()
        {
            using (var d = new TempDir())
            {
                var h = new Router((url, body) => url == TokenUrl
                    ? (HttpStatusCode.OK, TokenJson("fresh", "r2"))
                    : (HttpStatusCode.OK, Samples.Read("kimi_usages.json")));
                var a = Auth(d, h);
                a.Save(new KimiAuth.Tokens { Access = "old", Refresh = "r1", ExpiresAt = Now.AddSeconds(30) });
                var u = await new KimiProvider(d.Path, h, () => Now, a).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(2, h.Calls.Count);
                Assert.Equal(TokenUrl, h.Calls[0].Url);
                Assert.Contains("grant_type=refresh_token", h.Calls[0].Body);
                Assert.Contains("refresh_token=r1", h.Calls[0].Body);
                Assert.Contains("client_id=" + KimiAuth.ClientId, h.Calls[0].Body);
                Assert.Equal("Bearer fresh", h.Calls[1].Auth);
                var saved = a.Load();
                Assert.Equal("fresh", saved.Access);
                Assert.Equal("r2", saved.Refresh);
                Assert.Equal(Now.AddSeconds(900).ToUnixTimeSeconds(), saved.ExpiresAt.ToUnixTimeSeconds());
            }
        }

        [Fact]
        public async Task Rejected_Refresh_Removes_Login_And_Falls_Back_To_Kimi_Code()
        {
            using (var d = new TempDir())
            {
                d.Write(".kimi-code/credentials/kimi-code-env-1.json",
                    "{\"access_token\":\"cli\",\"refresh_token\":\"x\",\"expires_at\":" + Now.AddMinutes(10).ToUnixTimeSeconds() + "}");
                var h = new Router((url, body) => url == TokenUrl
                    ? (HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")
                    : (HttpStatusCode.OK, Samples.Read("kimi_usages.json")));
                var a = Auth(d, h);
                a.Save(new KimiAuth.Tokens { Access = "old", Refresh = "dead", ExpiresAt = Now.AddMinutes(-5) });
                var u = await new KimiProvider(d.Path, h, () => Now, a).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.False(a.HasLogin);
                Assert.Equal("Bearer cli", h.Calls.Last().Auth);
            }
        }

        [Fact]
        public async Task Refresh_Server_Error_Keeps_Login_And_Reports_Error()
        {
            using (var d = new TempDir())
            {
                var h = new Router((url, body) => (HttpStatusCode.InternalServerError, ""));
                var a = Auth(d, h);
                a.Save(new KimiAuth.Tokens { Access = "old", Refresh = "r1", ExpiresAt = Now.AddMinutes(-5) });
                var u = await new KimiProvider(d.Path, h, () => Now, a).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Error, u.Status);
                Assert.True(a.HasLogin);
                Assert.Equal("r1", a.Load().Refresh);
            }
        }

        [Fact]
        public async Task Device_Login_Waits_For_Approval_Then_Saves()
        {
            using (var d = new TempDir())
            {
                var polls = 0;
                var h = new Router((url, body) =>
                {
                    if (url.EndsWith("/device_authorization", StringComparison.Ordinal))
                        return (HttpStatusCode.OK, "{\"device_code\":\"dev\",\"user_code\":\"AB12-CD34\",\"verification_uri\":\"https://www.kimi.ai/code/authorize_device\",\"verification_uri_complete\":\"https://www.kimi.ai/code/authorize_device?user_code=AB12-CD34\",\"expires_in\":1800,\"interval\":5}");
                    return ++polls < 3
                        ? (HttpStatusCode.BadRequest, "{\"error\":\"authorization_pending\"}")
                        : (HttpStatusCode.OK, TokenJson("acc", "ref"));
                });
                var a = Auth(d, h);
                var dc = await a.StartLoginAsync(CancellationToken.None);
                Assert.Equal("AB12-CD34", dc.UserCode);
                Assert.Equal("https://www.kimi.ai/code/authorize_device?user_code=AB12-CD34", dc.VerificationUri);
                var waits = new List<TimeSpan>();
                var ok = await a.CompleteLoginAsync(dc, CancellationToken.None, (t, ct) => { waits.Add(t); return Task.CompletedTask; });
                Assert.True(ok);
                Assert.Equal(3, waits.Count);
                Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(5), w));
                Assert.Contains("device_code=dev", h.Calls.Last().Body);
                Assert.Equal("ref", a.Load().Refresh);
            }
        }

        [Fact]
        public async Task Device_Login_Denied_Returns_False_And_Saves_Nothing()
        {
            using (var d = new TempDir())
            {
                var h = new Router((url, body) => (HttpStatusCode.BadRequest, "{\"error\":\"access_denied\"}"));
                var a = Auth(d, h);
                var dc = new KimiAuth.DeviceCode { Code = "dev", UserCode = "X", VerificationUri = "https://www.kimi.ai/x", Interval = TimeSpan.FromSeconds(5), ExpiresAt = Now.AddMinutes(10) };
                var ok = await a.CompleteLoginAsync(dc, CancellationToken.None, (t, ct) => Task.CompletedTask);
                Assert.False(ok);
                Assert.False(a.HasLogin);
            }
        }

        [Fact]
        public async Task Device_Login_Rejects_Non_Https_Verification_Url()
        {
            using (var d = new TempDir())
            {
                var h = new Router((url, body) => (HttpStatusCode.OK, "{\"device_code\":\"dev\",\"user_code\":\"X\",\"verification_uri_complete\":\"file:///c:/evil.exe\"}"));
                await Assert.ThrowsAsync<FormatException>(() => Auth(d, h).StartLoginAsync(CancellationToken.None));
            }
        }
    }
}
