# UX-17 report — owner-PC checks and theme persistence

- **Branch:** `codex/magisk-windows-lab`
- **Commit:** `e2c5c7f` (parent `3b744d8`, UX-16). Pushed without force.
- **Date:** 2026-11-16
- **Host:** HP ProBook 430 G6, Windows 11 Pro 10.0.26300, Intel UHD 620,
  1920x1080. Scaling 125 % (120 DPI) for the baseline captures, 150 %
  (144 DPI) and 175 % (168 DPI) for the matrix, restored to 125 %.
- **Guest:** disposable lab root `D:\Emulator-Lab\UX17-Evidence`, instance
  `ux17lab`, image `system-images;android-35;default;x86_64` (pixel_6, API 35,
  x86_64), console 5554 / controller 8554, WHPX 10.0.26300, headless engine.
- **Evidence:** `artifacts/ui/ux17-*.png` (local; `artifacts/` is ignored by
  Git like every previous slice). Logs: `artifacts/ux17-final-build.log`,
  `artifacts/ux17-final-tests.log`.
- **Not committed by design:** `CODEX-EMULATOR-HANDOFF.md`,
  `CODEX-EMULATOR-MASTER-PROMPT.md`.

## Result at a glance

| Area | Result |
| --- | --- |
| Release build | 0 errors, **22 warnings** (exactly the baseline) |
| Tests | **577/577** (542 before, +35 new: 19 theme store, 16 platform gates) |
| `git diff --check` | clean |
| Theme key parity | **71/71/71**, identical key sets in Dark/Light/HighContrast |
| Colour literals outside theme dictionaries | none (two code comments only) |
| High Contrast | verified on the real Windows policy (Night sky) at 125/150/175 % |
| 150 % | verified, no clipping, 46x36 DIP caption buttons, HTMAXBUTTON exact |
| 200 % | **not executable on this panel** (see below); 175 % verified instead |
| Windows 10 | code review + build gates + 16 unit tests; no Win10 hardware |
| Theme persistence | saved, loaded before the first window, atomic, fail-safe, tested |

## Part A — High Contrast

High Contrast was switched on with the real Windows policy
(`SystemParametersInfo(SPI_SETHIGHCONTRAST)`, `tools/ux17-highcontrast.ps1`),
which applied Night sky: `Window #202020`, `WindowText #FFFFFF`,
`Highlight #8EE3F0`, `HighlightText #263B50`, `WindowFrame #FFFFFF`,
`GrayText #A6A6A6`, and `ControlLight #FFFFFF` (that value is the trigger for
one of the bugs below). The app's `ThemeManager` reacts to the
`SystemParameters.HighContrast` change, so the theme swapped live. The policy
was switched off at the end and verified off.

Captured (High Contrast): Studio boot stepper and Studio with a live frame,
Devices, Agents, the settings drawer (Appearance), the command palette, the
caption close at rest/hover/pressed, the maximize Snap Layouts flyout, and
the 1024 DIP and 150/175 % variants.

### Defects found and fixed

1. **Selected rows were unreadable (palette proven, Logcat/Agents by the same
   construction).** `SelectionBg` is `SystemColors.HighlightColor` in High
   Contrast (a solid light cyan) while row text kept the Graphite brushes
   (near-white), so the selected palette row was white on cyan
   (`ux17-hc-screen*.png` era, before the fix). Added `SelectionText` and
   `SelectionTextMuted` to all three dictionaries (Dark `#F0F2F4`/`#99A5B0`,
   Light `#1C2933`/`#5A6976`, High Contrast `SystemColors.HighlightTextColor`)
   and applied them to: TextBox `SelectionTextBrush`, ComboBoxItem /
   ListBoxItem / ListViewItem selected foreground, the palette row title and
   hint, and the Logcat time/tag/message styles. Dark and Light render
   exactly as before (the tokens equal the colours those surfaces already
   used); only High Contrast changes. Proven in `ux17-hc-palette.png`,
   `ux17-dpi150-hc-palette.png`.
2. **Neutral chips were white text on a white fill.** `ChipBg` and `HoverBg`
   mapped to `ControlLightColor`, which is pure white in this dark contrast
   scheme, so an unclassified state pill painted white on white (captured at
   150 %, `ux17-hc-crop-statepill.png`). Both now use `ControlColor`, the
   surface `ControlText` (here the window text colour) is defined against.
   Trade-off: a neutral chip no longer shows a filled body in High Contrast;
   its word and dot carry the state, which the theme comment already requires.
3. **Boot stepper clipped mid-word.** The stepper was a content-sized
   horizontal StackPanel inside a clipped card, so "waiting for the launcher"
   and "boot_completed reported" were cut at the card edge
   (`ux17-dpi150-crop-stepper.png`, `ux17-hc-boot-stepper.png` before the
   fix). It is now a five-column grid (step / connector / step / connector /
   step, star step columns) and the step template is a two-column grid, so a
   long supervisor word wraps inside its third. Verified in
   `ux17-hc-boot-stepper.png` (final build, Dark) and `ux17-hc-studio.png`
   (High Contrast).
