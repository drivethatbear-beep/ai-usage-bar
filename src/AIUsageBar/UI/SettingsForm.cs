using System;
using System.Drawing;
using System.Windows.Forms;

namespace AIUsageBar.UI
{
    internal sealed class SettingsForm : Form
    {
        private readonly NumericUpDown _refresh = new NumericUpDown { Minimum = Settings.MinRefreshSeconds, Maximum = 3600, Increment = 30, Width = 90 };
        private readonly NumericUpDown _budget = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 2, Increment = 5, Width = 90 };
        private readonly CheckBox _claude = new CheckBox { Text = "Claude 표시", AutoSize = true };
        private readonly CheckBox _codex = new CheckBox { Text = "Codex 표시", AutoSize = true };
        private readonly CheckBox _grok = new CheckBox { Text = "Grok 표시", AutoSize = true };
        private readonly CheckBox _cursor = new CheckBox { Text = "Cursor 표시", AutoSize = true };
        private readonly CheckBox _system = new CheckBox { Text = "PC 상태 표시 (CPU · 메모리 · 온도)", AutoSize = true };
        private readonly CheckBox _pin = new CheckBox { Text = "상세 카드를 바탕화면에 띄워두기", AutoSize = true };
        private readonly CheckBox _pinTop = new CheckBox { Text = "바탕화면 카드를 항상 위에 표시", AutoSize = true };
        private readonly NumericUpDown _cardScale = new NumericUpDown { Minimum = 50, Maximum = 150, Increment = 10, Width = 90 };
        private readonly Settings _orig;
        private readonly CheckBox _reset = new CheckBox { Text = "작업표시줄에 리셋 시각 표시", AutoSize = true };
        private readonly CheckBox _startup = new CheckBox { Text = "Windows 시작 시 실행", AutoSize = true };

        public Settings Result { get; private set; }

        public SettingsForm(Settings s)
        {
            _orig = s;
            Text = "AI Usage Bar 설정";
            Font = new Font("Malgun Gothic", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            _refresh.Value = Math.Max(_refresh.Minimum, Math.Min(_refresh.Maximum, s.RefreshSeconds));
            _budget.Value = (decimal)Math.Min((double)_budget.Maximum, s.GrokWeeklyBudgetUsd);
            _claude.Checked = s.ShowClaude;
            _codex.Checked = s.ShowCodex;
            _grok.Checked = s.ShowGrok;
            _cursor.Checked = s.ShowCursor;
            _system.Checked = s.ShowSystem;
            _pin.Checked = s.PinCardOnDesktop;
            _pinTop.Checked = s.DesktopCardTopMost;
            _cardScale.Value = (decimal)Math.Round(Math.Max(0.5, Math.Min(1.5, s.DesktopCardScale)) * 100);
            _reset.Checked = s.ShowResetOnBar;
            _startup.Checked = Startup.IsEnabled();

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            void Row(string label, Control c, string hint = null)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 4) });
                var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
                cell.Controls.Add(c);
                if (hint != null) cell.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 6, 0, 0) });
                grid.Controls.Add(cell);
            }
            Row("갱신 주기(초)", _refresh, "60~3600, Claude·Codex 조회 간격");
            Row("바탕화면 카드 크기(%)", _cardScale, "카드 위에서 Ctrl + 휠로도 조절");
            Row("Grok 주간 예산($)", _budget, "구독 한도를 못 읽을 때만 사용, 0이면 금액만 표시");
            var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            checks.Controls.AddRange(new Control[] { _claude, _codex, _grok, _cursor, _system, _reset, _pin, _pinTop, _startup });
            grid.Controls.Add(new Label { Text = "표시", AutoSize = true, Margin = new Padding(0, 10, 12, 4) });
            grid.Controls.Add(checks);

            var save = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            root.Controls.Add(grid);
            root.Controls.Add(buttons);
            Controls.Add(root);

            save.Click += (o, e) => Result = BuildResult();
        }

        internal Settings BuildResult() => new Settings
        {
            RefreshSeconds = (int)_refresh.Value,
            GrokWeeklyBudgetUsd = (double)_budget.Value,
            ShowClaude = _claude.Checked,
            ShowCodex = _codex.Checked,
            ShowGrok = _grok.Checked,
            ShowCursor = _cursor.Checked,
            ShowSystem = _system.Checked,
            PinCardOnDesktop = _pin.Checked,
            DesktopCardTopMost = _pinTop.Checked,
            DesktopCardX = _orig.DesktopCardX, // position is set by dragging the card, not here
            DesktopCardY = _orig.DesktopCardY,
            DesktopCardScale = (double)_cardScale.Value / 100,
            RunAtStartup = _startup.Checked,
            ShowResetOnBar = _reset.Checked,
        };
    }
}
