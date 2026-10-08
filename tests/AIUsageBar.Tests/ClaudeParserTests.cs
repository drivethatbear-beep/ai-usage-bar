using System;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class ClaudeParserTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

        [Fact]
        public void Parse_Sample()
        {
            var u = ClaudeParser.Parse(Samples.Read("claude_usage.json"), "max", Now);
            Assert.Equal("CL", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Equal("MAX", u.Plan);
            Assert.Equal(2, u.Windows.Count);
            Assert.Equal("5H", u.Windows[0].Label);
            Assert.Equal(9, u.Windows[0].UsedPercent);
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T11:10:00.295245+00:00"), u.Windows[0].ResetsAt);
            Assert.Equal("WK", u.Windows[1].Label);
            Assert.Equal(5, u.Windows[1].UsedPercent);
            Assert.Null(u.Detail("EXTRA"));
        }

        [Fact]
        public void Parse_NullFiveHour()
        {
            var u = ClaudeParser.Parse("{\"five_hour\":null,\"seven_day\":{\"utilization\":40,\"resets_at\":\"2026-10-02T00:00:00Z\"}}", null, Now);
            Assert.Single(u.Windows);
            Assert.Equal("WK", u.Windows[0].Label);
            Assert.Null(u.Plan);
        }

        [Fact]
        public void Parse_ExtraUsageEnabled()
        {
            var u = ClaudeParser.Parse("{\"seven_day\":{\"utilization\":1},\"extra_usage\":{\"is_enabled\":true,\"monthly_limit\":10000,\"used_credits\":2550}}", "pro", Now);
            Assert.Equal("$25.50/$100", u.Detail("EXTRA"));
        }

        [Fact]
        public void Parse_Garbage()
        {
            Assert.Throws<FormatException>(() => ClaudeParser.Parse("<html>", "max", Now));
        }
    }
}
