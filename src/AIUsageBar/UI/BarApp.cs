using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AIUsageBar.Core;
using AIUsageBar.Providers;

namespace AIUsageBar.UI
{
    /// <summary>
    /// Application root: owns settings, the scheduler, the latest usage per service, the bar window
    /// (recreated whenever Explorer restarts) and the detail popup.
    /// </summary>
    internal sealed class BarApp : ApplicationContext
    {
        private static readonly string[] Order = { "CL", "CX", "GK", "CU" };

        private readonly Control _ui = new Control();
        private readonly Dictionary<string, ServiceUsage> _data = new Dictionary<string, ServiceUsage>();
        private readonly TaskbarAttacher _attacher = new TaskbarAttacher();
        private readonly Timer _check = new Timer { Interval = 2000 };
        private readonly Timer _blink = new Timer { Interval = 500 };
        private readonly Timer _tick = new Timer { Interval = 30000 };
        private readonly Timer _sys = new Timer { Interval = 2000 };
        private readonly SystemMonitor _monitor = new SystemMonitor();
        private SystemSnapshot _pc;
        private readonly DetailPopup _popup = new DetailPopup();
        private readonly DesktopCard _desk = new DesktopCard();
        private readonly ContextMenuStrip _deskMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _deskTopItem;
        private readonly ToolStripMenuItem _pinItem;
        private float _dpiScale = 1f;
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _startupItem;

        private readonly IDisposable _quit;
        private Settings _settings;
        private Scheduler _scheduler;
        private BarWindow _bar;
        private TaskbarInfo _tb;
        private Rectangle _barScreen;
        private Size _barSize;
        private bool _pinned;
        private bool _light;

        public BarApp()
        {
            _ui.CreateControl();
            _settings = Settings.Load(Settings.DefaultPath);
            foreach (var t in Order)
                _data[t] = new ServiceUsage { Tag = t, Service = CardContent.NameFor(t), Status = ServiceStatus.Error, Error = "LOADING" };

            _menu.Items.Add("지금 새로고침", null, async (s, e) => await _scheduler.RefreshNowAsync());
            _menu.Items.Add("설정...", null, (s, e) => OpenSettings());
            _pinItem = new ToolStripMenuItem("바탕화면에 카드 띄우기", null, (s, e) => SetPinned(!_settings.PinCardOnDesktop));
            _menu.Items.Add(_pinItem);
            _startupItem = new ToolStripMenuItem("Windows 시작 시 실행", null, (s, e) => ToggleStartup());
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("종료", null, (s, e) => ExitThread());
            _menu.Opening += (s, e) =>
            {
                _startupItem.Checked = SafeStartupEnabled();
                _pinItem.Checked = _settings.PinCardOnDesktop;
            };

            _deskTopItem = new ToolStripMenuItem("항상 위에 표시", null, (s, e) =>
            {
                _settings.DesktopCardTopMost = !_settings.DesktopCardTopMost;
                SaveSettings();
                UpdateDesktopCard();
            });
            _deskMenu.Items.Add(_deskTopItem);
            var sizeMenu = new ToolStripMenuItem("크기");
            foreach (var pct in new[] { 50, 70, 85, 100, 120, 150 })
            {
                var value = pct / 100.0;
                var item = new ToolStripMenuItem(pct + "%", null, (s, e) => SetCardScale(value)) { Tag = value };
                sizeMenu.DropDownItems.Add(item);
            }
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());
            sizeMenu.DropDownItems.Add(new ToolStripMenuItem("Ctrl + 휠로도 조절") { Enabled = false });
            _deskMenu.Items.Add(sizeMenu);
            _deskMenu.Opening += (s, e) =>
            {
                foreach (ToolStripItem i in sizeMenu.DropDownItems)
                    if (i is ToolStripMenuItem mi && mi.Tag is double v) mi.Checked = Math.Abs(v - _settings.DesktopCardScale) < 0.001;
            };
            _deskMenu.Items.Add("카드 숨기기", null, (s, e) => SetPinned(false));
            _deskMenu.Items.Add(new ToolStripSeparator());
            _deskMenu.Items.Add("설정...", null, (s, e) => OpenSettings());
            _deskMenu.Opening += (s, e) => _deskTopItem.Checked = _settings.DesktopCardTopMost;
            _desk.MenuRequested += p =>
            {
                _deskMenu.Show(p);
                Native.SetForegroundWindow(_deskMenu.Handle);
            };
            _desk.ScaleRequested += delta => SetCardScale(DesktopCard.StepScale(_settings.DesktopCardScale, delta));
            _desk.Moved += p =>
            {
                _settings.DesktopCardX = p.X;
                _settings.DesktopCardY = p.Y;
                SaveSettings();
            };

            _check.Tick += (s, e) => EnsureBar();
            _blink.Tick += (s, e) => Blink();
            _tick.Tick += (s, e) => { Redraw(); RefreshPopup(); UpdateDesktopCard(); };
            _sys.Tick += (s, e) => SampleSystem();

