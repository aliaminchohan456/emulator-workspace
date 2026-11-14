# Interaction and visual specification

Status: proposed native design. The browser prototype illustrates composition
and selected flows; it is not an implementation of the supervisor contract.

## 1. Shell and information architecture

Studio / Devices / Captures / Recipes are primary destinations. Agent access
and Settings are persistent utility destinations. Root Lab is device-scoped,
reachable from details and the palette, never a global enable-root switch.

| Region | Native target | Behavior |
|---|---|---|
| Title bar | 40–48 DIP, native caption controls | App/workspace identity, command search; retain drag, resize, Snap, system menu, minimize/maximize and close. Closing a view does not silently stop a guest. |
| Navigation | 200–216 DIP expanded | Stable active indicator; recent devices below main destinations; no live thumbnails in every row. |
| Page header | 80–100 DIP | Device name, text state, API/ABI, one lifecycle primary action. Long names trim with full tooltip. |
| Tool rail | 44–48 DIP | Screenshot, install, rotate; zoom/fit/focus; overflow. Android Back/Home/Recents at bottom. 36 DIP minimum targets, 44 for touch-friendly mode. |
| Details | 240–280 DIP, collapsible | Device / Capture / Diagnostics sections; Root Lab entry. Copy serials, ports and diagnostic identifiers explicitly. |
| Guest stage | Remaining area, opaque | Aspect-preserving letterbox. Fit/1:1/zoom are view operations; guest resolution and window size remain independent. |
| Logcat | Collapsed initially, resizable 160–360 DIP | Search, tag, level, pause, clear/export; sticky controls; virtualized bounded rows. |
| Status | 28–32 DIP | Supervisor state, current root, operation summary. No secrets. Full path available through deliberate copy. |

At >=1280 DIP allow all panes. At 1050–1279 reduce optional details width;
below ~1050 close details into a keyboard-accessible dialog/flyout. At the
existing 980 × 620 minimum, keep lifecycle and error actions visible; no
wrapping toolbar. A 760 px browser fallback exists for sharing the prototype,
not a commitment to ship a mobile WPF app. At 200% Windows scaling, dimensions
are DIPs; do not mistake physical pixels for available logical width.

Focus view collapses navigation/details/logs while preserving device identity,
Stop, active recording/agent indicators, recovery warnings and an obvious exit.
Fullscreen remains separate. Restore pane preferences when leaving focus.

## 2. Visual tokens and components

`tokens.json` defines primitive → semantic → component values. Map semantic
brushes to DynamicResource; do not hardcode colors in control templates.
The prototype uses CSS aliases; native resource conversion is future work.

- Typeface: Segoe UI Variable Text, falling back to Segoe UI. Cascadia Mono or
  Consolas for identifiers/logs. No remote font dependency.
- Type ramp: title 28/36, page 24/32, section 16/24, body 14/20, caption 12/16.
  Prototype uses denser 10–12 px supporting metadata for screen composition;
  promote operational text to >=12 DIP in native implementation and validate
  text scaling. Do not shrink text to make fixed layouts fit.
- Spacing: 4, 8, 12, 16, 20, 24, 32 DIP. Align labels and controls to the same
  baseline; 1 DIP dividers; use layout rounding on fractional DPI.
- Radius: controls 6, panels 10, dialogs 12. Pills only for concise states.
- Icons: consistent 16/20/24 sizes, 1.5–1.75 stroke. Ship owned vectors or a
  pinned, licensed icon set; do not assume two font families share glyph codes.
- Shadows: only dialog/flyout elevation, never per-row blur or viewport effects.

