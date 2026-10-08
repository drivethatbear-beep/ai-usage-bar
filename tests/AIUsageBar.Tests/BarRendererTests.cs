using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class BarRendererTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        private static readonly AppleStyle Style = AppleStyle.For(true);

        private static ServiceUsage Svc(string tag, ServiceStatus st, DateTimeOffset last, params UsageWindow[] w)
        {
            var u = new ServiceUsage { Tag = tag, Status = st, LastSuccess = last };
            u.Windows.AddRange(w);
            return u;
        }

        private static List<ServiceUsage> Items() => new List<ServiceUsage>
        {
            Svc("CL", ServiceStatus.Ok, Now,
                new UsageWindow { Label = "5H", UsedPercent = 34, ResetsAt = Now.AddHours(2) },
                new UsageWindow { Label = "WK", UsedPercent = 15, ResetsAt = Now.AddDays(1) }),
            Svc("CX", ServiceStatus.NeedsLogin, Now),
            Svc("GK", ServiceStatus.NotInstalled, Now),
        };

        private static List<ServiceUsage> One(double used, DateTimeOffset last) => new List<ServiceUsage>
        {
            Svc("CL", ServiceStatus.Ok, last, new UsageWindow { Label = "5H", UsedPercent = used, ResetsAt = Now.AddHours(1) }),
        };

        [Fact]
        public void Measure_Grows_With_Reset_And_Names_And_Scales()
        {
            var plain = new BarRenderer(4).MeasureBar(Items());
            var reset = new BarRenderer(4) { ShowReset = true }.MeasureBar(Items());
            var noNames = new BarRenderer(4) { ShowNames = false }.MeasureBar(Items());
            var big = new BarRenderer(6).MeasureBar(Items());
            Assert.True(reset.Width > plain.Width);
            Assert.True(plain.Width > noNames.Width);
            Assert.Equal(plain.Height, reset.Height);
            Assert.InRange(big.Width, plain.Width * 1.4, plain.Width * 1.6);
            Assert.InRange(plain.Height, 24, 40); // fits a 48 px taskbar
        }

        [Fact]
        public void Measure_Handles_Placeholder_Item_For_Vertical_Layout()
        {
            var one = new BarRenderer(4).MeasureBar(new List<ServiceUsage>(1) { null });
            Assert.True(one.Width > 0 && one.Height > 0);
        }

        [Fact]
        public void Warning_Colour_Only_When_Low()
        {
            using (var low = Draw(One(85, Now), false)) Assert.True(Count(low, Style.Warning) > 0);
            using (var fine = Draw(One(10, Now), false)) Assert.Equal(0, Count(fine, Style.Warning));
        }

        [Fact]
        public void Rings_Take_Service_Colour()
        {
            using (var named = Draw(One(10, Now), false)) Assert.True(Count(named, AppleStyle.Claude) > 0);
            using (var bare = Draw(One(10, Now), false, names: false)) Assert.True(Count(bare, AppleStyle.Claude) > 0);
        }

        [Fact]
        public void Critical_Ring_Blinks()
        {
            using (var off = Draw(One(98, Now), false))
            using (var on = Draw(One(98, Now), true))
                Assert.True(Count(on, Style.Critical) > Count(off, Style.Critical));
        }

        [Fact]
        public void Stale_Block_Is_Dimmed()
        {
            using (var fresh = Draw(One(10, Now), false)) Assert.True(Count(fresh, Style.Label) > 0);
            using (var stale = Draw(One(10, Now.AddMinutes(-11)), false)) Assert.Equal(0, Count(stale, Style.Label));
        }

        [Fact]
        public void Pc_Text_Present()
        {
            var pc = new SystemSnapshot { CpuPercent = 35, MemPercent = 51, CpuTempC = 46 };
            var r = new BarRenderer(4);
            Assert.True(r.MeasureBar(Items(), pc).Width > r.MeasureBar(Items()).Width + 80);
            using (var bmp = Draw(new List<ServiceUsage>(), false, pc: pc)) Assert.True(Count(bmp, Style.Label) > 0);
        }

        private static Bitmap Draw(List<ServiceUsage> items, bool blink, bool showReset = false, SystemSnapshot pc = null, bool names = true)
        {
            var r = new BarRenderer(4) { ShowReset = showReset, ShowNames = names };
            var size = r.MeasureBar(items, pc);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                r.DrawBar(g, items, Now, blink, pc);
            }
            return bmp;
        }

        private static int Count(Bitmap b, Color c)
        {
            var n = 0;
            var want = c.ToArgb() | unchecked((int)0xFF000000);
            for (var y = 0; y < b.Height; y++)
                for (var x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).ToArgb() == want) n++;
            return n;
        }
    }
}
