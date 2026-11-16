# Android Emulator Workspace Plan

## Active next work: premium native UI (2026-11-14)

Owner requested research, detailed design and a handoff roadmap. Read
[PREMIUM-UI-MASTER-PLAN.md](PREMIUM-UI-MASTER-PLAN.md) and
[DEEPSEEK-UI-IMPLEMENTATION-PROMPT.md](DEEPSEEK-UI-IMPLEMENTATION-PROMPT.md).
Graphite Studio/Porcelain browser prototype, research, tokens, full interaction
spec and verification are in [the design package](docs/design/premium-workspace/README.md).
Research/design/prototype are complete; native implementation is pending.

UX-00 (evidence and capability inventory) and UX-01 (theme and control
foundation) are complete and committed on `codex/magisk-windows-lab`
(2026-11-14). UX-00 recorded the Release build/test baseline (400/400 tests)
against handoff commit `1bcae22`, captured the unstyled baseline window
(`artifacts/ui/ux00-baseline-dark.png`) and mapped every proposed desktop
command to its live supervisor operation, arguments and state source before any
UI change. UX-01 rewrote `Themes/Dark.xaml`, `Themes/Light.xaml`,
`Themes/HighContrast.xaml` and `Themes/Common.xaml` on the design-package token
set (dynamic resources, Graphite dark / Porcelain light / system HC mappings),
added complete normal/hover/pressed/disabled/focus states for buttons,
TextBox, ComboBox (with themed popup), CheckBox, ListBox/ListView, ScrollBar,
ToolTip, ContextMenu, MenuItem, ProgressBar, DataGrid and GroupBox, replaced
the literal `--` zoom-out glyph with line-icon geometries plus accessible
names, and added the native dark title-bar service
(`Services/Chrome.cs`, `DWMWA_USE_IMMERSIVE_DARK_MODE` with attribute-19
fallback, system chrome kept under High Contrast) wired into MainWindow,
CommandPaletteWindow and AgentSessionsWindow. All command bindings, the
viewport hierarchy and input targeting are unchanged. Verification: Release
build clean, 400/400 tests, `git diff --check` clean, rendered window evidence
for dark first launch, light theme, themed popup in both themes (screen
captures; PrintWindow misses popup HWNDs), dark title-bar restore, keyboard
focus rings and the 980x620 / 1280x800 / 1440x900 resize matrix at the host's
125% scaling (`artifacts/ui/ux01-*.png`).

Grok review round (2026-11-14, range `1bcae22..b55a4e1`): no binding, geometry
or targeting regressions; two Medium findings fixed and re-verified at runtime.
M1 `ThemeManager.Initialize()` now carries a `_initialized` guard (same pattern
as `Chrome.Initialize`), so opening the command palette or Agent Sessions no
longer resets an explicit Light/Dark choice and cannot stack High Contrast
handlers; verified by toggling Light, opening Ctrl+K and Agent Sessions and
closing both (`artifacts/ui/ux01r-palette-light.png`,
`ux01r-agents-light.png`, `ux01r-light-persists.png`). M2 the custom
`GridViewColumnHeader` template regained its `PART_HeaderGripper` Thumb
(8 DIP, transparent template, `SizeWE` cursor), restoring drag-resize and
double-click auto-fit on the log columns; verified by a SendInput drag that
widened the Time column by ~80 DIP (`artifacts/ui/ux01r-col-resized.png`).
Also applied from the review: `FocusVisualStyle={x:Null}` on ToolButton,
PrimaryButton, IconButton, ComboBox and CheckBox so keyboard focus shows one
rounded ring instead of ring + system dashed rectangle
(`ux01r-focus-single.png`), hover/focus trigger order corrected in TextBox and
CheckBox templates, an editable-combo caveat comment added, and a new
`tools/drag-at.ps1` SendInput helper plus `tools/uia-close.ps1`. Deferred to
UX-02 with pointers: indeterminate ProgressBar animation (L1), ListViewItem /
DataGrid focus and disabled states, ScrollBar thumb hover/drag, MenuItem
pressed state (L3), dedicated InputBorder/ScrollThumb tokens at >=3:1 (L4),
single-mechanism disabled dimming (Nit 1), light TextTertiary darkening if it
is ever used as text on tinted surfaces (Nit 2), and a Windows 10 caption
repaint nudge after theme toggles if observed (Nit 4).

Known deferrals: the toolbar still wraps and the Logcat drawer still starts
open (both are UX-02 shell scope); High Contrast and 150%/200% DPI verification
need owner-side system changes and stay pending. New validation helpers live in
`tools/` (capture-window, capture-screen, set-window-size, focus-cycle,
drag-at, uia-invoke, uia-combo, uia-close).

Next: UX-02 shell recomposition, then UX-03 states, UX-04 library, UX-05
privileged flows, UX-06 tools, UX-07 finish, one runnable slice at a time
through the documented gates. No production/runtime edits in this task.
Latest Grok approval of 24cc54d remains merge-only. Journal-write evidence nit
is optional backlog. Fresh provenance-backed WHPX and existing power-loss,
AOSP, signing/redistribution gates remain open; readiness patch excluded.

UX-08 Logcat drawer (2026-11-15, uncommitted working tree on
`codex/magisk-windows-lab`, base HEAD `9ee8355`): the dock is restyled to the
Graphite mockup (`shots/07-logcat.png`, `shots/02b-studio-logcat-drawer.png`).
The 36 DIP toolbar carries two filter fields (leading line icon, mono
placeholder), five independent level chips (V/D/I/W/E letter in the level colour
on its ~12 % tint), the stream state chip (Following / Paused / Not attached /
No guest) and four 30 DIP ghost icon actions (pause, clear, copy, export) with
real chords (Ctrl+P, Ctrl+Shift+K, Ctrl+Shift+C, Ctrl+E; Ctrl+F focuses the
search field). The table is a sticky 24 DIP header over 26 DIP Cascadia Mono 12
rows: tabular timestamp in text3, the level glyph as a tinted 16 DIP chip, tag in
text2, message in text1, warnings and errors marked by a 2 DIP leading bar, row
hover through the shared 120 ms fade, selection in the shared selection tint with
a 1 DIP accent focus edge. The dock keeps the 1 DIP specular top edge and its
220 ms cubic-bezier(0.22,1,0.36,1) expand (instant under reduced motion) at a new
256 DIP height that fits exactly seven rows. An err-soft band with Retry appears
only when the subscription is refused, and the calm empty state (icon, one line,
one sentence, at most one real action) covers no guest, no subscription, paused,
filtered-out and waiting-for-the-first-line. Level filtering moved into
`ViewModels/LogcatPresentation.cs` (pure, 21 tests); the level chips replaced the
single "minimum level" ComboBox because a minimum-level chip would have implied
that a Warn filter hides errors. No new theme key (every colour is an existing
Graphite token), no palette change, no viewport/geometry/engine/Magisk change.
Build 0 errors / 22 warnings, 443/443 tests, `git diff --check` clean; evidence in
`artifacts/ui/ux08-*.png`.

