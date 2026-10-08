using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class BackdropTests
    {
        [Theory]
        [InlineData(22621)]
        [InlineData(26100)]
        [InlineData(26300)]
        public void Choose_SystemBackdrop_On_22621_And_Later(int build)
        {
            Assert.Equal(BackdropMode.SystemBackdrop, BackdropWindow.Choose(build, forceLayered: false));
        }

        [Theory]
        [InlineData(19045)]
        [InlineData(22000)]
        public void Choose_Accent_On_Older_Builds(int build)
        {
            Assert.Equal(BackdropMode.AccentAcrylic, BackdropWindow.Choose(build, forceLayered: false));
        }

        [Fact]
        public void Choose_Layered_When_Forced_Or_Ancient()
        {
            Assert.Equal(BackdropMode.Layered, BackdropWindow.Choose(26300, forceLayered: true));
            Assert.Equal(BackdropMode.Layered, BackdropWindow.Choose(9600, forceLayered: false));
        }
    }
}