            _quit = QuitSignal.Listen(QuitSignal.DefaultName, () => _ui.BeginInvoke((Action)ExitThread));
            StartScheduler();
            SampleSystem();
            EnsureBar();
            UpdateDesktopCard();
            _check.Start();
            _sys.Start();
            _blink.Start();
            _tick.Start();
        }

        public List<ServiceUsage> VisibleItems()
        {
            var list = new List<ServiceUsage>();
            if (_settings.ShowClaude) list.Add(_data["CL"]);
            if (_settings.ShowCodex) list.Add(_data["CX"]);
            if (_settings.ShowGrok) list.Add(_data["GK"]);
            if (_settings.ShowCursor) list.Add(_data["CU"]);
            return list;
        }

        /// <summary>The latest PC metrics, or null when the PC block is turned off.</summary>
        public SystemSnapshot SystemOrNull() => _settings.ShowSystem ? _pc : null;

        private void SampleSystem()
        {
            if (!_settings.ShowSystem) return;
            try
            {
                var wasNull = _pc == null;
                _pc = _monitor.Sample();
                if (wasNull) EnsureBar();
                else Redraw();
                RefreshPopup();
                UpdateDesktopCard();
            }
            catch (Exception ex)
            {
                Program.Log(ex);
            }
        }

        private void StartScheduler()
        {
            _scheduler?.Dispose();
            var providers = new List<IUsageProvider>();
            if (_settings.ShowClaude) providers.Add(new ClaudeProvider());
            if (_settings.ShowCodex) providers.Add(new CodexProvider());
            GrokProvider grok = null;
            if (_settings.ShowGrok) providers.Add(grok = new GrokProvider(null, () => _settings.GrokWeeklyBudgetUsd));
            if (_settings.ShowCursor) providers.Add(new CursorProvider());
            _scheduler = new Scheduler(providers, () => _settings.RefreshSeconds);
            _scheduler.Updated += u => _ui.BeginInvoke((Action)(() => OnUpdated(u)));
            if (grok != null) _scheduler.Watch(grok.SessionsDir, "usage.json", "GK");
            _scheduler.Start();
        }

        private void OnUpdated(ServiceUsage u)
        {
            if (u.Service == null) u.Service = CardContent.NameFor(u.Tag);
            _data[u.Tag] = u;
            EnsureBar(); // re-measures, places and redraws
            RefreshPopup();
            UpdateDesktopCard();
        }

        private void EnsureBar()
        {
            try
            {
                var tb = TaskbarInfo.Find();
                if (tb == null)
                {
                    _bar?.Hide();
                    return;
                }
                if (_bar == null || _bar.IsDisposed || !_bar.IsHandleCreated)
                {
                    _bar?.Dispose();
                    _bar = new BarWindow(this);
                    _bar.CreateControl();
                    var _ = _bar.Handle;
                    _tb = null;
                }

                var light = TaskbarInfo.LightTheme();
                // Size level in 25 % steps: 4 = 100 % scaling (48 px taskbar), 6 = 150 %, ...
                var dpi = Native.GetDpiForWindow(tb.Handle);
                var dot = Math.Max(1, (int)Math.Round((dpi == 0 ? 96 : dpi) / 24.0));
                _dpiScale = (dpi == 0 ? 96 : dpi) / 96f; // cards follow the display scale, not the fitted bar size
                var reset = _settings.ShowResetOnBar;
                var names = true;
                if (tb.Horizontal && tb.IconsLeft != int.MaxValue)
                {
                    // Never run into the centered Start button / app icons; re-fitted every 2 s as icons move.
                    var items = VisibleItems();
                    var pc = SystemOrNull();
                    var maxWidth = tb.IconsLeft - tb.Bounds.X - TaskbarAttacher.Margin * 3;
                    var fit = BarLayout.Fit(dot, reset, (d, r, n) =>
                    {
                        using (var probe = new BarRenderer(d) { ShowReset = r, ShowNames = n }) return probe.MeasureBar(items, pc).Width;
                    }, maxWidth, BarRenderer.Level100);
                    dot = fit.Level;
                    reset = fit.Reset;
                    names = fit.Names;
                }
                if (_bar.Renderer.Dot != dot || _light != light || _bar.Renderer.ShowReset != reset || _bar.Renderer.ShowNames != names)
                {
                    var old = _bar.Renderer;
                    _bar.Renderer = new BarRenderer(dot) { Light = light, ShowReset = reset, ShowNames = names };
                    old.Dispose();
                    _light = light;
                }
                _bar.Vertical = !tb.Horizontal;

                if (!_attacher.IsAttached(_bar.Handle, tb)) _attacher.Attach(_bar.Handle, tb);
                var size = _bar.Measure(VisibleItems());
                _barScreen = _attacher.Place(_bar.Handle, tb, size);
                _barSize = size;
                _bar.Redraw(size);
                if (!_bar.Visible && (_attacher.ChildMode || !Native.ForegroundIsFullscreen())) _bar.Show();
                _tb = tb;
            }
            catch (Exception ex)
            {
                Program.Log(ex);
            }
        }

        private void Redraw()
        {
            if (_bar != null && !_bar.IsDisposed) _bar.Redraw(_barSize);
        }