| Component | Required details |
|---|---|
| Primary action | One per local decision, strong fill, 36–40 DIP height; busy retains label/width; disabled shows why. |
| Secondary action | Surface + border; hover surface change; pressed feedback without layout shift. |
| Icon button | Name + tooltip + shortcut when implemented; visible focus; equivalent palette item. |
| Device row | Name, concise state, optional API; selection distinct from hover; active guest does not imply selected guest. |
| Device card | Restrained optional thumbnail, image/API/ABI, state, inline lifecycle and overflow. Default illustration until capture is allowed; never leak FLAG_SECURE content. |
| Text input | Label remains visible; focus border; inline validation; error linked to control; retain value on error. |
| Select/check/radio | Theme-complete popups and focus states; disabled reason; no default Windows-white control in dark dialogs. |
| Menu | Group related commands; destructive action separated; keyboard arrows and Escape; target device named in context. |
| Tooltip | Approximately 400 ms pointer delay; keyboard focus accessible; never the sole location of critical consequences. |
| Toast | Success/info only, ~5 s with pause on hover/focus; optional Open/Copy; accessible live announcement; no forced focus. |
| Error infobar | Persistent until resolved/dismissed; plain cause, next action, Copy details; retry only if safe/idempotent. |
| Confirmation | Target + change + consequence + recovery; Cancel initially safe; full paths selectable; no affirmative default for privileged work. |
| Table/log | Sticky headers, keyboard row access, visible selection, bounded virtualization; raw content is untrusted. |

## 3. Lifecycle and failure states

The view reflects supervisor state. Never convert a timer or process existence
alone into readiness. Where an event does not exist yet, show an honest
indeterminate state rather than inventing a phase percentage.

| State | View | Allowed next step |
|---|---|---|
| No device | One empty-state illustration, concise explanation | Create/import; no dead Android controls |
| Setup missing | Host checklist: SDK/image, acceleration, capacity | Inspect issue; explicit consent for host change/reboot or SDK terms |
| Defined/stopped | Calm stage, last known profile | Start; edit supported stopped-only configuration |
| Starting | Engine / Android / interactive steps when evidenced | Other devices/pages remain usable; cancellation only if supervisor supports it |
| Running, no frame yet | Connected text with frame wait, elapsed duration | Diagnostics; no stale-frame input |
| Ready | Live frame; exact selected-device context | Capability-checked input, install, capture |
| Stopping | Inline pending state | Prevent duplicate lifecycle commands; retain operation status |
| Disconnect/stale frame | Last frame visibly stale and timestamped, input off | Reconnect/reconcile, inspect diagnostics |
| Capture restricted | Neutral redacted stage | Explain guest restriction; no screenshot, thumbnail or export bypass |
| Engine crash | Persistent failure with safe next action | Inspect/reconcile; cold boot only when supported |
| Lab recovery required | Amber context, precise recovery phase | Recovery flow; no agent mutations or ordinary start shortcut bypass |
| Permission denied | Target/capability/scopes explanation | Request appropriate pairing/approval, never silently escalate |
| Low disk | Exact required/available space if known | Choose storage/cleanup; preserve verified recovery artifacts |

Boot progress and long copy/hash work update without covering other devices.
Cancel wording distinguishes cancelling a request from stopping a guest.
Unknown progress is indeterminate. Re-entrancy prevention is per operation and
instance, not a global UI freeze.

## 4. Core user journeys

**Create:** name → pinned installed image/profile → supported resource/display
choices → summary with disk requirement → Create → boot status. Unique-name
validation; duplicate and disk errors remain inline. Image downloads and SDK
terms have their own flow. Presets are recommendations only after host checks.
Clone is a separate flow with source state, data implications and destination.

**Install:** drag highlights a bounded drop zone and names the selected device.
Do not log every drag event. Validate file type and backend capability; review
target before submission. Show transfer/install/launch as distinct operations.
Multi-file batches need a clear order, per-file result and cancellation policy.
Do not conflate `.apk`, `.apks` and bundle build/signing flows.

**Capture:** one action → success toast with Open/Copy → Captures page. Show
device, time, dimensions, protection/redaction and file type. Display permissions
apply to thumbnails and cached previews. Export remains explicit and bounded.
Screen recording is deferred until encoder, permission, storage and cancellation
contracts exist; an attractive record button must not imply it is ready.

**Recovery points:** list known compatible points, provenance and protected
baseline; show snapshot compatibility and data consequences before restore.
Pending/incompatible cannot look successful. No deleting the last verified
recovery point via a generic card overflow command.

