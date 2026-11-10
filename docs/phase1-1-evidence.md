# Phase 1 stabilization evidence (desktop + capture path)

Baseline for this pass: commit **e044c60**, clean tree. Pins unchanged: Android
Emulator 37.2.12.0 (build 16428233), vendored protos from the same SDK,
`system-images;android-35;default;x86_64` revision 2, WHPX. Runtime roots used:
`D:\Phase1Root`, `D:\Phase1Accept2`, `D:\Phase1Empty4`, `D:\FakeSdk`.
`QuickPastePremiumQA`, the default AVD home, and `D:\EmulatorWorkspace` were not
modified.

Build/test at start: the first build attempt hit a live supervisor holding the
DLLs; after stopping it, `dotnet build` is clean and `dotnet test` is
**23 passed** (21 existing + 2 injected disk-guard tests).

The original Phase 1 acceptance run `20261110-172411` remains **18/19** with the
SystemUI-ANR foreground failure and its artifacts. It is not overwritten.

## A. Strict launch check

The harness now calls `tools/launch-check.ps1`, which:

- requires `sys.boot_completed=1` and a settled, awake device;
- clears Logcat, force-stops the sample, returns Home, then launches;
- polls `dumpsys window` for up to 25 s and classifies
  `fail-anr`, `fail-timeout`, `fail-command`, `fail-responsiveness`, or `pass`;
- on pass, requires the sample activity as `mCurrentFocus`, no blocking
  “Not Responding” window, a tap that writes `WS_SAMPLE TAP index=…`, and
  before/after screenshots that differ.

Results:

| Scenario | Result | Artifacts |
| --- | --- | --- |
| Single warm guest, settled | **pass**, focus `dev.emulatorworkspace.sample/.MainActivity` | `artifacts/phase1-1/launch-t1-20261110-182928/` |
| Two booted guests (`qa1` + `qa2`) | **pass**, no ANR | `artifacts/phase1-1/launch-qa1-20261110-184522/` |
| Fresh acceptance root | **pass** (`class=pass`) | `artifacts/phase1-1/acceptance-20261110-183709/` |
| Original run, startup under load | **fail** (ANR), preserved | `artifacts/phase1/20261110-172411/` |

The original failure is therefore a startup/load race, not a launch or package
defect. The strict assertion is kept: an ANR in the required scenario is a
failure even when a later attempt passes.

Each attempt stores `launch-command.txt`, `windows.txt`, `activities.txt`,
`logcat.txt`, `logcat-sample.txt`, `before.png`, `after.png`, and `result.txt`.

## B. Capture path

Probe command (client-side, authenticated controller, same guest/scene):

```powershell
wsctl capture-bench --name t1 --format rgb --width 540 --height 1200 --seconds 5 --out artifacts\phase1-1\probe.json
```

Measured on one warm guest, sample app foreground, host GPU:

| Candidate | Requested | Actual | FPS | p50 / p95 interval | Bytes/frame | Bytes/s |
| --- | --- | --- | --- | --- | --- | --- |
| A. PNG (current control) | none | 1080x2400 | 4.55 | 205.8 / 270.5 ms | ~114 KB (static) | ~0.5 MB/s |
| B. RGBA8888 | none | 1080x2400 | 13.01 | 71.7 / 110.2 ms | 10,368,000 | 135 MB/s |
| B. RGB888 | 540x0 | 1080x2400 (width ignored) | 13.84 | 60.3 / 85.2 ms | 7,776,000 | 108 MB/s |
| B. RGBA8888 | 540x1200 | 540x1200 | 40.83 | 21.6 / 36.9 ms | 2,592,000 | 106 MB/s |
| **B. RGB888 (selected)** | **540x1200** | **540x1200** | **43.52** | **20.3 / 34.0 ms** | **1,944,000** | **85 MB/s** |
| C. 720x1600 guest/profile | not run | — | — | — | — | — |

Key findings:

- `width` alone is ignored; **both width and height** must be set for scaling.
  Requests at 720x1600 and 480x1067 aborted the stream twice; 540x1200 is the
  verified working pair. The aborts are recorded in the probe attempts and were
  not reproduced at 540x1200.