4. **False theme save when the drawer opened.** The theme radios are checked
   by the view-model refresh inside `OpenSettings`, which raised `Checked` and
   wrote `settings.json` without a user choice (observed at 10:27 and again at
   11:04 when High Contrast switched off changed the resolved mode). The
   suppress flag is now raised *before* the refresh, and `SetTheme` also
   skips apply/save when the requested mode is already in force. Verified:
   opening the drawer writes nothing; one click writes exactly one line.

Audited and clean: no `#RRGGBB` literal in any non-theme XAML (only two
comments in `TitleBar.xaml`); focus rings are `FocusRing`
(`SystemColors.HighlightColor`) and were visible on the caption, the radio and
the rail; every text/icon/border pair was checked against the live palette
(`TextPrimary` 14.5:1 on `WindowBg`, `TextSecondary`/`GrayText` 4.9:1,
`Accent` 10:1, `SelectionText` on `Highlight` ~12:1).

## Part B — DPI

`Settings > Display > Scale` was driven through UI Automation.

- **150 % (144 DPI):** full screen set captured — Studio, Devices, Agents,
  settings drawer, palette, 1024 DIP layout, caption hover/pressed, Snap
  Layouts flyout, plus a 1770x990 screenshot of the window at 1180 DIP.
- **175 % (168 DPI):** the highest scale this panel offers. Captured Studio
  at 1024 DIP (1792x1085) and the caption probe.
- **200 % is not offered** on this 1920x1080 panel: Windows hides a scale
  whose effective resolution would fall below 1024x768, and 1920/2 = 960
  (`Settings` offers 100/125/150/175 only). 200 % therefore stays unverified
  rather than simulated; a per-monitor 200 % host or a higher-resolution
  display is required.

Measurements (`tools/ux17-caption-probe.ps1`, real `WM_NCHITTEST`): caption
buttons are **46 x 36 DIP** at 125 %, 150 % and 175 % (58/45, 69/54, 80/63
physical); HTMAXBUTTON (9) is returned at four points 2 px inside each corner
and the centre of the maximize button, and HTCLIENT (1) three pixels outside
it, at every scale. The Snap Layouts flyout appears on hover on Windows 11
(`ux17-hc-snap-flyout.png`, `ux17-dpi150-hc-snap-flyout.png`).

No clipped text, no blurred glyphs (all icons are vector paths), viewport and
bezel proportions unchanged (1080x2400 fit, frame 540x1200 delivered), the
drawer stays 460 DIP and the palette 640 DIP, and the 1024 DIP layout stacks
as designed.

**Fix:** `ChromeShell` recomputed the maximized shell inset only on
`StateChanged`; a maximized window whose monitor scale changed kept the old
device-pixel overhang divided by the new scale. It now recomputes on
`Window.DpiChanged` as well (and re-clips immediately).

## Part C — Windows 10 compatibility (code review)

| OS-specific path | Gate | Windows 10 fallback |
| --- | --- | --- |
| UX-14 Snap Layouts / HTMAXBUTTON hook (`TitleBar`) | `PlatformSupport.SnapLayoutsSupported` = build >= 22000 (was already gated) | hook not installed; the WPF maximize button keeps ordinary client-area mouse input and hover/press |
| `DWMWA_WINDOW_CORNER_PREFERENCE` (`Chrome`) | **new** `RoundedCornersSupported` (>= 22000) — previously sent unconditionally | square DWM frame; the 12 DIP shell radius still rounds the content |
| `DWMWA_USE_IMMERSIVE_DARK_MODE` (`Chrome`) | **new** `ImmersiveDarkModeSupported` (>= 17763) and attribute id chosen per build: 20 from 2004 (19041), 19 on 1809-1909, skipped below | custom strip is drawn by WPF either way; caption colour falls back to the system |
| WindowChrome / ChromeShell / maximized inset | user32 `MonitorFromWindow`/`GetMonitorInfo`/`GetWindowRect` (Vista+) | unchanged, measured rather than assumed |
| Segoe UI Variable Text | comma fallback `Segoe UI Variable Text, Segoe UI` | Segoe UI |
| Icons | all path geometry (no Segoe Fluent Icons / MDL2 glyph font) | identical |
| Reduced motion | `SystemParameters.ClientAreaAnimation` | identical |

The gates live in a pure `Services/PlatformSupport` class (no WPF types) with
16 boundary tests (10240, 17762/17763, 18363, 19041, 19045, 21999, 22000,
22631, 26300, plus a live-consistency check against `OperatingSystem`).

**Only confirmable on a real Windows 10 PC:** the caption repaint after a
theme toggle (the known Nit 4), DWM's square frame and shadow, the absence of
the Snap flyout with the client-area maximize behaviour, and Segoe UI metrics
at 150 % (text wrapping in the boot stepper at a different font).

