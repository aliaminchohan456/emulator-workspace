# Copy this prompt to DeepSeek / the next coding agent

You are continuing an existing Windows emulator project. Implement its premium
native UI in small verified slices. Do not start a new app or redesign the
approved direction. Work autonomously on routine choices, but respect repository
safety requirements and ask only for concrete prerequisites you cannot resolve.

## Workspace and required reading

Repository: `C:\Users\aliam\OneDrive\Desktop\Emulator`
Current working branch: `codex/magisk-windows-lab`.
Remote: `https://github.com/aliaminchohan456/emulator-workspace.git`.
Run git status/log first; the design handoff commit is the latest commit that
adds this prompt and PREMIUM-UI-MASTER-PLAN.md. Do not reset to an older hash.

Read before editing:
1. agents.md, plan.md, task.md.
2. PREMIUM-UI-MASTER-PLAN.md (primary roadmap).
3. docs/design/premium-workspace/specification.md.
4. docs/design/premium-workspace/implementation-plan.md.
5. docs/design/premium-workspace/tokens.json, research.md, verification.md.
6. Open docs/design/premium-workspace/index.html and inspect studio-dark.jpg,
   devices-light.jpg and recovery-consent.jpg in that directory.

The owner wants a premium BlueStacks/LDPlayer-quality experience, implemented
in this existing developer/QA product. Graphite Studio is the chosen direction:
quiet graphite surfaces, pale blue action accent, crisp line icons, purposeful
spacing, large unobstructed guest display, coherent native controls. Porcelain
is the matching light theme. High Contrast must remain supported. The product
is still named Emulator Workspace.

The HTML is a visual/interaction prototype only. It uses simulated devices and
an illustrated launcher; do not ship those fake values as native runtime state,
replace Android's launcher, migrate to Electron/React/WebView, or connect the
prototype directly to the supervisor. Build the actual WPF UI.

## Your first task: UX-00 then UX-01 ONLY

1. Inspect current source, instructions and unrelated changes. Preserve the
   untracked CODEX-EMULATOR-HANDOFF.md and CODEX-EMULATOR-MASTER-PROMPT.md.
2. Record baseline build/tests and a current native screenshot. Prior baseline
   is Windows 400 passed; Linux 398 passed + 2 skipped; 22 existing warnings.
   Report any actual difference instead of silently treating it as your bug.
3. Inventory existing desktop commands and their supervisor operations. Do not
   assume old unchecked plan items mean the feature is missing.
4. Implement theme/control foundation: semantic brushes and type ramp, icon
   button/primary/secondary styles, focus rings, complete disabled/hover/pressed
   states, proper minus icon instead of the literal `--'`, and native dark
   title-bar treatment with a supported fallback. Use current DynamicResource
   theming and High Contrast precedence. Keep existing commands and layout/input
   targeting intact in this first slice.
5. Build, run appropriate tests, render the real WPF window, inspect/fix clipping,
   contrast, focus and popup appearance. Record what was actually tested.
6. Commit this runnable foundation. Do not combine it with a whole-shell rewrite,
   new UI library, controller change or Magisk cleanup change.

After UX-01 passes, continue the numbered slices in the master plan in order,
with a separate runnable checkpoint for each. When context is low, leave a
precise continuation note and clean checkpoint rather than rushing a rewrite.

## Exact visual requirements

Dark: base #111315; nav #151719; panel #1B1E21; raised #23272B; viewport #0C0E10;
stroke #353C42; primary text #F0F2F4, secondary #A4AFB8, tertiary #8D99A3;
accent #B3D9FA with #142A3B text. Success #8ED8AF, warning #EFC58A,
error #FFA6A6. Light colors and HC mappings are in tokens.json.

Segoe UI Variable Text with Segoe UI fallback. Cascadia Mono/Consolas only for
technical identifiers. Title 28/36, page 24/32, section 16/24, body 14/20,
caption 12/16 DIP. 4 DIP spacing grid. Controls radius 6, panels 10, dialogs 12.
Icons 16/20 DIP, consistent stroke; minimum 36 DIP target, 44 for touch density.
Subtle separation by surfaces/borders; shadows only on floating UI. No neon,
full-window gradients, glass everywhere, animated background, promotional tiles
or generic SaaS dashboard decorations. The guest supplies the visual content.

Every control needs normal/hover/pressed/focus/disabled/busy states as applicable.
Provide accessible names, keyboard tooltips, visible focus, readable text,
validation, empty states and error states. No inert native buttons. Disabled
actions explain why. Ordinary text contrast target >=4.5:1, essential focus/
control boundaries >=3:1. Test actual system accent before offering it.

