using System;
using System.Collections.Generic;
using System.Linq;
using AIUsageBar.Core;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class CardContentTests
    {
        // Thursday 2026-10-01 17:28 local.
        private static readonly DateTimeOffset Now = new DateTimeOffset(new DateTime(2026, 10, 1, 17, 28, 0, DateTimeKind.Local));

        private static DateTimeOffset Local(int month, int day, int h, int m) =>
            new DateTimeOffset(new DateTime(2026, month, day, h, m, 0, DateTimeKind.Local));

        [Fact]
        public void Reset_Korean()
        {
            Assert.Equal("오늘 20:09", Fmt.ResetKo(Local(10, 1, 20, 9), Now));
            Assert.Equal("내일 01:59", Fmt.ResetKo(Local(10, 2, 1, 59), Now));
            Assert.Equal("10월 7일 15:31", Fmt.ResetKo(Local(10, 7, 15, 31), Now));
        }

        [Theory]
        [InlineData(30, "1분 이내")]
        [InlineData(41 * 60, "41분")]
        [InlineData(2 * 3600 + 41 * 60, "2시간 41분")]
        [InlineData(3 * 3600, "3시간")]
        [InlineData(6 * 86400 + 23 * 3600 + 50 * 60, "6일 23시간")]
        public void Duration_Korean(int seconds, string expected)
        {
            Assert.Equal(expected, Fmt.DurationKo(TimeSpan.FromSeconds(seconds)));
        }

        [Theory]
        [InlineData("max", "Max")]
        [InlineData("MAX", "Max")]
        [InlineData("pro", "Pro")]
        [InlineData("SuperGrok Heavy", "SuperGrok Heavy")]
        [InlineData("SUPERGROK HEAVY", "Supergrok Heavy")]
        [InlineData(null, null)]
        public void Plan_Name(string raw, string expected)
        {
            Assert.Equal(expected, Fmt.PlanName(raw));
        }

        [Fact]
        public void Thousands()
        {
            Assert.Equal("62,500", Fmt.Thousands("62500"));
            Assert.Equal("abc", Fmt.Thousands("abc"));
        }

        [Fact]
        public void Build_Sections()
        {
            var cl = new ServiceUsage { Tag = "CL", Status = ServiceStatus.Ok, Plan = "Max", LastSuccess = Now };
            cl.Windows.Add(new UsageWindow { Label = "5H", UsedPercent = 34, ResetsAt = Local(10, 1, 20, 9) });
            cl.Windows.Add(new UsageWindow { Label = "WK", UsedPercent = 85.4, ResetsAt = Local(10, 2, 1, 59) });
            var cx = new ServiceUsage { Tag = "CX", Status = ServiceStatus.Ok, Plan = "Pro", LastSuccess = Now.AddMinutes(-1) };
            cx.Windows.Add(new UsageWindow { Label = "WK", UsedPercent = 12, ResetsAt = Local(10, 7, 15, 31) });
            cx.AddDetail("CREDITS", "62500");
            var gk = new ServiceUsage { Tag = "GK", Status = ServiceStatus.Ok, Plan = "SuperGrok Heavy", LastSuccess = Now };
            gk.Windows.Add(new UsageWindow { Label = "WK", UsedPercent = 6, ResetsAt = Local(10, 7, 8, 41) });
            gk.AddDetail("API EQUIV WK", "$3.93");
            gk.AddDetail("TODAY", "2.0M TOK");

            var sections = CardContent.Build(new List<ServiceUsage> { cl, cx, gk }, Now);
            Assert.Equal(new[] { "Claude", "Codex", "Grok" }, sections.Select(s => s.Name).ToArray());

            var c = sections[0];
            Assert.Equal("Max", c.Plan);
            Assert.Null(c.Notice);
            Assert.Equal("66%", c.Rows[0].Percent);
            Assert.Equal("남음 · 5시간", c.Rows[0].Caption);
            Assert.Equal("오늘 20:09 초기화", c.Rows[0].ResetText);
            Assert.Equal("2시간 41분 후", c.Rows[0].InText);
            Assert.Equal(66, c.Rows[0].Remaining, 6);
            Assert.Equal("15%", c.Rows[1].Percent);
            Assert.Equal("남음 · 주간", c.Rows[1].Caption);
            Assert.Equal(Severity.Low, c.Rows[1].Severity);

            Assert.Equal(new[] { ("크레딧", "62,500") }, sections[1].Extras.ToArray());
            Assert.Equal(new[] { ("API 환산 (이번 주)", "$3.93"), ("오늘 토큰", "2.0M") }, sections[2].Extras.ToArray());

            Assert.Equal("17:28 갱신", CardContent.UpdatedText(new List<ServiceUsage> { cl, cx, gk }));
        }

        [Fact]
        public void Build_Notices()
        {
            var login = new ServiceUsage { Tag = "CX", Status = ServiceStatus.NeedsLogin };
            var missing = new ServiceUsage { Tag = "GK", Status = ServiceStatus.NotInstalled };
            var err = new ServiceUsage { Tag = "CL", Status = ServiceStatus.Error, Error = "RATE LIMIT" };
            var s = CardContent.Build(new List<ServiceUsage> { err, login, missing }, Now);
            Assert.Equal("갱신 실패 · 요청 한도 초과", s[0].Notice);
            Assert.Equal(NoticeKind.Warning, s[0].NoticeKind);
            Assert.Equal("CLI에서 다시 로그인하세요", s[1].Notice);
            Assert.Equal("설치되지 않았거나 로그인 전이에요", s[2].Notice);
            Assert.Equal(NoticeKind.Muted, s[2].NoticeKind);
            Assert.Empty(s[2].Rows);
        }

        [Fact]
        public void Build_Budget_Fallback_And_Local_Source()
        {
            var gk = new ServiceUsage { Tag = "GK", Status = ServiceStatus.Ok };
            gk.AddDetail("WEEK", "$3.00 / $20");
            gk.AddDetail("LEFT", "$17.00");
            var cx = new ServiceUsage { Tag = "CX", Status = ServiceStatus.Ok };
            cx.AddDetail("SOURCE", "LOCAL");
            var s = CardContent.Build(new List<ServiceUsage> { gk, cx }, Now);
            Assert.Equal(new[] { ("이번 주 사용", "$3.00 / $20"), ("예산 잔액", "$17.00") }, s[0].Extras.ToArray());
            Assert.Equal(new[] { ("기준", "로컬 기록") }, s[1].Extras.ToArray());
        }
    }
}
