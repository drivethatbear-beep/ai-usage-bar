using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIUsageBar.Core;
using AIUsageBar.Providers;

namespace AIUsageBar.UI
{
    internal enum NoticeKind { None, Warning, Muted }

    internal sealed class CardRow
    {
        public string Percent;   // "66%"
        public string Caption;   // "남음 · 5시간"
        public string Window;    // "5시간" / "CPU" / "메모리"
        public string ResetText; // "오늘 20:09 초기화"
        public string InText;    // "2시간 41분 후"
        public double Remaining;
        public Severity Severity;
        /// <summary>Colours the small label under a PC ring (its temperature).</summary>
        public Severity LabelSeverity;
    }

    internal sealed class CardSection
    {
        public string Tag;
        public string Name;
        public string Plan;
        public string Notice;
        public NoticeKind NoticeKind;
        public List<CardRow> Rows = new List<CardRow>();
        public List<(string label, string value)> Extras = new List<(string, string)>();
    }

    /// <summary>Text and numbers for the detail card, in Korean. Pure data; CardRenderer draws it.</summary>
    internal static class CardContent
    {
        public static string NameFor(string tag)
        {
            switch (tag)
            {
                case "CL": return "Claude";
                case "CX": return "Codex";
                case "GK": return "Grok";
                case "CU": return "Cursor";
                case "GM": return "Gemini";
                case "KM": return "Kimi";
                case "PC": return "PC";
                default: return tag ?? "";
            }
        }

        public static string WindowName(string label)
        {
            switch (label)
            {
                case "5H": return "5시간";
                case "WK": return "주간";
                case "MO": return "월간";
                case "DY": return "일간";
                case "TOT": return "월간 전체";
                case "API": return "월간 API";
                case "GEM": return "Gemini 모델";
                case "3P": return "Claude·GPT 모델";
                default: return label;
            }
        }

        public static List<CardSection> Build(IReadOnlyList<ServiceUsage> items, DateTimeOffset now) =>
            items.Select(u => Section(u, now)).ToList();

        public static string UpdatedText(IReadOnlyList<ServiceUsage> items)
        {
            var latest = items.Where(u => u.LastSuccess.HasValue).Select(u => u.LastSuccess.Value).DefaultIfEmpty().Max();
            return latest == default ? "" : latest.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " 갱신";
        }

        private static CardSection Section(ServiceUsage u, DateTimeOffset now)
        {
            var s = new CardSection { Tag = u.Tag, Name = NameFor(u.Tag), Plan = Fmt.PlanName(u.Plan) };
            switch (u.Status)
            {
                case ServiceStatus.NotInstalled:
                    s.Notice = "설치되지 않았거나 로그인 전이에요";
                    s.NoticeKind = NoticeKind.Muted;
                    return s;
                case ServiceStatus.NeedsLogin:
                    s.Notice = "CLI에서 다시 로그인하세요";
                    s.NoticeKind = NoticeKind.Warning;
                    break;
                case ServiceStatus.Error when u.Error == "NOT RUNNING":
                    s.Notice = (u.Tag == "KM" ? "Kimi Code" : "Antigravity") + "를 켜면 갱신돼요";
                    s.NoticeKind = NoticeKind.Muted;
                    break;
                case ServiceStatus.Error:
                    s.Notice = "갱신 실패 · " + ErrorText(u.Error);
                    s.NoticeKind = NoticeKind.Warning;
                    break;
            }

            foreach (var w in u.Windows)
            {
                var rem = w.RemainingAt(now);
                var future = w.ResetsAt.HasValue && w.ResetsAt.Value > now;
                s.Rows.Add(new CardRow
                {
                    Percent = Math.Round(rem).ToString("0", CultureInfo.InvariantCulture) + "%",
                    Caption = "남음 · " + WindowName(w.Label),
                    Window = WindowName(w.Label),
                    ResetText = future ? Fmt.ResetKo(w.ResetsAt.Value, now) + " 초기화" : "",
                    InText = future ? Fmt.DurationKo(w.ResetsAt.Value - now) + " 후" : "",
                    Remaining = rem,
                    Severity = w.SeverityAt(now),
                });
            }

            foreach (var kv in u.Details)
            {
                var extra = Extra(kv.Key, kv.Value);
                if (extra.HasValue) s.Extras.Add(extra.Value);
            }
            return s;
        }

