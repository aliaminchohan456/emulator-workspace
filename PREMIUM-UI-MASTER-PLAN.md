# Premium Emulator Workspace — master implementation plan

Owner: Ali · 14 November 2026 · Native implementation pending.

## 1. Outcome and source of truth

Build a premium Windows Android emulator workspace with the visual discipline
of BlueStacks/LDPlayer and the clarity of a professional developer/QA tool.
Use the completed **Graphite Studio** design direction. The product name stays
Emulator Workspace. Keep WPF, .NET 10, MVVM and the existing supervisor.

Read in this order:

1. `agents.md`, `plan.md`, `task.md` — repository obligations and actual status.
2. This master plan — scope, priorities and implementation contract.
3. `docs/design/premium-workspace/specification.md` — detailed component,
   screen, state, motion, accessibility and runtime interaction requirements.
4. `docs/design/premium-workspace/implementation-plan.md` — per-slice gates.
5. `docs/design/premium-workspace/tokens.json` — proposed theme values.
6. `docs/design/premium-workspace/index.html`, `studio.css`, `studio.js` —
   interactive visual reference. Open it, do not merely read its source.
7. `research.md` and `verification.md` in the same directory — evidence/limits.

Reference screenshots in that directory: `studio-dark.jpg`,
`devices-light.jpg`, `recovery-consent.jpg`. The browser prototype is simulated;
its guest wallpaper, clock, resource presets and inventory are not native
features or measurements. Do not implement an Android launcher replacement.
Display actual guest frames in the production viewport.

## 2. Current baseline

Reviewed code at 24cc54d. Production WPF remains the original shell. Latest
verification: Windows 400 passed; Linux 398 passed + 2 Windows-only skips;
22 existing warnings, zero build errors. Cleanup tests 9/9. Historical hardware
and WPF evidence must stay tied to its recorded binaries. A fresh UI build
requires its own native validation and provenance.

Current desktop files:

| File | Responsibility / allowed first work |
|---|---|
| `src/EmulatorWorkspace.Desktop/MainWindow.xaml` | Recompose shell, panels, command rail, state presentation |
| `MainWindow.xaml.cs` | Preserve viewport mapping, drop, window, fullscreen and keyboard behavior; change layout glue carefully |
| `ViewModels/MainViewModel.cs` | Reuse existing commands; separate presentation responsibilities incrementally |
| `Themes/Common.xaml` | Shared typography, focus, button/menu/input/list/dialog templates |
| `Themes/Dark.xaml`, `Light.xaml`, `HighContrast.xaml` | Semantic brushes and complete state coverage |
| `Services/ThemeManager.cs` | Preserve High Contrast override and existing theme behavior |
| `CommandPaletteWindow.xaml` / `.xaml.cs` | Coherent search, shortcuts, disabled reasons, keyboard focus |
| `AgentSessionsWindow.xaml` / `.xaml.cs` | Coherent session presentation; preserve pairing/revocation/takeover |
| `Views/Converters.cs` | Presentation mappings only; no authorization decisions |

All relative rows above are under the Desktop directory. Do not rewrite engine,
supervisor, controller, ADB or Magisk code to make a visual design easier.

## 3. Exact design direction

- Dark shell: base #111315, navigation #151719, panel #1B1E21, raised #23272B,
  opaque viewport #0C0E10, stroke #353C42. Text #F0F2F4 / #A4AFB8 / #8D99A3.
- Accent #B3D9FA with text #142A3B. Success #8ED8AF, warning #EFC58A,
  error #FFA6A6. State always includes words/icons, not color alone.
- Light shell: base #F6F7F8, navigation #EEF0F2, panels white, text #1C2933,
  secondary #52616E; accent #235E89 with white text. Use the same layout.
- High Contrast: system brushes override every custom accent/surface.
- Fonts: Segoe UI Variable Text → Segoe UI fallback; Cascadia Mono → Consolas
  for identifiers/logs. Title 28/36, page 24/32, section 16/24, body 14/20,
  caption 12/16 DIP. Native operational text must not inherit tiny prototype
  metadata sizes blindly.
- Spacing: 4/8/12/16/20/24/32 DIP. Radius: controls 6, panels 10, dialogs 12.
  One-pixel structural borders. Shadows only for floating UI.
