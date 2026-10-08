using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AIUsageBar.UI
{
    internal enum BackdropMode { SystemBackdrop, AccentAcrylic, Layered }

    /// <summary>
    /// Base window for the cards: a real Acrylic backdrop where Windows offers one (system backdrop on Windows 11
    /// 22H2+, the older accent-policy blur before that), otherwise a per-pixel-alpha layered window. Content is a
    /// premultiplied bitmap copied straight into the buffered-paint bits, so translucent pixels blend with the blur.
    /// </summary>
    internal class BackdropWindow : Form
    {
        private const int WM_ERASEBKGND = 0x14;
        private Bitmap _frame;

        public BackdropWindow()
        {
            Mode = Choose(Environment.OSVersion.Version.Build, forceLayered: false);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        }

        public static BackdropMode Choose(int osBuild, bool forceLayered)
        {
            if (forceLayered || osBuild < 17763) return BackdropMode.Layered;
            return osBuild >= 22621 ? BackdropMode.SystemBackdrop : BackdropMode.AccentAcrylic;
        }

        public BackdropMode Mode { get; private set; }

        /// <summary>True when Windows draws the card's blur, rounded corners and shadow.</summary>
        public bool HasSystemShadow => Mode != BackdropMode.Layered;

        private bool _light = true;

        public bool Light
        {
            get => _light;
            set
            {
                if (_light == value) return;
                _light = value;
                if (IsHandleCreated) ApplyBackdrop();
            }
        }

        protected virtual bool TopMostWindow => false;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                if (Mode == BackdropMode.Layered) cp.ExStyle |= (int)Native.WS_EX_LAYERED;
                if (TopMostWindow) cp.ExStyle |= (int)Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyBackdrop();
        }

        private void ApplyBackdrop()
        {
            if (Mode == BackdropMode.SystemBackdrop && !Native.TrySystemBackdrop(Handle, !_light)) Mode = BackdropMode.AccentAcrylic;
            if (Mode == BackdropMode.AccentAcrylic && !Native.TryAccentAcrylic(Handle, _light ? 0xB8F8F6F6u : 0xB3201E1Eu))
            {
                Mode = BackdropMode.Layered;
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) | Native.WS_EX_LAYERED);
            }
        }

        /// <summary>Applies the theme and creates the window so <see cref="Mode"/> is final before content is rendered.</summary>
        public BackdropMode Prepare(bool light)
        {
            Light = light;
            if (!IsHandleCreated) CreateHandle();
            return Mode;
        }

        private const int FadeMs = 150, FadeSteps = 8;
        private Timer _fade;
        private Bitmap _fadeFrame;
        private Point _fadePos;
        private int _fadeStep;

        /// <summary>
        /// Shows <paramref name="pargb"/> (premultiplied) at <paramref name="screenPos"/>. With
        /// <paramref name="fadeIn"/>, a window that was hidden fades in over 150 ms; content updates never fade.
        /// </summary>
        public void Present(Bitmap pargb, Point screenPos, bool fadeIn = false)
        {
            var first = !Visible;
            if (Mode == BackdropMode.Layered)
            {
                StopFade();
                if (first)
                {
                    Bounds = new Rectangle(screenPos, pargb.Size);
                    Native.PushLayered(Handle, pargb, screenPos, fadeIn ? (byte)0 : (byte)255);
                    Show();
                    if (fadeIn) StartFade(pargb, screenPos);
                    return;
                }
                Native.PushLayered(Handle, pargb, screenPos);
                return;
            }

            var old = _frame;
            _frame = pargb.Clone(new Rectangle(Point.Empty, pargb.Size), PixelFormat.Format32bppPArgb);
            old?.Dispose();
            if (first)
            {
                Bounds = new Rectangle(screenPos, pargb.Size);
                if (fadeIn) Native.AnimateWindow(Handle, FadeMs, Native.AW_BLEND);
                Show(); // syncs WinForms' visible state; a no-op on screen after AnimateWindow
                return;
            }
            Native.SetWindowPos(Handle, IntPtr.Zero, screenPos.X, screenPos.Y, pargb.Width, pargb.Height,
                Native.SWP_NOACTIVATE | 0x4 /* SWP_NOZORDER */);
            Invalidate();
            Update();
        }

        private void StartFade(Bitmap pargb, Point pos)
        {
            _fadeFrame = pargb.Clone(new Rectangle(Point.Empty, pargb.Size), PixelFormat.Format32bppPArgb);
            _fadePos = pos;
            _fadeStep = 0;
            _fade = new Timer { Interval = FadeMs / FadeSteps };
            _fade.Tick += (s, e) =>
            {
                _fadeStep++;
                var alpha = (byte)Math.Min(255, 255 * _fadeStep / FadeSteps);
                Native.PushLayered(Handle, _fadeFrame, _fadePos, alpha);
                if (_fadeStep >= FadeSteps) StopFade();
            };
            _fade.Start();
        }

        private void StopFade()
        {
            if (_fade == null) return;
            _fade.Dispose();
            _fade = null;
            if (_fadeStep < FadeSteps && Visible) Native.PushLayered(Handle, _fadeFrame, _fadePos);
            _fadeFrame.Dispose();
            _fadeFrame = null;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) StopFade();
            base.OnVisibleChanged(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND && Mode != BackdropMode.Layered)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Mode == BackdropMode.Layered || _frame == null) return;
            var hdc = e.Graphics.GetHdc();
            try
            {
                Native.CopyToBufferedPaint(hdc, _frame);
            }
            finally
            {
                e.Graphics.ReleaseHdc(hdc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopFade();
                _frame?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
