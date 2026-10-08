using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using Xunit;

namespace AIUsageBar.Tests
{
    public class GrokBillingTests
    {
        private sealed class FakeRpc : IGrokRpc
        {
            private readonly Func<string, string> _f;
            public int Calls;
            public FakeRpc(Func<string, string> f) { _f = f; }
            public Task<string> CallAsync(string method, CancellationToken ct)
            {
                Calls++;
                return Task.FromResult(_f(method));
            }
        }

        [Fact]
        public void Parse_Billing_Response()
        {
            var b = GrokBillingParser.Parse(Samples.Read("grok_billing.json"));
            Assert.Equal("SuperGrok Heavy", b.Tier);
            Assert.Equal(6, b.UsedPercent);
            Assert.Equal("WK", b.Label);
            Assert.Equal(DateTimeOffset.Parse("2026-10-06T23:41:41.363691+00:00"), b.ResetsAt);
        }

        [Theory]
        [InlineData("USAGE_PERIOD_TYPE_MONTHLY", "MO")]
        [InlineData("USAGE_PERIOD_TYPE_DAILY", "DY")]
        [InlineData("SOMETHING_NEW", "WK")]
        public void Parse_Period_Labels(string type, string label)
        {
            var json = "{\"id\":1,\"result\":{\"config\":{\"creditUsagePercent\":1,\"currentPeriod\":{\"type\":\"" + type + "\",\"end\":\"2026-10-06T23:41:41Z\"}}}}";
            Assert.Equal(label, GrokBillingParser.Parse(json).Label);
        }

        [Fact]
        public void Parse_Just_After_Reset_Treats_Missing_Percent_As_Zero()
        {
            // Right after the weekly reset the API omits creditUsagePercent (zero values are not sent).
            var b = GrokBillingParser.Parse(Samples.Read("grok_billing_reset.json"));
            Assert.NotNull(b);
            Assert.Equal(0, b.UsedPercent);
            Assert.Equal("SuperGrok Heavy", b.Tier);
            Assert.Equal(DateTimeOffset.Parse("2026-10-13T23:41:41.363691+00:00"), b.ResetsAt);
        }

        [Fact]
        public void Parse_Error_Or_Missing_Percent_Returns_Null()
        {
            Assert.Null(GrokBillingParser.Parse("{\"id\":1,\"error\":{\"code\":-32000,\"message\":\"Authentication required\"}}"));
            Assert.Null(GrokBillingParser.Parse("{\"id\":1,\"result\":{\"config\":{}}}"));
        }

        private static string UsageFile() =>
            "{\"turns\":[{\"endedAt\":\"" + DateTimeOffset.Now.AddMinutes(-1).ToString("o") + "\",\"totalTokens\":1500,\"costUsdTicks\":39300000000}]}";

        [Fact]
        public async Task Provider_Uses_Subscription_Limit()
        {
            using (var d = new TempDir())
            {
                d.Write(".grok/sessions/x/s1/usage.json", UsageFile());
                var rpc = new FakeRpc(m => m == "_x.ai/billing" ? Samples.Read("grok_billing.json") : null);
                var u = await new GrokProvider(d.Path, () => 20, rpc).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal("SuperGrok Heavy", u.Plan);
                var w = u.Windows.Single();
                Assert.Equal("WK", w.Label);
                Assert.Equal(6, w.UsedPercent);
                Assert.Equal(DateTimeOffset.Parse("2026-10-06T23:41:41.363691+00:00"), w.ResetsAt);
                Assert.Equal("$3.93", u.Detail("API EQUIV WK"));
                Assert.Equal("1.5K TOK", u.Detail("TODAY"));
                Assert.Null(u.Detail("LEFT"));
            }
        }

        [Fact]
        public async Task Provider_Throttles_Billing_Calls()
        {
            using (var d = new TempDir())
            {
                d.Write(".grok/sessions/x/s1/usage.json", UsageFile());
                var rpc = new FakeRpc(m => Samples.Read("grok_billing.json"));
                var p = new GrokProvider(d.Path, () => 0, rpc);
                await p.FetchAsync(CancellationToken.None);
                var second = await p.FetchAsync(CancellationToken.None);
                Assert.Equal(1, rpc.Calls);
                Assert.Equal(6, second.Windows.Single().UsedPercent);
            }
        }

        [Fact]
        public async Task Provider_Falls_Back_To_Budget_When_Billing_Fails()
        {
            using (var d = new TempDir())
            {
                d.Write(".grok/sessions/x/s1/usage.json", UsageFile());
                var rpc = new FakeRpc(m => throw new TimeoutException());
                var u = await new GrokProvider(d.Path, () => 10, rpc).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(39.3, u.Windows.Single().UsedPercent, 6);
                Assert.Null(u.Plan);
            }
        }

        [Fact]
        public async Task Provider_Without_Sessions_But_With_Billing_Still_Shows_Limit()
        {
            using (var d = new TempDir())
            {
                var rpc = new FakeRpc(m => Samples.Read("grok_billing.json"));
                var u = await new GrokProvider(d.Path, () => 0, rpc, cliInstalled: () => true).FetchAsync(CancellationToken.None);
                Assert.Equal(ServiceStatus.Ok, u.Status);
                Assert.Equal(6, u.Windows.Single().UsedPercent);
            }
        }

        [Fact]
        public void Rpc_IsResponse_Matches_Exact_Id()
        {
            Assert.True(GrokAgent.IsResponse("{\"jsonrpc\":\"2.0\",\"id\": 2,\"result\":{}}", 2));
            Assert.False(GrokAgent.IsResponse("{\"jsonrpc\":\"2.0\",\"id\":20,\"result\":{}}", 2));
            Assert.False(GrokAgent.IsResponse("{\"jsonrpc\":\"2.0\",\"method\":\"x\",\"params\":{\"id\":2}}", 2));
            Assert.False(GrokAgent.IsResponse("not json", 2));
        }
    }
}
