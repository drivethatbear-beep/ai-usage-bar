using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AIUsageBar.Core;

namespace AIUsageBar.UI
{
    /// <summary>
    /// Draws the detail card in macOS Control Center style: a header, then one rounded module per service
    /// (letter chip, name, plan; per limit a big remaining number, countdown, thin bar and reset date) and the PC
    /// as three small modules (CPU ring, memory ring, temperature). With <see cref="Backdrop"/> the window's
    /// Acrylic shows through and nothing is drawn outside the modules; without it (layered fallback) the card
    /// gets a solid tinted body and a soft faux shadow. Sizes are px at 100 % and scale with the factor passed in.
    /// </summary>
    internal sealed class CardRenderer : IDisposable
    {
        private const float Shadow = 16, Width = 380, Pad = 12, Radius = 22, ModRadius = 16, ModPadX = 14, ModPadY = 12,
            ModGap = 8, Chip = 26, BarH = 6, MiniRing = 44, MiniStroke = 4.5f;

        private readonly float _s;
        private readonly AppleStyle _st;
        private readonly Font _title, _updated, _name, _plan, _chip, _big, _caption, _small, _miniValue;

        public CardRenderer(float scale, bool light)
        {
            _s = Math.Max(0.5f, scale);
            _st = AppleStyle.For(light);
            _title = AppleStyle.Korean(17.5f * _s, semibold: true);
            _updated = AppleStyle.Korean(14f * _s, semibold: false);
            _name = AppleStyle.Font(15.5f * _s, display: false, semibold: true);
            _plan = AppleStyle.Font(14f * _s, display: false, semibold: false);
            _chip = AppleStyle.Font(13.5f * _s, display: false, semibold: true);
            _big = AppleStyle.Font(31f * _s, display: true, semibold: true);
            _caption = AppleStyle.Korean(14f * _s, semibold: false);
            _small = AppleStyle.Korean(14f * _s, semibold: false);
            _miniValue = AppleStyle.Font(18f * _s, display: true, semibold: true);
        }

        /// <summary>True when the window supplies the frosted background, rounded corners and shadow.</summary>
        public bool Backdrop { get; set; }

        private float S(float v) => v * _s;
        private float Margin => Backdrop ? 0 : S(Shadow);

        public Size Measure(List<CardSection> sections)
        {
            var h = Layout(null, sections, "");
            return new Size((int)Math.Ceiling(S(Width) + Margin * 2), (int)Math.Ceiling(h + Margin * 2));
        }

        public void Draw(Graphics g, List<CardSection> sections, string updated)
        {
            Paint.Prepare(g);
            if (!Backdrop)
            {
                var card = new RectangleF(Margin, Margin, S(Width), Layout(null, sections, updated));
                for (var i = 6; i >= 1; i--)
                {
                    var grow = S(i * 2f);
                    var r = new RectangleF(card.X - grow / 2, card.Y - grow / 2 + S(3), card.Width + grow, card.Height + grow);
                    Paint.FillRounded(g, Color.FromArgb(_st.Light ? 7 : 14, 0, 0, 0), r, S(Radius) + grow / 2);
                }
                // No blur behind a layered window: the tint is made nearly opaque so text stays readable.
                Paint.FillRounded(g, Color.FromArgb(245, _st.AcrylicTint), card, S(Radius));
                using (var path = Paint.Rounded(card, S(Radius)))
                using (var pen = new Pen(_st.Separator, 1))
                    g.DrawPath(pen, path);
            }
            Layout(g, sections, updated);
        }

        /// <summary>Single source of truth for positions: measures when <paramref name="g"/> is null, draws otherwise. Returns the card height.</summary>
        private float Layout(Graphics g, List<CardSection> sections, string updated)
        {
            var left = Margin + S(Pad);
            var right = Margin + S(Width) - S(Pad);
            var y = Margin + S(Pad) + S(2);

            var title = Paint.Measure("AI 사용량", _title);
            DrawText(g, "AI 사용량", _title, _st.Label, left + S(4), y);
            if (!string.IsNullOrEmpty(updated))
                DrawTextRight(g, updated, _updated, _st.Secondary, right - S(4), y + title.Height - Paint.Measure(updated, _updated).Height);
            y += title.Height + S(10);

            foreach (var s in sections)
            {
                y = s.Tag == "PC" ? PcModules(g, s, left, right, y) : Module(g, s, left, right, y);
                y += S(ModGap);
            }
            return y - S(ModGap) + S(Pad) - Margin;
        }

