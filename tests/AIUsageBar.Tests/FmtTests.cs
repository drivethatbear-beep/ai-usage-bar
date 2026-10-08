using System;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class FmtTests
    {
        [Fact]
        public void Reset_Short_Format()
        {
            var now = new DateTimeOffset(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Local));
            Assert.Equal("20:10", Fmt.ResetShort(now.AddHours(5).AddMinutes(10), now));
            Assert.Equal("10/5", Fmt.ResetShort(new DateTimeOffset(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local)), now));
            Assert.Equal("", Fmt.ResetShort(now.AddMinutes(-1), now));
        }
    }
}
