using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using AIUsageBar.Providers;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class CardRendererTests
    {
        private static CardSection Section(string tag, string name, int rows, bool extra = false, string notice = null)
        {
            var s = new CardSection { Tag = tag, Name = name, Plan = "Max", Notice = notice, NoticeKind = notice == null ? NoticeKind.None : NoticeKind.Warning };
            for (var i = 0; i < rows; i++)
                s.Rows.Add(new CardRow { Percent = "66%", Caption = "남음 · 5시간", Window = "5시간", ResetText = "오늘 20:09 초기화", InText = "2시간 41분 후", Remaining = 66 });
            if (extra) s.Extras.Add(("크레딧", "62,500"));
            return s;
        }

        private static CardSection Pc() => CardContent.BuildSystem(new SystemSnapshot
        {
            CpuPercent = 35, MemPercent = 51, MemUsedGb = 32.6, MemTotalGb = 64, CpuTempC = 46, GpuName = "NVIDIA GeForce RTX 4090", GpuUtilPercent = 12,
        });

        [Fact]
        public void Height_Grows_And_Width_Fixed()
        {
            using (var r = new CardRenderer(1f, light: true))
            {
                var one = r.Measure(new List<CardSection> { Section("CL", "Claude", 1) });
                var more = r.Measure(new List<CardSection> { Section("CL", "Claude", 2), Section("CX", "Codex", 1, extra: true), Pc() });
                Assert.True(more.Height > one.Height);
                Assert.Equal(one.Width, more.Width);
            }
        }

        [Fact]
        public void Scales()
        {
            using (var a = new CardRenderer(1f, true))
            using (var b = new CardRenderer(1.5f, true))
            {
                var list = new List<CardSection> { Section("CL", "Claude", 2), Pc() };
                Assert.InRange(b.Measure(list).Width, a.Measure(list).Width * 1.4, a.Measure(list).Width * 1.6);
                Assert.InRange(b.Measure(list).Height, a.Measure(list).Height * 1.4, a.Measure(list).Height * 1.6);
            }
        }

        [Fact]
        public void Backdrop_Has_No_Shadow_Margin()
        {
            var list = new List<CardSection> { Section("CL", "Claude", 1) };
            using (var r = new CardRenderer(1.5f, true) { Backdrop = true })
            {
                Assert.InRange(r.Measure(list).Width, 380 * 1.5 - 1, 380 * 1.5 + 1);
                using (var bmp = Render(r, list)) Assert.Equal(0, bmp.GetPixel(0, 0).A);
            }
            using (var r = new CardRenderer(1.5f, true) { Backdrop = false })
            {
                Assert.InRange(r.Measure(list).Width, 412 * 1.5 - 1, 412 * 1.5 + 1);
                using (var bmp = Render(r, list))
                {
                    Assert.Equal(0, bmp.GetPixel(0, 0).A); // shadow margin corner stays clear
                    Assert.True(bmp.GetPixel(bmp.Width / 2, (int)(16 * 1.5) + 4).A > 200); // solid tinted body in the fallback
                }
            }
        }

        [Fact]
        public void Dark_Palette_Used_When_Light_False()
        {
            var list = new List<CardSection> { Section("CL", "Claude", 1) };
            using (var dark = new CardRenderer(1f, false) { Backdrop = true })
            using (var bmp = Render(dark, list))
            {
                Assert.True(Count(bmp, AppleStyle.For(false).Label) > 0);
                Assert.Equal(0, Count(bmp, AppleStyle.For(true).Label));
            }
            using (var light = new CardRenderer(1f, true) { Backdrop = true })
            using (var bmp = Render(light, list))
                Assert.True(Count(bmp, AppleStyle.For(true).Label) > 0);
        }

        [Fact]
        public void Service_Bar_And_Chip_Use_Service_Colour()
        {
            using (var r = new CardRenderer(1f, true) { Backdrop = true })
            using (var bmp = Render(r, new List<CardSection> { Section("CL", "Claude", 1), Section("GK", "Grok", 0, notice: "CLI에서 다시 로그인하세요") }))
                Assert.True(Count(bmp, AppleStyle.Claude) > 20);
        }

        [Fact]
        public void Pc_Row_Draws_Rings_In_Accent()
        {
            using (var r = new CardRenderer(1f, true) { Backdrop = true })
            {
                using (var without = Render(r, new List<CardSection> { Section("CL", "Claude", 1) }))
                    Assert.Equal(0, Count(without, AppleStyle.Accent));
                using (var with = Render(r, new List<CardSection> { Section("CL", "Claude", 1), Pc() }))
                    Assert.True(Count(with, AppleStyle.Accent) > 20);
            }
        }

        private static Bitmap Render(CardRenderer r, List<CardSection> list)
        {
            var size = r.Measure(list);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                r.Draw(g, list, "17:28 갱신");
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
