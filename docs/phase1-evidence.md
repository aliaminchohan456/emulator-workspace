# Phase 1 evidence

Windows-first developer MVP: persistent supervisor, SQLite state, authenticated
local pipe, WPF desktop with embedded guest viewport, deterministic sample APK,
APK install/launch, Logcat, and recovery evidence on a real WHPX guest.

Baseline commit at the start of this phase: **e675321**. This document describes
the work that follows it.

## Preflight and baseline reconciliation

| Item | Result |
| --- | --- |
| Git state | `master`, clean tree at `e675321` before Phase 1 changes |
| `dotnet build` | succeeded, 0 errors (CA1416 Windows-only analyzer warnings remain) |
| `dotnet test` | 21 passed, 0 failed |
| Phase 0 harness run | `artifacts/phase0/run-20261110-152217`, raw `summary.json` = **26 checks, 0 failures** |
| README claim | said 28/28; corrected to 26/26. The 28 came from a stale handoff line, not a second run |

The Phase 0 harness revision in this tree emits 26 checks. Any earlier 28-check
number is not reproducible from the artifacts and is not used.

## Host and pins (unchanged)

| Item | Value |
| --- | --- |
| OS | Windows 11 Pro 26H2, build 26300.9818 |
| CPU / RAM | Intel i5-8265U, 4C/8T / 31.8 GiB |
| GPU | Intel UHD Graphics 620, driver 31.0.101.2135 |
| Acceleration | WHPX 10.0.26300, `is installed and usable` |
| Emulator | 37.2.12.0, build_id 16428233 |
| Image | `system-images;android-35;default;x86_64` revision 2 |
| Controller protos | vendored from the same SDK `emulator\lib` |
| Guest | API 35 x86_64 userdebug, `Android/sdk_phone64_x86_64/emu64x:15/AE3A.240806.019/12368160:userdebug/test-keys` |
| Runtime roots | `D:\EmulatorWorkspace` (Phase 0), `D:\Phase1Root` (interactive), `D:\Phase1Accept` (acceptance), `D:\Phase1Empty` (first-run) |
| Free space | D: 397-398 GiB during runs; C: 12 GiB |

`QuickPastePremiumQA`, `%USERPROFILE%\.android\avd`, and every other existing
guest stayed outside the test scope. Runtime roots are workspace-local; no
global ADB shutdown is ever issued.

## What was built

### Persistent supervisor (Slice A)

- `EmulatorWorkspace.Supervisor` is the single lifecycle owner for a canonical
  workspace root. It takes an exclusive lock file (`supervisor.lock`) with owner
  metadata and canonicalizes the root through `GetFinalPathNameByHandle`, so
  junctions, subst drives, casing, and trailing separators collapse to one owner
  and one pipe.
- SQLite (`supervisor.db`, schema version 1, WAL) is the authoritative store.
  The supervisor is the only writer. Tables: `instances`, `operations`,
  `settings`, `schema_info`. A one-time transactional import of legacy JSON
  instance records is recorded in `settings.legacy_import_v1`; the JSON files
  are preserved and re-mirrored on every change for diagnostics. If the import
  fails, the transaction rolls back, the JSON records and all disks remain, and
  `legacy_import_v1_error` records the failure.
- One versioned contract (`EmulatorWorkspace.Contracts`) over a named pipe named
  from the canonical root hash. The server uses `PipeOptions.CurrentUserOnly`;
  after accept it verifies the client PID → logon session and token user SID,
  rejecting remote or cross-session peers before reading a request.
  Length-prefixed JSON frames are bounded (1 MiB request, 4 MiB response), with
  per-request deadlines, cancellation messages, typed error codes, and ordered
  subscription events.
- Operations: supervisor status/shutdown, doctor, diagnostics summary,
  instance list/create/start/stop/restart/coldboot/status/wait/delete/verify,
  frame subscribe/unsubscribe/snapshot/trace, input tap/swipe/key,
  display rotate, app install/launch/foreground, Logcat subscribe/unsubscribe,
  snapshot save/load.
- Engine launch, port reservation, ADB target selection, controller token
  discovery, and shutdown remain inside the supervisor. Engine stdout/stderr go
  to a per-instance log through inherited file handles (not a pipe pump), so the
  guest survives a supervisor crash and can be adopted.
- Reattachment compares the recorded PID plus process creation time and the
  engine's advertised `avd.id`, ADB port, and gRPC port. A process that cannot
  be verified is marked `Unknown` and left running.
- Agent claims are denied before dispatch and journaled; roles can only reduce
  privilege. `wsctl --agent` therefore still exits 4 through the supervisor.

### Desktop (Slice B)

`EmulatorWorkspace.Desktop` (WPF, net10.0-windows, MVVM, PerMonitorV2 manifest):

- First-run setup panel driven by supervisor doctor data: SDK root and version,
  pinned image revision, ADB, WHPX verdict, GPU and driver, workspace free
  space, actionable issues, and a redacted copyable diagnostics summary.
- Device rail with state badges, central embedded viewport, Inspector, Logcat
  drawer, toolbar, and status bar.