## Later shell and flow implementation

Studio: left compact navigation/device rows, selected-device identity with one
Start/Stop action, large opaque aspect-preserving frame, 44–48 DIP tool rail,
optional details, collapsed Logcat and status bar. Large device cards belong to
the full library. Collapse details around 1050 DIP; never crush the viewport or
wrap every command in the header. Keep Back/Home/Recents reachable; overflow
secondary tools at short heights. Preserve Ctrl+K, F11 and Escape.

Settings and command palette use the same command/binding source. Focus belongs
to either shell or guest; don't intercept normal guest typing. Theme all popup,
ComboBox, CheckBox, DataGrid and context-menu surfaces, not just the main window.
Keep native resize/Snap/system menu. Custom chrome comes only after a separate
Windows compatibility check. Mica is optional chrome, never a live-frame effect.

State flows must cover no device, setup missing, stopped, starting, waiting for
frame, ready, stopping, disconnected/stale, capture restricted, engine failure,
permission denial, low disk and recovery required. Use supervisor evidence;
never fake progress or FPS. Don't let an operation on one device block unrelated
work. APK drag affordance names the exact target; no per-drag-event log spam.
Captures show metadata and respect secure/redacted content, including thumbnails.

Root Lab is device-scoped, not a toggle. ADB root and app-level Magisk proof
remain separate. Consent must preserve exact paths/hashes, temporary ADB-root
wording, return to UID 2000, host-shell implications, backup/recovery details,
Zygisk off/no modules and snapshot fence. Cancel before prepare creates no
journal/transaction. Approval stays bound to caller/guest/generation/artifacts.
Active Magisk labs allow agent observation only. Take over/Revoke stays visible
while paired, including focus mode. UI state is never authorization.

## Architecture boundaries — do not violate

- Keep WPF net10.0-windows, MVVM, current supervisor operation registry and policy.
- Start in Desktop/MainWindow.xaml, Themes/*, ThemeManager and existing viewmodels.
- Preserve framebuffer mapping, orientation, letterbox, DPI, stale-frame rejection,
  semantic-selector freshness, process identity and selected ADB transport.
- Never target physical/unmanaged devices, globally kill ADB or run host shell
  as a new agent API. No integrity/DRM/root-detection bypass features.
- No runtime/core changes merely to simplify a mockup. No new dependency without
  an actual need, official source, pinned version and compatibility validation.
- Preserve immutable images and last verified recovery. Do not rewrite historical
  evidence or old databases to make paths convenient.
- All retained D: folders are now under D:\Emulator-Lab. Historical absolute
  paths remain inside them. Use a new disposable managed root for testing;
  do not casually launch relocated recovery roots. See relocation.json there.
- Host feature changes, reboot, SDK terms and signing credentials require the
  owner's concrete action/consent. Don't trigger these as cosmetic setup.

## Validation and reporting contract

Run appropriate checks, including:
`dotnet build -c Release`
`dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj -c Release --no-build`
`git diff --check`

Render the actual native UI. Check 980×620, 1280×800, 1440×900; later 1920×1080,
100/125/150/200% DPI, mixed monitors, 200% text, HC, reduced motion, Narrator,
keyboard-only, long Unicode names, 0/1/20/100 devices and burst/empty logs.
Do not claim a check passed if your environment cannot run it; mark it pending.
Use the normal desktop/CLI/MCP entry points for functional verification.

For hardware evidence, follow docs/build-provenance.md: fresh isolated checkout,
commit/tree and clean status before build, post-build source verification and
exact payload hashes. Benchmark the same workload; investigate >5% sustained
FPS loss or >10 ms p95 latency regression, accounting for measurement noise.
Never claim 60 FPS or a latency number without a measured run.

After every slice update task.md/plan.md with changes, commands/results, screenshot
paths, remaining gaps and next task. Commit only scoped files; push the working
branch as authorized by the owner. Never merge main/master automatically.
Final report: completed slice, commit, tests, rendered evidence, limitations and
copy-paste Grok review prompt with the exact diff range.

Grok approved 24cc54d for merge, not release. The optional third-journal-write
unroot-evidence ambiguity is existing fail-closed behavior: leave it outside
this UI work. Fresh provenance-backed WHPX, power-loss testing, source-built
AOSP, signing and redistribution remain release gates. Keep readiness patch
excluded. A polished screenshot is not a release-readiness claim.

Begin now by reading the required files and implementing only the first runnable
foundation slice. Do not spend the turn merely proposing another plan.
