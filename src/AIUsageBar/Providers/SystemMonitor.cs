using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageBar.Providers
{
    internal sealed class SystemSnapshot
    {
        public double? CpuPercent;
        public double MemPercent;
        public double MemUsedGb;
        public double MemTotalGb;
        public double? GpuTempC;
        public double? GpuUtilPercent;
        public string GpuName;
        public double? CpuTempC;
    }

    /// <summary>
    /// Cheap local PC metrics, sampled every few seconds without admin rights: CPU load (GetSystemTimes deltas),
    /// memory (GlobalMemoryStatusEx), NVIDIA GPU temperature/load (NVML from the driver, no process spawn),
    /// and CPU temperature only where Windows exposes an ACPI thermal zone (many desktops do not).
    /// </summary>
    internal sealed class SystemMonitor : IDisposable
    {
        private ulong _idle, _kernel, _user;
        private bool _haveTimes;
        private bool _nvmlTried, _nvmlOk;
        private IntPtr _gpu;
        private string _gpuName;
        private PerformanceCounter _thermal;
        private bool _thermalTried;

        public static double CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
        {
            var idle = idle1 - idle0;
            var total = (kernel1 - kernel0) + (user1 - user0); // kernel includes idle
            if (total == 0) return 0;
            return Math.Max(0, Math.Min(100, (total - idle) * 100.0 / total));
        }

        public SystemSnapshot Sample()
        {
            var s = new SystemSnapshot();

            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
                if (_haveTimes) s.CpuPercent = CpuPercent(_idle, _kernel, _user, idle, kernel, user);
                _idle = idle; _kernel = kernel; _user = user; _haveTimes = true;
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref mem))
            {
                const double gb = 1024.0 * 1024 * 1024;
                s.MemTotalGb = mem.ullTotalPhys / gb;
                s.MemUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / gb;
                s.MemPercent = mem.ullTotalPhys == 0 ? 0 : s.MemUsedGb * 100 / s.MemTotalGb;
            }

            ReadGpu(s);
            s.CpuTempC = ReadCpuTemp();
            return s;
        }

        private void ReadGpu(SystemSnapshot s)
        {
            if (!_nvmlTried)
            {
                _nvmlTried = true;
                try
                {
                    _nvmlOk = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out _gpu) == 0;
                    if (_nvmlOk)
                    {
                        var name = new StringBuilder(96);
                        if (nvmlDeviceGetName(_gpu, name, (uint)name.Capacity) == 0) _gpuName = name.ToString();
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
                {
                    _nvmlOk = false; // no NVIDIA driver: GPU fields stay empty
                }
            }
            if (!_nvmlOk) return;
            if (nvmlDeviceGetTemperature(_gpu, 0, out var t) == 0) s.GpuTempC = t;
            if (nvmlDeviceGetUtilizationRates(_gpu, out var u) == 0) s.GpuUtilPercent = u.gpu;
            s.GpuName = _gpuName;
        }

        private double? ReadCpuTemp()
        {
            if (!_thermalTried)
            {
                _thermalTried = true;
                try
                {
                    if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
                    {
                        var names = new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
                        if (names.Length > 0) _thermal = new PerformanceCounter("Thermal Zone Information", "Temperature", names[0], readOnly: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
                {
                    _thermal = null;
                }
            }
            if (_thermal == null) return null;
            try
            {
                var kelvin = _thermal.NextValue();
                return kelvin > 200 ? Math.Round(kelvin - 273.15, 1) : (double?)null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _thermal?.Dispose();
            if (_nvmlOk)
            {
                try { nvmlShutdown(); } catch (DllNotFoundException) { }
                _nvmlOk = false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low, High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlUtilization { public uint gpu, memory; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
    }
}
