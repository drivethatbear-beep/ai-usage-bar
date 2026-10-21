using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using AIUsageBar.Core;
using AIUsageBar.Providers;

namespace AIUsageBar.UI
{
    /// <summary>Shared drawing helpers for the bar and the detail card.</summary>
    internal static class Paint
    {
        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var path = Rounded(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        public static Color Fade(Color c, Color toward, float amount) => Color.FromArgb(c.A,
            (int)(c.R + (toward.R - c.R) * amount), (int)(c.G + (toward.G - c.G) * amount), (int)(c.B + (toward.B - c.B) * amount));

        /// <summary>Measures and draws without GDI+'s default padding so layouts line up.</summary>
        public static readonly StringFormat Tight = CreateTight();

        private static StringFormat CreateTight()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        private static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));

        public static SizeF Measure(string s, Font f)
        {
            lock (Measurer) return Measurer.MeasureString(s ?? "", f, PointF.Empty, Tight);
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Grayscale AA: ClearType cannot blend onto a transparent layered surface.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
    }


    /// <summary>
    /// The taskbar strip, macOS menu-bar style: per service a small ring in the service colour (remaining of the
    /// tightest window), the name and the remaining numbers ("95 · 58"), optionally the next reset time. Text is
    /// monochrome; orange/red mark low/critical windows. Sizes are px at 100 % and scale with <see cref="Dot"/> (4 = 100 %, 6 = 150 %).
    /// </summary>
    internal sealed class BarRenderer : IDisposable
    {
        public static readonly Color ClaudeColor = AppleStyle.Claude;
        public static readonly Color CodexColor = AppleStyle.Codex;
        public static readonly Color GrokDark = Color.FromArgb(0x1D, 0x1D, 0x1F);
        public static readonly Color GrokLight = Color.FromArgb(0xF5, 0xF5, 0xF7);
        public static readonly Color CursorColor = AppleStyle.Cursor;
        public static readonly Color PcColor = AppleStyle.Accent;
        public static readonly Color LowColor = AppleStyle.For(true).Warning;
        public static readonly Color CriticalColor = AppleStyle.For(true).Critical;
        public static readonly Color CriticalText = AppleStyle.For(true).Critical;

        private const float PadX = 6, Ring = 18, RingStroke = 2.8f, Gap = 5, SepPad = 6, SepH = 20, Height = 34;
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
        // Thin spaces around the dot keep "95·58" readable without the width of full spaces.
        private static readonly string Dot3 = (char)0x2009 + "·" + (char)0x2009;

        private readonly float _s;
        private readonly Font _name, _num, _small, _ko, _pcLabel, _pcNum;

        public const int Level100 = 4;

        public BarRenderer(int dot)
        {
            Dot = Math.Max(1, dot);
            _s = Dot / 4f;
            _name = AppleStyle.Font(15f * _s, display: false, semibold: false);
            _num = AppleStyle.Font(15.5f * _s, display: false, semibold: true);
            _small = AppleStyle.Font(14f * _s, display: false, semibold: false);
            _ko = AppleStyle.Korean(13.5f * _s, semibold: false);
            _pcLabel = AppleStyle.Font(12f * _s, display: false, semibold: false);
            _pcNum = AppleStyle.Font(13f * _s, display: false, semibold: true);
        }

        /// <summary>Size level in 25 % steps: 4 = 100 %. An integer so layout fitting can step it down.</summary>
        public int Dot { get; }
        public bool Light { get; set; } = true;
        public bool ShowReset { get; set; }
        /// <summary>Service names next to the rings; hidden (ring and numbers only) when the taskbar is short on room.</summary>
        public bool ShowNames { get; set; } = true;
        /// <summary>Every window's number ("95·58"); false shows only the tightest one, the same value as the ring.</summary>
        public bool ShowAllWindows { get; set; } = true;

        private AppleStyle St => AppleStyle.For(Light);
        public Color Muted => St.Secondary;
        public Color ColorFor(string tag) => St.ServiceColor(tag);

        private float S(float v) => v * _s;
        private float BarH => S(Height);
        private float ResetSlot => Math.Max(Paint.Measure("00:00", _small).Width, Paint.Measure("00/00", _small).Width);
        private float SepW => S(SepPad) * 2 + 1;