- Owned consistent vector icons, 16–20 DIP, 36 DIP hit targets; 44 in a
  touch-friendly density. No emoji as production control icons.
- Hover 120 ms, press 80 ms, drawer 180 ms, toast 140 ms, ease-out.
  Reduced motion disables these. No looping decorative animation.
- No ads, store banners, decorative performance charts, neon borders, blur on
  guest frames, giant hero marketing copy or gratuitous cards.

## 4. Screen composition

**Studio:** compact left navigation and recent-device rows; selected device
identity and one Start/Stop action; large aspect-preserving opaque guest stage;
short right tool rail; optional details; collapsed Logcat; quiet status bar.
Do not place 19 text buttons in a wrapping header. Correct the current `--'`
zoom-out label with a minus icon and accessible name.

**Device library:** searchable/sortable cards with name, API/ABI/profile,
state, lifecycle action and overflow. Rich cards live here, not in the narrow
Studio rail. Thumbnails are optional, bounded and capture-permission-aware.
No background stream for every device. Long names trim with a full tooltip.

**Details:** Device/Capture/Diagnostics sections with copyable fields, plus
Root Lab entry. Collapse before squeezing the viewport. At <=1050 DIP use a
dialog/flyout. Do not turn it back into one long unstructured mono-text scroll.

**Captures:** show device/time/dimensions/type and redaction state; Open/Copy
after success. Honor FLAG_SECURE for thumbnails, saved previews and exports.

**Recipes:** review steps, selected device and permissions before execution.
Recording and replay controls require actual supported backend capabilities.
Never imply video recording or synchronized input exists because a mock has
an icon. Separate advanced feature work from styling.

**Settings:** appearance, display/viewport, performance, shortcuts, agents,
diagnostics/about. Clearly separate global and per-device changes. Show restart
requirements; preserve pending values on validation failure. Window size,
viewport zoom and guest display resolution are three different settings.

**Agent access:** off by default. Pairing names caller/device/scopes/expiry.
Active sessions show identity, expiry and Take over/Revoke even in focus mode.
Active Magisk labs permit observation only.

**Root Lab:** separate ADB UID from app-level Magisk state. Privileged context
is amber; destructive/failure is red. Consent must show exact device, artifacts,
hashes, temporary ADB-root wording, UID 2000 restoration, host-shell root access,
backup/recovery implications, Zygisk off/modules absent and snapshot fence.
Cancel must create no transaction/journal. UI consent never replaces supervisor
authorization. Recovery must remain usable when the guest cannot boot.

## 5. Small details that determine quality

- Every button: normal, hover, pressed, keyboard focus, disabled reason, busy.
- Every input: label, focus, validation message, retained value, accessible name.
- Every menu: grouping, selected device context, keyboard navigation, Escape.
- Every dialog: deliberate initial focus, safe Cancel, focus trap and return.
- Every long operation: real phase/indeterminate progress, duplicate prevention,
  clear cancellation semantics, no freezing unrelated devices.
- Every error: useful cause, exact affected device, safe next action, Copy details
  with redaction. Persistent failures must not vanish in a short-lived toast.
- Tooltips include implemented shortcuts; palette uses the same command source.
- Guest focus must not lose ordinary typing shortcuts to shell hotkeys.
- Frame waits and stale captures must not look ready; block stale-frame input.
- APK drag target names its device; avoid logging every DragOver event.
- No fake FPS, latency, CPU or completion percentages. Display unknown honestly.
- Preserve guest aspect ratio through resize, orientation, zoom and DPI changes.
- Keep Android Back/Home/Recents reachable at small sizes; overflow optional
  zoom/focus tools before shrinking targets or cutting off navigation.
- Scrollbars, ComboBox popups, CheckBox, DataGrid and ContextMenu must match
  dark/light/HC; no accidental default white controls in a dark surface.
- Native chrome must retain Snap, resize, Alt+Space, minimize/maximize/close.
  Apply dark native chrome first; custom WindowChrome is a later verified spike.

## 6. Roadmap: implement one runnable slice at a time

