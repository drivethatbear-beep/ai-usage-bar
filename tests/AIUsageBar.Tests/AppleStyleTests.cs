using System.Drawing;
using AIUsageBar.Core;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class AppleStyleTests
    {
        private static int Rgb(Color c) => c.ToArgb() & 0xFFFFFF;

        [Fact]
        public void Light_And_Dark_Tokens_Match_Spec()
        {
            var l = AppleStyle.For(true);
            var d = AppleStyle.For(false);
            Assert.Equal(0x1D1D1F, Rgb(l.Label));
            Assert.Equal(0xF5F5F7, Rgb(d.Label));
            Assert.Equal(0x6E6E73, Rgb(l.Secondary));
            Assert.Equal(0x98989D, Rgb(d.Secondary));
            Assert.Equal(0xFF9500, Rgb(l.Warning));
            Assert.Equal(0xFF9F0A, Rgb(d.Warning));
            Assert.Equal(0xFF3B30, Rgb(l.Critical));
            Assert.Equal(0xFF453A, Rgb(d.Critical));
            Assert.Equal(0x0A84FF, Rgb(l.PcAccent));
            Assert.Equal(0x0A84FF, Rgb(d.PcAccent));
            Assert.Equal((int)(0.12 * 255 + 0.5), l.Separator.A);
            Assert.Equal((int)(0.72 * 255 + 0.5), l.AcrylicTint.A);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(50, 180)]
        [InlineData(100, 360)]
        [InlineData(130, 360)]
        public void Sweep_Is_Clamped(double remaining, float degrees)
        {
            Assert.Equal(degrees, AppleStyle.Sweep(remaining), 3);
        }

        [Fact]
        public void StateColor_Only_Changes_When_Low()
        {
            var s = AppleStyle.For(true);
            var normal = Color.FromArgb(0x12, 0x34, 0x56);
            Assert.Equal(normal, s.StateColor(Severity.Normal, normal));
            Assert.Equal(s.Warning, s.StateColor(Severity.Low, normal));
            Assert.Equal(s.Critical, s.StateColor(Severity.Critical, normal));
        }

        [Fact]
        public void Font_Falls_Back_To_Segoe_UI()
        {
            using (var display = AppleStyle.Font(26, display: true, semibold: true))
            using (var text = AppleStyle.Font(13, display: false, semibold: false))
            {
                Assert.StartsWith("Segoe UI", display.Name);
                Assert.StartsWith("Segoe UI", text.Name);
                Assert.Equal(26f, display.Size, 3);
                Assert.Equal(GraphicsUnit.Pixel, display.Unit);
            }
        }
    }
}
