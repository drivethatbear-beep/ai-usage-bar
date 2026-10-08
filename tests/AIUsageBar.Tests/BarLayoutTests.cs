using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class BarLayoutTests
    {
        // Width model: level * (base + reset? 30 : 0 + names? 40 : 0), level 4 = 100 %.
        private static int Width(int level, bool reset, bool names) => level * (100 + (reset ? 30 : 0) + (names ? 40 : 0));

        private static BarFit Fit(int width, bool wantReset = true) => BarLayout.Fit(6, wantReset, Width, width, minLevel: 4);

        [Fact]
        public void Full_Layout_When_Room()
        {
            Assert.Equal(new BarFit(6, true, true), Fit(5000));
        }

        [Fact]
        public void Keeps_The_Size_First_Hiding_Names_Then_Reset()
        {
            Assert.Equal(new BarFit(6, true, false), Fit(6 * 130));
            Assert.Equal(new BarFit(6, false, false), Fit(6 * 100));
        }

        [Fact]
        public void Then_Shrinks_Down_To_100_Percent()
        {
            Assert.Equal(new BarFit(5, false, false), Fit(590));
            Assert.Equal(new BarFit(4, false, false), Fit(450));
        }

        [Fact]
        public void Finally_Goes_Below_100_Percent()
        {
            Assert.Equal(new BarFit(3, false, false), Fit(3 * 100));
            Assert.Equal(new BarFit(1, false, false), Fit(10));
        }

        [Fact]
        public void Respects_Reset_Turned_Off()
        {
            Assert.Equal(new BarFit(6, false, true), Fit(5000, wantReset: false));
            Assert.Equal(new BarFit(6, false, false), Fit(600, wantReset: false));
            Assert.Equal(new BarFit(5, false, false), Fit(500, wantReset: false));
        }
    }
}