        private static (string, string)? Extra(string key, string value)
        {
            switch (key)
            {
                case "CREDITS": return ("크레딧", Fmt.Thousands(value));
                case "EXTRA": return ("추가 사용", value);
                case "API EQUIV WK": return ("API 환산 (이번 주)", value);
                case "TODAY": return ("오늘 토큰", value.Replace(" TOK", ""));
                case "WEEK": return ("이번 주 사용", value);
                case "LEFT": return ("예산 잔액", value);
                case "SOURCE": return ("기준", value == "LOCAL" ? "로컬 기록" : value);
                case "UNLIMITED": return ("한도", value);
                default: return null; // e.g. BILLING / "$ IS" are implied by the plan badge
            }
        }

        /// <summary>The "이 PC" section: usage bars (not remaining) for CPU and memory, temperatures as extras.</summary>
        public static CardSection BuildSystem(SystemSnapshot pc)
        {
            var s = new CardSection { Tag = "PC", Name = "이 PC" };
            if (pc.CpuPercent.HasValue) s.Rows.Add(UsageRow(pc.CpuPercent.Value, "CPU", WithTemp("CPU", pc.CpuTempC), "", pc.CpuTempC));
            var memGb = Gb(pc.MemUsedGb, pc.MemTotalGb);
            s.Rows.Add(UsageRow(pc.MemPercent, "메모리", "메모리", memGb, null));
            if (pc.GpuUtilPercent.HasValue) s.Rows.Add(UsageRow(pc.GpuUtilPercent.Value, "GPU", WithTemp("GPU", pc.GpuTempC), "", pc.GpuTempC));
            var vram = pc.VramUsedGb.HasValue && pc.VramTotalGb > 0;
            if (vram) s.Rows.Add(UsageRow(pc.VramUsedGb.Value / pc.VramTotalGb.Value * 100, "VRAM", "VRAM", Gb(pc.VramUsedGb.Value, pc.VramTotalGb.Value), null));

            s.Extras.Add(("메모리", memGb));
            if (vram) s.Extras.Add(("VRAM", Gb(pc.VramUsedGb.Value, pc.VramTotalGb.Value)));
            // Without a GPU usage reading the temperature still gets its own small module.
            if (!pc.GpuUtilPercent.HasValue && pc.GpuTempC.HasValue) s.Extras.Add(("GPU 온도", Deg(pc.GpuTempC.Value) + "C"));
            if (!string.IsNullOrEmpty(pc.GpuName)) s.Extras.Add(("GPU", ShortGpu(pc.GpuName)));
            return s;
        }

        private static string Deg(double celsius) => Math.Round(celsius, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "°";

        private static string WithTemp(string name, double? celsius) => celsius.HasValue ? name + " · " + Deg(celsius.Value) : name;

        private static Severity TempSeverity(double? celsius) => celsius >= 90 ? Severity.Critical : celsius >= 80 ? Severity.Low : Severity.Normal;

        private static string Gb(double used, double total) =>
            used.ToString("0.0", CultureInfo.InvariantCulture) + " / " + total.ToString("0.0", CultureInfo.InvariantCulture) + " GB";

        /// <summary>Usage severity for the PC: 80 % and up warns, 95 % and up is critical.</summary>
        public static Severity UsageSeverity(double used) => used >= 95 ? Severity.Critical : used >= 80 ? Severity.Low : Severity.Normal;

        private static CardRow UsageRow(double used, string name, string label, string right, double? celsius) => new CardRow
        {
            Percent = Math.Round(used).ToString("0", CultureInfo.InvariantCulture) + "%",
            Caption = "사용 중 · " + name,
            Window = label,
            ResetText = right,
            InText = "",
            Remaining = used,
            Severity = UsageSeverity(used),
            LabelSeverity = TempSeverity(celsius),
        };

        private static string ShortGpu(string name) => name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Trim();

        private static string ErrorText(string error)
        {
            if (string.IsNullOrEmpty(error)) return "알 수 없는 오류";
            switch (error)
            {
                case "RATE LIMIT": return "요청 한도 초과";
                case "OFFLINE": return "오프라인";
                case "TIMEOUT": return "시간 초과";
                case "LOADING": return "불러오는 중";
                case "STALE": return "이전 값 표시 중";
                case "LOGIN": return "다시 로그인 필요";
            }
            return error.StartsWith("HTTP ", StringComparison.Ordinal) ? "서버 오류 " + error.Substring(5) : error;
        }
    }
}