        private void Blink()
        {
            if (_bar == null || _bar.IsDisposed) return;
            var now = DateTimeOffset.Now;
            var critical = VisibleItems().Any(u => u.Status != ServiceStatus.NeedsLogin && u.Windows.Any(w => w.SeverityAt(now) == Severity.Critical));
            if (!critical && !_bar.BlinkPhase) return;
            _bar.BlinkPhase = critical && !_bar.BlinkPhase;
            Redraw();
        }

        public void ShowPopup(bool pinned)
        {
            _pinned |= pinned;
            RefreshPopup(force: true);
        }

        public void HidePopupUnlessPinned()
        {
            if (!_pinned) _popup.Hide();
        }

        public void TogglePinnedPopup()
        {
            if (_pinned && _popup.Visible)
            {
                _pinned = false;
                _popup.Hide();
                return;
            }
            ShowPopup(pinned: true);
        }

        private void RefreshPopup(bool force = false)
        {
            if (!force && !_popup.Visible) return;
            if (_bar == null) return;
            var items = VisibleItems();
            _popup.Show(CardSections(items), CardContent.UpdatedText(items), _dpiScale, _light, _barScreen, _tb?.AtTop ?? false);
        }

        private List<CardSection> CardSections(List<ServiceUsage> items)
        {
            var sections = CardContent.Build(items, DateTimeOffset.Now);
            var pc = SystemOrNull();
            if (pc != null) sections.Add(CardContent.BuildSystem(pc));
            return sections;
        }

        /// <summary>The optional desktop widget: same content as the hover card, kept open.</summary>
        private void UpdateDesktopCard()
        {
            try
            {
                if (!_settings.PinCardOnDesktop)
                {
                    if (_desk.Visible) _desk.Hide();
                    return;
                }
                var items = VisibleItems();
                Point? saved = _settings.DesktopCardX.HasValue && _settings.DesktopCardY.HasValue
                    ? new Point(_settings.DesktopCardX.Value, _settings.DesktopCardY.Value)
                    : (Point?)null;
                _desk.SetTopMost(_settings.DesktopCardTopMost);
                _desk.Render(CardSections(items), CardContent.UpdatedText(items), (_desk.MonitorScale() ?? _dpiScale) * (float)_settings.DesktopCardScale, _light, saved);
            }
            catch (Exception ex)
            {
                Program.Log(ex);
            }
        }

        private void SetCardScale(double scale)
        {
            _settings.DesktopCardScale = Math.Max(Settings.MinCardScale, Math.Min(Settings.MaxCardScale, scale));
            SaveSettings();
            UpdateDesktopCard();
        }

        private void SetPinned(bool pinned)
        {
            _settings.PinCardOnDesktop = pinned;
            SaveSettings();
            UpdateDesktopCard();
        }

        private void SaveSettings()
        {
            try { _settings.Save(Settings.DefaultPath); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { Program.Log(ex); }
        }

        public void ShowMenu(Point screen)
        {
            _popup.Hide();
            _pinned = false;
            _menu.Show(screen);
            // The bar never activates, so give the menu focus explicitly (keyboard + click-away dismissal).
            Native.SetForegroundWindow(_menu.Handle);
        }

        private void ToggleStartup()
        {
            try
            {
                var on = !Startup.IsEnabled();
                Startup.Set(on);
                _settings.RunAtStartup = on;
                _settings.Save(Settings.DefaultPath);
            }
            catch (Exception ex)
            {
                Program.Log(ex);
            }
        }

        private static bool SafeStartupEnabled()
        {
            try { return Startup.IsEnabled(); }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { return false; }
        }

        private void OpenSettings()
        {
            using (var f = new SettingsForm(_settings))
            {
                if (f.ShowDialog() != DialogResult.OK || f.Result == null) return;
                var old = _settings;
                _settings = f.Result;
                try
                {
                    _settings.Save(Settings.DefaultPath);
                    if (_settings.RunAtStartup != SafeStartupEnabled()) Startup.Set(_settings.RunAtStartup);
                }
                catch (Exception ex)
                {
                    Program.Log(ex);
                }
                if (!_settings.ShowSystem) _pc = null;
                else if (_pc == null) SampleSystem();
                if (old.ShowClaude != _settings.ShowClaude || old.ShowCodex != _settings.ShowCodex || old.ShowGrok != _settings.ShowGrok || old.ShowCursor != _settings.ShowCursor)
                    StartScheduler();
                else if (old.GrokWeeklyBudgetUsd != _settings.GrokWeeklyBudgetUsd)
                    _ = _scheduler.RefreshNowAsync();
                EnsureBar();
                RefreshPopup();
                UpdateDesktopCard();
            }
        }

        protected override void ExitThreadCore()
        {
            _quit.Dispose();
            _check.Stop();
            _blink.Stop();
            _tick.Stop();
            _sys.Stop();
            _monitor.Dispose();
            _scheduler?.Dispose();
            _attacher.RestoreRebar();
            _popup.Dispose();
            _desk.Dispose();
            _deskMenu.Dispose();
            _bar?.Dispose();
            _menu.Dispose();
            _ui.Dispose();
            base.ExitThreadCore();
        }
    }
}
