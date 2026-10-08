# Apple-style Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the taskbar bar as a macOS menu bar and both cards as Control Center (real Acrylic on Windows 11) without changing data, settings or behaviour.

**Architecture:** One token/helper class (`AppleStyle`) feeds two rewritten renderers with unchanged public APIs. Cards move onto a `BackdropWindow` base that picks DWM system backdrop → accent Acrylic → layered fallback and paints a premultiplied bitmap over the backdrop with buffered painting.

**Tech Stack:** C# / .NET Framework 4.8 WinForms, GDI+, DWM (`dwmapi.dll`), `uxtheme.dll` buffered paint, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-08-apple-redesign-design.md`

## Global Constraints

- Public APIs stay: `BarRenderer(int dot)`, `MeasureBar(items, pc=null)`, `DrawBar(g, items, now, blink, pc=null)`, `Dot`, `Light`, `ShowReset`, `ShowNames`, static service colours; `CardRenderer(float scale, bool light)`, `Measure(sections)`, `Draw(g, sections, updated)`; `DetailPopup.Show(sections, updated, scale, light, anchor, atTop)`; `DesktopCard.Render(sections, updated, scale, light, saved)` + its events and static helpers.
- Tokens exactly as spec §2 (light/dark), geometry/typography as spec §2 at 100 %, level 4 = 100 %.
- Fonts: "Segoe UI Variable Display"/"Segoe UI Variable Text" when installed, else "Segoe UI"; Korean strings via "Malgun Gothic".
- Bar numbers carry no "%"; PC text keeps "%" and "°".
- Auto-fit order unchanged; bar height ≤ 40 px at level 4.
- No new NuGet/runtime dependencies.

## Review Focus

1. Windows 10 / Windows 11 21H2 (no `DWMWA_SYSTEMBACKDROP_TYPE`) → accent Acrylic or layered fallback, never an invisible or black card. → Task 1 `Choose_*` tests + fallback path.
2. Theme switch light↔dark while a card is open → next render uses the new palette and dark-mode window attribute. → Task 4 `Dark_Palette_Used_When_Light_False`.
3. A service with 0 % remaining / 100 % used → ring arc empty/full, no negative sweep. → Task 2 `Sweep_Is_Clamped`.
4. Card on a 100 % monitor next to a 150 % one → size follows that monitor (existing `MonitorScale`) after moving to `BackdropWindow`. → Task 5 live check.
5. Desktop card drag / Ctrl+wheel / right-click still work on the non-layered backdrop window (hit-test returns caption over the whole card). → Task 5 live check with `cardq.ps1`.

---

### Task 1: BackdropWindow (spike first)

**Files:** Create `src/AIUsageBar/UI/BackdropWindow.cs`; Modify `src/AIUsageBar/UI/Native.cs` (DWM + buffered paint imports); Test `tests/AIUsageBar.Tests/BackdropTests.cs`.