- Viewport pipeline: one latest-frame file per instance, supervisor event
  notification, background decode (`DecodePixelWidth=540`), frozen
  `BitmapSource`, bounded loop that always presents the newest frame. Guest
  framebuffer dimensions are kept separate from the decoded bitmap, so input
  coordinates always map to the guest, not the scaled image.
- Fit, 1:1, zoom, pop-out window, fullscreen (F11/Escape), rotation, drag-resize,
  and window-independent zoom. Input maps through the rendered image rectangle
  and is rejected outside it.
- Dark/light theme, high-contrast brush dictionary, reduced-motion-respecting
  behavior, keyboard focus, accessible names, and status/empty/error states.
- APK install from the toolbar or drag/drop, install progress events, package
  resolution with `aapt2 dump badging`, launch, screenshot, and bounded Logcat
  with search, tag, severity, pause, clear, and export.

### Sample APK (Slice C)

`sample/` builds `dev.emulatorworkspace.sample` with pinned SDK
build-tools 35.0.0, platform android-35, and JDK 17 through
`sample\build-sample-apk.ps1` (javac + d8 + aapt2 + zipalign + apksigner). No
Gradle or network access.

- Tapping anywhere increments `TAPS: N`, cycles a solid marker colour
  (palette[N % 6]) centred at `(w/2, h*0.30)`, and writes
  `I/WS_SAMPLE: TAP index=N ...` to Logcat.
- A Choreographer-driven circle provides a steady scene; the app draws its own
  rendered FPS.
- Built APK: `artifacts/phase1/sample/ws-sample.apk`, 12,691 bytes,
  SHA-256 `47D84A69368D513D141A0FFFDFB7B2436B2DDBF0890ADD349731FFED43A19843`,
  package `dev.emulatorworkspace.sample`, activity `.MainActivity`,
  signed with the standard debug keystore (v3 scheme).

## Acceptance run `20261110-172411`

Command:

```powershell
powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1
```

Fresh root `D:\Phase1Accept`; artifacts in
`artifacts/phase1/20261110-172411/` (`summary.json`, `transcript.txt`,
`doctor.json`, `frame-trace.json`, `screenshot.png`, `logcat-marker.txt`).

Result: **19 checks, 18 passed, 1 failed.**

| Check | Result | Evidence |
| --- | --- | --- |
| build | pass | `dotnet build` exit 0 |
| unit-tests | pass | 21 passed, 0 failed |
| supervisor.status | pass | pid 26588, protocol 1, root `d:\phase1accept`, SQLite path |
| doctor.whpx | pass | WHPX installed and usable |
| doctor.image | pass | pinned AOSP API 35 image present |
| instance.create | pass | `ws_qa1`, `emulator-5556`, controller 8555 |
| instance.boot | pass | boot 131.29 s, interactive 132.1 s |
| adb.target | pass | `get-serialno` = `emulator-5556` |
| controller.auth | pass | no-token and forged calls rejected, loopback only |
| capture.continuous | pass | 11 frames in 10 s, 1.07 fps, p50 881.8 ms, p95 1641.1 ms |
| apk.install | pass | 12,691-byte APK installed on qa1 |
| apk.launch | **fail** | foreground was `Application Not Responding: com.android.systemui` |
| screenshot | pass | 137,892-byte PNG saved |
| input.visible | pass | `WS_SAMPLE: TAP index=...` appeared after a controller tap |
| agent.denied | pass | exit 4 |
| snapshot.roundtrip | pass | save 9.39 s, load 8.66 s under two-guest load |
| isolation.second-guest | pass | qa2 on `emulator-5558`, boot 326.81 s |
| error.port-conflict | pass | typed `port_conflict` with the exact busy port trio |
| recovery.reattach | pass | supervisor killed, restarted, qa1 verified alive and booted |

The `apk.launch` failure is a SystemUI ANR overlay on this 4-core host, not a
launch failure: the sample package was installed, the subsequent tap reached it,
and Logcat emitted `WS_SAMPLE TAP`, so the activity was running. The check is
kept as a failure because the acceptance criterion is the foreground window, and
the harness does not dismiss ANR dialogs. This is recorded as an open gate
rather than masked.

## Performance

Raw samples: `artifacts/phase1/frame-trace-host-gpu.json` and the acceptance
run's `frame-trace.json`.

| Scenario | Frames / 10 s | Delivered FPS | p50 interval | p95 interval | Dropped | Guest rendered FPS |
| --- | --- | --- | --- | --- | --- | --- |
| One guest, host GPU (`t1`, settled) | 37 | 3.66 | 239.4 ms | 351.9 ms | 465 | 57 |
| Two guests, host GPU (qa1 while qa2 boots) | 11 | 1.07 | 881.8 ms | 1641.1 ms | ~ (seq gaps) | 51-57 |

- The guest scene renders at ~57 FPS inside Android; the limiter is the
  emulator's screenshot path (PNG encode of 1080x2400 frames) plus host CPU
  contention with a 4-vCPU guest. The WPF side keeps up with the events it
  receives: decode is downscaled to 540x1200 and no decode failures were
  recorded.
