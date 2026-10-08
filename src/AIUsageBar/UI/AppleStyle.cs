using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AIUsageBar.Core;

namespace AIUsageBar.UI
{
    /// <summary>
    /// Apple-style design tokens (macOS menu bar / Control Center) and the shared ring, bar and chip helpers.
    /// Values follow docs/superpowers/specs/2026-10-08-apple-redesign-design.md §2.
    /// </summary>
    internal sealed class AppleStyle
    {
        public static readonly Color Claude = Color.FromArgb(0xD9, 0x77, 0x57);
        public static readonly Color Codex = Color.FromArgb(0x10, 0xA3, 0x7F);
        public static readonly Color Cursor = Color.FromArgb(0x6E, 0x56, 0xCF);
        public static readonly Color Accent = Color.FromArgb(0x0A, 0x84, 0xFF);

        // After the colour constants: static fields initialise in declaration order.
        private static readonly AppleStyle LightStyle = new AppleStyle(true);
        private static readonly AppleStyle DarkStyle = new AppleStyle(false);

        public static AppleStyle For(bool light) => light ? LightStyle : DarkStyle;

        private AppleStyle(bool light)
        {
            Light = light;
            Label = light ? Rgb(0x1D1D1F) : Rgb(0xF5F5F7);
            Secondary = light ? Rgb(0x6E6E73) : Rgb(0x98989D);
            Separator = light ? Alpha(0.12, 0x000000) : Alpha(0.14, 0xFFFFFF);
            Track = light ? Alpha(0.10, 0x000000) : Alpha(0.16, 0xFFFFFF);
            ModuleFill = light ? Alpha(0.62, 0xFFFFFF) : Alpha(0.08, 0xFFFFFF);
            AcrylicTint = light ? Alpha(0.72, 0xF6F6F8) : Alpha(0.70, 0x1E1E20);
            Warning = light ? Rgb(0xFF9500) : Rgb(0xFF9F0A);
            Critical = light ? Rgb(0xFF3B30) : Rgb(0xFF453A);
            PcAccent = Accent;
        }

        public bool Light { get; }
        public Color Label { get; }
        public Color Secondary { get; }
        public Color Separator { get; }
        public Color Track { get; }
        public Color ModuleFill { get; }
        public Color AcrylicTint { get; }
        public Color Warning { get; }
        public Color Critical { get; }
        public Color PcAccent { get; }

        public Color ServiceColor(string tag)
        {
            switch (tag)
            {
                case "CL": return Claude;
                case "CX": return Codex;
                case "GK": return Label;
                case "CU": return Cursor;
                case "PC": return PcAccent;
                default: return Label;
            }
        }

        public Color StateColor(Severity s, Color normal) =>
            s == Severity.Critical ? Critical : s == Severity.Low ? Warning : normal;

        private static Color Rgb(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));
        private static Color Alpha(double a, int rgb) => Color.FromArgb((int)(a * 255 + 0.5), Color.FromArgb(rgb));

        // ---- Fonts ----
        private static readonly HashSet<string> Families = LoadFamilies();

        private static HashSet<string> LoadFamilies()
        {
            using (var fc = new InstalledFontCollection())
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in fc.Families) set.Add(f.Name);
                return set;
            }
        }

        /// <summary>Segoe UI Variable (Display for big numbers, Text otherwise) with a Segoe UI fallback. Size in px.</summary>
        public static Font Font(float px, bool display, bool semibold)
        {
            var family = display
                ? (semibold ? "Segoe UI Variable Display Semib" : "Segoe UI Variable Display")
                : (semibold ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text");
            if (!Families.Contains(family)) family = semibold ? "Segoe UI Semibold" : "Segoe UI";
            if (!Families.Contains(family)) family = "Segoe UI";
            return new Font(family, px, GraphicsUnit.Pixel);
        }

        /// <summary>Hangul text: Malgun Gothic (Segoe UI Variable has no Hangul glyphs).</summary>
        public static Font Korean(float px, bool semibold) =>
            new Font("Malgun Gothic", px, semibold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

        // ---- Drawing helpers ----

        /// <summary>Arc length in degrees for a remaining percentage, clamped to 0–360.</summary>
        public static float Sweep(double remaining) => (float)(Math.Max(0, Math.Min(100, remaining)) * 3.6);

        /// <summary>Activity-style ring: full track circle, then the remaining arc clockwise from 12 o'clock.</summary>
        public static void Ring(Graphics g, RectangleF r, float stroke, double remaining, Color arc, Color track)
        {
            var inset = stroke / 2;
            var c = new RectangleF(r.X + inset, r.Y + inset, r.Width - stroke, r.Height - stroke);
            using (var p = new Pen(track, stroke))
                g.DrawEllipse(p, c);
            var sweep = Sweep(remaining);
            if (sweep <= 0) return;
            using (var p = new Pen(arc, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (sweep >= 360) g.DrawEllipse(p, c);
                else g.DrawArc(p, c, -90, sweep);
            }
        }

        /// <summary>Rounded progress bar; the fill is never thinner than a dot so tiny values stay visible.</summary>
        public static void Bar(Graphics g, RectangleF r, double fraction, Color fill, Color track)
        {
            Paint.FillRounded(g, track, r, r.Height / 2);
            var f = Math.Max(0, Math.Min(1, fraction));
            if (f <= 0) return;
            Paint.FillRounded(g, fill, new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * f)), r.Height), r.Height / 2);
        }

        /// <summary>Rounded-square service chip with a centred white letter.</summary>
        public static void Chip(Graphics g, RectangleF r, Color fill, string letter, Font f)
        {
            Paint.FillRounded(g, fill, r, r.Height * 0.27f);
            var fg = fill.GetBrightness() > 0.6f ? Color.FromArgb(0x1D, 0x1D, 0x1F) : Color.White;
            var size = Paint.Measure(letter, f);
            using (var b = new SolidBrush(fg))
                g.DrawString(letter, f, b, r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2, Paint.Tight);
        }
    }
}