**Interfaces:**
- Produces: `enum BackdropMode { SystemBackdrop, AccentAcrylic, Layered }`; `static BackdropMode BackdropWindow.Choose(int osBuild, bool forceLayered)`; `class BackdropWindow : Form` with `BackdropMode Mode { get; }`, `bool Light { get; set; }`, `void Present(Bitmap pargb, Point screenPos)`, `bool HasSystemShadow` (true unless Layered); keeps `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, `ShowWithoutActivation = true`.

- [ ] Step 1: tests `Choose_SystemBackdrop_On_22621_And_Later` (22621, 26100 → SystemBackdrop), `Choose_Accent_On_Older_Builds` (19045, 22000 → AccentAcrylic), `Choose_Layered_When_Forced_Or_Ancient` (forceLayered → Layered; 9600 → Layered).
- [ ] Step 2: run → FAIL (type missing).
- [ ] Step 3: implement. SystemBackdrop: `DwmExtendFrameIntoClientArea(-1)`, `DWMWA_SYSTEMBACKDROP_TYPE(38)=3`, `DWMWA_WINDOW_CORNER_PREFERENCE(33)=2`, `DWMWA_USE_IMMERSIVE_DARK_MODE(20)=!Light`. Accent: `SetWindowCompositionAttribute(WCA_ACCENT_POLICY=19, ACCENT_ENABLE_ACRYLICBLURBEHIND=4, GradientColor = tint ABGR)` + corner preference. Layered: `WS_EX_LAYERED` + `Native.PushLayered`. `Present` for non-layered modes sets bounds (SetWindowPos, no activate), keeps the bitmap and invalidates; `WM_PAINT` draws it via `BeginBufferedPaint(hdc, BPBF_TOPDOWNDIB, BPPF_ERASE)` + GDI+ `DrawImage` + `EndBufferedPaint`; `WM_ERASEBKGND` returns 1. If any DWM call fails, fall back to the next mode once.
- [ ] Step 4: tests PASS; then spike on this PC: a throwaway `--backdrop-demo` is NOT added — instead Task 5's live check proves it; record in the ledger whether SystemBackdrop was active (`DwmGetWindowAttribute(38)` returns 3).
- [ ] Step 5: commit `feat: BackdropWindow with system backdrop / accent acrylic / layered fallback`.

### Task 2: AppleStyle tokens and drawing helpers

**Files:** Create `src/AIUsageBar/UI/AppleStyle.cs`; Test `tests/AIUsageBar.Tests/AppleStyleTests.cs`.

**Interfaces:**
- Produces: `sealed class AppleStyle` with `static AppleStyle For(bool light)`; colour properties `Label, Secondary, Separator, Track, ModuleFill, AcrylicTint, Warning, Critical, PcAccent`; `Color ServiceColor(string tag)` (CL/CX/GK/CU/PC); `Color StateColor(Severity s, Color normal)`; fonts `static Font Font(float px, bool display, bool semibold)` (Variable fallback to Segoe UI), `static Font Korean(float px, bool semibold)`; helpers `static float Sweep(double remaining)` (degrees, clamped 0–360), `static void Ring(Graphics g, RectangleF r, float stroke, double remaining, Color arc, Color track)`, `static void Bar(Graphics g, RectangleF r, double fraction, Color fill, Color track)`, `static void Chip(Graphics g, RectangleF r, Color fill, string letter, Font f)`; `Paint.Rounded`, `Paint.Measure`, `Paint.Tight`, `Paint.Prepare` stay where they are.

- [ ] Step 1: tests `Light_And_Dark_Tokens_Match_Spec` (Label #1D1D1F/#F5F5F7, Secondary #6E6E73/#98989D, Warning #FF9500/#FF9F0A, Critical #FF3B30/#FF453A, PcAccent #0A84FF), `Sweep_Is_Clamped` (−5→0, 50→180, 100→360, 130→360), `StateColor_Only_Changes_When_Low` (Normal→given colour, Low→Warning, Critical→Critical), `Font_Falls_Back_To_Segoe_UI` (returned font Name is either "Segoe UI Variable Display/Text" or "Segoe UI").
- [ ] Step 2: run → FAIL.
- [ ] Step 3: implement (colours with alpha as spec §2).
- [ ] Step 4: PASS. Step 5: commit `feat: AppleStyle design tokens and ring/bar/chip helpers`.

### Task 3: Bar — menu-bar layout

**Files:** Modify `src/AIUsageBar/UI/BarRenderer.cs` (draw/measure internals only); Test `tests/AIUsageBar.Tests/BarRendererTests.cs` (replace capsule-specific asserts).

**Interfaces:** Consumes `AppleStyle`. Produces the unchanged public API; static service colours remain (re-pointed to AppleStyle values) so callers compile.

Layout (px at level 4): pad 6; per service ring 16 Ø stroke 2.6, gap 6, name 13/500 (if `ShowNames`), gap 6, numbers 13/600 tabular "95 · 58", reset 12/400 Secondary (if `ShowReset`, earliest future reset among the windows, `Fmt.ResetShort`), separator 1×18 with 10 px padding each side; PC text "CPU 35% · 46°  RAM 51%" 13/500 with warning colours per threshold; height 30.

- [ ] Step 1: tests `Measure_Grows_With_Reset_And_Names_And_Scales` (reset > plain, names > no-names, level 6 ≈ 1.5× level 4, height ≤ 40 at level 4), `Warning_Colour_Only_When_Low` (85 % used → Warning pixels present; 10 % used → no Warning pixels), `Critical_Ring_Blinks` (98 % used: Critical present with blink, absent without), `Stale_Block_Is_Dimmed` (no exact Label pixel for an 11-min-old block on white), `Pc_Text_Present` (measure with pc wider by > 80 px).
- [ ] Step 2: FAIL. Step 3: implement. Step 4: PASS (whole suite). Step 5: commit `feat: menu-bar style taskbar strip`.

### Task 4: Card — Control Center modules

**Files:** Modify `src/AIUsageBar/UI/CardRenderer.cs`; Test `tests/AIUsageBar.Tests/CardRendererTests.cs`.

**Interfaces:** Consumes `AppleStyle`, `CardSection/CardRow/NoticeKind`. Produces unchanged `Measure/Draw` plus `bool Backdrop { get; set; }` — when true, no faux shadow/margin and no background fill (the window backdrop shows through; modules use `ModuleFill`); when false (layered fallback) draws a 16 px faux shadow margin and an `AcrylicTint` rounded background (radius 22).

Layout (px at 100 %): width 340; outer padding 12; header title 15/600 + updated time 12 Secondary; module per section (radius 16, padding 12×14, gap 8): chip 22 (radius 6, letter: CL→C, CX→X, GK→G, CU→Cu) + name 13/600 + plan 12 Secondary right; per row: big number 26/600 Display without "%", then "% <window> 남음" 12 Secondary, countdown right 12 Secondary, 5 px bar, reset line 12 Secondary; notices 12; extras label/value 12 Secondary. PC section (Tag "PC") renders as three equal mini modules: CPU ring 38/4.5 + "35%" + "CPU"; memory ring + "51%" + "메모리"; temperature big "46°" + "온도" (row data from `CardContent.BuildSystem`: rows[0] CPU, rows[1] memory with GB in `ResetText`, extras "CPU 온도"/"GPU 온도").

- [ ] Step 1: tests `Height_Grows_And_Width_Fixed`, `Scales`, `Backdrop_Has_No_Shadow_Margin` (Backdrop=true → width == 340×scale ±1 and corner pixel alpha 0 because nothing is drawn there; Backdrop=false → width == (340+32)×scale ±1), `Dark_Palette_Used_When_Light_False` (dark render contains #F5F5F7 text pixels, light render contains #1D1D1F), `Pc_Row_Draws_Rings_In_Accent` (PcAccent pixels present when a PC section is included).
- [ ] Step 2: FAIL. Step 3: implement. Step 4: PASS. Step 5: commit `feat: Control Center style cards`.

### Task 5: Cards on BackdropWindow, fade-in, ship

**Files:** Modify `src/AIUsageBar/UI/DetailPopup.cs`, `src/AIUsageBar/UI/DesktopCard.cs` (derive from `BackdropWindow`, render with `Backdrop = Mode != Layered`, call `Present`), `src/AIUsageBar/UI/BarApp.cs` (pass theme), `README.md`; Test: existing `DesktopCardTests` stay green.

- [ ] Step 1: DetailPopup: on first show of a hover, fade window opacity 0→1 over 150 ms (`SetLayeredWindowAttributes` is not usable on non-layered windows → use `DwmSetWindowAttribute`-free approach: `AnimateWindow(hwnd, 150, AW_BLEND)` for non-layered, `PushLayered` constant alpha ramp for layered). No fade on content refresh.
- [ ] Step 2: DesktopCard keeps drag (HTCAPTION), Ctrl+wheel, right-click menu, topmost toggle, position memory, monitor scale.
- [ ] Step 3: full suite PASS; build installer; upgrade-install; verify live: backdrop attribute = 3 on both cards (`DwmGetWindowAttribute`), hit-test inside card true / outside false, drag + Ctrl+wheel via `cardq.ps1`, bar width ≤ available and capture of the bar.
- [ ] Step 4: README design notes; commit `feat: cards on Acrylic backdrop with fade-in`; merge to main; copy installer to Downloads.