### UX-00 — evidence and capability inventory
Read instructions, preserve user changes, record git status, build/run baseline.
List each proposed action's actual registry operation, arguments, scope,
approval/cancellation semantics, supported states and event source. Use current
code, not stale historical task checkboxes. Record exact source tree and payload
hashes in a fresh isolated checkout; see docs/build-provenance.md.

### UX-01 — first implementation task
Add semantic resources/type styles; complete normal/hover/pressed/disabled/focus
states; fix minus glyph; add consistent accessible icons to working actions;
apply native dark title-bar treatment with fallback. Preserve command bindings,
viewport hierarchy/geometry, all theme modes and normal desktop operations.
No new package, root changes, engine changes or large view-model rewrite.
Verify and commit this slice before moving the shell.

### UX-02 — shell
Recompose title/commands, compact navigation, tool rail, collapsible details,
Logcat and focus view. Reuse original input handlers. Test portrait/landscape,
Fit/1:1/zoom, pop-out/fullscreen, selected-device changes and DPI mapping.

### UX-03 — honest state flows
Inline boot readiness, frame wait, stopped/failed/recovery/restricted states,
typed error infobar, APK drag affordance and capture toast. Drive state from
existing supervisor events. Missing backend support is a stated gap, not a
timer that turns green. Preserve independent work during long operations.

### UX-04 — library
Search/sort/cards, create and clone flows, compatibility-aware recovery points.
Validate unique names, image availability, resources, disk and source state.
Batch actions and parallel operations remain separate until targeting is proven.

### UX-05 — privileged and agent surfaces
In-app Root Lab consent and integrated sessions with persistent takeover.
Re-run cancel/no-journal, approval binding, expiry, artifact change, UID proofs,
agent fence and exact target checks through the normal desktop path.

### UX-06 — supporting tools
Settings, shortcut conflict editor, diagnostic export/redaction, Captures and
recipe review. Expose only supported operations. Implement advanced recording
or input features under their own plans if separately authorized/needed.

### UX-07 — final quality
Narrator, keyboard, HC, reduced motion, 200% text, mixed-DPI, empty/large data,
performance comparison and real screenshot review. Optional Fluent/WPF-UI/Mica
spike is isolated and pinned; adopt only after passing those gates.

## 7. Verification and release limits

Commands from repo root:

```powershell
git status --short
dotnet build -c Release
dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj -c Release --no-build
git diff --check
```

Use a fresh disposable managed root for native UI testing; never casually open
retained relocated lab roots. All 40 selected D: folders were moved under
`D:\Emulator-Lab`; original absolute paths inside historical metadata remain.
See `D:\Emulator-Lab\relocation.json`. Creating a new test root avoids corrupting
old evidence/recovery data. Do not auto-migrate historical databases or paths.

UI matrix: 980×620, 1280×800, 1440×900, 1920×1080 logical sizes; 100/125/150/200%
DPI; dark/light/HC; portrait/landscape; 0/1/20/100 devices; long Unicode names;
empty/burst logs; keyboard-only and Narrator. Keep cancellation, recovery,
permission denial and stale capture cases explicit. Do not change host settings
or reboot without the owner's consent.

Compare actual FPS/input latency against the same workload and host. Proposed
review thresholds: investigate >5% sustained FPS loss or >10 ms p95 latency
increase across repeated comparable runs. Do not claim these targets are met
without measurements. Never decorate the live frame with expensive effects.

No release-readiness claim: power loss, source-built AOSP, signing and
redistribution remain open. Fresh WHPX on provenance-recorded binaries is
required before a new release claim. Keep the readiness patch excluded.

## 8. Handoff discipline

After each slice: update task/plan with actual checks, show a rendered screenshot,
record limitations, commit only scoped files and push the working branch when
authorized. Never merge main/master automatically. Preserve the unrelated
untracked `CODEX-EMULATOR-HANDOFF.md` and `CODEX-EMULATOR-MASTER-PROMPT.md`.
Use a continuation note naming the last commit, changed files, commands/results,
current blockers and next exact slice. Do not leave a partially compiled rewrite.

The latest Grok optional nit—journal write 3 obscures successful unroot evidence—
is existing fail-closed behavior. Keep it outside this UI task. The owner wants
premium design implemented carefully, not a new runtime or a generic dashboard.
