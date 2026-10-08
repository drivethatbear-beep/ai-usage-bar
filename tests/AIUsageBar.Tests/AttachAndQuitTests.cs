using System;
using System.Threading;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class AttachAndQuitTests
    {
        [Fact]
        public void OriginalRebarLeft_Uses_Rightmost_Sibling_Before_Rebar()
        {
            // Start 0-48, search 48-300; rebar was shifted to 520 by a killed instance.
            Assert.Equal(300, TaskbarAttacher.OriginalRebarLeft(520, new[] { 48, 300, 2400 }));
            // Untouched layout: rebar already sits right after the search box.
            Assert.Equal(300, TaskbarAttacher.OriginalRebarLeft(300, new[] { 48, 300 }));
            // No siblings to the left: keep the rebar's own edge.
            Assert.Equal(10, TaskbarAttacher.OriginalRebarLeft(10, new int[0]));
        }

        [Fact]
        public void QuitSignal_Fires_Listener()
        {
            var name = "AIUsageBarTest." + Guid.NewGuid().ToString("N");
            using (var fired = new ManualResetEventSlim())
            using (QuitSignal.Listen(name, fired.Set))
            {
                Assert.True(QuitSignal.Signal(name));
                Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public void QuitSignal_Without_Listener_Returns_False()
        {
            Assert.False(QuitSignal.Signal("AIUsageBarTest." + Guid.NewGuid().ToString("N")));
        }
    }
}
