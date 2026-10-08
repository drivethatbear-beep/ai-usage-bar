using System.Drawing;
using System.IO;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class DesktopCardTests
    {
        private static readonly Rectangle Primary = new Rectangle(0, 0, 3840, 2088);
        private static readonly Rectangle Left = new Rectangle(-1920, -9, 1920, 1032);
        private static readonly Size Card = new Size(560, 900);

        [Fact]
        public void Default_Is_Bottom_Right_Of_Primary_Work_Area()
        {
            var p = DesktopCard.ResolvePosition(null, Card, new[] { Primary, Left }, Primary, margin: 24);
            Assert.Equal(new Point(3840 - 560 - 24, 2088 - 900 - 24), p);
        }

        [Fact]
        public void Saved_Position_Kept_When_Fully_Visible_On_Any_Screen()
        {
            Assert.Equal(new Point(100, 50), DesktopCard.ResolvePosition(new Point(100, 50), Card, new[] { Primary, Left }, Primary, 24));
            Assert.Equal(new Point(-1800, 10), DesktopCard.ResolvePosition(new Point(-1800, 10), new Size(560, 900), new[] { Primary, Left }, Primary, 24));
        }

        [Fact]
        public void Saved_Position_Off_Screen_Falls_Back_To_Default()
        {
            var p = DesktopCard.ResolvePosition(new Point(5000, 100), Card, new[] { Primary }, Primary, 24);
            Assert.Equal(new Point(3840 - 560 - 24, 2088 - 900 - 24), p);
        }

        [Fact]
        public void Partly_Off_Screen_Is_Pulled_Back_Inside_That_Screen()
        {
            // Mostly on the primary screen but hanging off the bottom-right corner.
            var p = DesktopCard.ResolvePosition(new Point(3400, 1300), Card, new[] { Primary }, Primary, 24);
            Assert.Equal(new Point(3840 - 560, 2088 - 900), p);
        }

        [Theory]
        [InlineData(1.0, 120, 1.1)]
        [InlineData(1.0, -120, 0.9)]
        [InlineData(0.5, -120, 0.5)]
        [InlineData(1.5, 240, 1.5)]
        [InlineData(0.84, 120, 0.9)]
        public void Wheel_Steps_Scale_By_Ten_Percent_Within_Limits(double current, int delta, double expected)
        {
            Assert.Equal(expected, DesktopCard.StepScale(current, delta), 6);
        }

        [Fact]
        public void Settings_Roundtrip_Card_Scale_Clamped()
        {
            using (var d = new TempDir())
            {
                var path = Path.Combine(d.Path, "s.json");
                new Settings { DesktopCardScale = 0.7 }.Save(path);
                Assert.Equal(0.7, Settings.Load(path).DesktopCardScale, 6);
                new Settings { DesktopCardScale = 9 }.Save(path);
                Assert.Equal(1.5, Settings.Load(path).DesktopCardScale, 6);
                Assert.Equal(1.0, Settings.Load(Path.Combine(d.Path, "none.json")).DesktopCardScale, 6);
            }
        }

        [Fact]
        public void Settings_Roundtrip_Desktop_Card()
        {
            using (var d = new TempDir())
            {
                var path = Path.Combine(d.Path, "settings.json");
                new Settings { PinCardOnDesktop = true, DesktopCardTopMost = true, DesktopCardX = -1800, DesktopCardY = 12 }.Save(path);
                var s = Settings.Load(path);
                Assert.True(s.PinCardOnDesktop);
                Assert.True(s.DesktopCardTopMost);
                Assert.Equal(-1800, s.DesktopCardX);
                Assert.Equal(12, s.DesktopCardY);
                var defaults = Settings.Load(Path.Combine(d.Path, "none.json"));
                Assert.False(defaults.PinCardOnDesktop);
                Assert.False(defaults.DesktopCardTopMost);
                Assert.Null(defaults.DesktopCardX);
            }
        }
    }
}