- `RGB888` payload is top-down, 3 bytes per pixel, interpreted as **RGB**
  (`PixelFormats.Rgb24`); an initial BGR interpretation showed a blue marker
  where the sample expected red. The 540x1200 screenshot after the fix shows the
  correct palette (`TAPS: 20` → blue marker).
- Unscaled RGBA is ~10.37 MB/frame, so full-resolution raw is transport-bound;
  scaled RGB is the only candidate that beat PNG by an order of magnitude.

### Implemented adapter

`FramePump` now has two transports behind the same subscription API:

- **rgb24** (default for the viewport): controller `RGB888` at 540x1200, written
  to a bounded ring of the six newest `name-<seq>.bgr` files; the event carries
  `path`, `seq`, `width`, `height`, `stride`, `guestWidth`, `guestHeight`, and
  `dropped`. Old files are deleted; the latest frame is never queued in memory.
- **png** (one-shot screenshot and explicit fallback): existing single-file
  atomic replace path.

The desktop reads the event path with `FileShare.ReadWrite|Delete`, validates
`bytes == width*height*3`, builds a frozen `BitmapSource` (`Rgb24`) on the
background decode task, and presents on the dispatcher. Input mapping uses
`guestWidth/guestHeight` from `wm size` (1080x2400), not the scaled bitmap.

Measured through the supervisor on one warm guest:

| Path | FPS | p50 / p95 interval | Dropped / 10 s | Files kept |
| --- | --- | --- | --- | --- |
| PNG 1080 | 3.66 (previous run) | 239 / 352 ms | 465 | 1 |
| RGB24 540x1200 | **32.6-34.5** | **24.4-25.9 / 45.2-45.9 ms** | 80-95 | 6 |

WPF Inspector showed **29.4 FPS** delivered with 11 dropped frames while the
guest rendered 31 FPS (`desktop-rgb24.png`), versus 1.8 FPS on the PNG path.

Two booted guests, first guest actively streamed (second idle at launcher):

| Metric | Value |
| --- | --- |
| Supervisor FPS | 20.1 |
| p50 / p95 interval | 47.3 / 85.7 ms |
| Dropped / 10 s | 1 |
| Input p50 / p95 | 694.7 / 1012.1 ms |

Software fallback (`-gpu swiftshader_indirect`, single guest):

| Metric | Value |
| --- | --- |
| Supervisor FPS | 17.2 |
| p50 / p95 interval | 55.3 / 93.5 ms |
| Input p50 / p95 | 740.6 / 857.6 ms |

The target (60 delivered FPS) is still missed. The remaining bottleneck is the
engine-side screenshot/scale/encode path plus host CPU: raw 540x1200 still moves
~85 MB/s, and the emulator drops or coalesces frames under two guests. WPF decode
is not the limiter (540x1200 BGR24, one copy per unique frame). The next
candidate is an encoded H.264/scrcpy-style transport or engine-side downscale;
neither is integrated because the pinned controller path already improved 9x and
the remaining gap needs profiling on a faster host.

PNG remains available for `frame.snapshot`/screenshot and as an explicit
fallback; it is not used for the interactive viewport.

## C. Input latency

The desktop composition-proxy probe (`--latency-probe`, next WPF
`CompositionTarget.Rendering` after the response frame) is implemented but
**not validated**: in probe mode the UI presented queued frames only when the
process was terminated, so no trial records were produced. The cause was not
isolated in this pass; the probe is left experimental and is not used for
claims. Next action: add dispatcher heartbeat logging and run it with the window
foreground on a quiet host.

A headless capture-visible probe produced real samples through the same
supervisor/controller path. Metric: dispatch → first captured frame whose sample
marker colour changed. This is **not** a composition or display measurement.

| Scenario | Trials | Valid | Timeouts | p50 | p95 |
| --- | --- | --- | --- | --- | --- |
| Host GPU, single guest | 20 | 20 | 0 | **433.9 ms** | **474.2 ms** |
| Host GPU, two guests | 20 | 20 | 0 | 694.7 ms | 1012.1 ms |
| Software GPU, single guest | 20 | 20 | 0 | 740.6 ms | 857.6 ms |

Raw JSON with every sample: `artifacts/phase1-1/input-latency-*.json`. The
target p95 ≤ 100 ms is missed; this metric includes the engine capture cadence
(~20-43 FPS) and host scheduling, so it cannot be compared to a display-latency
number. The composition proxy remains the required measurement for the target.