UX-08 fix pass (2026-11-15, same uncommitted tree): every item in
`igr-feedback-ux08.md` is folded in. The dock header is now 36 DIP with the
Logcat tab, a segmented "N lines · N warnings · N errors" summary (warn/err in
their tokens, each hidden at zero) and the pop-out that opens the new full
Logcat page, next to the collapse chevron. The dock is 180 DIP by default,
resized by a 4 DIP splitter with a 24 x 3 grip between 120 DIP and half the
window, remembered for the session only; rows are 20 DIP with 14 DIP level
badges (V text3, D text2, I/W/E soft tints) and warn/err message tints; the tail
follows while pinned and a "Jump to latest · N new" pill appears after the
operator scrolls up; appends batch at 100 ms on the existing 2,500-line ring
buffer, virtualized in Recycling + pixel scroll. The page (rail Logcat, dock
pop-out, Back to Studio) has the DIAGNOSTICS/Logcat header with the real serial,
a Paused · N buffered chip backed by a real paused buffer that flushes on
resume, Pause/Resume + ghost Copy/Clear + one accent Export, the 240 DIP search,
tag field, five dot chips (raised + stroke on, text3 off) and the Live/Paused
state chip with a reduced-motion-aware pulse. Its 12 radius table has a 32 DIP
caps header, 120/40/180/* columns (no PID: the supervisor sends none), 28 DIP
rows with stroke-soft dividers, hover/selected states, Ctrl+C or the context
menu copying selected lines, palette `HighlightTextBlock` matches, and the two
named empty states. UX-07 nits: the user-facing "Defined" label is "Stopped"
everywhere (header chip, palette context row, library card and tooltip), the
About footnote is gone, and the palette's selected-row bar now uses the rail's
exact 2 x 18 geometry. Validation and the per-item status are in
`ux08fix-report.md`; evidence in `artifacts/ui/ux08fix-*.png`.


UX-09 Agent Sessions, privileged consent and the UX-08 fold-in (2026-11-15, same
uncommitted tree, base HEAD `921c829` = UX-08): the two remaining privileged
surfaces are restyled to the Graphite mockup (`shots/08-agent-sessions.png`,
`shots/06-magisk-consent-dialog.png`) and `igr-feedback-ux09.md` is folded in
whole. Agent Sessions: AUTOMATION eyebrow, 24 DIP title, the two-line
description, the live session summary, one err-outline "Take over · revoke all",
a 280 DIP approval card (request list with mono ids and soft Pending/Expired/
Approved/Completed chips, an accent edge only while a real request waits, the
selected request's client/template, a live expiry countdown that turns warn in
the last minute, the scope field and one accent Approve), and two table cards -
Paired Sessions and Recent agent operations - over a 42 DIP caps header, a 30 DIP
column header and 34/30 DIP rows with mono ids, up to three scope chips plus a
"+N" chip, a live "in 1h 46m" expiry, an Active/Expiring (last five minutes)/
Expired/Revoked chip, a per-row err-outline Revoke that is disabled with its
reason, outcome chips that carry the supervisor's code ("Denied ·
forbidden_scope") and a right-aligned `ms`. At 1024 DIP or narrower the card
stacks above the tables and the body scrolls. The three collections, every
supervisor call and every decision are unchanged (approve cloned a real guest,
revoke turned the session chip red, takeover revoked every session on the
guest); display rules live in `ViewModels/AgentSessionsPresentation.cs` with 55
tests. The consent surface is one shared `ConsentDialogWindow` (50 % scrim, 560
DIP card, 160 ms dialog curve, Esc/scrim/Cancel all cancel, focus starts on
Cancel) used by the Magisk install (err tile, three artifacts with their real
SHA-256 and a check, the five consequences, acknowledgment and the typed guest
name), ADB root (warn tile, acknowledgment only) and stock recovery (accent
tile, acknowledgment and typed name) - its copy is split out of the old
MessageBox text only. Cancel returns before the caller's first supervisor
request: after cancelling every variant `magisk.status` still said
`not_configured` and `journal/` held no files. `igr-feedback-ux09.md`'s UX-08
nits are folded in too: the dock is header plus lines (no filter row, no column
header, one 28 DIP search that expands to 200 DIP, Resume while held, actions on
the page), the counts are compact with 6 DIP separators, rows carry 8 DIP after
the time and the level badge, the page shows one paused chip with its buffered
count, the status bar holds segments only (transient sentences are a 2.5 s
bottom-centre toast), the Logcat rail item carries the active indicator while
the page is open, and every message keeps its full text in a tooltip. Build 0
errors / 22 warnings, 514/514 tests, `git diff --check` clean; evidence in
`artifacts/ui/ux09-*.png`.

UX-10 Light theme parity (2026-11-15, same uncommitted tree, base HEAD `e41c277`
= UX-09): the same Graphite structure in the light theme, tuned to the light
system sheet (`shots/09-light-theme-studio.png`, `10-light-theme-devices.png`,
`17-design-system-light.png`) with light token values only - no Dark palette
change, no indigo retheme. `Themes/Light.xaml` moves stroke to the sheet's
`#CDD5DC` (was `#B8C2CB`, which made every field, chip and group edge read as a
border), raised to `#EEF1F4` (was `#E4E9ED`, a grey slab inside the white
settings cards and the inspector's target card), the neutral chip to `#E9EDF0`,
accent-soft and selection to the sheet's active-tile blue `#DCEAF4` (was the
grey `#E1E8ED`), and the soft state tints to `#E8EFEC` / `#F8EFE3` / `#F8EEEF`
(the err-soft acknowledgement callout no longer reads as a pink slab). Elevation
follows the sheet too: the floating shadow is `#1C2933` at 8 % (blur 16, y6) and
6 % (blur 2, y1), the specular edge is off in favour of the 1 px stroke, the
stage glow is ~4 % with no noise tile, and the device bezel is `#1C2933`. New
tokens resolve in all three dictionaries: `StatusBarBg` (dark keeps `PanelBg`;
light uses the navigation `#EEF0F2` strip), `LogHeaderBg` (the log table header
on that same strip in light, transparent in dark), `DialogScrimBg` (light 40 %,
dark 50 %), `OnDanger` (the label on a solid error primary) and
`AccentDisabled` / `AccentDisabledText`, so a disabled primary keeps the sheet's
muted accent fill with its on-accent label instead of turning into an invisible
ghost on white (dark resolves to `#556675` + `#142A3B`, exactly what sheet 15
draws; High Contrast uses the system control surface and grey text).
`WarnButton` gains the warn-soft fill both sheets give the privileged action and
`DangerPrimaryButton` (the consent confirm) is a solid error fill that falls back
to err-soft at 50 % while its gates are unmet. No Dark token value changed - only
the new keys, at their existing colours - so dark renders as at `e41c277` apart
from the palette scrim, which the reviewer set to 30 %.

`igr-feedback-ux10.md` (21:40, newer than the prompt) is folded in whole. Its
UX-09 nits: the pairing card's dead gap is gone (the request list sits directly
under the description, caps at three 44 DIP rows with its own scroll, and the
Client/Template/Scopes detail follows it; the title and key glyph are text1);
the scopes field now separates with ", " so WPF wraps at commas instead of
mid-scope and all eight scopes fit; supervisor result codes are mapped to words
("forbidden_scope" -> "Scope not allowed") with the raw code kept in the row
tooltip, via a tested `CodeLabel`; the duplicate header Revoke is gone; the
paired-sessions card sizes to its content with a floor for the empty state and
Recent operations takes the rest; the consent target line prints the readable
image name ("Android 15 · AOSP · x86_64") with the raw package in the tooltip;
the consent confirm has the sheet's ready and disabled states; every artifact row
gains a small copy action (full digest already in its tooltip); the consent is
owned by the window and dims the app at 40 %; and the Inspector's Diagnostics tab
prints three label/value doctor rows - image, acceleration with its Ready chip,
GPU - instead of the raw dump, with Copy still handing over the whole report.
Its light-parity list drove the token work above; two items are recorded as
deviations in the UX-10 report (the media state tint the mockup itself does not
show, and text3 kept at `#55636E` because `#62727F` measures 4.3:1 on raised).
The pass also fixed a real live-switch defect it exposed: the tone chips resolved
their brushes through converters ONCE, so a dark-theme chip stayed dark on a
light surface after toggling (the Magisk lab chip was `#23272B` on white). They
now use tag-driven styles whose brushes are all DynamicResource. Verified on a
disposable root with a real API 35 guest: Studio, Device Manager, Logcat,
Settings drawer, command palette, Agent Sessions and both consent variants in
both themes, plus hover, keyboard focus, the disabled primary, the Create drawer
and the consent ready/disabled pair in light; every light text pair measures
>= 5.2:1, inputs 3.6:1 and the focus ring 6.9:1. Build 0 errors / 22 warnings,
526/526 tests, `git diff --check` clean. Theme persistence and Follow Windows
remain deferred: they need a preference store, which this slice does not add.
Evidence in `artifacts/ui/ux10-*.png`.

UX-11 responsive 1024 (2026-11-15/16, same uncommitted tree, base HEAD
`c454730` = UX-10): the 1024 DIP layout both responsive shots draw, in Dark and
Light, without changing the wide layout's structure. Two breakpoints carry it:
`NarrowLayout` (1120 DIP, command bar and Device Manager) and `InspectorNarrow`
(1180 DIP, the inspector only, as `igr-feedback-ux11.md` asks). Studio: below
1180 the inspector collapses to a new 48 DIP strip at the window edge (Device /
Capture / Diagnostics glyphs); opening it from the strip floats the same panel
over the stage behind a 30 % `InspectorScrimBg` scrim, because the stage spans the
panel's columns at that width - the viewport keeps its full measure, and nothing
in the panel is reparented or duplicated. The command bar hides the telemetry pill
(the status bar keeps the live frame rate), the Install APK label (icon-only, name
and tooltip kept) and the two lowest-priority groups, which move into a "..."
menu with the same commands: fit/1:1/zoom and pop-out/fullscreen. Install APK and
the power action never overflow. The status bar drops the Agents segment at 1024
(there is no sample segment on this bar). The collapse is owned by the layout only
when the layout performed it: widening restores the panel, an operator toggle is
never undone, and Focus view keeps ownership while active. Device Manager: at 1120
the header moves search, sort and New device to their own row under the title
(shot 13), the description widens to one line, the grid becomes two columns of
half-measure cards (`TileWidth`) and the four counters go 2x2. Everything else was
already responsive: the Logcat dock and page fit with no horizontal scroll, the
Settings slide-over stays 460 DIP, and the palette, Agent Sessions and the 560 DIP
consent dialog fit. `igr-feedback-ux11-carryover.md` is verified item by item
(header Revoke gone, content-sized sessions card, ImageNaming consent target,
solid err confirm with a 50 % disabled state, owned/centred consent, doctor rows in
Diagnostics) - all were already done in UX-10.
`igr-feedback-ux11.md` (23:55, newer than the prompt) is folded in: the library
card media strip takes its tint from the tokens (ok-soft Running, warn-soft
recovery/attention, raised neutral Stopped; the old one-off radial glows are
deleted, 66 theme keys per dictionary); light `TextTertiary` is the sheet's
`#5A6976` (4.98:1 on raised, >=4.88:1 on every tint) and the stage captions stay
`TextSecondary` `#52616E`; the dark settings scrim stays 50 %; the sparkline is a
1.5 DIP accent line over a 12 % area; the frame-stall chip is anchored at the top
of the stage above the bezel instead of over guest pixels; rail tooltips are names
only; the palette footer keeps only its key hints; the active Settings tab is a
raised fill with text1 and no border (accent ring stays keyboard-only); the
Devices footer is the "Instances N" segment with the transient sentence kept,
the Running count sits on one baseline with "of N", and Needs attention prints 0
with a "none" caption and takes the warn tone only above 0; a W log line colours
only its badge and tag, E/F rows get a ~6 % full-row err tint; a no-request pairing
card collapses its detail and disables Approve, and Take over is disabled with no
sessions; the first-run card's raw doctor dump is replaced by the three doctor
rows. Deferred with reason: hosting Agent Sessions inside the shell content area
(its own later slice - the window, its data and its consent path do not change
here). Evidence at 1024 DIP (`1280x1000` physical, host 125 % scaling: 1024 x
800 DIP) and wide (`1200x810` DIP = `1500x1012` physical), both themes, in
`artifacts/ui/ux11b-*.png`. Build 0 errors / 22 warnings, 526/526 tests,
`git diff --check` clean; theme keys 66/66/66. Also deferred: theme persistence,
Follow Windows, and a true 1024 physical-pixel capture (would require changing the
host display scale).
UX-12 custom title bar and Ctrl+1..5 (2026-11-16, same uncommitted tree, base HEAD
`21636f8` = UX-11): both shell windows drop the native caption. `WindowChrome`
keeps the real window (44 DIP caption, 6 DIP resize border, Aero Snap, Alt+Space,
the system menu and the three `SystemCommands`) and the shared `Views/TitleBar`
draws the strip: 22 DIP app mark, 13 DIP workspace name, breadcrumbs built from
live state (`Studio` / `Logcat` / `Devices` plus the selected guest and its real
API level), the centred Ctrl+K pill with mono keycaps, and 36 DIP min/max/close
buttons (120/80 ms hover-press through `Views/Motion`, the 2 px accent focus ring,
err-soft close hover, the maximize glyph switching to restore). DWM rounds the
HWND (`DWMWA_WINDOW_CORNER_PREFERENCE`), the shell clips to the 12 DIP radius and
squares it when maximized; a WindowChrome window is maximized 7 DIP larger than
the work area on every side (measured 1938x1038 at -9,-9 for a 1920x1020 work
area) and WPF hands that whole rectangle to the client, so `Views/ChromeShell`
insets the shell by exactly the measured overhang - which is itself off screen, so
nothing is cropped and no frame ring appears. Ctrl+1..5 run the handlers the five
rail destinations already run, in rail order (Studio, Devices, Logcat, Agents,
Root Lab), exact modifiers only, numpad included; each rail tooltip prints its
chord in Cascadia Mono. The command palette is anchored to the shell body instead
of the client area, so its scrim dims the page and leaves the strip legible, and
the Device Manager hosts the same palette over itself on the Studio's view model
(its title bar shows that window's live selection). At 1024 the workspace name and
its chevron leave, the crumbs keep a 10 DIP gap and the pill narrows to 300.
Fold-in from igr-feedback-ux12.md (03:11): the narrow inspector overlay closes on a
scrim click and on Esc and returns focus to the strip icon; the strip is a permanent
top-aligned column with 40 DIP targets and the open tab's icon in the rail active
state; Install APK keeps its label down to 960 DIP; inspector human values (fps,
seconds, resolutions) use the UI face with tabular figures while serial, ports, ABI
and package stay mono, an unknown ADB daemon reads 'Not checked'; the header
thumbnail takes the token media tint; the Device Manager rail carries all five
destinations plus the four bottom utilities (a Studio-owned destination closes the
page and lands the Studio on it) and its status bar names the ADB serial instead of
an orphan dot; first run hides the pill and the two top chips and centres the setup
card. The stall chip stays centred above the bezel rather than on a header row:
a real row would cost the guest 34 DIP, which this slice's scope forbids.
Deferred: theme persistence and Follow Windows (no preference store), the Windows
11 Snap Layouts hover flyout (the caption buttons live in the client area, so
hover/press and the focus ring win; drag-Snap, Win+Arrow and double-click still
work), and a High Contrast / 150-200 % DPI render (owner-side, as before).
Evidence in `artifacts/ui/ux12-*.png`: Studio, command palette and Device Manager
at 1200 DIP and 1024 DIP in both themes, plus the rail tooltip, the close hover,
the maximized window and the 1024 inspector overlay. Build 0 errors / 22 warnings,
526/526 tests, `git diff --check` clean, theme keys 66/66/66.
UX-13 Agent Sessions as a shell page (2026-11-16, same uncommitted tree, base HEAD
`c98d352` = UX-12): the separate `AgentSessionsWindow` is removed and its whole
surface is a `UserControl` (`Views/AgentSessionsPage`) hosted in the shell body
under the rail, title bar ("Agents" crumb), command bar and status bar. The
collections, the supervisor calls, every handler and every display rule are the
UX-09 ones; the page exposes its real counts (`SessionCount`, `PairingCount`,
`PendingPairingCount`, `ActiveSessionCount`, `SummaryText`, `HasSessions`) so the
header, the rail badge and the status bar read the same state. The command bar
keeps the device context and only the page's own actions (Refresh, ghost-err
"Take over · revoke all"); Install APK, the guest controls and the frame
telemetry are hidden while the page is open. Shot 08 layout: 320 DIP pairing card
beside Paired and Recent, stacked above them below 1180 DIP, content-sized cards
with 120 DIP empty states, counts at the header's right. Pending pairing requests
put a real count badge on the rail item (hidden at 0); the status bar gains
"Agents N" for live sessions (hidden at 0). Selection and scroll position survive
page switches because the page is collapsed, never rebuilt. `--agent-sessions-probe`
now drives the page in the shell; `--open-agents` and `--select-device` are
evidence hooks. No engine, csproj, root, backend or Studio viewport change.
Deferred: polling for pending requests while the page is closed (counts are real
but appear after the page's first refresh or an explicit Refresh), and a
High Contrast render (theme keys unchanged, owner-side as before).
Evidence in `artifacts/ui/ux13-*.png`: the page wide and at 1024 in both themes
with a real pending request, two real sessions and real operations, plus the
empty state on a fresh root. Build 0 errors / 22 warnings, 526/526 tests,
`git diff --check` clean, theme keys 69/69/69 (the three caption-close tokens
added to every dictionary).
UX-16 open items (2026-11-16, same uncommitted tree, base HEAD `8febeee` = UX-15),
minimal code only:
A) the shell has one overlay state (`ViewModels/OverlayStack`, pure and tested).
The settings drawer and the command palette take the Shell scope (rail, title
bar content and page body), the narrow inspector takes the Stage scope (guest
frame, chips and pill; its own scrim stays clickable). While a scope is open the
chrome behind it is disabled, out of the hit test and out of Tab order, so UI
Automation reports it as unavailable, while the overlay keeps its focus, its tab
cycle and its automation tree. The title bar's crumbs and Ctrl+K pill go with it;
the three caption buttons stay live. Focus returns to the invoking control in
every case. WPF on .NET has no `AutomationProperties.AccessibilityView`, so
`IsEnabled` is the exposure control the framework offers.
B) `Services/ExitBreadcrumb` writes one `exit reason=<cause> code=<n>` line to
the existing `logs/desktop.log` from `Application.OnExit`, with notes from the
window close, `SessionEnding`, the dispatcher/AppDomain/TaskScheduler handlers
and the explicit probe shutdowns; `DesktopLog` appends with `AppendAllText`, so
the line is flushed before the process ends.
C) `ViewModels/SupervisorWatchdog` (pure and tested) bounds recovery at five
attempts with 1/2/4/8/15 s delays; `SupervisorConnection.Closed` plus the failed
requests start exactly one run, which reuses `SupervisorLocator` (attach first,
start only when nothing answers) and the existing `InitializeAsync`. The existing
err-soft bar with Retry is what remains when the budget is spent. Evidence in
`artifacts/ui/ux16-*.png` and `artifacts/ux16/` (UI Automation walks, watchdog
and exit logs). Build 0 errors / 22 warnings, 542/542 tests, `git diff --check`
clean, theme keys 69/69/69 (none added).
## Goal and baseline

Build a Windows-first Android emulator workspace for developers and QA. The release combines a responsive embedded Android viewport, recoverable device management, reproducible QA recipes, local agent control, and two separately verified root workflows. Deliver the source, a reproducible installer or installable release candidate, and evidence from real guests.

The folder was empty at planning time. Product name, repository setup, engine revision, SDK package revisions, and reference hardware are open. Start with an official x86_64 AOSP API 35 image, subject to SDK metadata. Every compatibility row and performance target is pending measurement. Track implementation and gates in [task.md](task.md). Follow [agents.md](agents.md) during implementation.

Phase 0 status (2026-11-10): the workspace is initialized and the runtime is
proven on the reference host. Pinned for testing: Android Emulator 37.2.12
(build_id 16428233), `system-images;android-35;default;x86_64` revision 2, and
the engine's own gRPC controller protos. Evidence, deviations, and remaining
measurements are in [docs/phase0-evidence.md](docs/phase0-evidence.md).

Phase 2 status (2026-11-10, closure pass): the local-only agent integration is
implemented and verified on WHPX. Pairing creates a revocable, expiring, scoped
session bound to one disposable AVD; TypeScript and Python MCP clients and wsctl
use the same supervisor registry. FLAG_SECURE observations return redacted typed
results, guest-only shaping via `tc netem` reproduces a bounded login timeout,
the WPF Agent Sessions surface lists/approves/revokes/takes over sessions, and
cleanup is approval-bound. The clean run `20261110-212427` passed 41/41 checks;
the final-code run `20261110-213845` passed 41/42 (the only failure was a
state-dependent test selector, corrected and re-verified separately) with 32
unit tests. Remaining verification gaps are the two-running-guest/external
transport cross-check and a live queued-input takeover race; Phase 1's desktop
click-through and performance targets remain separate open items. Evidence:
[docs/phase2-evidence.md](docs/phase2-evidence.md).

Phase 3 status (2026-11-11): the device library, snapshot, transport-identity,
simulation, deployment and external-runner slices are implemented and verified on
WHPX in `tools/phase3-acceptance.ps1`. A snapshot load no longer leaves the
guest unusable: the supervisor waits for the guest to answer again and performs
a clean boot when it does not. Two managed guests, an exact serial/transport/
engine-pid identity, a queued-input takeover race, a targeted `.apks`
deployment with pinned bundletool and a Maestro run against one selected guest
are all recorded in `docs/phase3-evidence.md`. Open: external SDK AVD import,
recipe record/replay, package/deep-link/instrumentation operations, crash/ANR
and bug-report capture, the second simulation pack, the desktop command palette
and the WPF click-through run. Evidence:
[docs/phase3-evidence.md](docs/phase3-evidence.md).

Phase 1 status (2026-11-10, stabilization pass): the developer MVP runs on real
Windows hardware. A persistent per-user supervisor owns lifecycle, SQLite state,
ports, credentials, and a versioned named-pipe contract; the WPF desktop embeds
the live guest with input, Inspector, Logcat, and APK drop. Acceptance run
`20261110-183709` passed 19/19 checks, including a strict launch check that
rejects ANR overlays; the original 18/19 run and its artifacts are preserved.
The interactive capture path moved from PNG to controller RGB888 at 540x1200,
measuring 32-34 FPS through the supervisor and 29.4 FPS in WPF versus 3.66 FPS
before. The 60 FPS and p95 <=100 ms targets are still missed. Evidence, raw
samples, and open gates are in
[docs/phase1-1-evidence.md](docs/phase1-1-evidence.md).

## Initial architecture decisions

Magisk handoff review (2026-11-14): the Linux bundle is checksum-verified,
but Windows setup is gated on clarification. The pinned upstream
`Environment#setupEnvironment` installs Shamiko and test modules as well as
the base environment, conflicting with this lab's scope. Preserve the Linux
raw results but do not treat them as base-only installation acceptance.
The Windows stock-only probe was interrupted and cleaned up before patched
boot or setup. [Questions and evidence](docs/magisk-handoff-questions.md).

Follow-up: corrected docs `485b093` and the fresh base-only Linux bundle
have now been reviewed. All 48 file hashes, the tar/inventory hashes, the
13 staged payload hashes and the app probe signature pass verification.
The new procedure uses ADB root temporarily for provisioning, then verifies
Magisk with adbd back at UID 2000. Grok explicitly requested an owner decision
on that dependency; the owner explicitly approved it. Windows feasibility
passed 132 checks and the supervisor install/two-cold-boot/stock-recovery
transaction passed. Failure-path and desktop validation are recorded in
[Windows evidence](docs/magisk-lab-windows-evidence.md). The app-UI
provisioning alternative remains unverified.

| Area | Starting decision | What must be proven |
| --- | --- | --- |
| Windows desktop | WPF, C#, .NET 10 LTS, MVVM, and native Windows packaging | Mixed-DPI rendering, accessible controls, and a responsive embedded viewport |
| Runtime | Per-user supervisor with narrow engine, ADB, and rendering adapters. SQLite stores durable state, leases, and operation records | Crash reconciliation, verified reattachment, exact target mapping, and two independent guests |
| Engine | Pin a compatible official Android Emulator SDK build and use WHPX on Windows 11 x64. Prefer headless instances and authenticated frame streaming. Retain a separate engine window as a diagnostic fallback | Actual binary, flags, controller protocol, endpoint binding, input, named snapshots, GPU modes, and software fallback |
| Device state | Separate immutable base images, profile configuration, writable instance overlays, and transient process state | Atomic metadata updates, one writer per instance, safe port reservations, compatible snapshots, and rollback |
| Client contract | Versioned requests and ordered events over a Windows named pipe restricted to the current user and logon session. Desktop, CLI, SDK, and MCP stdio use one operation registry and one authorization policy | Pairing and revocation, bounded payloads and events, typed errors, idempotent retry, stale-frame rejection, and explicit external ADB access |
| Root lab | ADB daemon root on supported debug guests. Magisk on a separate pinned AOSP x86_64 build produced on Linux | Targeted UID verification for ADB root. Verified boot input, app-level `su`, repeat cold boot, and stock restoration for Magisk |
| Build and tests | Pin toolchain inputs, build owned code independently of SDK assets, and use deterministic sample APK and Windows validation scripts | Clean-machine install, reproducible commands, separate real WHPX test lane, and confirmed redistribution rights |

Use documented engine interfaces first. Consider an engine fork only after measured requirements are blocked and its licensing, build maintenance, and security update obligations are recorded. Keep emulator and image pinning provisional until the feasibility spike closes.

## Phases and exit gates

| Phase | Build | Evidence to close |
| --- | --- | --- |
| 0. Feasibility | Bootstrap the workspace and prove real Windows boot-to-frame, input, snapshots, and exact ADB targeting. Start the Magisk boot-path probe in parallel with engine work | WHPX boot with two isolated AVDs, streamed frame and input trace, endpoint and snapshot results, and Magisk boot-input finding or named blocker |
| 1. Developer MVP | First-run SDK setup, one AOSP profile, lifecycle and recovery, embedded viewport, APK install and launch, Logcat, screenshot, and diagnostics | Fresh Windows setup through guest boot, APK deploy, resize and interaction, logs, screenshot, and graceful shutdown |
| 2. Agent control | Supervisor contract, CLI, MCP, SDK examples, semantic and visual interaction, pairing, permissions, audit, and evidence | Real MCP client and CLI complete the sample scenario. Denial, revocation, stale input, and cross-device tests pass |
| 3. Device and QA depth | Multi-instance lifecycle, snapshots and clones, simulation packs, recipes, artifacts, and external developer tooling | Repeatable clone and restore, rollback and two-device load tests, one deployment integration, and one external test runner |
| 4. Root Lab | ADB root and unroot, plus transactional Magisk setup and recovery on the verified lab profile | Targeted ADB UID transitions, app-level `su` on the Magisk guest, second cold boot, and boot-loop restoration |
| 5. Release | Package the app, document the verified matrix, benchmark it, and harden recovery and authorization | Clean install and launch, profile-preserving upgrade, uninstall, security review, soak run, and release report |

Keep each phase runnable. Continue SDK-independent work on phases 1 and 2 while hardware and image build prerequisites are pending. When a prerequisite blocks validation, record what was attempted, what remains pending, and the specific action needed. Keep simulators and test doubles visibly labeled.

## Phase 0 outcomes and material deviations

Verified on the reference host, with evidence in
[docs/phase0-evidence.md](docs/phase0-evidence.md):

- Two isolated AVDs cold boot on WHPX; boot-to-`boot_completed` measured at
  43-70 s single and 69.8/105.5 s for two concurrent guests.
- ADB targeting is exact per instance; non-emulator serials are rejected.
- The engine's gRPC controller is loopback-only and requires a per-launch token.
  The token is advertised in `%TEMP%\avd\running\pid_<qemu-pid>.ini`, not in
  `~/.emulator_console_auth_token`; the engine help text for `-grpc-use-token`
  is misleading for build 16428233.
- Named snapshots save in 5.7 s and restore in 5.5 s, restoring the exact
  foreground window. Budget ~1.6-1.7 GiB per named snapshot and a further
  ~2.5 GiB `default_boot` quickboot image per AVD that the engine writes during
  cold boot despite `-no-snapshot-save`.
- `-ramdisk` accepts a locally repacked, unsigned ramdisk; the engine appends
  its own bootconfig to the generated per-AVD `initrd`. This is the Magisk boot
  input. Dropping the second (first-stage) LZ4 frame hangs boot; both frames
  must be preserved.
- Agent access is denied by default and every attempt is journaled.

Deviations recorded during the spike:

1. Runtime state lives outside the repository and, on this host, on `D:` via
   `EMULATOR_WORKSPACE_HOME`. The C: volume could not hold two AVDs plus
   snapshots plus quickboot images.
2. `-gpu host` is the default after measurement; `swiftshader_indirect` remains
   selectable and is recorded separately.
3. Phase 0 uses an interim `wsctl engine-host` process for engine stdio. The
   Phase 1 supervisor replaces it; identity-checked reconciliation already
   exists.
4. The AVD home is workspace-local so the operator's existing AVDs are never
   touched.
5. The desktop app, sample APK, and frame-pacing measurements remain Phase 1
   work.

## Phase 1 outcomes and deviations

Verified in [docs/phase1-evidence.md](docs/phase1-evidence.md), acceptance run
`20261110-172411` (19 checks, 18 pass):

- A persistent supervisor owns one canonical root (exclusive lock file,
  final-path canonicalization), stores state in SQLite schema 1 with a
  transactional legacy-JSON import, and serves a versioned bounded pipe that
  verifies the peer's user SID and logon session.
- Guests survive supervisor death and are re-adopted only after PID + creation
  time and engine-advertised AVD/ports match.
- The WPF desktop embeds continuous real frames, maps input through the rendered
  image rectangle, installs/launches the sample APK, and shows bounded Logcat.
- Measured on this host: one guest delivered 3.66 FPS (p50 239 ms, p95 352 ms);
  two guests 1.07 FPS (p50 882 ms, p95 1641 ms). The guest scene renders at
  ~57 FPS, so the limiter is the emulator's PNG screenshot path plus host CPU,
  not WPF decode or the pipe. The 60 FPS / p95 100 ms targets are missed and the
  open gate is capture-path profiling.
- The sample APK's launch foreground check failed once because SystemUI raised an
  ANR overlay; the package installed, answered a tap, and logged `WS_SAMPLE`.
- The engine ignores `ImageFormat.width` for PNG; host-side decode uses
  `DecodePixelWidth=540` while input remains in guest pixels.
- WPF requires real globalization: the desktop project overrides the repo-wide
  `InvariantGlobalization=true`.
- The desktop viewport uses a clipped grid instead of a `ScrollViewer` around an
  auto-sized image; zoom beyond the viewport is clipped rather than pannable.
- Input latency (20 samples per GPU mode), low-space forcing, multi-touch, and
  software-renderer re-measurement remain open.

Recipe recording and replay are implemented on the supervisor registry: a
recording session captures supported operations for one guest, the export keeps
only runtime references, the runner validates the whole recipe before any
mutation, resolves and restores the baseline snapshot (reporting `restored` or
`cold_boot`) and replays every step through the same scope, approval and lease
checks. The two-guest workflow, the negatives and the interruption are
verified; the post-restore application foreground on a restored guest is still
open.

The post-restore transition was fixed: the fixture relaunch loop is no longer
dead code, the baseline verification is bounded and typed, the fixture package
is checked through the registry before launch, and a replay resets the recorded
original network profile first so an interrupted run cannot leave shaping behind.
The record -> export -> replay workflow is verified on WHPX in run
`20261111-143213` (36 of 37 checks; the remaining failure is a stopped instance
left behind after cleanup, now retried in the harness).

Ownership-aware import of a stopped external SDK AVD is implemented in the
supervisor: `instance.importAvd` previews the source (ownership, reparse
points, configuration fingerprint, writable disk hashes), refuses a source that
already belongs to the workspace or has no `config.ini`, and on apply produces
an independent workspace-owned copy that reports the source fingerprint and disk
hashes before and after. `tools/avd-import-acceptance.ps1` builds a real
external AVD outside the workspace and proves the source is unchanged.

The recipe lane is green on the dedicated launcher-visible baseline run
`20261111-184156` (39 of 39 checks). The fixture-foreground baseline variant
runs separately. Host hygiene is part of the harness now: leftover guests are
swept by their own AVD name, because seven orphaned guests once held about
16 GB and exhausted the pagefile.

The ownership-aware AVD import is verified end to end: run
`artifacts/phase3/import/20261111-201839` (9 checks, 0 failures) builds an
external SDK AVD outside the workspace, previews it, imports an independent
managed copy and proves the source configuration and writable disks are
unchanged.

The fixture-foreground baseline variant is green as well
(`artifacts/phase3/recipe/20261112-092743`, 39 of 39 checks). Both dedicated
baseline runs are fully green, so the recipe lane is complete. Seven product
defects were fixed to get there: the keyguard, the package-manager readiness,
the readiness contract, the SystemUI ANR dialog, the launcher that takes the
focus back, the repeated install of a package that is still present, and the
untyped emulator-console error that aborted a replay without a report. The
harness now recovers a guest that does not boot with a bounded number of
sequential starts and waits up to 40 minutes for a replay, because a restore
with a cold-boot fallback plus a relaunch legitimately takes minutes.

The release track is complete and produces a verified installer. `tools/package-release.ps1`
publishes the desktop, the supervisor and the CLI self-contained for win-x64,
harvests the payload into a **per-user WiX 5 MSI** and writes the release
record, the SBOM, the notices and the provenance (commit, SDK versions, build
commands, pins and the signing status). The candidate is `EmulatorWorkspace-0.1.0.msi`,
55 380 192 bytes, SHA-256 `8742367339f2b0b4daa65ba23ddf2e842ead707b52b9a344e3835d0d7f96fc5f`,
unsigned because no user-owned certificate was supplied.
`tools/installer-acceptance.ps1` passed **16 of 16 checks** in
`artifacts/phase3/install/20261112-194153`: the clean install, the installed
files, the sample APK, the first launch, a boot through the installed CLI on the
pinned image, the offline reuse, an upgrade that preserves the operator
profiles, the uninstall registration, the clean uninstall and the untouched
profiles.

`docs/compatibility-matrix.md` and `docs/security-review.md` record what was
verified and what is still open.

The historical benchmark is documented in [docs/benchmark.md](docs/benchmark.md).
The 39.65 s and 44.92 s cold-boot medians were both supervisor-managed. The
raw CLI control is still required. The historical warm restore median reused
cold-boot readiness and is invalid for the 8 s gate. The Logcat and PNG
round-trip measurements are diagnostics with separate endpoints from the WPF
composition proxy.

The Root Lab ADB daemon workflow is implemented and verified on the pinned
userdebug image (`artifacts/phase3/rootlab/20261111-222756`, 10 of 10 checks):
the targeted UID transition 2000 → 0 → 2000, the transport reattachment after
adbd restarts, and the state refresh after a boot and after a snapshot restore.
The WPF Root Lab surface and the Magisk profile remain. On Linux the pinned
Magisk v30.7 APK built and patched a copy of the SDK regression ramdisk. The
first run used the upstream full test environment, which included Shamiko and
test modules, and proved only shell UID 2000 `su`. A base-only rerun on a fresh
guest then passed two cold boots with no modules and Zygisk off. Shell UID 2000
and an app-level probe (UID 10149) both reached UID 0, and a stock cold boot
restored the guest marker and removed Magisk. The source-built AOSP image is still
blocked by builder capacity. Windows WHPX base-only install, two app-root
cold boots and stock recovery now pass. The supervisor also rejects corrupt
patches and recovers interrupted staging and boot timeouts.
[Windows evidence](docs/magisk-lab-windows-evidence.md),
[Linux evidence](docs/magisk-lab-linux-evidence.md)

## Phase 2 outcomes and blockers

- Protocol 1 envelopes gained an opaque session credential and idempotency key;
  typed errors cover unauthorized, forbidden scope, expired, revoked, wrong
  instance, stale observation, ambiguous selector, approval required and
  unknown outcome. In-progress mutations are reconciled as `unknown_outcome`.
- Pairing approval creates a fresh disposable AVD from a stopped template
  (never a running disk copy), keeps engine endpoints/tokens inside the
  supervisor, and supports revocation and takeover.
- MCP stdio tools map to the same operation registry as wsctl; stdout is
  protocol-only and the token comes from a file.
- FLAG_SECURE is detected from the window's symbolic `fl=... SECURE ...` flags
  (Android 15 does not print `mSecure=true` here). Screenshot/tree return a
  redacted `secure_window` result, selector input is denied, and probe errors
  fail closed. The emulator framebuffer path is never exposed while secure.
- Guest-only shaping uses `tc netem` on `wlan0`/`eth0` through `su 0` while the
  ADB daemon stays at UID 2000; a 2,500 ms delay produces `NETWORK TIMEOUT` and
  removal restores `WELCOME`. The emulator console delay/speed settings are
  recorded but do not shape virtio traffic on this image.
- The WPF Agent Sessions surface is implemented and probed; the Python MCP
  client negotiates `2024-11-05` and lists 19 tools.
- Active-session state is re-read immediately before dispatch and before input
  or mutation, so takeover/revocation cannot authorize a queued request.
- Verified live: MCP/CLI login/assert/logs/screenshot/snapshot save+restore,
  network timeout and recovery, secure redaction, cleanup with approval,
  unpaired/wrong-instance/stale/ambiguous/revoked denials, idempotent replay
  and conflict. Unit coverage adds expiry, event overflow/resync, restart
  reconciliation and active-session rechecks.
- Remaining verification gaps: two running managed guests plus an external ADB
  transport, and a live queued-input takeover race (implementation and unit test
  are in place; a scheduled race is not). Phase 1 desktop click-through and
  performance remain separately open.

## Initial compatibility matrix

Each verified run must record the host architecture and CPU, Windows build, engine and image revisions, guest API and ABI, GPU and driver, backend, graphics and snapshot support, root mode, and Magisk version where applicable. Record Intel, AMD, and software-rendering results separately when the tested configurations differ.

| Candidate | Windows host | Engine and image | Guest | Graphics and snapshots | Root workflow | Status |
| --- | --- | --- | --- | --- | --- | --- |
| First developer profile | Windows 11 26H2 26300.9818, i5-8265U, 31.8 GiB, UHD 620 | Emulator 37.2.12 (16428233); `system-images;android-35;default;x86_64` rev 2 | API 35, x86_64, userdebug | Host GPU verified; swiftshader verified; save 5.7 s, load 5.5 s | `adb root` uid 0 and `adb unroot` uid 2000 verified | Verified (Phase 0 + desktop MVP) |
| Second developer profile | Same host | Same engine and image, second AVD | API 35, x86_64 | Own ports, token, and data; concurrent boot verified | Same image | Verified concurrently; desktop viewport on this profile |
| Google Play profile | Windows 11 x64 with WHPX | Official Play API 36 rev 7 and API 35 16 KB rev 5 | API 35/36, x86_64 | Host GPU, APK and PNG on both. API 36 restored. API 35 used cold-boot fallback | Stock user builds, ADB UID 2000 | Measured in the compatibility matrix |
| Magisk Lab (Linux) | Debian 13 x86_64, 8 threads, 15.6 GiB RAM, KVM | Emulator 37.2.13.0 build 16740729; `system-images;android-35;default;x86_64` rev 2; Magisk v30.7 `e8a58776` | API 35, x86_64, userdebug | Software GPU; cold boot with `-ramdisk`; patched ramdisk hash `d5b47070…` | Base-only (no modules, Zygisk off): shell UID 2000 and app probe UID 10149 -> 0 on two cold boots; stock restore passed | Verified on Linux (SDK profile) |
| Magisk Lab (Windows) | Windows 11 x64 with WHPX | Emulator 37.2.12.0 build 16428233; same image and Magisk APK | API 35, x86_64 | `-ramdisk` substitution proven in Phase 0 | Two cold boots with shell/app root and stock recovery passed; failure-path verification in progress | Experimental SDK lab |

Track Windows on ARM, other host OSes, nonstandard GPUs, and ARM-only APKs as separate compatibility entries. Inspect native APK ABIs before deployment and surface incompatibility with the selected guest. Publish simulation and interoperability support per verified configuration.

## Benchmark plan

Declare the reference host, Windows build, CPU, GPU and driver, RAM, storage, WHPX status, and deterministic test workload before collecting results. Compare against the raw Android Emulator using the same binary, image, vCPU and RAM allocation, display, renderer, and workload. Use monotonic timestamps, retain raw samples, and collect at least 20 valid observations per latency and boot scenario for p50 and p95. Report cold boot and snapshot restore separately. Measure idle resources, app install, and live Logcat alongside the targets below. Report software fallback measurements separately.

| Metric | Initial target |
| --- | --- |
| Cold and warm boot | Median overhead within the larger of 10% or 1 second over the same-engine baseline |
| Warm restore | Median time to interactive readiness within 8 seconds from a compatible reference snapshot |
| Viewport | 60 FPS on a 60 FPS hardware-rendered scene. Report delivered FPS, p95 frame time, and drops |
| Local input | p95 dispatch-to-visible-response latency of at most 100 ms |
| Multi-instance | Two default guests on a documented 16 GB host with bounded memory and usable foreground interaction |
| Stability | 30 start/stop cycles and a four-hour two-guest soak. Track crashes, process and port leaks, handles, and memory growth |

Compare completed workflows with Android Studio agent integration, the raw Emulator CLI, and an existing mobile MCP server. Use measured results, reproducible steps, and documented limitations in release claims.

Measured Phase 1 stabilization (host GPU, RGB24 540x1200): one guest 32.6-34.5
delivered FPS (p50 24-26 ms, p95 45-46 ms), two booted guests 20.1 FPS (p50
47 ms, p95 86 ms), software GPU 17.2 FPS (p50 55 ms, p95 94 ms). Input latency
dispatch-to-captured-frame was p50 434 ms / p95 474 ms single guest, 695/1012 ms
two guests, 741/858 ms software. The 60 FPS target is still missed; the remaining
limiter is engine capture plus host CPU. Composition-proxy latency is not yet
validated.

The Phase 3 benchmark measured 20 cold boots per managed profile. The
matched raw CLI control remains open. The corrected harness measures restore
wall time, distinguishes clean-boot fallback, and checks an independent
guest-state marker. The verified warm-restore sample count and the viewport
presentation targets remain release gates. See [docs/benchmark.md](docs/benchmark.md).
## Continuation checkpoint

The Windows reference host ran the corrected two-guest benchmark under WHPX
with an isolated workspace root containing spaces and non-ASCII characters.
Both latency scenarios collected 20 fresh fixture markers, with both guests
removed through the supervisor. Packaging ran concurrently, so a quiet-host
WPF composition run remains the next presentation measurement. The exact
commands, raw run paths and current candidate hash are in [task.md](task.md).
The Magisk experiment has pinned sources and a documented build preflight. A
Linux builder completed the v30.7 build and SDK-ramdisk patch, proved base-only
shell and app-level `su` on two cold boots, and restored stock; the AOSP source image is still capacity
blocked. [Magisk Lab build inputs](docs/magisk-lab-build.md),
[Linux evidence](docs/magisk-lab-linux-evidence.md)

The 0.1.1 candidate from `8d40469` passed 21 installer checks in
`20261112-212707`. The original 0.1.0 MSI served as the predecessor for the
verified managed-profile upgrade. The corrected read-only simulation probe
ran through the installed CLI. Final readiness still requires the open
checks in [task.md](task.md), including disconnected reuse, signed
acceptance, presentation performance, and Magisk.

## Final verification in progress

The preserved 0.1.1 MSI and installer acceptance `20261112-212707` were
rechecked. The full two-guest soak `20261112-213629` passed 200 paired
probes over 14,411.36 seconds. The harness used the hash-verified candidate
payload from `8d40469` with the pinned WHPX engine and API 35 image.
Both guest generations were stopped and removed. ADB showed no attached
devices, and the scoped supervisor was then shut down.
[Benchmark evidence](docs/benchmark.md)

After cleanup, use the prepared candidate lanes for the quiet-host WPF,
matched raw CLI, verified restore, and runtime regression. The candidate
installer hash belongs to the preserved 0.1.1 payload. A diagnostic desktop
override has separate binary and source provenance. Disconnected reuse
requires an isolated Windows 11 x64 environment that can run the pinned
Android Emulator with WHPX. The operator approved enabling Sandbox and
Hyper-V management. The post-soak elevation prompt was canceled before
Windows servicing started. Feature installation requires an accepted
administrator prompt, with any required reboot handled separately.

The preserved desktop rendered frames in the quiet-host probe but its
startup callback did not dispatch trials. Register the callback before the
window is shown and repeat with separate desktop provenance. The raw
control reached the interactive endpoint in a smoke trial after correcting
child identity detection. The full comparison uses normalized AVD settings
and requires 20 valid observations in each arm. See [task.md](task.md).

The corrected desktop at `526fb72` collected 25 valid composition-proxy
trials against the preserved 0.1.1 runtime. It measured p95 840.85 ms and
22.01 delivered FPS. Both performance targets are open. The changed frame
reached the WPF composition callback within 3.42 ms p95, so further
measurement starts with controller input, capture, and delivery. All 20
snapshot trials on the preserved payload verified fresh focus and restored
state, with zero fallbacks. Median 9.19 s exceeded the 8 s target. The
matched cold-boot comparison passed with 20 observations per arm. The
managed median was 44.36 s against 41.41 s raw, within the allowed 4.14 s
overhead. The latest source includes the readiness fix and supervisor split
install path. Its Release build passed all 58 tests. The separate unsigned
0.1.2 MSI passed 22 installer checks, including packaged MCP and a real
upgrade preserving a managed profile from 0.1.1. The preserved 0.1.1
installer still has its original hash. The 0.1.2 runtime suite passed
seven of nine lanes and exposed a recipe retry bug and a Maestro launcher
ANR. The recipe fix and viewport changes are in the separate unsigned
0.1.3 build. Its 22 installer checks passed. The runtime rerun found
that launcher-baseline replay also needed to defer fixture verification
until the recorded launch. The source fix and reserved-port cleanup are
in 0.1.4. Later source work bounds app foreground waits after restore.
The exact 0.1.4 candidate passed both recipe baselines, 22 installer
checks and all nine runtime lanes across verified reruns. Its final
performance, stability and disconnected reuse checks remain open.

The packaged 0.1.2 viewport delivered 21.82 FPS and 789.56 ms p95 on
25 valid single-guest trials. The separately measured controller RGB stream
delivered 49.08 FPS and guest H.264 delivered 21.12 FPS. The next desktop
build coalesces frames before UI dispatch and excludes stale subscriptions.
The exact 0.1.4 payload delivered 23.09 FPS and 679.56 ms p95 with one
guest, and 15.49 FPS and 1,145.69 ms p95 with two. All 50 trials were
valid and the three test guests were removed. Both performance gates
remain open. The lifecycle rerun passed all 30 start/stop cycles with
verified port release and no engines remaining. The current candidate
verified 20 warm restores with 11.15 s median and one cold fallback.
The 8 s target remains open. Its matched cold-boot comparison completed
20 trials per arm, with 14.53 s median overhead against 3.54 s allowed.
The four-hour soak is now repeating on the exact 0.1.4 candidate.
[Benchmark evidence](docs/benchmark.md)

The packaged 0.1.4 first-run screen and the missing-image failure each
passed three checks and produced a PNG and automation tree. The failure
used a process-local SDK directory that omitted the image and reused the
installed tools. Both roots contained spaces, with no guest started.
Repeat these checks on the final packaged build.
The source now requires successful WHPX probe status, the exact emulator
build and image revision, and the required SDK executables before enabling
Create. A source-only viewport change also avoids one full RGB copy per
frame and relaxes the raw arrival threshold to accommodate 60 Hz jitter.
Release build and 64 tests passed. Re-measure WPF with one and two guests
after the soak and record the packaged source separately from 0.1.4.

The next package uses the rebuilt fixture APK with its instrumentation
runner. Verify the runner and invalid component rejection with
`tools/candidate-regression.ps1 -RequireInstrumentation`. The app-bundle
fixture build can explicitly use the local Android debug key with
`-SignBundleForTesting`. That signed test AAB and its generated APK set
built successfully. Run the scoped Mobile MCP comparison and the Android
Studio deployment with one disposable guest after the timed workloads.
Record the live desktop click-through separately.

Unsigned 0.1.5 was packaged from `a062459`, with its MSI and all 426
payload files verified against the release record and inventory. It
includes the setup, frame, controller, readiness and fixture changes.
The exact artifact and its pending checks are in [task.md](task.md).
The intermediate 0.1.4 soak was stopped at the user's direction after
64 successful paired probes over 4,793.44 seconds. Both owned guests
and the scoped supervisor were cleaned up. Keep the partial run and
use short diagnostics for the next changes.

Use short functional and timing checks to evaluate intermediate changes.
Schedule another full four-hour soak only after selecting the release
candidate and passing its shorter checks. Preserve the completed 0.1.1
run and the partial 0.1.4 run as evidence for their exact payloads.
The first 0.1.5 diagnostics completed with verified cleanup. Controller
setup accounted for 245–275 ms per WPF tap. Reusing the channel for
the verified engine generation reduced setup to 0.08–0.34 ms after
the first tap. Five diagnostic trials measured a 211.72 ms median
and 27.17 FPS. The same channel behavior passed checks before and after
restart, including both rejected token cases. The frame timing diagnostic
measured 3.59–11.82 ms for the changed-frame write and 21.92–47.87 ms
for desktop decode and dispatch. The paired draw check measured 4–22 ms
from touch release to draw in the guest and 183–232 ms from host
dispatch to first observed changed controller frame on the four reused
connections. With the same binaries and fixture, SwiftShader delivered
15.91 FPS and took 277–362 ms for the same interval. Host GPU remains
the default. Half-size capture reached 37.28 FPS and 166.12 ms median
composition in five trials. The guest stayed at 1080 × 2400, and
the app logged one-pixel coordinate differences on each axis.
Direct controller capture at that size reached 54–58 FPS in RGB and
about 58 FPS in RGBA. The bounded handoff measured 36.68 FPS against
37.28 FPS at half size and was reverted. Keep the recorded diagnostic.
Three verified warm restores had an 8.90 s median after narrowing the
readiness query and combining independent ADB verification. One
attempt fell back to a 139.45 s cold boot. Measure readiness outcomes
per attempt next. Full acceptance counts and targets remain for the
selected release build.

## Magisk review follow-up (2026-11-14)

Work is isolated on `codex/magisk-windows-lab` from `485b093`. Grok review
confirmed temporary provisioning ADB root is part of the lab adapter. Its
owner-approved exception is now explicit in agents.md, restricted to the
opted-in activating instance generation, and root/unroot evidence is durable.
The strict probe parser fails closed on incomplete or malformed output.
Supervisor discovery prefers Release, with fail-closed explicit paths.
Release build and 89 tests pass. Full final-binary hardware transaction `transaction-20261114-142606-818906`
passed install, two parsed app-root cold boots, journaled unroot, stock
recovery/app denial and cleanup. Source and payload hashes stayed unchanged;
results and hashes are committed with the Windows evidence record.

## Second Magisk review (2026-11-14)

- M1: replaced stock shell-denial inference with explicit Magisk absence,
  retained independent adbd/app proof, and corrected historical evidence.
- M2: decided active Magisk labs are observation-only for agents, enforced
  at outer and nested dispatch. Operator/host ADB shell root reach is documented.
- L1-L6: patch read lease and enrollment rehash; both provisioning/unroot errors
  retained; approval claim narrowed to Windows session; consent before prepare;
  desktop deadline 60 minutes; reviewed Git blob IDs added without overwriting
  historical source-hash provenance. Recovered generations remain single-use.
- Baseline: 89/89 tests. Release build passes with 22 existing warnings; the
  expanded suite passed 114/114 and the fresh hardware transaction passed 8/8.
- New tests exercise actual dispatcher authorization with simulated sessions,
  verified/recovered guards, exact shell markers, corrupt enrolled patch and
  restore allowlist/failed-overlay preservation. No guest is launched by tests.
- Missing fake-ADB cancellation/root-timeout tests and a fresh WPF consent-cancel
  click-through remain explicit verification gaps; existing finally uses its
  independent cleanup token. No host feature, signing or release claim changed.
- Real transaction command: `python tools/magisk-transaction-acceptance.py
  --root D:/WS-Magisk-Review2-20261114 --allow-provisioning-adb-root --mode normal`.
  Evidence: `artifacts/magisk-lab/transaction-20261114-150136-508009`.

Verification complete: `transaction-20261114-150136-508009` passed install,
two app-root cold boots, stock Magisk-absence/app-denial proof and cleanup.
Stock AOSP `su 0 id` **does succeed** for shell UID 2000 (result UID 0,
context `u:r:su:s0`); stock is not shell-root denial. A separate Windows
write-open on the leased patch was rejected. Tested payload hashes stayed
unchanged. Evidence: `docs/evidence/magisk-windows-review2-20261114.json`.
Build: `dotnet build -c Release --no-restore`; tests: `dotnet test
 tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj -c Release --no-restore`.
Build/test logs: `artifacts/magisk-review2-{build,tests}.log`. Python harnesses
compile and `git diff --check` passes. No merge or publication performed.

## Third Magisk review follow-up (2026-11-14)

N1: Install now uses LeaseVerified: open FileShare.Read, hash that same handle,
rewind it, and retain it through install/rollback. Hash failure disposes the
handle. Tests reject replacement bytes, verify rewind/disposal, and on Windows
verify write, rename and delete exclusion. The previous implementation also
re-hashed patched.img in the pins loop after acquiring the lease; that check
already preceded every boot. The new helper makes the handle/hash relationship
explicit rather than depending on that second path-based check.

N2 accepted/documented: later operator starts of an already verified lab still
hash by path without retaining a lease. They do not request provisioning ADB
root. Same-user host processes are not isolated by this lab; that remaining
operator-start TOCTOU is not claimed closed.

N3: expanded real-dispatcher/simulated-session and direct guard tests across
all 12 active phases. Recipe replay targeting the lab is denied at the outer
entry; the nested internal dispatch entry is separately exercised with that
same agent session. This is simulated authorization evidence, not guest replay.
N4: renamed review2 reviewedCommit to baseCommit. Preserve historical
magisk_absent exit markers: 0 means the absence-test succeeded, 1 means it
failed. No historical proof output or source blob ID was changed.

Recommended fake-ADB cancellation/root-timeout/double-failure tests and WPF
Cancel click-through remain open, as does the boot-completed substring nit.
Power-loss, source-built AOSP capacity, signing and redistribution remain open.
The unrelated readiness patch stays excluded. No new hardware run is claimed;
review2's passing 8/8 applies to its recorded binaries only.

Verification: `dotnet build -c Release --no-restore` passed with 22 existing
warnings; `dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj
-c Release --no-restore` passed 388/388 on Windows. Logs:
`artifacts/magisk-review3-build.log` and `artifacts/magisk-review3-tests.log`.
`git diff --check` passed. These are build/unit checks, not a fresh WHPX run.

## Fourth Magisk review follow-up (2026-11-14)

Grok independently passed the Linux Release build and 388 tests at 8eace58,
with no merge blockers. The original N1 swapped-boot finding was withdrawn:
the prior pins loop already re-hashed the patch under the lease before boot.

F1: split rewind/release coverage from Windows write/delete/rename exclusion.
WindowsFact reports an explicit skip on non-Windows runners; platform-specific
assertions no longer silently disappear inside a passing portable test.
F2: the 60 dispatcher magisk.* phase cases prove end-to-end scope denial, not
phase-guard reach: AuthorizeAgent rejects them first. The direct Guard theory
covers all 144 phase/operation pairs, including the magisk.* early-return fence.
F3: removed the File.Exists precheck from LeaseVerified. IOException and
UnauthorizedAccessException during open now return invalid_request, including
missing/deleted and sharing-denied files. Hash/seek failures still dispose the
opened handle. Tests cover missing files and Windows sharing denial.

No new WHPX or WPF run is claimed. Fake-ADB cleanup tests, WPF Cancel,
boot-completed exact markers, later operator-start lease and replay-loop
session-forwarding regression coverage remain optional follow-ups. Power-loss,
source-built AOSP, signing and redistribution gates remain open. Readiness
changes remain excluded. Merge approval is not release-readiness approval.

Verification: Release build passed with 22 existing warnings; Windows tests
passed 391/391, zero skipped. The two WindowsFact cases executed on this host.
Linux skip reporting is implemented but was not run here; expected Linux
result is 389 passed / 2 skipped, subject to independent verification.
Commands: `dotnet build -c Release --no-restore` and
`dotnet test -c Release --no-build --no-restore`. Logs:
`artifacts/magisk-review4-build.log`, `artifacts/magisk-review4-tests.log`.
`git diff --check` passed. No fresh hardware evidence is attached to this change.


## WSL and provisioning cleanup verification (2026-11-14)

Extracted the existing lab-only root/provision/finally-unroot lifetime into an
internal helper without changing enrollment, targeting, or authorization.
Six simulated-ADB tests use real subprocesses to cover cancellation during
root/provision, root timeout, double failure, enrollment rejection and initial
journal failure. This is simulated failure evidence, not a power-loss test.

Windows Release: 397 passed, zero skipped. WSL Ubuntu 24.04.5 / .NET 10.0.404:
395 passed, two Windows-only skips. Both builds: 22 existing warnings, no errors.
The local 7f08299 archive baseline had one CRLF-dependent test fixture failure;
normalizing only the fixture fixes it. See [WSL setup and baseline](docs/wsl-development.md).

Fresh real WHPX acceptance passed 8/8 on the tested binaries: two cold boots,
journaled unroot to UID 2000, app-level Magisk proof, stock recovery with app
root denial, and guest deletion. WPF consent Cancel was clicked after selecting
the three pinned files on a separate stopped guest; status remained
not_configured, state Defined, enginePid null, and no Magisk transaction or
operation journal was created. That guest was then deleted normally.

Reproduce hardware acceptance with:
`python tools/magisk-transaction-acceptance.py --root D:/WS-Magisk-CleanupVerification-NEW --allow-provisioning-adb-root --mode normal`.
The root must be fresh and the documented pinned local artifacts must exist.
Evidence: [binary hashes, source identities and results](docs/evidence/magisk-cleanup-verification-20261114.json).
Raw local run: `artifacts/magisk-lab/transaction-20261114-163803-229961`.
WPF local evidence: `artifacts/wpf-cancel-{consent,after}.txt` and
`artifacts/wpf-cancel-{status,instances}.json`.

Remaining optional work: exact boot-completed marker, verified-boot lease on
later operator starts, replay-loop session-forwarding test, and narrower
IOException classification. Power-loss, source-built AOSP, signing and
redistribution remain release gates. Readiness changes remain excluded.

Cleanup follow-up: 92 old disposable D: roots were removed through the normal
supervisor instance.delete path after SHA256-verified evidence archiving.
An old stopped root using port 5554 initially timed out while the independent
hardware acceptance guest held that port. No unrelated process was killed;
retry succeeded after acceptance completed. Cross-root reserved-port contention
is a cleanup limitation to investigate separately. Recovery roots, offline kits,
tools, current release/installer state and unrelated personal data are retained.
The private archive and deletion manifests are under
`D:\Emulator-Cleanup-Archive-20261114`; they are not published to Git.

Cleanup complete: 92 roots removed; 17 loose files archived. D: free space
measured 448.91 GiB at completion. Private archive README and
completed.json record scope and retained data. All 92 targets were verified absent.

## D: storage consolidation (2026-11-14)

Moved all 40 owner-listed retained folders under `D:\Emulator-Lab` using
same-volume directory renames. Verified destinations and old-path absence;
no running processes referenced the source roots. Nested SDK junctions remain
intact. `D:\Emulator-Lab\relocation.json` maps all old/new paths. The cleanup
archive is now `D:\Emulator-Lab\Emulator-Cleanup-Archive-20261114`.
Historical absolute paths in evidence and persisted instance metadata were
preserved. Before reusing an old root, migrate and validate its paths or restore
that stopped directory to its original location. No runtime revalidation is
claimed for relocated roots. This is storage organization only; no code change.

## Grok follow-up on e77753f (2026-11-14)

Grok reported no blockers and independently passed the Linux suite three times.
Added in-process regression cases for journal failure on writes 2 and 3 (after
root and immediately before unroot), plus a successful unroot command returning
UID 0. These supplement the six real-subprocess fake-ADB cases; production
cleanup code is unchanged. Verification results will be recorded separately.

The earlier hardware evidence lists selected source blobs, not a complete
build-time tree identity. That limitation cannot be repaired retrospectively.
A new build will record its committed source tree, pre/post tracked status and
payload hashes. This new build provenance does not extend the earlier hardware
or WPF acceptance to new binaries. Existing release gates remain open.


Verification completed: focused cleanup tests 9/9; clean isolated Windows
Release build/test 400 passed, zero skipped; separate Linux checkout .NET
10.0.404 build/test 398 passed, two Windows-only skips. Both builds had 22
existing warnings and zero errors. Source commit: 81e98d2; full tree:
0792e7c24d60a04b95ba5e4d08c466b76c32c825. Both checkouts had empty porcelain
status before and after verification. No new hardware/WPF claim.
See [build provenance](docs/build-provenance.md) and
[evidence](docs/evidence/magisk-review-build-20261114.json).
Logs: artifacts/grok-clean-{build,tests}.log and
artifacts/grok-linux-{build,tests}.log. Production code is unchanged.
