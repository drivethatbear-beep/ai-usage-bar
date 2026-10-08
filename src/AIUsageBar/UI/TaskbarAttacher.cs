using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Win32;

namespace AIUsageBar.UI
{
    /// <summary>Snapshot of the primary taskbar's geometry (screen pixels).</summary>
    internal sealed class TaskbarInfo
    {
        public IntPtr Handle;
        public Rectangle Bounds;
        public bool Horizontal;
        public bool AtTop;
        public bool Win11;
        public bool CenterAligned;
        public Rectangle Tray;   // TrayNotifyWnd, screen coords
        public IntPtr Rebar;     // Win10 task band host
        /// <summary>Win11 centered: screen x where the Start button begins (int.MaxValue when unknown).</summary>
        public int IconsLeft = int.MaxValue;

        public int Thickness => Horizontal ? Bounds.Height : Bounds.Width;

        public static TaskbarInfo Find()
        {
            var h = Native.FindWindow("Shell_TrayWnd", null);
            if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return null;
            var t = new TaskbarInfo { Handle = h, Bounds = r.ToRectangle() };
            t.Horizontal = t.Bounds.Width >= t.Bounds.Height;
            t.AtTop = t.Horizontal && t.Bounds.Top <= System.Windows.Forms.Screen.FromHandle(h).Bounds.Top;
            t.Win11 = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero;
            t.CenterAligned = t.Win11 && ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1) != 0;
            var tray = Native.FindWindowEx(h, IntPtr.Zero, "TrayNotifyWnd", null);
            t.Tray = tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr) ? tr.ToRectangle() : Rectangle.Empty;
            var rebar = Native.FindWindowEx(h, IntPtr.Zero, "ReBarWindow32", null);
            t.Rebar = !t.Win11 && rebar != IntPtr.Zero && Native.IsWindowVisible(rebar) ? rebar : IntPtr.Zero;
            // Win11 still keeps the task band window over the centered app icons; Start sits one
            // taskbar-thickness to its left.
            if (t.Win11 && t.CenterAligned && t.Horizontal && rebar != IntPtr.Zero && Native.GetWindowRect(rebar, out var rr) && rr.Width > 0)
                t.IconsLeft = rr.Left - t.Thickness;
            return t;
        }

        public static bool LightTheme() =>
            ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;

        private static int ReadDword(string key, string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                    return k?.GetValue(name) is int v ? v : def;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
            {
                return def;
            }
        }
    }

    /// <summary>
    /// Embeds the bar as a child window of Shell_TrayWnd (falls back to a topmost overlay) and keeps it placed.
    /// </summary>
    internal sealed class TaskbarAttacher
    {
        public const int Margin = 4;

        private IntPtr _attachedTo;
        private IntPtr _rebar;
        private Rectangle _rebarOriginal; // client coords of the rebar before we shifted it
        private int _rebarShiftedLeft = int.MinValue;
        private int _reserved;

        public bool ChildMode { get; private set; }

        public bool IsAttached(IntPtr bar, TaskbarInfo tb) =>
            _attachedTo == tb.Handle && Native.IsWindow(bar) &&
            (ChildMode ? Native.GetParent(bar) == tb.Handle : true);

        /// <summary>Tries child mode first; returns false when it had to fall back to overlay mode.</summary>
        public bool Attach(IntPtr bar, TaskbarInfo tb)
        {
            RestoreRebar();
            var style = Native.GetWindowLong(bar, Native.GWL_STYLE);
            style = (style & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME)) | Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
            Native.SetWindowLong(bar, Native.GWL_STYLE, style);
            var ok = Native.SetParent(bar, tb.Handle) != IntPtr.Zero || Native.GetParent(bar) == tb.Handle;
            if (ok)
            {
                // Per-pixel alpha (UpdateLayeredWindow); BarWindow pushes its bitmap after each render.
                var ex = Native.GetWindowLong(bar, Native.GWL_EXSTYLE) | Native.WS_EX_LAYERED;
                Native.SetWindowLong(bar, Native.GWL_EXSTYLE, ex);
            }
            if (!ok)
            {
                // Overlay: top-level, topmost, same per-pixel alpha rendering.
                Native.SetParent(bar, IntPtr.Zero);
                style = (Native.GetWindowLong(bar, Native.GWL_STYLE) & ~Native.WS_CHILD) | Native.WS_POPUP;
                Native.SetWindowLong(bar, Native.GWL_STYLE, style);
                var ex = Native.GetWindowLong(bar, Native.GWL_EXSTYLE) | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                Native.SetWindowLong(bar, Native.GWL_EXSTYLE, ex);
            }
            ChildMode = ok;
            _attachedTo = tb.Handle;
            return ok;
        }

        /// <summary>Positions the bar; returns its screen rectangle.</summary>
        public Rectangle Place(IntPtr bar, TaskbarInfo tb, Size size)
        {
            var local = LocalPosition(tb, bar, size); // relative to taskbar top-left
            if (ChildMode)
            {
                Native.SetWindowPos(bar, Native.HWND_TOP, local.X, local.Y, size.Width, size.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else
            {
                var hide = Native.ForegroundIsFullscreen();
                Native.SetWindowPos(bar, Native.HWND_TOPMOST, tb.Bounds.X + local.X, tb.Bounds.Y + local.Y, size.Width, size.Height,
                    Native.SWP_NOACTIVATE | (hide ? 0 : Native.SWP_SHOWWINDOW));
                if (hide) System.Windows.Forms.Control.FromHandle(bar)?.Hide();
            }
            return new Rectangle(tb.Bounds.X + local.X, tb.Bounds.Y + local.Y, size.Width, size.Height);
        }

        private Point LocalPosition(TaskbarInfo tb, IntPtr bar, Size size)
        {
            if (!tb.Horizontal)
            {
                // Stack above the tray (Start sits at the top of a vertical taskbar).
                var trayTop = tb.Tray.IsEmpty ? tb.Bounds.Bottom : tb.Tray.Top;
                return new Point(Math.Max(0, (tb.Bounds.Width - size.Width) / 2), Math.Max(0, trayTop - tb.Bounds.Y - size.Height - Margin * 2));
            }
            var y = Math.Max(0, (tb.Bounds.Height - size.Height) / 2);
            if (tb.Rebar != IntPtr.Zero) return new Point(ReserveInRebar(tb, bar, size.Width), y);
            if (tb.Win11 && !tb.CenterAligned && !tb.Tray.IsEmpty)
                return new Point(Math.Max(Margin, tb.Tray.Left - tb.Bounds.X - size.Width - Margin * 2), y);
            return new Point(Margin, y);
        }

        /// <summary>
        /// Win10: shrink the task band from the left so the bar never covers task buttons. The band's
        /// original left edge is derived from its left-hand siblings (Start, search), so a band left shifted
        /// by a killed instance is not shifted twice; a changed bar width re-reserves.
        /// </summary>
        private int ReserveInRebar(TaskbarInfo tb, IntPtr bar, int width)
        {
            if (!Native.GetWindowRect(tb.Rebar, out var r)) return Margin;
            var left = r.Left - tb.Bounds.X;
            var right = r.Right - tb.Bounds.X;
            var top = r.Top - tb.Bounds.Y;
            var need = width + Margin * 2;
            var ours = _rebar == tb.Rebar && left == _rebarShiftedLeft;
            if (!ours || need != _reserved)
            {
                var origLeft = ours ? _rebarOriginal.Left : OriginalRebarLeft(left, SiblingRights(tb, bar));
                _rebar = tb.Rebar;
                _rebarOriginal = new Rectangle(origLeft, top, right - origLeft, r.Height);
                _rebarShiftedLeft = origLeft + need;
                _reserved = need;
                Native.MoveWindow(tb.Rebar, _rebarShiftedLeft, top, Math.Max(0, right - _rebarShiftedLeft), r.Height, true);
            }
            return _rebarOriginal.Left + Margin;
        }

        /// <summary>The rightmost sibling edge at or left of the band, i.e. where the band starts in Explorer's own layout.</summary>
        internal static int OriginalRebarLeft(int rebarLeft, IEnumerable<int> siblingRights)
        {
            var best = int.MinValue;
            foreach (var right in siblingRights)
                if (right <= rebarLeft && right > best) best = right;
            return best == int.MinValue ? rebarLeft : best;
        }

        private static List<int> SiblingRights(TaskbarInfo tb, IntPtr bar)
        {
            var rights = new List<int>();
            var child = IntPtr.Zero;
            while ((child = Native.FindWindowEx(tb.Handle, child, null, null)) != IntPtr.Zero)
            {
                if (child == bar || child == tb.Rebar || !Native.IsWindowVisible(child)) continue;
                if (Native.GetWindowRect(child, out var cr) && cr.Width > 0) rights.Add(cr.Right - tb.Bounds.X);
            }
            return rights;
        }

        public void RestoreRebar()
        {
            if (_rebar != IntPtr.Zero && Native.IsWindow(_rebar))
                Native.MoveWindow(_rebar, _rebarOriginal.X, _rebarOriginal.Y, _rebarOriginal.Width, _rebarOriginal.Height, true);
            _rebar = IntPtr.Zero;
            _rebarShiftedLeft = int.MinValue;
            _reserved = 0;
        }
    }
}
