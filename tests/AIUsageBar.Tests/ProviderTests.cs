using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using Xunit;

namespace AIUsageBar.Tests
{
    internal sealed class NullRpc : IGrokRpc
    {
        public Task<string> CallAsync(string method, CancellationToken ct) => Task.FromResult<string>(null);
    }

    public class ProviderTests
    {
        private const string ClaudeCreds = "{\"claudeAiOauth\":{\"accessToken\":\"tok\",\"refreshToken\":\"r\",\"expiresAt\":1000,\"subscriptionType\":\"max\"}}";
        private const string CodexAuth = "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"ctok\",\"account_id\":\"acc\"}}";

        [Fact]
        public async Task Claude_NoCredentials_NotInstalled()
        {
            using (var d = new TempDir())
            {
                var h = FakeHandler.Status(HttpStatusCode.OK, "{}");
                var u = await new ClaudeProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NotInstalled, u.Status);
                Assert.Empty(h.Requests);
            }
        }

        [Fact]
        public async Task Claude_401_NeedsLogin()
        {
            using (var d = new TempDir())
            {
                var path = d.Write(".claude/.credentials.json", ClaudeCreds);
                var h = FakeHandler.Status(HttpStatusCode.Unauthorized);
                var u = await new ClaudeProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NeedsLogin, u.Status);
                Assert.Equal("CL", u.Tag);
                Assert.Single(h.Requests);
                Assert.Equal(ClaudeCreds, File.ReadAllText(path));
            }
        }

        [Fact]
        public async Task Claude_Ok_SendsHeaders()
        {
            using (var d = new TempDir())
            {
                d.Write(".claude/.credentials.json", ClaudeCreds);
                var h = FakeHandler.Status(HttpStatusCode.OK, Samples.Read("claude_usage.json"));
                var u = await new ClaudeProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal("MAX", u.Plan);
                var req = h.Requests.Single();
                Assert.Equal("https://api.anthropic.com/api/oauth/usage", req.RequestUri.ToString());
                Assert.Equal("Bearer tok", req.Headers.Authorization.ToString());
                Assert.Equal("oauth-2025-04-20", req.Headers.GetValues("anthropic-beta").Single());
            }
        }

        [Fact]
        public async Task Claude_429_RateLimitError()
        {
            using (var d = new TempDir())
            {
                d.Write(".claude/.credentials.json", ClaudeCreds);
                var u = await new ClaudeProvider(d.Path, FakeHandler.Status((HttpStatusCode)429)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Error, u.Status);
                Assert.Equal("RATE LIMIT", u.Error);
            }
        }

        [Fact]
        public async Task Claude_Network_Error()
        {
            using (var d = new TempDir())
            {
                d.Write(".claude/.credentials.json", ClaudeCreds);
                var h = new FakeHandler(_ => throw new HttpRequestException("offline"));
                var u = await new ClaudeProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Error, u.Status);
                Assert.Equal("CL", u.Tag);
            }
        }

        [Fact]
        public async Task Codex_Ok_SendsAccountHeader()
        {
            using (var d = new TempDir())
            {
                d.Write(".codex/auth.json", CodexAuth);
                var h = FakeHandler.Status(HttpStatusCode.OK, Samples.Read("codex_wham.json"));
                var u = await new CodexProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Null(u.Detail("SOURCE"));
                var req = h.Requests.Single();
                Assert.Equal("https://chatgpt.com/backend-api/wham/usage", req.RequestUri.ToString());
                Assert.Equal("Bearer ctok", req.Headers.Authorization.ToString());
                Assert.Equal("acc", req.Headers.GetValues("chatgpt-account-id").Single());
            }
        }

        [Fact]
        public async Task Codex_ApiFails_FallsBackToSession()
        {
            using (var d = new TempDir())
            {
                d.Write(".codex/auth.json", CodexAuth);
                d.Write(".codex/sessions/2026/10/01/x.jsonl", Samples.Read("codex_session.jsonl"));
                var u = await new CodexProvider(d.Path, FakeHandler.Status(HttpStatusCode.InternalServerError)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal("LOCAL", u.Detail("SOURCE"));
                Assert.Equal(10, u.Windows.Single().UsedPercent);
            }
        }

        [Fact]
        public void ReadTailLines_DropsPartialFirstLine()
        {
            using (var d = new TempDir())
            {
                var nl = ((char)10).ToString();
                var p = d.Write("t.jsonl", "aaa" + nl + "bbb" + nl + "ccc" + nl);
                Assert.Equal(new[] { "ccc" }, Io.ReadTailLines(p, 6));
                Assert.Equal(new[] { "aaa", "bbb", "ccc" }, Io.ReadTailLines(p, 1000));
            }
        }

        [Fact]
        public async Task Codex_Fallback_Reads_Tail_Of_Large_File()
        {
            using (var d = new TempDir())
            {
                d.Write(".codex/auth.json", CodexAuth);
                var filler = new string('x', 1000);
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < 6000; i++) sb.Append("{\"type\":\"response_item\",\"pad\":\"").Append(filler).Append("\"}").Append((char)10);
                sb.Append(Samples.Read("codex_session.jsonl"));
                d.Write(".codex/sessions/2026/10/01/big.jsonl", sb.ToString());
                var u = await new CodexProvider(d.Path, FakeHandler.Status(HttpStatusCode.InternalServerError)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(10, u.Windows.Single().UsedPercent);
            }
        }

        [Fact]
        public async Task Codex_429_With_Local_Carries_Api_Error()
        {
            using (var d = new TempDir())
            {
                d.Write(".codex/auth.json", CodexAuth);
                d.Write(".codex/sessions/2026/10/01/x.jsonl", Samples.Read("codex_session.jsonl"));
                var u = await new CodexProvider(d.Path, FakeHandler.Status((HttpStatusCode)429)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal("RATE LIMIT", u.Error);
                Assert.Equal("LOCAL", u.Detail("SOURCE"));
            }
        }

        [Fact]
        public async Task Codex_401_NoLocal_NeedsLogin()
        {
            using (var d = new TempDir())
            {
                d.Write(".codex/auth.json", CodexAuth);
                var u = await new CodexProvider(d.Path, FakeHandler.Status(HttpStatusCode.Unauthorized)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NeedsLogin, u.Status);
            }
        }

        [Fact]
        public async Task Codex_NothingInstalled()
        {
            using (var d = new TempDir())
            {
                var h = FakeHandler.Status(HttpStatusCode.OK);
                var u = await new CodexProvider(d.Path, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NotInstalled, u.Status);
                Assert.Empty(h.Requests);
            }
        }

        [Fact]
        public async Task Grok_Reads_UsageFiles()
        {
            using (var d = new TempDir())
            {
                var ended = DateTimeOffset.Now.AddMinutes(-1).ToString("o");
                d.Write(".grok/sessions/C%3A/s1/usage.json", "{\"turns\":[{\"endedAt\":\"" + ended + "\",\"totalTokens\":1500,\"costUsdTicks\":20000000000}]}");
                var u = await new GrokProvider(d.Path, () => 10, new NullRpc()).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(20, u.Windows.Single().UsedPercent, 6);
                Assert.Equal("1.5K TOK", u.Detail("TODAY"));
            }
        }

        [Fact]
        public async Task Grok_NotInstalled()
        {
            using (var d = new TempDir())
            {
                var u = await new GrokProvider(d.Path, () => 0, new NullRpc(), cliInstalled: () => false).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NotInstalled, u.Status);
            }
        }
    }

    public class SchedulerTests
    {
        private sealed class FakeProvider : IUsageProvider
        {
            private readonly Queue<ServiceUsage> _q;
            public FakeProvider(params ServiceUsage[] r) { _q = new Queue<ServiceUsage>(r); }
            public string Tag => "CX";
            public Task<ServiceUsage> FetchAsync(CancellationToken ct) => Task.FromResult(_q.Dequeue());
        }

        private static ServiceUsage Ok(double used)
        {
            var u = new ServiceUsage { Tag = "CX", Status = ServiceStatus.Ok, Plan = "PRO", LastSuccess = DateTimeOffset.Now };
            u.Windows.Add(new UsageWindow { Label = "WK", UsedPercent = used });
            u.AddDetail("CREDITS", "1");
            return u;
        }

        private static ServiceUsage Err(string e) => new ServiceUsage { Tag = "CX", Status = ServiceStatus.Error, Error = e };

        [Fact]
        public async Task Scheduler_KeepsLastValueOnError()
        {
            var seen = new List<ServiceUsage>();
            using (var s = new Scheduler(new IUsageProvider[] { new FakeProvider(Ok(10), Err("HTTP 500")) }, () => 120))
            {
                s.Updated += seen.Add;
                await s.RefreshNowAsync();
                await s.RefreshNowAsync();
            }
            Assert.Equal(2, seen.Count);
            Assert.Equal(ServiceStatus.Error, seen[1].Status);
            Assert.Equal("HTTP 500", seen[1].Error);
            Assert.Equal(10, seen[1].Windows.Single().UsedPercent);
            Assert.Equal("PRO", seen[1].Plan);
            Assert.Equal(seen[0].LastSuccess, seen[1].LastSuccess);
        }

        [Fact]
        public async Task Scheduler_Backoff_When_Local_Fallback_Masks_RateLimit()
        {
            var local = Ok(10);
            local.Error = "RATE LIMIT";
            using (var s = new Scheduler(new IUsageProvider[] { new FakeProvider(local) }, () => 120))
            {
                await s.RefreshNowAsync();
                Assert.Equal(240, s.NextDelaySeconds("CX"));
            }
        }

        [Fact]
        public async Task Scheduler_Keeps_Newer_Value_Over_Older_Snapshot()
        {
            var fresh = Ok(10);
            var old = Ok(50);
            old.LastSuccess = fresh.LastSuccess.Value.AddDays(-1);
            old.Error = "HTTP 500";
            var seen = new List<ServiceUsage>();
            using (var s = new Scheduler(new IUsageProvider[] { new FakeProvider(fresh, old) }, () => 120))
            {
                s.Updated += seen.Add;
                await s.RefreshNowAsync();
                await s.RefreshNowAsync();
            }
            Assert.Equal(10, seen[1].Windows.Single().UsedPercent);
            Assert.Equal(ServiceStatus.Error, seen[1].Status);
            Assert.Equal("HTTP 500", seen[1].Error);
        }

        [Fact]
        public async Task Scheduler_Backoff_On_RateLimit()
        {
            using (var s = new Scheduler(new IUsageProvider[] { new FakeProvider(Err("RATE LIMIT"), Err("RATE LIMIT"), Err("RATE LIMIT"), Ok(1)) }, () => 120))
            {
                Assert.Equal(120, s.NextDelaySeconds("CX"));
                await s.RefreshNowAsync(); Assert.Equal(240, s.NextDelaySeconds("CX"));
                await s.RefreshNowAsync(); Assert.Equal(480, s.NextDelaySeconds("CX"));
                await s.RefreshNowAsync(); Assert.Equal(600, s.NextDelaySeconds("CX"));
                await s.RefreshNowAsync(); Assert.Equal(120, s.NextDelaySeconds("CX"));
            }
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void Settings_Roundtrip_And_Clamp()
        {
            using (var d = new TempDir())
            {
                var p = Path.Combine(d.Path, "sub", "settings.json");
                new Settings { RefreshSeconds = 30, GrokWeeklyBudgetUsd = 20.5, ShowCodex = false, RunAtStartup = false }.Save(p);
                var s = Settings.Load(p);
                Assert.Equal(60, s.RefreshSeconds);
                Assert.Equal(20.5, s.GrokWeeklyBudgetUsd);
                Assert.True(s.ShowClaude);
                Assert.False(s.ShowCodex);
                Assert.True(s.ShowGrok);
                Assert.False(s.RunAtStartup);
                Assert.True(s.ShowResetOnBar);
                Assert.Contains("\"grokWeeklyBudgetUsd\"", File.ReadAllText(p));
            }
        }

        [Fact]
        public void Settings_Roundtrip_ShowResetOnBar_False()
        {
            using (var d = new TempDir())
            {
                var p = Path.Combine(d.Path, "settings.json");
                new Settings { ShowResetOnBar = false }.Save(p);
                Assert.False(Settings.Load(p).ShowResetOnBar);
            }
        }

        [Fact]
        public void Settings_Corrupt_Or_Missing_Defaults()
        {
            using (var d = new TempDir())
            {
                var missing = Settings.Load(Path.Combine(d.Path, "none.json"));
                Assert.Equal(120, missing.RefreshSeconds);
                Assert.Equal(0, missing.GrokWeeklyBudgetUsd);
                Assert.True(missing.RunAtStartup);
                var bad = Settings.Load(d.Write("bad.json", "{oops"));
                Assert.Equal(120, bad.RefreshSeconds);
            }
        }
    }
}
