using System;
using System.IO;
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
        public void Bar_Shows_Only_The_Gemini_Pool()
        {
            var u = GeminiParser.Parse(Samples.Read("gemini_status.json"), Now);
            var bar = BarRenderer.BarWindows(u);
            Assert.Single(bar);
            Assert.Equal("GEM", bar[0].Label);
            Assert.Equal(new[] { "Gemini 모델", "Claude·GPT 모델" }, CardContent.Build(new[] { u }, Now)[0].Rows.Select(r => r.Window));

            // Only the Claude·GPT pool reported: the bar still shows what there is.
            var thirdOnly = new ServiceUsage { Tag = "GM" };
            thirdOnly.Windows.Add(new UsageWindow { Label = "3P" });
            Assert.Equal(new[] { "3P" }, BarRenderer.BarWindows(thirdOnly).Select(w => w.Label));

            var claude = new ServiceUsage { Tag = "CL" };
            claude.Windows.Add(new UsageWindow { Label = "5H" });
            claude.Windows.Add(new UsageWindow { Label = "WK" });
            claude.Windows.Add(new UsageWindow { Label = "X" });
            Assert.Equal(new[] { "5H", "WK" }, BarRenderer.BarWindows(claude).Select(w => w.Label));
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
        public void Daemon_Hub_File_Gives_Endpoint_Only_For_Matching_Pid_And_Listening_Port()
        {
            const string exe = @"C:\Users\u\AppData\Local\Programs\antigravity\resources\bin\language_server.exe";
            var hubCmd = exe + " --standalone --override_ide_name antigravity --subclient_type hub --override_ide_version 2.34.0 --https_server_port 0 --app_data_dir antigravity --read_host_bridge_token_from_stdin";
            Assert.True(AntigravityServer.IsAntigravityProcess(exe, hubCmd));
            Assert.False(AntigravityServer.IsAntigravityProcess(@"C:\Other\language_server.exe", hubCmd));
            Assert.False(AntigravityServer.IsAntigravityProcess(exe, exe + " --override_ide_name windsurf"));
            Assert.Equal("antigravity", AntigravityServer.AppDataNameFrom(hubCmd));

            var dir = Path.Combine(Path.GetTempPath(), "aiusagebar-hub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "hub_49182.json"),
                    "{\"pid\":44192,\"httpsPort\":49181,\"httpPort\":49182,\"lsVersion\":\"2.32.2\",\"csrfToken\":\"stale-tok\"}");
                File.WriteAllText(Path.Combine(dir, "hub_53585.json"),
                    "{\"pid\":37868,\"httpsPort\":53584,\"httpPort\":53585,\"lsVersion\":\"2.34.0\",\"csrfToken\":\"hub-tok-1\"}");

                var ep = AntigravityServer.EndpointFromDaemon(37868, new[] { 52443, 53584, 53585 }, dir);
                Assert.NotNull(ep);
                Assert.Equal("hub-tok-1", ep.Csrf);
                Assert.Equal(new[] { 53584, 52443 }, ep.Ports); // httpsPort first, plain httpPort skipped

                Assert.Null(AntigravityServer.EndpointFromDaemon(99999, new[] { 53584 }, dir));
                Assert.Null(AntigravityServer.EndpointFromDaemon(37868, new int[0], dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ParseSummary_And_ParseCli_Keep_Both_FiveHour_And_Weekly_Windows()
        {
            var summaryJson = "{\"response\":{\"groups\":[" +
                "{\"displayName\":\"Gemini Models\",\"buckets\":[" +
                "{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.915,\"resetTime\":\"2026-12-01T02:00:32Z\"}," +
                "{\"bucketId\":\"gemini-5h\",\"window\":\"5h\",\"remainingFraction\":0.817,\"resetTime\":\"2026-11-25T07:51:15Z\"}]}," +
                "{\"displayName\":\"Claude and GPT models\",\"buckets\":[" +
                "{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.984,\"resetTime\":\"2026-11-25T09:35:40Z\"}," +
                "{\"bucketId\":\"3p-5h\",\"window\":\"5h\",\"remainingFraction\":1,\"resetTime\":\"2026-11-25T11:14:15Z\"}]}]}}";

            var s = GeminiParser.ParseSummary(summaryJson, "Google AI Ultra", Now);
            Assert.NotNull(s);
            Assert.Equal("Google AI Ultra", s.Plan);
            Assert.Equal(new[] { "5H", "WK", "3P" }, s.Windows.Select(w => w.Label));
            Assert.Equal(18.3, s.Windows[0].UsedPercent, 1);
            Assert.Equal(8.5, s.Windows[1].UsedPercent, 1);
            Assert.Equal(1.6, s.Windows[2].UsedPercent, 1);

            // Bar shows Gemini 5H · WK (like Claude), leaving 3P for the card.
            Assert.Equal(new[] { "5H", "WK" }, BarRenderer.BarWindows(s).Select(w => w.Label));
            Assert.Equal(new[] { "5시간", "주간", "Claude·GPT 모델" }, CardContent.Build(new[] { s }, Now)[0].Rows.Select(r => r.Window));

            var cliJson = "{\"status\":\"SUCCESS\",\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[" +
                "{\"name\":\"Gemini Models\",\"buckets\":[" +
                "{\"id\":\"gemini-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.92,\"reset_time\":\"2026-10-18T02:00:32Z\"}," +
                "{\"id\":\"gemini-5h\",\"window\":\"5h\",\"remaining_fraction\":0.99,\"reset_time\":\"2026-10-12T06:00:00Z\"}]}," +
                "{\"name\":\"Claude and GPT models\",\"buckets\":[" +
                "{\"id\":\"3p-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.85,\"reset_time\":\"2026-10-15T09:35:40Z\"}," +
                "{\"id\":\"3p-5h\",\"window\":\"5h\",\"remaining_fraction\":1,\"reset_time\":\"2026-10-12T06:00:00Z\"}]}]}}}";

            var u = GeminiParser.ParseCli(cliJson, Now);
            Assert.NotNull(u);
            Assert.Equal("GM", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal(new[] { "5H", "WK", "3P" }, u.Windows.Select(w => w.Label));
            Assert.Equal(1, u.Windows[0].UsedPercent, 1);
            Assert.Equal(DateTimeOffset.Parse("2026-10-12T06:00:00Z"), u.Windows[0].ResetsAt);
            Assert.Equal(8, u.Windows[1].UsedPercent, 1);
            Assert.Equal(DateTimeOffset.Parse("2026-10-18T02:00:32Z"), u.Windows[1].ResetsAt);
            Assert.Equal(15, u.Windows[2].UsedPercent, 1);
            Assert.Equal(DateTimeOffset.Parse("2026-10-15T09:35:40Z"), u.Windows[2].ResetsAt);
        }

        [Fact]
        public async Task Fetch_Combines_Plan_From_GetUserStatus_With_5H_And_Weekly_From_QuotaSummary()
        {
            var summaryJson = "{\"response\":{\"groups\":[" +
                "{\"displayName\":\"Gemini Models\",\"buckets\":[" +
                "{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.90,\"resetTime\":\"2026-12-01T02:00:32Z\"}," +
                "{\"bucketId\":\"gemini-5h\",\"window\":\"5h\",\"remainingFraction\":0.80,\"resetTime\":\"2026-11-25T07:51:15Z\"}]}]}}";
            var h = new FakeHandler(req =>
            {
                if (req.RequestUri.Port != 2222) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (req.RequestUri.AbsolutePath.EndsWith("/GetUserStatus", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Samples.Read("gemini_status.json")) };
                if (req.RequestUri.AbsolutePath.EndsWith("/RetrieveUserQuotaSummary", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(summaryJson) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var p = new GeminiProvider(() => new AntigravityServer.Endpoint("tok-1", new[] { 2222 }), () => true, h);
            var u = await p.FetchAsync(CancellationToken.None);

            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal("Google AI Pro", u.Plan);
            Assert.Equal(new[] { "5H", "WK" }, u.Windows.Select(w => w.Label));
            Assert.Equal(20, u.Windows[0].UsedPercent, 1);
            Assert.Equal(10, u.Windows[1].UsedPercent, 1);
            Assert.Equal(new[] { "5H" }, BarRenderer.BarWindows(new ServiceUsage { Tag = "GM", Windows = { new UsageWindow { Label = "5H" }, new UsageWindow { Label = "3P" } } }).Select(w => w.Label));
        }

        [Fact]
        public async Task Fetch_Falls_Back_To_Agy_Cli_When_Desktop_Server_Not_Running()
        {
            var calls = 0;
            var cliJson = "{\"status\":\"SUCCESS\",\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[" +
                "{\"name\":\"Gemini Models\",\"buckets\":[{\"id\":\"gemini-5h\",\"remaining_fraction\":0.74,\"reset_time\":\"2026-10-12T05:00:00Z\"}]}]}}}";
            var h = FakeHandler.Status(HttpStatusCode.OK);
            var p = new GeminiProvider(() => null, () => true, h, _ => { calls++; return Task.FromResult(cliJson); });

            var first = await p.FetchAsync(CancellationToken.None);
            var second = await p.FetchAsync(CancellationToken.None);

            Assert.Equal(ServiceStatus.Ok, first.Status);
            Assert.Equal(26, first.Windows.Single().UsedPercent, 1);
            Assert.Equal(ServiceStatus.Ok, second.Status);
            Assert.Equal(1, calls); // cached within CLI interval
            Assert.Empty(h.Requests);
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