- The engine ignores `ImageFormat.width` for PNG: requests at 270, 540, and
  1080 all returned 1080x2400. Host-side downscaling was therefore applied in
  the decoder.
- Targets (60 delivered FPS, p95 input latency <= 100 ms) are **missed** on
  this host. The measured limiter is emulator capture/encoding, not guest
  rendering, WPF decoding, or the pipe.
- Software fallback (`-gpu swiftshader_indirect`) was not re-measured in Phase 1;
  Phase 0 boot-only numbers (66-95 s) remain the only software evidence. This is
  an open check, not a pass.

Input latency: the desktop records dispatch-to-first-presented-frame only when a
tap occurs while the frame subscription is live and the marker colour changes.
The acceptance run verified the visible response marker and Logcat entry, but
did not collect 20 valid latency samples. Reported as pending, not estimated.

## Interactive desktop evidence

Screenshots (not committed; under `artifacts/phase1/`):

| File | Shows |
| --- | --- |
| `desktop-first-run.png` | First-run setup on `D:\Phase1Empty`: SDK, pinned versions, WHPX, GPU, free space, Create/Copy actions |
| `desktop-live.png` | Live embedded guest (`TAPS: 6`), device rail, Inspector, Logcat drawer with 2500 retained lines, status bar |
| `desktop-full.png` | Maximized viewport with the sample app and Inspector capture metrics |

Manual WPF sequence (repeatable):

1. `EmulatorWorkspace.Desktop.exe --root D:\Phase1Empty` → setup panel.
2. Create device → supervisor creates `ws_dev` from the pinned image and boots;
   progress and readiness are shown; Cancel is not yet offered for create.
3. The viewport shows live frames; clicking the marker changes `TAPS: N` and the
   Inspector records the input latency when a frame with the new colour arrives.
4. Drag `ws-sample.apk` onto the window → install progress → automatic launch →
   Logcat shows `WS_SAMPLE` lines.
5. Search/tag/severity filters, Pause, Clear, Export in the Logcat drawer.
6. Fit / 1:1 / +/- / Ctrl+wheel; Rotate; Pop-out; Fullscreen; resize the window
   and move it to a monitor with a different scale; input remains mapped to the
   guest.
7. Close the window while the guest runs, reopen with the same `--root`; the
   supervisor keeps the guest and the desktop reattaches.
8. Stop from the toolbar; the supervisor performs a graceful emulator console
   kill and records the state.

Deviations and limits found while verifying the desktop:

- `InvariantGlobalization=true` from the repository-wide build props breaks WPF
  binding culture lookup; the desktop project overrides it to `false`.
- The WPF `ScrollViewer` around an auto-sized guest image caused a layout
  recursion on this host; the viewport uses a clipped grid with explicit image
  sizing instead. Zoom above the viewport is clipped rather than pannable in
  Phase 1.
- The host runs at 125% Windows scaling; screen captures require a
  PerMonitorV2-aware capture process, otherwise the right-hand panels appear
  cropped. The app itself lays out with `dpi=1.25` correctly.
- Multi-touch is not implemented; the Inspector labels the capability as
  single-touch tap and drag.

## Recovery and isolation checks

| Case | Result |
| --- | --- |
| Supervisor killed while qa1 running | engine survived; new supervisor adopted qa1 after PID/start-time and discovery identity checks |
| Desktop closed and reopened | supervisor kept the guest and the desktop resubscribed; verified in interactive session |
| Interrupted launch | engine exit is detected by `wait`, state becomes Failed with the log path; retry starts a new process |
| Port collision | typed `port_conflict` naming the exact trio before any process starts |
| Stale records | adoption clears dead PIDs and marks verified/unverified separately |
| Two guests | qa1/qa2 distinct serials, ports, AVD dirs, tokens; qa2 booted while qa1 stayed interactive |
| Agent denial | exit 4, journaled as `denied` |
| Snapshot | qa-baseline save/load under two-guest load |
| Low disk | the guard exists (`low_disk` with free/required bytes) but was not forced on the 398 GiB volume; open check |

## Open gates

| Gate | State | Next action |
| --- | --- | --- |
| Viewport 60 FPS / p95 <= 100 ms | Missed on this host | Profile emulator capture: test raw RGBA with scaling, lower guest resolution (720x1600), or an engine-side downscale; repeat with software GPU |
| 20 valid input observations per GPU mode | Pending | Add a desktop `--latency-probe N` mode that taps the marker and records presented-frame latency; run host and software modes |
| APK launch foreground check | Failed once (SystemUI ANR) | Dismiss ANR in the harness, or wait for calm state; keep the check strict |
| Desktop create/drag-drop click-through | Implemented, exercised via the same supervisor ops and CLI; screenshots show the UI | Record a click-through with UI automation on a quiet host |
| Low-space behaviour | Guard implemented, not forced | Run against a small VHD or a nearly full volume |
| Multi-touch | Not implemented | Verify the engine path, then add and label |
| Software renderer ANRs | Observed in Phase 0 | Re-measure in Phase 1 with the sample scene |

## Reproduce

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File sample\build-sample-apk.ps1
powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1 -Root D:\EmulatorWorkspace
powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1
src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe --root D:\Phase1Empty
```
