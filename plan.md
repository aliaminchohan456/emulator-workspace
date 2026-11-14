# Android Emulator Workspace Plan

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
