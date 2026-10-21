using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class GeminiTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T01:00:00Z");

        [Fact]
        public void Parse_Groups_Gemini_Models_And_Other_Models()
        {
            var u = GeminiParser.Parse(Samples.Read("gemini_status.json"), Now);
            Assert.Equal("GM", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal("Google AI Pro", u.Plan);
            Assert.Equal(2, u.Windows.Count);

            var gem = u.Windows[0];
            Assert.Equal("GEM", gem.Label);
            Assert.Equal(58, gem.UsedPercent, 1); // the tightest Gemini model: 42 % left
            Assert.Equal(DateTimeOffset.Parse("2026-10-12T05:29:18Z"), gem.ResetsAt);

            var other = u.Windows[1];
            Assert.Equal("3P", other.Label);
            Assert.Equal(25, other.UsedPercent, 1);
            Assert.Equal(DateTimeOffset.Parse("2026-10-12T05:40:27Z"), other.ResetsAt);
        }

        [Fact]
        public void Missing_Remaining_Fraction_Means_Used_Up()
        {
            // Protobuf JSON drops zero values: a quota with only a reset time has nothing left.
            var json = "{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":[" +
                       "{\"modelId\":\"gemini-3.8-flash-high\",\"quotaInfo\":{\"resetTime\":\"2026-10-12T05:29:18Z\"}}]}}}";
            var u = GeminiParser.Parse(json, Now);
            Assert.Single(u.Windows);
            Assert.Equal(100, u.Windows[0].UsedPercent, 1);
        }

        [Fact]
        public void Models_Without_Quota_Are_Ignored()
        {
            var json = "{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"modelId\":\"gemini-x\"}]}}}";
            Assert.Empty(GeminiParser.Parse(json, Now).Windows);
        }

        [Fact]
        public void Command_Line_Gives_Csrf_Token_Only_For_Antigravity()
        {
            const string exe = @"C:\Users\u\AppData\Local\Programs\antigravity\resources\bin\language_server.exe";
            var cmd = exe + " --standalone --override_ide_name antigravity --https_server_port 0 --csrf_token 1234-abcd --app_data_dir antigravity";
            Assert.Equal("1234-abcd", AntigravityServer.CsrfFrom(exe, cmd));
            Assert.Null(AntigravityServer.CsrfFrom(@"C:\Other\language_server.exe", cmd.Replace(exe, @"C:\Other\language_server.exe")));
            Assert.Null(AntigravityServer.CsrfFrom(exe, exe + " --override_ide_name windsurf --csrf_token x"));
            Assert.Null(AntigravityServer.CsrfFrom(exe, exe + " --override_ide_name antigravity"));
        }

        [Fact]
        public void Self_Signed_Certificate_Accepted_Only_On_Loopback()
        {
            Assert.True(GeminiProvider.CertificateOk(new Uri("https://127.0.0.1:56730/x"), SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(GeminiProvider.CertificateOk(new Uri("https://example.com/x"), SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(GeminiProvider.CertificateOk(new Uri("https://example.com/x"), SslPolicyErrors.None));
            using (var h = GeminiProvider.CreateHandler()) Assert.False(h.AllowAutoRedirect);
        }

        [Fact]
        public async Task Fetch_Asks_The_Local_Server_With_Its_Csrf_Token()
        {
            var h = new FakeHandler(req => req.RequestUri.Port == 2222
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Samples.Read("gemini_status.json")) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
            var p = new GeminiProvider(() => new AntigravityServer.Endpoint("tok-1", new[] { 1111, 2222 }), () => true, h);
            var u = await p.FetchAsync(CancellationToken.None);

            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal(2, u.Windows.Count);
            var req = h.Requests.Last();
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://127.0.0.1:2222/exa.language_server_pb.LanguageServerService/GetUserStatus", req.RequestUri.ToString());
            Assert.Equal("tok-1", req.Headers.GetValues("X-Codeium-Csrf-Token").Single());
        }

        [Fact]
        public async Task Closed_App_Is_Not_Running_And_Missing_App_Is_Not_Installed()
        {
            var h = FakeHandler.Status(HttpStatusCode.OK);
            var closed = await new GeminiProvider(() => null, () => true, h).FetchAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.Error, closed.Status);
            Assert.Equal("NOT RUNNING", closed.Error);
            var missing = await new GeminiProvider(() => null, () => false, h).FetchAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.NotInstalled, missing.Status);
            Assert.Empty(h.Requests);
        }

        [Fact]
        public void Card_Names_The_Windows_And_Explains_A_Closed_App()
        {
            Assert.Equal("Gemini", CardContent.NameFor("GM"));
            Assert.Equal("Gemini 모델", CardContent.WindowName("GEM"));
            Assert.Equal("Claude·GPT 모델", CardContent.WindowName("3P"));
            var s = CardContent.Build(new[] { new ServiceUsage { Tag = "GM", Status = ServiceStatus.Error, Error = "NOT RUNNING" } }, Now)[0];
            Assert.Equal("Antigravity를 켜면 갱신돼요", s.Notice);
        }
    }
}
