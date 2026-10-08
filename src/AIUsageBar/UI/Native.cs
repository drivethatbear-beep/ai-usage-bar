using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageBar.UI
{
    internal static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000,
            WS_CLIPSIBLINGS = 0x04000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000,
            WS_EX_TOPMOST = 0x00000008, WS_EX_APPWINDOW = 0x00040000;
        public const uint LWA_COLORKEY = 0x1;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        public const uint ULW_ALPHA = 0x2;
        public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        /// <summary>
        /// Pushes a bitmap to a WS_EX_LAYERED window with per-pixel alpha. The pixels are copied from a
        /// premultiplied (PArgb) bitmap, as UpdateLayeredWindow requires, so anti-aliased edges, soft
        /// shadows and translucent text blend correctly. <paramref name="screenPos"/> moves a top-level
        /// window at the same time; pass null for child windows.
        /// </summary>
        public static bool PushLayered(IntPtr hwnd, System.Drawing.Bitmap bmp, System.Drawing.Point? screenPos = null, byte alpha = 255)
        {
            var w = bmp.Width;
            var h = bmp.Height;
            System.Drawing.Bitmap pargb = null;
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(mem, ref header, 0, out var bits, IntPtr.Zero, 0);
            var old = dib == IntPtr.Zero ? IntPtr.Zero : SelectObject(mem, dib);
            try
            {
                if (dib == IntPtr.Zero) return false;
                pargb = bmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppPArgb
                    ? bmp
                    : bmp.Clone(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var data = pargb.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (var y = 0; y < h; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, bits + y * w * 4, row.Length);
                    }
                }
                finally
                {
                    pargb.UnlockBits(data);
                }

                var size = new SIZE { cx = w, cy = h };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
                if (screenPos.HasValue)
                {
                    var dst = new POINT { X = screenPos.Value.X, Y = screenPos.Value.Y };
                    var p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(POINT)));
                    try
                    {
                        Marshal.StructureToPtr(dst, p, false);
                        return UpdateLayeredWindow(hwnd, screen, p, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(p);
                    }
                }
                return UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (pargb != null && !ReferenceEquals(pargb, bmp)) pargb.Dispose();
                if (dib != IntPtr.Zero)
                {
                    SelectObject(mem, old);
                    DeleteObject(dib);
                }
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

        public const int AW_BLEND = 0x80000;
        [DllImport("user32.dll")] public static extern bool AnimateWindow(IntPtr hwnd, int time, int flags);

        // ---- DWM backdrop / accent blur ----
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

        /// <summary>Windows 11 22H2+: transient-window Acrylic, rounded corners, theme-matched frame.</summary>
        public static bool TrySystemBackdrop(IntPtr hwnd, bool dark)
        {
            try
            {
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                if (DwmExtendFrameIntoClientArea(hwnd, ref m) != 0) return false;
                var darkVal = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, 4);
                var round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                var acrylic = 3; // DWMSBT_TRANSIENTWINDOW
                return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, 4) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

        /// <summary>Older Windows 10/11: undocumented accent-policy Acrylic with an ABGR tint.</summary>
        public static bool TryAccentAcrylic(IntPtr hwnd, uint abgrTint)
        {
            try
            {
                var accent = new AccentPolicy { AccentState = 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, AccentFlags = 2, GradientColor = abgrTint };
                var size = Marshal.SizeOf(typeof(AccentPolicy));
                var p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, p, false);
                    var data = new WindowCompositionAttribData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, SizeOfData = size };
                    if (SetWindowCompositionAttribute(hwnd, ref data) == 0) return false;
                    var round = 2;
                    try { DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4); } catch (EntryPointNotFoundException) { }
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return false;
            }
        }

        // ---- Buffered paint: copy premultiplied pixels straight into the paint buffer (keeps per-pixel alpha) ----
        [StructLayout(LayoutKind.Sequential)]
        private struct BP_PAINTPARAMS { public int cbSize, dwFlags; public IntPtr prcExclude, pBlendFunction; }

        [DllImport("uxtheme.dll")] private static extern IntPtr BeginBufferedPaint(IntPtr hdcTarget, ref RECT prcTarget, int format, ref BP_PAINTPARAMS p, out IntPtr hdc);
        [DllImport("uxtheme.dll")] private static extern int EndBufferedPaint(IntPtr hpb, bool update);
        [DllImport("uxtheme.dll")] private static extern int GetBufferedPaintBits(IntPtr hpb, out IntPtr bits, out int cxRow);

        public static void CopyToBufferedPaint(IntPtr hdc, System.Drawing.Bitmap pargb)
        {
            var rect = new RECT { Left = 0, Top = 0, Right = pargb.Width, Bottom = pargb.Height };
            var pp = new BP_PAINTPARAMS { cbSize = Marshal.SizeOf(typeof(BP_PAINTPARAMS)), dwFlags = 1 /* BPPF_ERASE */ };
            var hpb = BeginBufferedPaint(hdc, ref rect, 2 /* BPBF_TOPDOWNDIB */, ref pp, out _);
            if (hpb == IntPtr.Zero) return;
            try
            {
                if (GetBufferedPaintBits(hpb, out var bits, out var cxRow) != 0) return;
                var data = pargb.LockBits(new System.Drawing.Rectangle(0, 0, pargb.Width, pargb.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[pargb.Width * 4];
                    for (var y = 0; y < pargb.Height; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, bits + y * cxRow * 4, row.Length);
                    }
                }
                finally
                {
                    pargb.UnlockBits(data);
                }
            }
            finally
            {
                EndBufferedPaint(hpb, true);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern uint GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern uint SetWindowLong(IntPtr hwnd, int index, uint value);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>True when the foreground window covers its whole monitor (games, video, presentations).</summary>
        public static bool ForegroundIsFullscreen()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            var cls = ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
            if (!GetWindowRect(fg, out var r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }
}
