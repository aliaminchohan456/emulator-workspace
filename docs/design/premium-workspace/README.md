# Graphite Studio — Emulator Workspace design proposal

14 November 2026 · Design and implementation plan, not a shipped WPF redesign.

Open **index.html** in a browser, or serve this directory locally:

```powershell
python -m http.server 8877 --bind 127.0.0.1 --directory docs/design/premium-workspace
```

Then visit http://127.0.0.1:8877. No packages, remote fonts, analytics or network
APIs are needed. All devices, captures, permissions and guest content are
illustrative. Closing/reloading resets the prototype. Nothing connects to ADB,
the supervisor or real guests. Native window buttons are decorative chrome in
this browser study, not working OS controls.

## Deliverables

- [Research and decisions](research.md): official sources, Grok comparison and current-code audit.
- [Detailed interaction specification](specification.md): screens, states, components, accessibility and WPF integration.
- [Delivery plan](implementation-plan.md): small runnable slices, dependencies and acceptance gates.
- [Token source](tokens.json): proposed semantic design values, separate from production resources.
- [Verification](verification.md): checks actually performed and limitations.
- `index.html`, `studio.css`, `studio.js`: interactive concept with Graphite and Porcelain appearances.
- `studio-dark.jpg`, `devices-light.jpg`, `recovery-consent.jpg`: rendered reference views.

## The direction

The Android display is the visual anchor. A restrained graphite shell gives
content room; a pale blue accent identifies the next action. Compact device
rows keep switching immediate. A dedicated library accommodates richer cards.
The device inspector is optional and collapses before the guest gets squeezed.
There is one obvious lifecycle action, a short tool rail, and a searchable
command palette. Root operations and agent access have deliberate, readable
flows instead of competing with ordinary tools.

This keeps the developer/QA purpose of the project while adopting the useful
shell conventions of consumer emulators. Original line icons and a procedural
sample launcher provide a complete visual study without copying competitor
branding, screenshots or wallpaper. The launcher is **not** a proposed Android
home-screen replacement: actual guest pixels remain untouched in the product.

## Try these paths

1. Open Devices, search, create a uniquely named preview device, then open it.
2. Start a stopped device; observe the explicitly simulated boot transition.
3. Rotate the sample display, toggle Details, open Logcat or enter Focus view.
4. Press Ctrl+K, search for a command, press Enter; Esc dismisses dialogs.
5. Open Magisk lab, review recovery, inspect the consent, then Cancel.
6. Open Settings and switch to Porcelain; try reduced motion.
7. Use Explore states to inspect running, stopped, booting and recovery states.

Actual WPF work is still pending. Do not interpret the prototype's timing,
sample metrics, resource presets or capabilities as engine measurements.
