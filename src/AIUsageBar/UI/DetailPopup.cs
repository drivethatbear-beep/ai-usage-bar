using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace AIUsageBar.UI
{
    /// <summary>
    /// The detail card above (or below) the bar: a frosted Acrylic window where Windows offers one, otherwise a
    /// layered window with per-pixel alpha. Fades in when it opens; content refreshes do not fade.
    /// </summary>
    internal sealed class DetailPopup : BackdropWindow
    {
        private const int Gap = 4; // px between the card (or its faux shadow) and the bar

        public DetailPopup()
        {
            Text = "AI Usage";
        }

        protected override bool TopMostWindow => true;

        /// <summary>Renders the card and places it next to <paramref name="anchor"/> (screen coords).</summary>
        public void Show(List<CardSection> sections, string updated, float scale, bool light, Rectangle anchor, bool taskbarAtTop)
        {
            var backdrop = Prepare(light) != BackdropMode.Layered;
            using (var r = new CardRenderer(scale, light) { Backdrop = backdrop })
            {
                var size = r.Measure(sections);
                var margin = backdrop ? 0 : (int)(16 * scale);
                var gap = (int)((backdrop ? 8 : Gap) * scale);
                var wa = Screen.FromRectangle(anchor).WorkingArea;
                var x = Math.Max(wa.Left, Math.Min(anchor.Left - margin + (int)(4 * scale), wa.Right - size.Width));
                var y = taskbarAtTop ? anchor.Bottom + gap : anchor.Top - size.Height - gap;
                y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - size.Height + margin));

                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        r.Draw(g, sections, updated);
                    }
                    Present(bmp, new Point(x, y), fadeIn: true);
                }
            }
        }
    }
}
