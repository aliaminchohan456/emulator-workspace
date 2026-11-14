# Native delivery plan

Design baseline: 24cc54d. All native implementation slices below are pending.
This task delivers research, specification, tokens and a browser prototype.
Keep the existing readiness patch excluded and preserve runtime release gates.

| Slice | Small runnable result | Relevant files / dependency | Exit gate |
|---|---|---|---|
| UX-00 — baseline | Capture current desktop, timing and capability inventory with fresh provenance | Existing WPF/engine acceptance tools; exact SDK/image | Clean build tree + payloads; baseline window sizes, FPS/input data, UIA and known failures recorded |
| UX-01 — foundation | Complete dark/light/HC resources, typography, icon button, focus, proper minus glyph, native dark title bar | `Themes/Common.xaml`, Dark/Light/HighContrast, `ThemeManager`, title-bar interop | Existing actions preserved; every control themed, no missing icons, tab traversal and HC pass |
| UX-02 — Studio shell | Compact device rail, bounded command bar, device tools, collapsible details, focus view | `MainWindow.xaml`, code-behind input layout; reuse commands | 980×620 / 1280×800 / 1440×900, 100/125/150/200% DPI, pop-out and rotation targeting pass |
| UX-03 — lifecycle states | Inline readiness, typed persistent errors, visible APK drop target, capture feedback | Presentation state mapping and existing operation events | No timer-derived success; switch device mid-operation; denied capture/stale input/disconnect tests; cancel semantics honest |
| UX-04 — device library | Search/sort, rich cards, create/clone/recovery dialogs as supported | Backend inventory before wiring any new control | Exact selected instance; duplicate/low-disk/incompatible/locked paths; protected recovery preserved |
| UX-05 — privileged flows | Root Lab, exact in-app consent, session strip and takeover | `AgentSessionsWindow`, root commands; existing approval contract | Cancel produces no journal; approval expiry/artifact change rejected; UID/app proof and agent fencing unchanged |
| UX-06 — settings & tools | Display vs viewport settings, shortcuts, diagnostics, Captures; recipe review | Explicit supported-operation matrix; no invented recorder APIs | Shortcut collisions/guest focus, restart-required changes, redaction and bounded artifacts verified |
| UX-07 — finish | Motion, density, localization stress, optional backdrop/chrome spike | Prior slices stable; no UI package introduced opportunistically | Narrator, reduced motion, HC, keyboard, text scaling, mixed-DPI, performance and screenshot review pass |

## First implementation increment

Start UX-00 then UX-01. Fix the malformed minus label, establish shared
semantic resources and coherent control states, and add accessible icons to
the working command surfaces. Verify through the normal desktop path. Then
change layout in UX-02 without simultaneously altering permission logic.
Screens from this prototype guide hierarchy, not a literal HTML-to-XAML port.

## Capability inventory before each flow

For each visible action record: current supervisor operation name, caller
scope, supported states, arguments, cancellation/approval behavior, event
source, error codes and evidence output. Check current registry/dispatcher and
tests; do not rely on historical unchecked boxes in plan.md. UI affordances
appear only when their capability is implemented and safe for that guest.

New or unverified capabilities stay separate: video recording, background
FPS caps, resource hot-change, game keymapping, multi-touch, synchronized input
and multi-device live monitoring. Browser mock controls cannot close these.

## Quality acceptance matrix

- **Geometry:** 980×620, 1280×800, 1440×900 and 1920×1080 logical views; portrait,
  landscape, ultrawide letterbox, minimized/restored and mixed monitors.
- **Density:** 0, 1, 20, 100 devices; long Unicode names, no matches, empty logs,
  burst logs, long errors and paths. No clipped primary actions.
- **States:** stopped, booting, frame-wait, running, stopping, disconnected,
  restricted capture, permissions denied, failed install, recovery required.
- **Input:** tab/shift-tab, arrow menus, palette, focus return, F11/Escape,
  guest text input, drag/drop, scaling and stale-frame fencing.
- **Trust:** exact selected device on every change, agent off by default,
  approval binding, normal Cancel without writes, secure content redacted.
- **Host:** 100/125/150/200% DPI, 200% text, dark/light/HC, reduced motion,
  Narrator, GPU fallback. Do not change the owner's host settings without consent.
- **Performance:** same workload and sampling as baseline, raw measurements,
  no extra guest subscriptions, bounded logs/thumbnails, no dispatcher flood.
- **Release:** fresh WHPX on provenance-recorded binaries before a new release
  claim; power loss, source-built AOSP, signing and redistribution remain open.

## Review boundaries

Visual approval means hierarchy/style agreement, not safety or release approval.
Historical Grok approval of 24cc54d remains code-review evidence. Its optional
third-journal-write observation is backlog: unroot succeeds but the failed
journal write prevents storing the unroot-result fields; install fails closed
and rolls back. Do not mix that runtime change into a cosmetic UI patch.

No calendar promise is made from mockups. Estimate each native slice only after
its baseline/capability inventory; update task.md with actual evidence after
verification, not when its screenshot looks finished.