        private float Module(Graphics g, CardSection s, float left, float right, float top)
        {
            var h = ModuleBody(null, s, left, right, top);
            if (g != null)
            {
                Paint.FillRounded(g, _st.ModuleFill, new RectangleF(left, top, right - left, h), S(ModRadius));
                ModuleBody(g, s, left, right, top);
            }
            return top + h;
        }

        /// <summary>Contents of one service module; returns its height.</summary>
        private float ModuleBody(Graphics g, CardSection s, float left, float right, float top)
        {
            var svc = _st.ServiceColor(s.Tag);
            var l = left + S(ModPadX);
            var r = right - S(ModPadX);
            var y = top + S(ModPadY);

            // Header: chip, name, plan.
            var chip = new RectangleF(l, y, S(Chip), S(Chip));
            if (g != null) AppleStyle.Chip(g, chip, svc, Letter(s.Tag), _chip);
            var ns = Paint.Measure(s.Name, _name);
            DrawText(g, s.Name, _name, _st.Label, l + S(Chip) + S(8), y + (S(Chip) - ns.Height) / 2);
            if (!string.IsNullOrEmpty(s.Plan))
                DrawTextRight(g, s.Plan, _plan, _st.Secondary, r, y + (S(Chip) - Paint.Measure(s.Plan, _plan).Height) / 2);
            y += S(Chip);

            if (s.Notice != null)
            {
                y += S(8);
                DrawText(g, s.Notice, _small, s.NoticeKind == NoticeKind.Warning ? _st.Warning : _st.Secondary, l, y);
                y += Paint.Measure(s.Notice, _small).Height;
            }

            foreach (var row in s.Rows)
            {
                y += S(10);
                var num = row.Percent.TrimEnd('%');
                var big = Paint.Measure(num, _big);
                var stateColor = _st.StateColor(row.Severity, _st.Label);
                DrawText(g, num, _big, stateColor, l, y);
                var caption = "% " + (row.Window ?? "") + " 남음";
                var cap = Paint.Measure(caption, _caption);
                var baseline = y + big.Height * 0.80f;
                DrawText(g, caption, _caption, _st.Secondary, l + big.Width + S(3), baseline - cap.Height * 0.82f);
                if (!string.IsNullOrEmpty(row.InText))
                    DrawTextRight(g, row.InText, _small, _st.Secondary, r, baseline - Paint.Measure(row.InText, _small).Height * 0.82f);
                y += big.Height * 0.92f + S(4);

                if (g != null)
                    AppleStyle.Bar(g, new RectangleF(l, y, r - l, S(BarH)), row.Remaining / 100, _st.StateColor(row.Severity, svc), _st.Track);
                y += S(BarH);
                if (!string.IsNullOrEmpty(row.ResetText))
                {
                    y += S(6);
                    DrawText(g, row.ResetText, _small, _st.Secondary, l, y);
                    y += Paint.Measure(row.ResetText, _small).Height;
                }
            }

            if (s.Extras.Count > 0) y += S(8);
            foreach (var (label, value) in s.Extras)
            {
                DrawText(g, label, _small, _st.Secondary, l, y);
                DrawTextRight(g, value, _small, _st.Label, r, y);
                y += S(22);
            }
            return y + S(ModPadY) - top;
        }

