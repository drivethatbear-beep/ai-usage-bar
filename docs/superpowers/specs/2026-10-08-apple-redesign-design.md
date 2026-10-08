# Apple-style redesign — design spec

- Date: 2026-10-08
- Status: direction approved in chat (bar A "menu bar", card "Control Center" with real Acrylic)
- Scope: visuals of the taskbar bar, the hover card and the desktop card. Data, providers, settings semantics,
  auto-fit behaviour, startup and installer are unchanged.

## 1. Goal and success criteria

Make the three surfaces read like macOS: the bar like the menu bar, the cards like Control Center.

Success:
- Bar: monochrome text and small rings; colour appears only for warnings; reads at a glance at 100–150 % DPI.
- Cards: frosted, see-through background (real blur on Windows 11), rounded modules, large tabular numbers.
- Light and dark mode both look native; nothing regresses in layout fitting, hit-testing or DPI handling.

Constraint: SF Pro cannot be redistributed. Use Segoe UI Variable (Display for large numbers, Text for the rest),
falling back to Segoe UI on Windows 10.

## 2. Design tokens (one place: `UI/AppleStyle.cs`)

| Token | Light | Dark |
|---|---|---|
| Label (primary text) | #1D1D1F | #F5F5F7 |
| Secondary text | #6E6E73 | #98989D |
| Separator | rgba(0,0,0,0.12) | rgba(255,255,255,0.14) |
| Ring/bar track | rgba(0,0,0,0.10) | rgba(255,255,255,0.16) |
| Module fill (on acrylic) | rgba(255,255,255,0.62) | rgba(255,255,255,0.08) |
| Acrylic tint | #F6F6F8 @ 72 % | #1E1E20 @ 70 % |
| Warning (≤ 20 % left / ≥ 80 % used) | #FF9500 | #FF9F0A |
| Critical (< 5 % left / ≥ 95 % used) | #FF3B30 | #FF453A |
| PC accent | #0A84FF | #0A84FF |
| Service chips | Claude #D97757, Codex #10A37F, Grok #1D1D1F/#F5F5F7, Cursor #6E56CF |

Geometry (px at 100 %): 8-pt grid; card radius 22; module radius 16; module padding 12×14; module gap 8;
bar ring 16 Ø / 2.6 stroke; card ring 38 Ø / 4.5 stroke; bar height ≤ 40.

Typography (px at 100 %): bar 13 (name 500, numbers 600 tabular); card title 15/600; module name 13/600;
big number 26/600 Display tabular; secondary 12/400.

## 3. Bar — menu-bar style

Per service, left to right: ring (remaining of the most constrained window) · name · numbers.
- Numbers: remaining % of each window joined by " · " (e.g. `Claude 95 · 58`). No "%" on the bar.
- Ring stroke: Label colour normally; Warning/Critical colour when that window is low. Number text likewise.
- Optional reset time after the numbers in Secondary colour (`17:00` / `10/9`) — still governed by "작업표시줄에
  리셋 시각 표시" and auto-fit.
- PC block: `CPU 35% · 46°  RAM 51%` as text, warning colours by usage/temperature thresholds already in use.
- Separators: 1 px Separator colour, 18 px tall.
- Auto-fit order unchanged: shrink to 100 % → hide names (ring + numbers stay) → hide reset → shrink further.
- Critical blink: the ring arc blinks (text stays steady), as today with the capsule.
- Stale (> 10 min): whole block at 50 % opacity.

## 4. Cards — Control Center style (hover card and desktop card share one renderer)

Window:
- Windows 11 22H2+: non-layered window with `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` (Acrylic),
  `DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`, `DWMWA_USE_IMMERSIVE_DARK_MODE` following the theme, frame extended into
  the whole client area; content painted with per-pixel alpha over the backdrop. DWM supplies the shadow.
- Older Windows 11 / Windows 10: accent policy `ACCENT_ENABLE_ACRYLICBLURBEHIND` with the tint above; if that fails,
  the current layered window with a solid tinted background (no blur) — identical layout.
- Keep: no activation, tool window, drag/position memory, Ctrl+wheel and menu sizing, always-on-top option,
  per-monitor DPI, clamp-to-screen.
- Opening the hover card: 150 ms fade-in (window opacity), none when updating content.

Content (top to bottom):
- Header: "AI 사용량" (title) and update time (Secondary), no hairline.
- One module per service: chip (rounded square, service colour, letter C/X/G/Cu) + name + plan (Secondary, right).
  Per window: big remaining number + "% <window> 남음" (Secondary), countdown on the right ("2시간 41분 후"),
  5 px rounded bar in service colour (Warning/Critical colours when low), then "<date> 초기화" (Secondary).
  Notices (login / error / not installed) as Secondary or Warning text inside the module.
  Extras (credits, API equivalent, today tokens) as Secondary label/value rows.
- PC: a row of three small modules — CPU ring with %, memory ring with %, temperature (CPU if available,
  else GPU) as a large number with "°"; memory GB as Secondary under the ring.

## 5. Components

- `UI/AppleStyle.cs` — tokens (colours per theme, sizes, fonts) and shared drawing helpers (ring, rounded bar,
  module, chip). Replaces the colour/size constants scattered in BarRenderer/CardRenderer/Paint.
- `UI/BarRenderer.cs` — rewritten draw/measure for the menu-bar layout; public API unchanged
  (`MeasureBar`, `DrawBar`, `ShowReset`, `ShowNames`, `Light`, `Dot`).
- `UI/CardRenderer.cs` — rewritten for modules; `Measure`/`Draw` API unchanged; draws without its own shadow when
  the window has a system backdrop (`Backdrop` flag), with the faux shadow only in the layered fallback.
- `UI/BackdropWindow.cs` — base Form for cards: picks Acrylic backdrop / accent / layered fallback, paints a
  premultiplied bitmap onto the frame-extended client area, exposes `Render(bitmap, position)`.
- `DetailPopup` and `DesktopCard` derive from `BackdropWindow`; their behaviour code stays.

## 6. Risks and how we de-risk

- Acrylic with custom per-pixel painting on WinForms/net48 is the main unknown → Task 1 is a spike that proves it
  on this PC (Win11, 150 % and 100 % monitors) before any renderer work; if it fails, the fallback path ships.
- Screen capture here cannot see layered/backdrop windows → verification uses offline renders (same renderer),
  hit-testing and window attributes (`DwmGetWindowAttribute`) rather than screenshots.
- Wider bar text → auto-fit already handles; widths measured in tests at 100/125/150 %.

## 7. Testing

- Unit: tokens per theme; ring geometry (arc length ∝ remaining); bar measure with/without names/reset at
  levels 4/5/6; card measure grows with modules, PC row present; renderers draw warning colours only when low.
- Offline renders (light/dark, bar and card) reviewed visually before installing.
- Live: install on this PC, check bar width vs. available space, card backdrop attribute set, hit-test,
  drag/size still work, popup fade.

## 8. Out of scope

- New data, new services, animations beyond the popup fade, custom icons/logos for services (letter chips only),
  Liquid-Glass specular effects.