        public Size MeasureBar(IReadOnlyList<ServiceUsage> items, SystemSnapshot pc = null)
        {
            var now = DateTimeOffset.Now;
            var w = S(PadX) * 2;
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) w += SepW;
                w += items[i] == null ? PlaceholderW() : Block(null, 0, 0, items[i], now, false);
            }
            if (pc != null)
            {
                if (items.Count > 0) w += SepW;
                w += PcBlock(null, 0, 0, pc);
            }
            return new Size((int)Math.Ceiling(w), (int)Math.Ceiling(BarH));
        }

        public void DrawBar(Graphics g, IReadOnlyList<ServiceUsage> items, DateTimeOffset now, bool blinkPhase, SystemSnapshot pc = null)
        {
            Paint.Prepare(g);
            var x = S(PadX);
            var mid = BarH / 2;
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) x = Separator(g, x, mid);
                x += Block(g, x, mid, items[i], now, blinkPhase);
            }
            if (pc != null)
            {
                if (items.Count > 0) x = Separator(g, x, mid);
                PcBlock(g, x, mid, pc);
            }
        }

        private float Separator(Graphics g, float x, float mid)
        {
            x += S(SepPad);
            var lx = (float)Math.Round(x) + 0.5f;
            using (var p = new Pen(St.Separator, 1))
                g.DrawLine(p, lx, mid - S(SepH) / 2, lx, mid + S(SepH) / 2);
            return x + 1 + S(SepPad);
        }

        /// <summary>Widest block a vertical stack needs: a named service with two 3-digit windows, or the PC block.</summary>
        private float PlaceholderW()
        {
            var svc = S(Ring) + S(Gap) + (ShowNames ? Paint.Measure("Cursor", _name).Width + S(Gap) : 0)
                + Paint.Measure(ShowAllWindows ? "100" + Dot3 + "100" : "100", _num).Width + (ShowReset ? S(Gap) + ResetSlot : 0);
            var pc = PcBlock(null, 0, 0, new SystemSnapshot { CpuPercent = 100, MemPercent = 100, CpuTempC = 100, GpuUtilPercent = 100, GpuTempC = 100, VramUsedGb = 1, VramTotalGb = 1 });
            return Math.Max(svc, pc);
        }

        /// <summary>One service. Measures when <paramref name="g"/> is null, draws otherwise; returns the width.</summary>
        private float Block(Graphics g, float x0, float mid, ServiceUsage u, DateTimeOffset now, bool blink)
        {
            var st = St;
            var stale = u.LastSuccess.HasValue && now - u.LastSuccess.Value > StaleAfter;
            Color Dim(Color c) => stale ? Color.FromArgb(c.A / 2, c) : c;

            var windows = u.Status == ServiceStatus.Ok || u.Status == ServiceStatus.Error
                ? u.Windows.GetRange(0, Math.Min(2, u.Windows.Count)) : new List<UsageWindow>();

            // Ring: the tightest window decides the arc and its colour; a critical arc blinks.
            UsageWindow tight = null;
            foreach (var w in windows)
                if (tight == null || w.RemainingAt(now) < tight.RemainingAt(now)) tight = w;
            if (!ShowAllWindows && tight != null) windows = new List<UsageWindow> { tight };
            if (g != null)
            {
                var rect = new RectangleF(x0, mid - S(Ring) / 2, S(Ring), S(Ring));
                var sev = tight?.SeverityAt(now) ?? Severity.Normal;
                // Rings always carry the service colour (as on the card chips) so each one says which service it is;
                // low windows show in the numbers, and only a critical arc turns red and blinks. The track is a faint
                // tint of the same colour so a service without data is still recognisable.
                var svc = st.ServiceColor(u.Tag);
                var arc = sev == Severity.Critical ? (blink ? st.Critical : Color.Transparent) : svc;
                AppleStyle.Ring(g, rect, S(RingStroke), tight?.RemainingAt(now) ?? 0, Dim(arc), Dim(Color.FromArgb(70, svc)));
            }
            var x = x0 + S(Ring) + S(Gap);

            if (ShowNames)
                x += Text(g, CardContent.NameFor(u.Tag), _name, Dim(st.Label), x, mid) + S(Gap);

            if (u.Status == ServiceStatus.NeedsLogin)
                x += Text(g, "로그인 필요", _ko, Dim(st.Warning), x, mid);
            else if (windows.Count == 0 && u.Status == ServiceStatus.Ok && u.Detail("WEEK") != null)
                x += Text(g, u.Detail("WEEK"), _num, Dim(st.Label), x, mid);
            else if (windows.Count == 0)
                x += Text(g, "—", _small, Dim(st.Secondary), x, mid);
            else
            {
                var digits = Paint.Measure("00", _num).Width;
                for (var i = 0; i < windows.Count; i++)
                {
                    if (i > 0) x += Text(g, Dot3, _num, Dim(st.Secondary), x, mid);
                    var w = windows[i];
                    var num = Math.Round(w.RemainingAt(now)).ToString("0", CultureInfo.InvariantCulture);
                    x += Math.Max(digits, Text(g, num, _num, Dim(st.StateColor(w.SeverityAt(now), st.Label)), x, mid));
                }
            }

            if (ShowReset)
            {
                DateTimeOffset? next = null;
                foreach (var w in windows)
                    if (w.ResetsAt.HasValue && w.ResetsAt.Value > now && (next == null || w.ResetsAt.Value < next)) next = w.ResetsAt;
                x += S(Gap);
                if (next.HasValue) Text(g, Fmt.ResetShort(next.Value, now), _small, Dim(st.Secondary), x, mid);
                x += ResetSlot;
            }
            return x - x0;
        }

        /// <summary>
        /// The PC in two short rows like the taskbar clock ("CPU 35%·46°" over "RAM 51%"), so it takes half the
        /// width of one line: labels in the PC blue, values in the label colour or warning colours.
        /// </summary>
        private float PcBlock(Graphics g, float x0, float mid, SystemSnapshot pc)
        {
            var st = St;
            var top = mid - S(8.5f);
            var bottom = mid + S(8.5f);
            var labelW = Math.Max(Paint.Measure("CPU", _pcLabel).Width, Paint.Measure("RAM", _pcLabel).Width) + S(4);
            Text(g, "CPU", _pcLabel, st.PcAccent, x0, top);
            Text(g, "RAM", _pcLabel, st.PcAccent, x0, bottom);

            var gpu = pc.GpuUtilPercent.HasValue;
            var cpu = pc.CpuPercent ?? 0;
            var x = x0 + labelW;
            x += Text(g, Pct(cpu), _pcNum, st.StateColor(CardContent.UsageSeverity(cpu), st.Label), x, top);
            // With a GPU column its temperature sits there; otherwise the GPU's stands in for a missing CPU sensor.
            x += Temp(g, gpu ? pc.CpuTempC : pc.CpuTempC ?? pc.GpuTempC, x, top);
            var ram = x0 + labelW + Text(g, Pct(pc.MemPercent), _pcNum, st.StateColor(CardContent.UsageSeverity(pc.MemPercent), st.Label), x0 + labelW, bottom);
            var right = Math.Max(x, ram);
            if (!gpu) return right - x0;

            // Second column: "GPU 12%·52°" over "VRAM 30%".
            var x1 = right + S(Gap) * 2;
            var gpuLabelW = Math.Max(Paint.Measure("GPU", _pcLabel).Width, Paint.Measure("VRAM", _pcLabel).Width) + S(4);
            Text(g, "GPU", _pcLabel, st.PcAccent, x1, top);
            var gx = x1 + gpuLabelW;
            var util = pc.GpuUtilPercent.Value;
            gx += Text(g, Pct(util), _pcNum, st.StateColor(CardContent.UsageSeverity(util), st.Label), gx, top);
            gx += Temp(g, pc.GpuTempC, gx, top);
            var vx = x1 + gpuLabelW;
            if (pc.VramUsedGb.HasValue && pc.VramTotalGb > 0)
            {
                var v = pc.VramUsedGb.Value / pc.VramTotalGb.Value * 100;
                Text(g, "VRAM", _pcLabel, st.PcAccent, x1, bottom);
                vx += Text(g, Pct(v), _pcNum, st.StateColor(CardContent.UsageSeverity(v), st.Label), vx, bottom);
            }
            return Math.Max(gx, vx) - x0;
        }

        /// <summary>"·52°" after a usage value; orange from 80°, red from 90°. Returns its width (0 without a reading).</summary>
        private float Temp(Graphics g, double? celsius, float x, float mid)
        {
            if (!celsius.HasValue) return 0;
            var st = St;
            var t = celsius.Value;
            var w = Text(g, Dot3, _pcNum, st.Secondary, x, mid);
            return w + Text(g, Math.Round(t).ToString("0", CultureInfo.InvariantCulture) + "°", _pcNum, t >= 90 ? st.Critical : t >= 80 ? st.Warning : st.Label, x + w, mid);
        }

        private static string Pct(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%";

        /// <summary>Draws <paramref name="s"/> vertically centred on <paramref name="mid"/> (when g is set); returns its width.</summary>
        private static float Text(Graphics g, string s, Font f, Color c, float x, float mid)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var size = Paint.Measure(s, f);
            if (g != null && c.A > 0)
                using (var b = new SolidBrush(c))
                    g.DrawString(s, f, b, x, mid - size.Height / 2, Paint.Tight);
            return size.Width;
        }

        public void Dispose()
        {
            _name.Dispose();
            _num.Dispose();
            _small.Dispose();
            _ko.Dispose();
            _pcLabel.Dispose();
            _pcNum.Dispose();
        }
    }
}
