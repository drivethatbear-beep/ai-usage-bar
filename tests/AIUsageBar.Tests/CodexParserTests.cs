using System;
using System.IO;
using System.Linq;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class CodexParserTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

        [Fact]
        public void ParseApi_Sample()
        {
            var u = CodexParser.ParseApi(Samples.Read("codex_wham.json"), Now);
            Assert.Equal("CX", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal("PRO", u.Plan);
            Assert.Single(u.Windows);
            Assert.Equal("WK", u.Windows[0].Label);
            Assert.Equal(10, u.Windows[0].UsedPercent);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791354699), u.Windows[0].ResetsAt);
            Assert.Equal("62500", u.Detail("CREDITS"));
        }

        [Fact]
        public void ParseApi_BothWindows()
        {
            var json = "{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":12,\"limit_window_seconds\":604800,\"reset_at\":1791354699}," +
                       "\"secondary_window\":{\"used_percent\":30,\"limit_window_seconds\":18000,\"reset_at\":1791300000}}}";
            var u = CodexParser.ParseApi(json, Now);
            Assert.Equal(2, u.Windows.Count);
            Assert.Equal("5H", u.Windows[0].Label);
            Assert.Equal(30, u.Windows[0].UsedPercent);
            Assert.Equal("WK", u.Windows[1].Label);
            Assert.Equal(12, u.Windows[1].UsedPercent);
            Assert.Null(u.Detail("CREDITS"));
        }

        [Fact]
        public void ParseSessionTail_UsesLast()
        {
            var lines = Samples.Read("codex_session.jsonl").Split('\n');
            var u = CodexParser.ParseSessionTail(lines, Now);
            Assert.NotNull(u);
            Assert.Equal("PRO", u.Plan);
            Assert.Single(u.Windows);
            Assert.Equal("WK", u.Windows[0].Label);
            Assert.Equal(10, u.Windows[0].UsedPercent);
            Assert.Equal("62500", u.Detail("CREDITS"));
        }

        [Fact]
        public void ParseSessionTail_FiveHourWindow()
        {
            var line = "{\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":40,\"window_minutes\":300,\"resets_at\":1791300000},\"secondary\":{\"used_percent\":7,\"window_minutes\":10080,\"resets_at\":1791354699}}}}";
            var u = CodexParser.ParseSessionTail(new[] { line }, Now);
            Assert.Equal("5H", u.Windows[0].Label);
            Assert.Equal("WK", u.Windows[1].Label);
        }

        private const string Good = "{\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":10,\"window_minutes\":10080,\"resets_at\":1791354699}}}}";

        [Fact]
        public void ParseSessionTail_SkipsTruncatedLastLine()
        {
            var u = CodexParser.ParseSessionTail(new[] { Good, "{\"payload\":{\"rate_limits\":{\"primary\":{\"used_per" }, Now);
            Assert.Equal(10, u.Windows.Single().UsedPercent);
        }

        [Fact]
        public void ParseSessionTail_SkipsNullRateLimits()
        {
            var u = CodexParser.ParseSessionTail(new[] { Good, "{\"payload\":{\"rate_limits\":null}}" }, Now);
            Assert.Equal(10, u.Windows.Single().UsedPercent);
        }

        [Fact]
        public void ParseSessionTail_MillisecondReset_DoesNotThrow()
        {
            var line = "{\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":10,\"window_minutes\":10080,\"resets_at\":1791354699000}}}}";
            var u = CodexParser.ParseSessionTail(new[] { line }, Now);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791354699), u.Windows.Single().ResetsAt);
        }

        [Fact]
        public void ParseSessionTail_NoEvent()
        {
            Assert.Null(CodexParser.ParseSessionTail(new[] { "{\"type\":\"x\"}", "", "not json" }, Now));
        }
    }
}