## Part D — Theme persistence

- File: `%LOCALAPPDATA%\EmulatorWorkspace\settings.json`, `{"theme":"Dark"}`
  (UTF-8, no BOM, two-space indent, hand-editable; comments and trailing
  commas are tolerated on read).
- `App.OnStartup` resolves the mode and calls `ThemeManager.Initialize(mode)`
  **before** the first window is constructed, so the first frame is already
  in the saved theme. Proven by `ux17-theme-persisted-restart.png`: Windows is
  dark, the saved choice is Light, and the first window is Light.
- Writes are atomic: temp file in the same folder, then
  `File.Move(temp, path, overwrite: true)` (one directory-entry replace on a
  volume). A refused write logs one line, cleans the temp and returns false.
- Missing / corrupt / unknown: exactly one line in `logs/desktop.log` and the
  shipped default (Follow Windows). Runtime evidence:
  `not found; following Windows`, `unreadable (JsonReaderException);
  following Windows` (window then rendered Dark), `Light from …`.
- "Follow Windows" is the default and is not a drawer control (the drawer only
  names the two concrete themes it has). A hand-written `"HighContrast"` is
  recognised as a Windows policy and resolved to Follow Windows rather than
  pinning the app in a scheme the operator cannot leave.
- Tests (19): round-trip for Dark/Light/System, no-BOM, folder creation,
  atomic replace with no temp left, missing, corrupt, unknown, no `theme`
  property, stored `HighContrast`, hand-edited comments/trailing commas,
  refused destination, spelling parsing, default path, name round-trip.

## Bugs found and fixed

1. Selected row text on the solid system highlight (palette proven; Logcat and
   Agent Sessions fixed by the same tokens).
2. Neutral chip fill white in a dark contrast scheme (`ChipBg`, `HoverBg`).
3. Boot stepper status clipped mid-word at 150 % (and at 125 % for the long
   "waiting for the launcher" string).
4. Maximized inset stale after a DPI change (`WM_DPICHANGED`).
5. `DWMWA_WINDOW_CORNER_PREFERENCE` sent to Windows 10 without a build gate.
6. Theme written without a user choice when the drawer opened.

No change to any command, binding, existing `x:Name`, handler, viewport
geometry, engine, supervisor or root. The test project gained three
`Compile Include` entries for the new pure files.

## Observed but not changed (outside this slice)

Twice during a long automation session the desktop process exited without an
`exit code=` breadcrumb, and the same period shows two supervisors competing
for one pipe (`Pipe listener error: The pipe is being closed`) with
`Adoption: stopped` for the guest. It is the UX-16 supervisor watchdog /
`SupervisorConnection.Closed` path that ends the app when the pipe breaks
during a slow guest start, not a presentation defect, and it was not
reproduced on demand. It deserves its own pass (single-supervisor ownership,
and a visible reason instead of a silent exit).

## Still unverified

- 200 % scaling (panel limit above).
- Every Windows 10 item in Part C on real hardware.
- Agent Sessions and Logcat **row selection** use the new tokens but had no
  rows to render: the evidence guest has no paired session and the log stream
  stays empty on this headless image.
- Mixed-DPI (moving the window between monitors at different scales) — this
  host has one monitor; the `DpiChanged` fix is code-reviewed, and the
  single-monitor 125→150→175 transitions were exercised live.

---

## Summary for Grok Bot

UX-17 is on `codex/magisk-windows-lab` at `e2c5c7f` (pushed, no force).
Release build 0 errors / 22 warnings, 577/577 tests (35 new), `git diff
--check` clean, theme keys 71/71/71. High Contrast was verified against the
real Windows policy (Night sky) at 125/150/175 % and four contrast bugs are
fixed: selected rows painted near-white text on the solid system highlight
(new `SelectionText`/`SelectionTextMuted` tokens), the neutral chip used
`ControlLight` (pure white) under white text, the boot stepper clipped a
supervisor word mid-line, and opening the drawer wrote a theme nobody chose.
DPI: caption buttons measure 46x36 DIP and a real WM_NCHITTEST returns
HTMAXBUTTON across the whole maximize button at every scale; the maximized
inset now follows `Window.DpiChanged`. 200 % is impossible on this 1920x1080
panel (Windows hides scales below 1024x768 effective), so 175 % is the highest
verified step — flag that, not a code gap. Windows 10: the DWM corner
attribute is now build-gated like the four semantics already were, the
immersive-dark attribute id is chosen per build, and the gates have 16
boundary tests; real Win10 hardware is still needed for the caption repaint
and font metrics. Theme persistence lives in
`%LOCALAPPDATA%\EmulatorWorkspace\settings.json`, is read before the first
window (no flash), written atomically without a BOM, and falls back to Follow
Windows with one `desktop.log` line on any failure. Evidence:
`artifacts/ui/ux17-*.png`.
