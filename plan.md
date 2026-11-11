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

The fixture-foreground baseline variant now runs end to end as well
(`artifacts/phase3/recipe/20261111-215921`, 38 of 39 checks): the launch
operation dismisses the keyguard, the baseline branch waits for the package
manager and re-runs the recorded install, and the baseline is verified against
the recipe's first targeted control. The one open check is the retry after an
interrupted replay.

The Root Lab ADB daemon workflow is implemented and verified on the pinned
userdebug image (`artifacts/phase3/rootlab/20261111-222756`, 12 of 12 checks):
the targeted UID transition 2000 → 0 → 2000, the transport reattachment after
adbd restarts, and the state refresh after a boot and after a snapshot restore.
The WPF Root Lab surface and the Magisk profile remain; the Magisk profile needs
a Linux builder (or a stock ramdisk proven compatible with the pinned image).

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
| Google Play profile | Windows 11 x64 with WHPX | Official unmodified Google Play image; installed but not yet booted | API 35/36, x86_64 | Pending | Unmodified compatibility profile | Planned |
| Magisk Lab | Windows 11 x64 with WHPX | Emulator pinned; ramdisk substitution proven; Magisk build pending on Linux | API 35, x86_64 | `-ramdisk` override boots a modified archive | App-level `su` pending; stock restore verified | In progress |

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