## D. Functional acceptance

New run on a fresh root `D:\Phase1Accept2`:
`artifacts/phase1-1/acceptance-20261110-183709/` (copy of
`artifacts/phase1/20261110-183709`).

**19 checks, 19 passed, 0 failed.**

| Check | Detail |
| --- | --- |
| build / unit-tests | build 0 errors; 23 passed, 0 failed |
| supervisor.status | protocol 1, SQLite, peer-verified pipe |
| doctor.whpx / doctor.image | WHPX usable; pinned image present |
| instance.create / boot | qa1 boot 80.98 s, interactive 81.44 s |
| adb.target / controller.auth | exact serial; token + loopback |
| capture.continuous | 144 frames/10 s, 14.3 FPS, p50 28.6 / p95 275.4 ms |
| apk.install / apk.launch | installed; **strict pass**, sample focused, no ANR |
| screenshot / input.visible | PNG saved; WS_SAMPLE marker |
| agent.denied | exit 4 |
| snapshot.roundtrip | save 5.25 s / load 5.68 s |
| isolation.second-guest | qa2 boot 74.3 s, distinct serial |
| error.port-conflict | typed `port_conflict` |
| recovery.reattach | supervisor restart, qa1 engine alive and targeted |

The Phase 0 regression `run-20261110-174228` remains **26/26** through the
supervisor (reported separately, unchanged).

## E. Desktop evidence and scope

- `desktop-rgb24.png`: live viewport on the RGB24 path, `TAPS: 20`, Inspector
  `540x1200 - 29.4 fps - 11 dropped`, Logcat drawer populated.
- `desktop-missing-sdk-2.png`: missing SDK path with actionable official steps,
  pinned components named, Create disabled, no download/consent performed.
- `desktop-first-run.png` (from the first pass): usable-SDK first run.
- First-run setup is verified with a usable pinned SDK and with an incomplete
  SDK. The missing-component path explains the official SDK Manager steps and
  requires operator consent; it does not download or accept terms automatically.
- Touch mapping is verified at the host’s 125% scaling. 150% and mixed-DPI
  monitor moves were not available in this session; recorded as pending.
- Portrait is verified by screenshots; rotation (landscape), Fit/1:1/zoom,
  pop-out, and fullscreen are implemented and previously exercised. A single
  recorded WPF create-to-drag-drop-to-shutdown click-through was not captured in
  this pass; the same operations are covered through the supervisor and the
  strict launch check. Drag/drop remains implemented but not click-through
  automated.
- Multi-touch is not implemented and is labeled single-touch.
- Disk guard is exercised by injected unit tests (`DiskSpaceGuardTests`, 2
  passing) so the host’s real free space was not altered; a constrained-volume
  run remains pending.

## F. Open items and next actions

| Item | State | Next action |
| --- | --- | --- |
| 60 FPS viewport | Missed (29-34 FPS single, 20 FPS two guests) | Profile encoded transport (H.264/scrcpy) or engine downscale; repeat on reference host |
| Composition-proxy latency | Probe not validated | Add dispatcher heartbeat, keep window foreground, re-run 20 samples |
| Display latency target | Not measured | Instrument real presentation or high-speed capture |
| 150% / mixed-DPI | 125% verified only | Re-run on a host with 150% and two monitors |
| WPF click-through create→shutdown | Partial | Automate Create, drag/drop, stop with UI Automation IDs |
| Low-space constrained volume | Injected tests only | Run on a small VHD |
| 720x1600 guest profile | Not run | Create a separate profile and compare |
| 720x1600 raw request | Aborted twice | Investigate engine scaling bounds; keep 540x1200 |

## Reproduce

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File tools\launch-check.ps1 -Root D:\Phase1Root -Name t1
powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1 -Root D:\Phase1Accept2
wsctl capture-bench --name t1 --format rgb --width 540 --height 1200 --seconds 5 --out artifacts\phase1-1\probe.json
wsctl frame-trace --name t1 --seconds 10 --out artifacts\phase1-1\trace.json
wsctl input-latency --name t1 --samples 20 --out artifacts\phase1-1\latency.json
powershell -ExecutionPolicy Bypass -File tools\latency-probe.ps1 -Root D:\Phase1Root -Name t1 -Count 20
```
