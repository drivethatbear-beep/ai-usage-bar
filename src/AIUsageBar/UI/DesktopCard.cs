using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace AIUsageBar.UI
{
    /// <summary>
    /// The detail card kept open on the desktop as a widget (optional). A frosted (or, as a fallback, layered)
    /// non-activating tool window that can be dragged anywhere; by default it sits at normal z-order (other
    /// windows cover it), or stays on top.
    /// </summary>
    internal sealed class DesktopCard : BackdropWindow
    {
        private const int WM_NCHITTEST = 0x84, HTCAPTION = 2, WM_EXITSIZEMOVE = 0x232, WM_NCRBUTTONUP = 0xA5, WM_NCLBUTTONDBLCLK = 0xA3,
            WM_MOUSEWHEEL = 0x20A, MK_CONTROL = 0x8;

        private bool _topMost;

        /// <summary>Raised after the user finishes dragging the card (screen position of its top-left).</summary>
        public event Action<Point> Moved;

        /// <summary>Raised on right-click; the owner shows the card's menu at the given screen point.</summary>
        public event Action<Point> MenuRequested;

        /// <summary>Raised on Ctrl + mouse wheel over the card with the raw wheel delta (+120 per notch up).</summary>
        public event Action<int> ScaleRequested;

        /// <summary>One wheel notch = 10 %, rounded to the 10 % grid, kept within the allowed range.</summary>
        public static double StepScale(double current, int wheelDelta)
        {
            var steps = wheelDelta / 120.0;
            var next = Math.Round((current + steps * 0.1) * 10, MidpointRounding.AwayFromZero) / 10;
            return Math.Max(Settings.MinCardScale, Math.Min(Settings.MaxCardScale, next));
        }

        /// <summary>Display scale of the monitor the card is on (null before it is shown).</summary>
        public float? MonitorScale()
        {
            if (!IsHandleCreated || !Visible) return null;
            var dpi = Native.GetDpiForWindow(Handle);
            return dpi == 0 ? (float?)null : dpi / 96f;
        }

        /// <summary>The real top-left on screen (Form.Location can lag behind native moves across monitors).</summary>
        private Point? ActualPosition()
        {
            if (!IsHandleCreated || !Visible || !Native.GetWindowRect(Handle, out var r)) return null;
            return new Point(r.Left, r.Top);
        }

        public DesktopCard()
        {
            MaximizeBox = false;
            MinimizeBox = false;
            Text = "AI Usage Card";
        }

        protected override bool TopMostWindow => _topMost;

        /// <summary>
        /// Saved position if the card fits a screen (pulled fully inside the screen it mostly overlaps);
        /// otherwise the bottom-right corner of the primary work area.
        /// </summary>
        public static Point ResolvePosition(Point? saved, Size size, IEnumerable<Rectangle> workAreas, Rectangle primary, int margin)
        {
            var fallback = new Point(primary.Right - size.Width - margin, primary.Bottom - size.Height - margin);
            if (!saved.HasValue) return fallback;
            var rect = new Rectangle(saved.Value, size);
            Rectangle best = Rectangle.Empty;
            var bestArea = 0;
            foreach (var wa in workAreas)
            {
                var i = Rectangle.Intersect(wa, rect);
                var area = i.Width * i.Height;
                if (area > bestArea) { bestArea = area; best = wa; }
            }
            if (bestArea * 2 < size.Width * size.Height) return fallback; // mostly off every screen
            var x = Math.Max(best.Left, Math.Min(rect.X, best.Right - size.Width));
            var y = Math.Max(best.Top, Math.Min(rect.Y, best.Bottom - size.Height));
            return new Point(x, y);
        }

        public void SetTopMost(bool topMost)
        {
            if (_topMost == topMost && IsHandleCreated) return;
            _topMost = topMost;
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, topMost ? Native.HWND_TOPMOST : new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Renders the card at <paramref name="saved"/> (or the default corner) and shows it.</summary>
        public void Render(List<CardSection> sections, string updated, float scale, bool light, Point? saved)
        {
            var backdrop = Prepare(light) != BackdropMode.Layered;
            using (var r = new CardRenderer(scale, light) { Backdrop = backdrop })
            {
                var size = r.Measure(sections);
                // Re-resolve on every render: the card grows as data arrives, so keep it inside the screen.
                var anchor = ActualPosition() ?? saved;
                var pos = ResolvePosition(anchor, size, Screen.AllScreens.Select(s => s.WorkingArea), Screen.PrimaryScreen.WorkingArea, 24);
                var first = !Visible;
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        r.Draw(g, sections, updated);
                    }
                    Present(bmp, pos);
                }
                if (first) SetTopMost(_topMost);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    if ((int)m.Result != 0) m.Result = (IntPtr)HTCAPTION; // drag anywhere on the card
                    return;
                case WM_NCLBUTTONDBLCLK:
                    return; // no maximize on double-click
                case WM_NCRBUTTONUP:
                    MenuRequested?.Invoke(Cursor.Position);
                    return; // instead of the system menu
                case WM_EXITSIZEMOVE:
                    base.WndProc(ref m);
                    Moved?.Invoke(ActualPosition() ?? Location);
                    return;
                case WM_MOUSEWHEEL:
                    var w = (long)m.WParam;
                    if ((w & MK_CONTROL) != 0)
                    {
                        ScaleRequested?.Invoke((short)((w >> 16) & 0xFFFF));
                        return;
                    }
                    break;
            }
            base.WndProc(ref m);
        }
    }
}
