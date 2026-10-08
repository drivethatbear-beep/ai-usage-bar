using System;
using System.Collections.Generic;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class GrokAggregatorTests
    {
        // Thursday 2026-10-01 15:00 local; week starts Monday 2026-09-28 00:00 local.
        private static readonly DateTimeOffset Now = new DateTimeOffset(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Local));
        private static readonly DateTime T0 = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static string Turn(DateTime local, double ticks, long tokens) =>
            "{\"endedAt\":\"" + new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToString("o") +
            "\",\"totalTokens\":" + tokens + ",\"costUsdTicks\":" + ticks.ToString("0") + "}";

        private static readonly string FileA = "{\"session\":{},\"turns\":[" +
            Turn(new DateTime(2026, 9, 28, 1, 0, 0), 1e10, 1000) + "," +
            Turn(new DateTime(2026, 10, 1, 9, 0, 0), 2e10, 2000000) + "," +
            Turn(new DateTime(2026, 9, 27, 23, 0, 0), 5e10, 9) + "]}";

        private static GrokAggregator Make(Dictionary<string, string> files, out Func<int> reads)
        {
            var count = 0;
            reads = () => count;
            return new GrokAggregator(p => { count++; return files[p]; });
        }

        [Fact]
        public void Week_And_Today_Sums()
        {
            var agg = Make(new Dictionary<string, string> { ["a"] = FileA }, out _);
            agg.Update(new[] { ("a", T0) });
            var u = agg.Build(Now, 20);
            Assert.Equal("GK", u.Tag);
            Assert.Equal(ServiceStatus.Ok, u.Status);
            Assert.Single(u.Windows);
            Assert.Equal("WK", u.Windows[0].Label);
            Assert.Equal(15, u.Windows[0].UsedPercent, 6);
            Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local)), u.Windows[0].ResetsAt);
            Assert.Equal("$3.00 / $20", u.Detail("WEEK"));
            Assert.Equal("$17.00", u.Detail("LEFT"));
            Assert.Equal("2.0M TOK", u.Detail("TODAY"));
        }

        [Fact]
        public void NoBudget_NoWindow()
        {
            var agg = Make(new Dictionary<string, string> { ["a"] = FileA }, out _);
            agg.Update(new[] { ("a", T0) });
            var u = agg.Build(Now, 0);
            Assert.Empty(u.Windows);
            Assert.Equal("$3.00", u.Detail("WEEK"));
            Assert.Null(u.Detail("LEFT"));
        }

        [Fact]
        public void Corrupt_File_Skipped()
        {
            var agg = Make(new Dictionary<string, string> { ["a"] = FileA, ["b"] = "{\"turns\":[" }, out _);
            agg.Update(new[] { ("a", T0), ("b", T0) });
            Assert.Equal("$3.00", agg.Build(Now, 0).Detail("WEEK"));
        }

        [Fact]
        public void Locked_File_Skipped()
        {
            var agg = new GrokAggregator(p => p == "b" ? throw new System.IO.IOException("locked") : FileA);
            agg.Update(new[] { ("a", T0), ("b", T0) });
            Assert.Equal("$3.00", agg.Build(Now, 0).Detail("WEEK"));
        }

        [Fact]
        public void Cache_By_Mtime()
        {
            var agg = Make(new Dictionary<string, string> { ["a"] = FileA, ["c"] = FileA }, out var reads);
            var list = new[] { ("a", T0), ("c", T0) };
            agg.Update(list);
            agg.Update(list);
            Assert.Equal(2, agg.ParseCount);
            agg.Update(new[] { ("a", T0.AddSeconds(5)), ("c", T0) });
            Assert.Equal(3, agg.ParseCount);
            Assert.Equal("$6.00", agg.Build(Now, 0).Detail("WEEK"));
        }

        [Fact]
        public void Removed_File_Dropped()
        {
            var agg = Make(new Dictionary<string, string> { ["a"] = FileA, ["c"] = FileA }, out _);
            agg.Update(new[] { ("a", T0), ("c", T0) });
            agg.Update(new[] { ("a", T0) });
            Assert.Equal("$3.00", agg.Build(Now, 0).Detail("WEEK"));
        }

        [Fact]
        public void No_Files_NotInstalled()
        {
            var agg = new GrokAggregator(p => "");
            agg.Update(new (string, DateTime)[0]);
            Assert.Equal(ServiceStatus.NotInstalled, agg.Build(Now, 20).Status);
        }

        [Theory]
        [InlineData(999, "999 TOK")]
        [InlineData(12345, "12.3K TOK")]
        [InlineData(18051419, "18.1M TOK")]
        public void Token_Format(long tokens, string expected)
        {
            Assert.Equal(expected, Fmt.Tokens(tokens));
        }
    }
}