        /// <summary>The PC as a row of small modules (CPU, memory, temperature), then any other PC lines.</summary>
        private float PcModules(Graphics g, CardSection s, float left, float right, float top)
        {
            var minis = new List<Action<Graphics, RectangleF>>();
            foreach (var row in s.Rows)
            {
                var r = row;
                var sub = r.Window == "메모리" && !string.IsNullOrEmpty(r.ResetText)
                    ? "메모리 " + r.ResetText.Split('/')[0].Trim() + " GB" : r.Window;
                minis.Add((gr, box) => MiniRingModule(gr, box, r.Remaining, _st.StateColor(r.Severity, _st.PcAccent), r.Percent, sub));
            }
            var temp = s.Extras.FirstOrDefault(e => e.label == "CPU 온도");
            if (temp.label == null) temp = s.Extras.FirstOrDefault(e => e.label == "GPU 온도");
            if (temp.label != null)
            {
                var t = temp;
                minis.Add((gr, box) => MiniTempModule(gr, box, t.value.Replace("°C", "°"), t.label, TempValue(t.value)));
            }
            var others = s.Extras.Where(e => e.label != "CPU 온도" && e.label != "GPU 온도").ToList();

            var y = top;
            if (minis.Count > 0)
            {
                var gap = S(ModGap);
                var w = (right - left - gap * (minis.Count - 1)) / minis.Count;
                var h = S(ModPadY) * 2 + S(MiniRing) + S(6) + Paint.Measure("0%", _miniValue).Height + Paint.Measure("메모리", _small).Height;
                if (g != null)
                    for (var i = 0; i < minis.Count; i++)
                    {
                        var box = new RectangleF(left + i * (w + gap), y, w, h);
                        Paint.FillRounded(g, _st.ModuleFill, box, S(ModRadius));
                        minis[i](g, box);
                    }
                y += h;
            }
            if (others.Count > 0)
            {
                y += S(ModGap);
                var h = S(ModPadY) * 2 + others.Count * S(22) - S(3);
                if (g != null) Paint.FillRounded(g, _st.ModuleFill, new RectangleF(left, y, right - left, h), S(ModRadius));
                var ly = y + S(ModPadY);
                foreach (var (label, value) in others)
                {
                    DrawText(g, label, _small, _st.Secondary, left + S(ModPadX), ly);
                    DrawTextRight(g, value, _small, _st.Label, right - S(ModPadX), ly);
                    ly += S(22);
                }
                y += h;
            }
            return y;
        }

        private void MiniRingModule(Graphics g, RectangleF box, double used, Color arc, string value, string label)
        {
            var cx = box.X + box.Width / 2;
            var ring = new RectangleF(cx - S(MiniRing) / 2, box.Y + S(ModPadY), S(MiniRing), S(MiniRing));
            AppleStyle.Ring(g, ring, S(MiniStroke), used, arc, _st.Track);
            MiniLabels(g, box, ring.Bottom, value, label);
        }

        private void MiniTempModule(Graphics g, RectangleF box, string value, string label, double celsius)
        {
            var area = new RectangleF(box.X, box.Y + S(ModPadY), box.Width, S(MiniRing));
            var color = celsius >= 90 ? _st.Critical : celsius >= 80 ? _st.Warning : _st.Label;
            var size = Paint.Measure(value, _big);
            DrawText(g, value, _big, color, area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
            MiniLabels(g, box, area.Bottom, "", label.Replace(" 온도", "") + " 온도");
        }

        private void MiniLabels(Graphics g, RectangleF box, float y, string value, string label)
        {
            var cx = box.X + box.Width / 2;
            y += S(6);
            var vs = Paint.Measure(string.IsNullOrEmpty(value) ? "0" : value, _miniValue);
            if (!string.IsNullOrEmpty(value)) DrawText(g, value, _miniValue, _st.Label, cx - vs.Width / 2, y);
            y += vs.Height;
            var ls = Paint.Measure(label, _small);
            DrawText(g, label, _small, _st.Secondary, cx - ls.Width / 2, y);
        }

        private static double TempValue(string text)
        {
            var digits = new string((text ?? "").TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static string Letter(string tag)
        {
            switch (tag)
            {
                case "CL": return "C";
                case "CX": return "X";
                case "GK": return "G";
                case "CU": return "Cu";
                default: return string.IsNullOrEmpty(tag) ? "?" : tag.Substring(0, 1);
            }
        }

        private static void DrawText(Graphics g, string text, Font f, Color c, float x, float y)
        {
            if (g == null || string.IsNullOrEmpty(text)) return;
            using (var b = new SolidBrush(c))
                g.DrawString(text, f, b, x, y, Paint.Tight);
        }

        private static void DrawTextRight(Graphics g, string text, Font f, Color c, float right, float y) =>
            DrawText(g, text, f, c, right - Paint.Measure(text, f).Width, y);

        public void Dispose()
        {
            _title.Dispose();
            _updated.Dispose();
            _name.Dispose();
            _plan.Dispose();
            _chip.Dispose();
            _big.Dispose();
            _caption.Dispose();
            _small.Dispose();
            _miniValue.Dispose();
        }
    }
}
