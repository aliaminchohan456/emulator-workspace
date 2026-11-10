# Android Emulator Workspace Plan

## Goal and baseline

Build a Windows-first Android emulator workspace for developers and QA. The release combines a responsive embedded Android viewport, recoverable device management, reproducible QA recipes, local agent control, and two separately verified root workflows. Deliver the source, a reproducible installer or installable release candidate, and evidence from real guests.

The folder was empty at planning time. Product name, repository setup, engine revision, SDK package revisions, and reference hardware are open. Start with an official x86_64 AOSP API 35 image, subject to SDK metadata. Every compatibility row and performance target is pending measurement. Track implementation and gates in [task.md](task.md). Follow [agents.md](agents.md) during implementation.

Phase 0 status (2026-11-10): the workspace is initialized and the runtime is
proven on the reference host. Pinned for testing: Android Emulator 37.2.12
(build_id 16428233), `system-images;android-35;default;x86_64` revision 2, and
the engine's own gRPC controller protos. Evidence, deviations, and remaining
measurements are in [docs/phase0-evidence.md](docs/phase0-evidence.md).

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

## Initial compatibility matrix

Each verified run must record the host architecture and CPU, Windows build, engine and image revisions, guest API and ABI, GPU and driver, backend, graphics and snapshot support, root mode, and Magisk version where applicable. Record Intel, AMD, and software-rendering results separately when the tested configurations differ.

| Candidate | Windows host | Engine and image | Guest | Graphics and snapshots | Root workflow | Status |
| --- | --- | --- | --- | --- | --- | --- |
| First developer profile | Windows 11 26H2 26300.9818, i5-8265U, 31.8 GiB, UHD 620 | Emulator 37.2.12 (16428233); `system-images;android-35;default;x86_64` rev 2 | API 35, x86_64, userdebug | Host GPU verified; swiftshader verified; save 5.7 s, load 5.5 s | `adb root` uid 0 and `adb unroot` uid 2000 verified | Verified (Phase 0) |
| Second developer profile | Same host | Same engine and image, second AVD | API 35, x86_64 | Own ports, token, and data; concurrent boot verified | Same image | Verified concurrently |
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
