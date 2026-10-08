using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using Xunit;

namespace AIUsageBar.Tests
{
    public class CursorTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T03:00:00Z");

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Jwt(string sub) => B64("{\"alg\":\"HS256\"}") + "." + B64("{\"sub\":\"" + sub + "\",\"exp\":1795744102}") + ".sig";

        [Fact]
        public void Parse_Usage_Summary()
        {
            var u = CursorParser.Parse(Samples.Read("cursor_usage.json"), Now);
            Assert.Equal("CU", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal("ultra", u.Plan);
            Assert.Equal(2, u.Windows.Count);
            Assert.Equal("TOT", u.Windows[0].Label);
            Assert.Equal(20.0006, u.Windows[0].UsedPercent, 3);
            Assert.Equal(DateTimeOffset.Parse("2026-10-13T05:52:04.000Z"), u.Windows[0].ResetsAt);
            Assert.Equal("API", u.Windows[1].Label);
            Assert.Equal(66.74, u.Windows[1].UsedPercent, 2);
        }

        [Fact]
        public void Parse_Unlimited_Has_No_Windows()
        {
            var u = CursorParser.Parse("{\"membershipType\":\"enterprise\",\"isUnlimited\":true,\"billingCycleEnd\":\"2026-10-13T05:52:04Z\"}", Now);
            Assert.Empty(u.Windows);
            Assert.Equal("무제한", u.Detail("UNLIMITED"));
        }

        [Fact]
        public void UserId_From_Jwt_Sub()
        {
            Assert.Equal("user_01ABC", CursorParser.UserIdFromJwt(Jwt("google-oauth2|user_01ABC")));
            Assert.Equal("user_02", CursorParser.UserIdFromJwt(Jwt("user_02")));
            Assert.Null(CursorParser.UserIdFromJwt("not-a-jwt"));
        }

        [Fact]
        public async Task Provider_Sends_Session_Cookie()
        {
            var token = Jwt("github|user_77");
            var h = FakeHandler.Status(HttpStatusCode.OK, Samples.Read("cursor_usage.json"));
            var u = await new CursorProvider(() => token, h).FetchAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            var req = h.Requests.Single();
            Assert.Equal("https://cursor.com/api/usage-summary", req.RequestUri.ToString());
            Assert.Equal("WorkosCursorSessionToken=user_77%3A%3A" + token, req.Headers.GetValues("Cookie").Single());
        }

        [Fact]
        public void Default_Handler_Keeps_Manual_Cookie_Header()
        {
            // With UseCookies=true, HttpClientHandler drops a hand-set Cookie header and the request goes out unauthenticated.
            using (var h = CursorProvider.CreateHandler())
                Assert.False(h.UseCookies);
            // Never follow a redirect: the hand-set session cookie would travel to the new address.
            using (var h = CursorProvider.CreateHandler())
                Assert.False(h.AllowAutoRedirect);
            using (var h = Io.CreateHandler())
                Assert.False(h.AllowAutoRedirect);
        }

        [Fact]
        public async Task Provider_No_Token_NotInstalled()
        {
            var h = FakeHandler.Status(HttpStatusCode.OK, "{}");
            var u = await new CursorProvider(() => null, h).FetchAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.NotInstalled, u.Status);
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task Provider_401_NeedsLogin()
        {
            var u = await new CursorProvider(() => Jwt("x|user_1"), FakeHandler.Status(HttpStatusCode.Unauthorized)).FetchAsync(CancellationToken.None);
            Assert.Equal(ServiceStatus.NeedsLogin, u.Status);
            Assert.Equal("CU", u.Tag);
        }
    }
}
