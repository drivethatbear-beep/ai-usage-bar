using System.Linq;
using System.Windows.Forms;
using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class SettingsFormTests
    {
        private static T Find<T>(Control root, System.Func<T, bool> pred) where T : Control
        {
            foreach (Control c in root.Controls)
            {
                if (c is T t && pred(t)) return t;
                var inner = Find(c, pred);
                if (inner != null) return inner;
            }
            return null;
        }

        [Fact]
        public void Save_Maps_Controls_To_Settings()
        {
            using (var f = new SettingsForm(new Settings { RefreshSeconds = 300, GrokWeeklyBudgetUsd = 20, ShowCodex = false, ShowResetOnBar = true }))
            {
                var nums = new[] { Find<NumericUpDown>(f, n => n.Value == 300), Find<NumericUpDown>(f, n => n.Value == 20) };
                Assert.All(nums, Assert.NotNull);
                nums[1].Value = 35.5m;
                Find<CheckBox>(f, c => c.Text.StartsWith("Codex")).Checked = true;
                Find<CheckBox>(f, c => c.Text.Contains("리셋")).Checked = false;
                var r = f.BuildResult();
                Assert.Equal(300, r.RefreshSeconds);
                Assert.Equal(35.5, r.GrokWeeklyBudgetUsd);
                Assert.True(r.ShowCodex);
                Assert.False(r.ShowResetOnBar);
            }
        }

        [Fact]
        public void Keeps_Dragged_Card_Position_And_Maps_Pin_Options()
        {
            using (var f = new SettingsForm(new Settings { DesktopCardX = -1800, DesktopCardY = 12 }))
            {
                Find<CheckBox>(f, c => c.Text.Contains("바탕화면에 띄워두기")).Checked = true;
                Find<CheckBox>(f, c => c.Text.Contains("항상 위")).Checked = true;
                var r = f.BuildResult();
                Assert.True(r.PinCardOnDesktop);
                Assert.True(r.DesktopCardTopMost);
                Assert.Equal(-1800, r.DesktopCardX);
                Assert.Equal(12, r.DesktopCardY);
            }
        }
    }
}
