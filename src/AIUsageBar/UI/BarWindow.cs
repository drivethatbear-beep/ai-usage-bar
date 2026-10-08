using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AIUsageBar.Core;

namespace AIUsageBar.UI
{
    /// <summary>
    /// The strip that lives inside the taskbar (capsule design). Rendered with per-pixel alpha (UpdateLayeredWindow):
    /// the background is alpha 1 — invisible, yet the whole strip still receives the mouse.
    /// </summary>
    internal sealed class BarWindow : Form
    {
        private static readonly Color HitBackground = Color.FromArgb(1, 0, 0, 0);
        private const int VerticalGap = 3;

        private readonly BarApp _app;
        private readonly Timer _hover = new Timer { Interval = 300 };

        public BarRenderer Renderer { get; set; } = new BarRenderer(BarRenderer.Level100);
        public bool Vertical { get; set; }
        public bool BlinkPhase { get; set; }

        public BarWindow(BarApp app)
        {
            _app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "AI Usage Bar";
            Size = new Size(10, 10);
            _hover.Tick += (s, e) => { _hover.Stop(); _app.ShowPopup(pinned: false); };
            MouseEnter += (s, e) => _hover.Start();
            MouseLeave += (s, e) => { _hover.Stop(); _app.HidePopupUnlessPinned(); };
            MouseUp += OnMouseUp;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
                cp.ExStyle &= ~(int)Native.WS_EX_APPWINDOW;
                return cp;
            }
        }

        public Size Measure(IReadOnlyList<ServiceUsage> items)
        {
            var pc = _app.SystemOrNull();
            if (!Vertical) return Renderer.MeasureBar(items, pc);
            var one = Renderer.MeasureBar(new List<ServiceUsage>(1) { null });
            var n = Math.Max(1, items.Count + (pc != null ? 1 : 0));
            return new Size(one.Width, n * one.Height + (n - 1) * VerticalGap * Renderer.Dot / 2);
        }

        /// <summary>Renders the current data at <paramref name="size"/> and pushes it to the layered window.</summary>
        public void Redraw(Size size)
        {
            if (!IsHandleCreated || size.Width <= 0 || size.Height <= 0) return;
            using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(HitBackground);
                    Draw(g);
                }
                Native.PushLayered(Handle, bmp);
            }
        }

        private void Draw(Graphics g)
        {
            var items = _app.VisibleItems();
            var now = DateTimeOffset.Now;
            var pc = _app.SystemOrNull();
            if (!Vertical)
            {
                Renderer.DrawBar(g, items, now, BlinkPhase, pc);
                return;
            }
            var step = Renderer.MeasureBar(new List<ServiceUsage>(1) { null }).Height + VerticalGap * Renderer.Dot / 2;
            for (var i = 0; i < items.Count; i++)
            {
                var state = g.Save();
                g.TranslateTransform(0, i * step);
                Renderer.DrawBar(g, new List<ServiceUsage> { items[i] }, now, BlinkPhase);
                g.Restore(state);
            }
            if (pc != null)
            {
                g.TranslateTransform(0, items.Count * step);
                Renderer.DrawBar(g, new List<ServiceUsage>(), now, BlinkPhase, pc);
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _app.TogglePinnedPopup();
            else if (e.Button == MouseButtons.Right) _app.ShowMenu(Cursor.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hover.Dispose();
            base.Dispose(disposing);
        }
    }
}
