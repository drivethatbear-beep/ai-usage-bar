using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIUsageBar.Core;

namespace AIUsageBar
{
    /// <summary>User settings at %APPDATA%\AIUsageBar\settings.json. Missing or corrupt file → defaults.</summary>
    internal sealed class Settings
    {
        public const int MinRefreshSeconds = 60;

        public int RefreshSeconds = 120;
        public double GrokWeeklyBudgetUsd;
        public bool ShowClaude = true;
        public bool ShowCodex = true;
        public bool ShowGrok = true;
        public bool RunAtStartup = true;
        public bool ShowResetOnBar = true;
        public bool ShowCursor = true;
        public bool ShowSystem = true;
        /// <summary>Keep the detail card open on the desktop as a widget.</summary>
        public bool PinCardOnDesktop;
        public bool DesktopCardTopMost;
        /// <summary>Last dragged position of the desktop card (screen px); null = default corner.</summary>
        public int? DesktopCardX;
        public int? DesktopCardY;
        /// <summary>Desktop card size relative to the display scale (0.5 – 1.5).</summary>
        public double DesktopCardScale = 1.0;
        public const double MinCardScale = 0.5, MaxCardScale = 1.5;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageBar", "settings.json");

        public static Settings Load(string path)
        {
            var s = new Settings();
            try
            {
                if (!(Json.Parse(File.ReadAllText(path)) is Dictionary<string, object> d)) return s;
                s.RefreshSeconds = (int)(Json.Num(Json.Get(d, "refreshSeconds")) ?? s.RefreshSeconds);
                s.GrokWeeklyBudgetUsd = Math.Max(0, Json.Num(Json.Get(d, "grokWeeklyBudgetUsd")) ?? 0);
                s.ShowClaude = Bool(d, "showClaude", s.ShowClaude);
                s.ShowCodex = Bool(d, "showCodex", s.ShowCodex);
                s.ShowGrok = Bool(d, "showGrok", s.ShowGrok);
                s.RunAtStartup = Bool(d, "runAtStartup", s.RunAtStartup);
                s.ShowResetOnBar = Bool(d, "showResetOnBar", s.ShowResetOnBar);
                s.ShowCursor = Bool(d, "showCursor", s.ShowCursor);
                s.ShowSystem = Bool(d, "showSystem", s.ShowSystem);
                s.PinCardOnDesktop = Bool(d, "pinCardOnDesktop", s.PinCardOnDesktop);
                s.DesktopCardTopMost = Bool(d, "desktopCardTopMost", s.DesktopCardTopMost);
                s.DesktopCardX = (int?)Json.Num(Json.Get(d, "desktopCardX"));
                s.DesktopCardY = (int?)Json.Num(Json.Get(d, "desktopCardY"));
                s.DesktopCardScale = Json.Num(Json.Get(d, "desktopCardScale")) ?? 1.0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                return new Settings();
            }
            s.RefreshSeconds = Math.Max(MinRefreshSeconds, Math.Min(3600, s.RefreshSeconds));
            s.DesktopCardScale = Math.Max(MinCardScale, Math.Min(MaxCardScale, s.DesktopCardScale));
            return s;
        }

        public void Save(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            string B(bool b) => b ? "true" : "false";
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"refreshSeconds\": ").Append(RefreshSeconds.ToString(inv)).Append(",\n");
            sb.Append("  \"grokWeeklyBudgetUsd\": ").Append(GrokWeeklyBudgetUsd.ToString("0.##", inv)).Append(",\n");
            sb.Append("  \"showClaude\": ").Append(B(ShowClaude)).Append(",\n");
            sb.Append("  \"showCodex\": ").Append(B(ShowCodex)).Append(",\n");
            sb.Append("  \"showGrok\": ").Append(B(ShowGrok)).Append(",\n");
            sb.Append("  \"runAtStartup\": ").Append(B(RunAtStartup)).Append(",\n");
            sb.Append("  \"showResetOnBar\": ").Append(B(ShowResetOnBar)).Append(",\n");
            sb.Append("  \"showCursor\": ").Append(B(ShowCursor)).Append(",\n");
            sb.Append("  \"showSystem\": ").Append(B(ShowSystem)).Append(",\n");
            sb.Append("  \"pinCardOnDesktop\": ").Append(B(PinCardOnDesktop)).Append(",\n");
            sb.Append("  \"desktopCardTopMost\": ").Append(B(DesktopCardTopMost)).Append(",\n");
            sb.Append("  \"desktopCardX\": ").Append(DesktopCardX.HasValue ? DesktopCardX.Value.ToString(inv) : "null").Append(",\n");
            sb.Append("  \"desktopCardY\": ").Append(DesktopCardY.HasValue ? DesktopCardY.Value.ToString(inv) : "null").Append(",\n");
            sb.Append("  \"desktopCardScale\": ").Append(DesktopCardScale.ToString("0.##", inv)).Append("\n}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());
        }

        private static bool Bool(Dictionary<string, object> d, string key, bool def) =>
            d.TryGetValue(key, out var v) && v is bool b ? b : def;
    }
}
