using System;
using AIUsageBar.Core;
using Xunit;

namespace AIUsageBar.Tests
{
    public class UsageModelTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        private static readonly DateTimeOffset Future = DateTimeOffset.Parse("2026-10-01T11:10:00Z");

        private static UsageWindow W(double used, DateTimeOffset? resets) =>
            new UsageWindow { Label = "5H", UsedPercent = used, ResetsAt = resets };

        [Fact]
        public void Window_Remaining_Rules()
        {
            var w = W(9, Future);
            Assert.Equal(91, w.RemainingAt(Now));
            Assert.Equal(9, w.FilledDots(Now));
            Assert.Equal(Severity.Normal, w.SeverityAt(Now));

            Assert.Equal(15, W(85, Future).RemainingAt(Now));
            Assert.Equal(Severity.Low, W(85, Future).SeverityAt(Now));

            Assert.Equal(4, W(96, Future).RemainingAt(Now));
            Assert.Equal(Severity.Critical, W(96, Future).SeverityAt(Now));

            Assert.Equal(0, W(120, Future).RemainingAt(Now));
            Assert.Equal(100, W(50, DateTimeOffset.Parse("2026-10-01T09:00:00Z")).RemainingAt(Now));
        }

        [Fact]
        public void Severity_Boundaries()
        {
            Assert.Equal(Severity.Normal, W(79, Future).SeverityAt(Now)); // 21 left
            Assert.Equal(Severity.Low, W(80, Future).SeverityAt(Now));    // 20 left
            Assert.Equal(Severity.Low, W(95, Future).SeverityAt(Now));    // 5 left
        }
    }
}
