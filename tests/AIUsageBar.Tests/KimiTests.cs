using System;
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
    public class KimiTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        private static readonly long Valid = Now.AddMinutes(10).ToUnixTimeSeconds();
        private static readonly long Expired = Now.AddMinutes(-1).ToUnixTimeSeconds();

        private static string Creds(string token, long expiresAt) =>
            "{\"access_token\":\"" + token + "\",\"refresh_token\":\"r\",\"expires_at\":" + expiresAt + ",\"scope\":\"kimi-code\",\"token_type\":\"Bearer\",\"expires_in\":900}";

        private static KimiProvider Provider(TempDir d, FakeHandler h) => new KimiProvider(d.Path, h, () => Now);

        [Fact]
        public void Parse_Sample()
        {
            var u = KimiParser.Parse(Samples.Read("kimi_usages.json"), Now);
            Assert.Equal("KM", u.Tag);
            Assert.Equal("Kimi", u.Service);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal(2, u.Windows.Count);
            Assert.Equal("5H", u.Windows[0].Label);
            Assert.Equal(3.56, u.Windows[0].UsedPercent, 6);
            Assert.Equal(DateTimeOffset.Parse("2026-10-24T14:13:48Z"), u.Windows[0].ResetsAt);
            Assert.Equal("MO", u.Windows[1].Label);
            Assert.Equal(0.66, u.Windows[1].UsedPercent, 6);
            Assert.Equal(DateTimeOffset.Parse("2026-11-24T00:00:00Z"), u.Windows[1].ResetsAt);
        }

        [Fact]
        public void Parse_Falls_Back_To_Limits_For_Five_Hours()
        {
            var json = "{\"limits\":[{\"window\":{\"duration\":300,\"timeUnit\":\"TIME_UNIT_MINUTE\"},\"detail\":{\"limit\":\"200\",\"used\":\"50\",\"resetTime\":\"2026-10-24T14:00:00Z\"}}]}";
            var u = KimiParser.Parse(json, Now);
            var w = Assert.Single(u.Windows);
            Assert.Equal("5H", w.Label);
            Assert.Equal(25, w.UsedPercent);
            Assert.Equal(DateTimeOffset.Parse("2026-10-24T14:00:00Z"), w.ResetsAt);
        }

        [Fact]
        public void Parse_Garbage()
        {
            Assert.Throws<FormatException>(() => KimiParser.Parse("<html>", Now));
        }

        [Fact]
        public async Task No_Credentials_NotInstalled()
        {
            using (var d = new TempDir())
            {
                var h = FakeHandler.Status(HttpStatusCode.OK, "{}");
                var u = await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NotInstalled, u.Status);
                Assert.Empty(h.Requests);
            }
        }

        [Fact]
        public async Task Ok_Sends_Bearer_To_Kimi_And_Leaves_Credentials_Untouched()
        {
            using (var d = new TempDir())
            {
                var path = d.Write(".kimi-code/credentials/kimi-code-env-1.json", Creds("ktok", Valid));
                var before = File.ReadAllText(path);
                var h = FakeHandler.Status(HttpStatusCode.OK, Samples.Read("kimi_usages.json"));
                var u = await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(2, u.Windows.Count);
                var req = h.Requests.Single();
                Assert.Equal("https://api.kimi.ai/coding/v1/usages", req.RequestUri.ToString());
                Assert.Equal("Bearer ktok", req.Headers.Authorization.ToString());
                Assert.Equal(before, File.ReadAllText(path));
            }
        }

        [Fact]
        public async Task Uses_Newest_Credentials_File()
        {
            using (var d = new TempDir())
            {
                var old = d.Write(".kimi-code/credentials/kimi-code-env-old.json", Creds("old", Valid));
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
                d.Write(".kimi-code/credentials/kimi-code-env-new.json", Creds("new", Valid));
                var h = FakeHandler.Status(HttpStatusCode.OK, Samples.Read("kimi_usages.json"));
                await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal("Bearer new", h.Requests.Single().Headers.Authorization.ToString());
            }
        }

        [Fact]
        public async Task Expired_Token_Is_Not_Sent_And_Waits_For_Kimi_Code()
        {
            using (var d = new TempDir())
            {
                d.Write(".kimi-code/credentials/kimi-code-env-1.json", Creds("ktok", Expired));
                var h = FakeHandler.Status(HttpStatusCode.OK, "{}");
                var u = await Provider(d, h).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Error, u.Status);
                Assert.Equal("NOT RUNNING", u.Error);
                Assert.Empty(h.Requests);
            }
        }

        [Fact]
        public async Task Status_401_NeedsLogin()
        {
            using (var d = new TempDir())
            {
                d.Write(".kimi-code/credentials/kimi-code-env-1.json", Creds("ktok", Valid));
                var u = await Provider(d, FakeHandler.Status(HttpStatusCode.Unauthorized)).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.NeedsLogin, u.Status);
                Assert.Equal("KM", u.Tag);
            }
        }

        [Fact]
        public void Card_Names_Kimi_And_Says_To_Open_Kimi_Code()
        {
            var s = CardContent.Build(new[] { new ServiceUsage { Tag = "KM", Status = ServiceStatus.Error, Error = "NOT RUNNING" } }, Now)[0];
            Assert.Equal("Kimi", s.Name);
            Assert.Equal("Kimi Code를 켜면 갱신돼요", s.Notice);
        }

        [Fact]
        public void Kimi_Has_Its_Own_Colour()
        {
            var st = AppleStyle.For(true);
            var kimi = st.ServiceColor("KM");
            foreach (var other in new[] { "CL", "CX", "GK", "CU", "GM", "PC" })
                Assert.NotEqual(st.ServiceColor(other), kimi);
        }

        [Fact]
        public void Settings_Round_Trip_ShowKimi()
        {
            using (var d = new TempDir())
            {
                var path = Path.Combine(d.Path, "settings.json");
                new Settings { ShowKimi = false }.Save(path);
                Assert.False(Settings.Load(path).ShowKimi);
                Assert.True(new Settings().ShowKimi);
            }
        }
    }
}
