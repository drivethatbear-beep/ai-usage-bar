using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using AIUsageBar.Core;
using AIUsageBar.Providers;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class SystemTests
    {
        private static SystemSnapshot Snap(double? cpu = 12, double mem = 32, double? gpuTemp = 43, double? cpuTemp = null) => new SystemSnapshot
        {
            CpuPercent = cpu,
            MemPercent = mem,
            MemUsedGb = 40.1,
            MemTotalGb = 125.4,
            GpuTempC = gpuTemp,
            GpuName = "NVIDIA GeForce RTX 5080",
            GpuUtilPercent = 2,
            CpuTempC = cpuTemp,
        };

        [Fact]
        public void Cpu_Percent_From_System_Times()
        {
            // Kernel time includes idle time (GetSystemTimes semantics).
            Assert.Equal(66.67, SystemMonitor.CpuPercent(100, 300, 100, 150, 400, 150), 2);
            Assert.Equal(0, SystemMonitor.CpuPercent(100, 300, 100, 100, 300, 100));
            Assert.Equal(100, SystemMonitor.CpuPercent(0, 0, 0, 0, 50, 50));
        }

        [Fact]
        public void Card_Section_For_System()
        {
            var s = CardContent.BuildSystem(Snap(cpu: 85, mem: 32, gpuTemp: 91));
            Assert.Equal("PC", s.Tag);
            Assert.Equal("이 PC", s.Name);
            Assert.Equal("85%", s.Rows[0].Percent);
            Assert.Equal("사용 중 · CPU", s.Rows[0].Caption);
            Assert.Equal(85, s.Rows[0].Remaining); // the bar shows usage for the PC
            Assert.Equal(Severity.Low, s.Rows[0].Severity);
            Assert.Equal("32%", s.Rows[1].Percent);
            Assert.Equal("사용 중 · 메모리", s.Rows[1].Caption);
            Assert.Equal("40.1 / 125.4 GB", s.Rows[1].ResetText);
            Assert.Equal(Severity.Normal, s.Rows[1].Severity);
            Assert.Contains(("GPU 온도", "91°C"), s.Extras);
            Assert.Contains(("GPU", "RTX 5080 · 사용률 2%"), s.Extras);
            Assert.DoesNotContain(s.Extras, e => e.label == "CPU 온도");
            Assert.Equal(NoticeKind.None, s.NoticeKind);
        }

        [Fact]
        public void Card_Shows_Cpu_Temp_When_Available()
        {
            var s = CardContent.BuildSystem(Snap(cpuTemp: 61.5));
            Assert.Contains(("CPU 온도", "62°C"), s.Extras);
        }

        [Fact]
        public void Bar_Includes_Pc_Block()
        {
            var r = new BarRenderer(4);
            var none = r.MeasureBar(new List<ServiceUsage>());
            var withPc = r.MeasureBar(new List<ServiceUsage>(), Snap());
            Assert.True(withPc.Width > none.Width + 60);
            using (var bmp = new Bitmap(withPc.Width, withPc.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    r.DrawBar(g, new List<ServiceUsage>(), DateTimeOffset.Now, false, Snap());
                }
                var found = false;
                for (var y = 0; y < bmp.Height && !found; y++)
                    for (var x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() == AppleStyle.Accent.ToArgb()) { found = true; break; }
                Assert.True(found);
            }
        }

        [Fact]
        public void Bar_Without_Names_Is_Narrower()
        {
            var items = new List<ServiceUsage> { new ServiceUsage { Tag = "CL", Status = ServiceStatus.NotInstalled } };
            Assert.True(new BarRenderer(4) { ShowNames = false }.MeasureBar(items).Width < new BarRenderer(4).MeasureBar(items).Width);
        }
    }
}