**Recipes:** author/review named steps and semantic targets → select exact
device → preflight permissions → run → inspect evidence. Recording shows an
always-visible indicator and Stop. Capabilities must be inventoried before
wiring a recorder UI; old plan log entries are not a reliable capability list.
Parallel/synchronized execution and game-keymapping are later scope.

**Settings:** General/appearance, Viewport & display, Performance, Shortcuts,
Agent defaults, Diagnostics, About. Global and per-device settings visibly
separated. Apply shows pending changes and restart requirement. Discard is
explicit. Guest resolution must never silently change to fit a resized window.

**Agent access:** off by default → caller/device/scopes/expiry review → pairing.
Active session strip names caller and selected guest, countdown expiry and
Take over/Revoke. Those controls remain visible in focus view. Magisk labs
offer observation only. Clipboard/paths/logs are redacted before export.

**Root Lab:** distinct ADB daemon UID and Magisk/app-root states. Eligibility
checks precede artifact selection. Consent names exact guest, pinned files and
hashes, temporary ADB root, UID 2000 requirement, host-shell access implications,
Zygisk/modules policy, backup status and snapshot fence. Cancel creates no
transaction/journal. After approval, show actual phases and bound identity.
Approval expiry or changed artifact invalidates the pending UI decision.
Recovery names stock input and possible data rollback; success requires proofs.
The prototype consent is a composition only and cannot grant access.

## 5. Motion and feedback

Hover 100–120 ms; press 70–90 ms; drawer/flyout 160–200 ms ease-out; toast
140 ms fade/8 px translation. These are design targets, not measured timings.
Animate opacity and transforms of shell surfaces. No bouncing lifecycle button,
looping decorative motion, animated blur or layout animation around guest input.
Stop immediately when superseded; never queue repeated transitions.
Respect Windows animation preference and user reduced-motion setting. High
Contrast uses semantic system colors, solid surfaces and visible boundaries.

## 6. Keyboard, accessibility and density

Keep existing Ctrl+K and F11/Escape. Propose Ctrl+Shift+S for screenshot only
after conflict review. Do not steal common guest text shortcuts. Focus is
explicitly either shell or guest. Tooltips show implemented shortcuts only.
Palette filters immediately, Enter activates, Escape restores prior focus.
Dialogs trap focus, close on Escape when safe, and restore to a surviving
invoker. Re-rendered content must receive an intentional focus destination.

Every icon has a UIA name; toggles expose state; progress/errors use appropriate
live announcements. Color is never the only state signal. Minimum contrast
targets: 4.5:1 ordinary text, 3:1 large text and essential control/focus
boundaries. Decorative separators may be subtler. Measure every theme and
system accent separately. Test Narrator, tab-only use, long names, empty
inventory, 200% text, High Contrast and mixed monitor DPI. Native acceptance
is mandatory; browser semantic markup is not a WPF UIA test.

## 7. WPF architecture and performance

Keep WPF/MVVM and existing supervisor/client boundaries. Initial work changes
`Themes/*`, `MainWindow.xaml`, presentation view models and views. Preserve
existing commands and input geometry before splitting `MainViewModel` into
shell, device list, viewport, details and lab presentation responsibilities.
Do not combine that refactor with changes to engine, root or agent policy.

Use native chrome/DWM dark title treatment first. Spike custom WindowChrome,
built-in Fluent or a pinned third-party package separately; verify SDK support,
High Contrast, UIA, popups, DPI and Snap. No package was added by this study.
Keep guest Image opaque, no DropShadowEffect/BlurEffect on its ancestor tree,
no continuous acrylic sampling near frames. Virtualize device/log lists, bound
thumbnails, coalesce telemetry and keep one selected-frame subscription.

Default Studio must not start background capture for every device. Thumbnail
refresh is explicit or tightly budgeted and permission-aware. Pause optional
render work when minimized; do not interrupt guest lifecycle. Benchmark shell
changes against the same binary/image/GPU/capture workload, with warm/cold runs
and raw data. Proposed gate: no >5% sustained FPS loss or >10 ms p95 input
latency increase across repeated comparable runs; investigate noise before
declaring pass. These are review thresholds, not performance already achieved.
