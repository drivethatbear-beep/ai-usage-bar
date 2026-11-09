using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class OpenCodeTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T03:00:00Z");

        private const string Usage = "{\"usage\":{\"rolling\":{\"status\":\"ok\",\"percent\":12,\"resetsAt\":\"2026-11-09T08:29:21.489Z\"}," +
            "\"weekly\":{\"status\":\"ok\",\"percent\":1,\"resetsAt\":\"2026-11-16T00:00:00.000Z\"}," +
            "\"monthly\":{\"status\":\"limited\",\"percent\":100,\"resetsAt\":\"2026-12-03T14:45:00.000Z\"}}}";

        private static OpenCodeProvider Provider(TempDir d, FakeHandler h, Dictionary<string, string> env = null, Func<string, string, string> db = null) =>
            new OpenCodeProvider(d.Path, h, k => env != null && env.TryGetValue(k, out var v) ? v : null, db ?? ((p, sql) => null));

        [Fact]
        public void Parse_Three_Windows_As_Percent()
        {
            var u = OpenCodeParser.Parse(Usage, Now);
            Assert.Equal("OC", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal(new[] { "5H", "WK", "MO" }, u.Windows.Select(w => w.Label).ToArray());
            Assert.Equal(12, u.Windows[0].UsedPercent);
            Assert.Equal(1, u.Windows[1].UsedPercent); // 1 means 1 %, not 100 %
            Assert.Equal(DateTimeOffset.Parse("2026-11-16T00:00:00Z"), u.Windows[1].ResetsAt);
            Assert.Equal("limited", u.Detail("LIMIT MO"));
            Assert.Null(u.Detail("LIMIT 5H"));
        }

        [Fact]
        public void Parse_Without_Windows_Throws()
        {
            Assert.Throws<FormatException>(() => OpenCodeParser.Parse("{\"usage\":{}}", Now));
        }

        [Fact]
        public async Task No_Key_Is_NotInstalled_And_Sends_Nothing()
        {
            using (var d = new TempDir())
            {
                var h = FakeHandler.Status(HttpStatusCode.OK, Usage);
                var u = await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NotInstalled, u.Status);
                Assert.Empty(h.Requests);
            }
        }

        [Fact]
        public async Task Key_From_AuthJson_Sent_As_Bearer_And_File_Untouched()
        {
            using (var d = new TempDir())
            {
                var path = d.Write(".local/share/opencode/auth.json", "{\"openai\":{\"type\":\"oauth\"},\"opencode-go\":{\"type\":\"api\",\"key\":\"gokey\"}}");
                var before = File.ReadAllText(path);
                var h = FakeHandler.Status(HttpStatusCode.OK, Usage);
                var u = await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                var req = h.Requests.Single();
                Assert.Equal("https://opencode.ai/zen/go/v1/usage", req.RequestUri.ToString());
                Assert.Equal("Bearer gokey", req.Headers.Authorization.ToString());
                Assert.Equal(before, File.ReadAllText(path));
            }
        }

        [Fact]
        public void Key_Order_Env_Then_Db_Then_AuthJson()
        {
            using (var d = new TempDir())
            {
                d.Write(".local/share/opencode/auth.json", "{\"opencode-go\":{\"key\":\"filekey\"}}");
                string seenDb = null, seenSql = null;
                Func<string, string, string> db = (p, sql) => { seenDb = p; seenSql = sql; return "{\"type\":\"api\",\"key\":\"dbkey\"}"; };

                Assert.Equal("envkey", Provider(d, null, new Dictionary<string, string> { ["OPENCODE_API_KEY"] = " envkey " }, db).ApiKey());
                Assert.Equal("dbkey", Provider(d, null, null, db).ApiKey());
                Assert.Equal(Path.Combine(d.Path, ".local", "share", "opencode", "opencode.db"), seenDb);
                Assert.Contains("integration_id = 'opencode-go'", seenSql);
                Assert.Contains("active IS NULL", seenSql); // never-toggled connections have active = NULL
                Assert.Equal("filekey", Provider(d, null).ApiKey());
            }
        }

        [Fact]
        public void Xdg_Data_Home_Moves_The_Data_Dir()
        {
            using (var d = new TempDir())
            {
                var xdg = Path.Combine(d.Path, "xdg");
                var p = Provider(d, null, new Dictionary<string, string> { ["XDG_DATA_HOME"] = xdg });
                Assert.Equal(Path.Combine(xdg, "opencode"), p.DataDir);
            }
        }

        [Fact]
        public async Task Rejected_Key_Needs_Login()
        {
            using (var d = new TempDir())
            {
                d.Write(".local/share/opencode/auth.json", "{\"opencode-go\":{\"key\":\"bad\"}}");
                var u = await Provider(d, FakeHandler.Status(HttpStatusCode.Unauthorized)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NeedsLogin, u.Status);
                Assert.Equal("OC", u.Tag);
            }
        }

        [Fact]
        public void Card_Names_And_Extras()
        {
            var s = CardContent.Build(new[] { OpenCodeParser.Parse(Usage, Now) }, Now)[0];
            Assert.Equal("OpenCode", s.Name);
            Assert.Contains(("월간 한도", "limited"), s.Extras);
        }

        [Fact]
        public void OpenCode_Has_Its_Own_Colour()
        {
            var st = AppleStyle.For(true);
            var oc = st.ServiceColor("OC");
            foreach (var other in new[] { "CL", "CX", "GK", "GM", "KM", "PC" })
                Assert.NotEqual(st.ServiceColor(other), oc);
        }

        [Fact]
        public void Settings_Round_Trip_ShowOpenCode()
        {
            using (var d = new TempDir())
            {
                var path = Path.Combine(d.Path, "settings.json");
                new Settings { ShowOpenCode = false }.Save(path);
                Assert.False(Settings.Load(path).ShowOpenCode);
                Assert.True(new Settings().ShowOpenCode);
            }
        }
    }

    public class GrokCursorTests
    {
        [Theory]
        [InlineData("unified_ultra", "Ultra")]
        [InlineData("unified_pro_plus", "Pro Plus")]
        [InlineData("ultra", null)]
        [InlineData("unified_", null)]
        [InlineData(null, null)]
        public void Tier_From_Membership(string membership, string expected)
        {
            Assert.Equal(expected, CursorPlan.TierFrom(membership));
        }

        [Fact]
        public async Task Unified_Plan_Shows_Grok_And_Cursor_As_One_Item()
        {
            using (var d = new TempDir())
            {
                var ended = DateTimeOffset.Now.AddMinutes(-1).ToString("o");
                d.Write(".grok/sessions/C%3A/s1/usage.json", "{\"turns\":[{\"endedAt\":\"" + ended + "\",\"totalTokens\":1500,\"costUsdTicks\":20000000000}]}");
                var u = await new GrokProvider(d.Path, () => 10, new NullRpc(), cursorTier: () => "Ultra").FetchAsync(CancellationToken.None);
                Assert.Equal("GK", u.Tag);
                Assert.Equal(GrokProvider.SharedName, u.Service);
                Assert.Equal("Ultra", u.Plan);
                Assert.Equal("Grok · Grok Build · Cursor", u.Detail("SHARED"));

                var s = CardContent.Build(new[] { u }, DateTimeOffset.Now)[0];
                Assert.Equal("Grok · Cursor", s.Name);
                Assert.Contains(("공유 한도", "Grok · Grok Build · Cursor"), s.Extras);
            }
        }

        [Fact]
        public async Task Without_Unified_Plan_Grok_Stays_Grok()
        {
            using (var d = new TempDir())
            {
                var u = await new GrokProvider(d.Path, () => 0, new NullRpc(), cliInstalled: () => false, cursorTier: () => null).FetchAsync(CancellationToken.None);
                Assert.Equal("Grok", CardContent.Build(new[] { u }, DateTimeOffset.Now)[0].Name);
                Assert.Null(u.Detail("SHARED"));
            }
        }
    }
}
