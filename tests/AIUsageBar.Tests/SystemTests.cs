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
        private static SystemSnapshot Snap(double? cpu = 12, double mem = 32, double? gpuTemp = 43, double? cpuTemp = null,
            double? gpuUtil = 2, double? vramUsed = 6.1, double? vramTotal = 16) => new SystemSnapshot
        {
            CpuPercent = cpu,
            MemPercent = mem,
            MemUsedGb = 40.1,
            MemTotalGb = 125.4,
            GpuTempC = gpuTemp,
            GpuName = "NVIDIA GeForce RTX 5080",
            GpuUtilPercent = gpuUtil,
            CpuTempC = cpuTemp,
            VramUsedGb = vramUsed,
            VramTotalGb = vramTotal,
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
            Assert.Equal("메모리", s.Rows[1].Window);

            // GPU usage and graphics memory as rings, the GPU temperature next to its label.
            Assert.Equal(4, s.Rows.Count);
            Assert.Equal("2%", s.Rows[2].Percent);
            Assert.Equal("GPU · 91°", s.Rows[2].Window);
            Assert.Equal(Severity.Critical, s.Rows[2].LabelSeverity); // 90° and up
            Assert.Equal("38%", s.Rows[3].Percent);                    // 6.1 of 16 GB
            Assert.Equal("VRAM", s.Rows[3].Window);
            Assert.Contains(("메모리", "40.1 / 125.4 GB"), s.Extras);
            Assert.Contains(("VRAM", "6.1 / 16.0 GB"), s.Extras);
            Assert.Contains(("GPU", "RTX 5080"), s.Extras);
            Assert.DoesNotContain(s.Extras, e => e.label.EndsWith("온도"));
            Assert.Equal(NoticeKind.None, s.NoticeKind);
        }

        [Fact]
        public void Card_Shows_Cpu_Temp_Next_To_Cpu()
        {
            var s = CardContent.BuildSystem(Snap(cpuTemp: 61.5));
            Assert.Equal("CPU · 62°", s.Rows[0].Window);
            Assert.Equal(Severity.Normal, s.Rows[0].LabelSeverity);
        }

        [Fact]
        public void Card_Without_Nvidia_Gpu_Keeps_Cpu_And_Memory_Only()
        {
            var s = CardContent.BuildSystem(Snap(gpuTemp: null, gpuUtil: null, vramUsed: null, vramTotal: null));
            Assert.Equal(2, s.Rows.Count);
            Assert.DoesNotContain(s.Extras, e => e.label == "VRAM");
        }

        [Fact]
        public void Bar_Adds_A_Gpu_Column_When_The_Gpu_Is_Known()
        {
            var r = new BarRenderer(4);
            var noGpu = r.MeasureBar(new List<ServiceUsage>(), Snap(gpuTemp: null, gpuUtil: null, vramUsed: null, vramTotal: null)).Width;
            var withGpu = r.MeasureBar(new List<ServiceUsage>(), Snap()).Width;
            Assert.True(withGpu > noGpu + 30);
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
