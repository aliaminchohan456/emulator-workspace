# Benchmark results

Raw evidence is under `artifacts/phase3/bench/` and `artifacts/phase3/wpf/`.

Runs used for this report: `20261112-173642`, `20261112-190940`, `20261112-210012`, `20261112-205244`, `20261112-213629`, `20261113-020314`, `20261113-023049`, `20261113-071213`, `20261113-073629`, `20261113-074512`, `20261113-015728`, `20261113-025638`, `20261113-040149`, `20261113-040351`, `20261113-070554`, `20261113-070756`, `20261113-095328`, `20261113-100133`, `20261113-100519`, `20261113-100819`, `20261113-101158`.

Guest profile: AOSP API 35 x86_64 revision 2 on WHPX. Headless host GPU is the default. Renderer-specific viewport runs identify their GPU mode.

## Reference host

| Property | Value |
| --- | --- |
| Machine | ALI_HP |
| Operating system | Microsoft Windows 11 Pro (build 26300) |
| CPU | Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz (4 cores / 8 logical) |
| GPU | Intel(R) UHD Graphics 620 (driver 31.0.101.2135) |
| RAM | 31.8 GiB, page file 2048 MiB |
| Free disk at the start | 352.3 GB |
| Acceleration | False |
| Emulator | None |
| ADB | Android Debug Bridge version 1.0.41 |
| .NET SDK | 10.0.302 |

## Method

* Historical cold-boot rows came from two supervisor-managed profiles.
  The raw CLI row is populated only by an independently launched control.
* The historical restore harness could reuse cold-boot readiness after
  a load. The corrected harness times each load with a monotonic clock,
  records fallback and verifies a guest-state marker independently.
* Logcat latency runs from ADB tap dispatch to host receipt of a fresh
  fixture marker. WPF reports dispatch to the first composition pass after the changed frame.
* One-shot PNG cost includes the CLI, capture and output write. The
  historical RGB viewport delivered 29.4 FPS in WPF.

## Scenarios

| Scenario | Valid samples | p50 | p95 | Run | Target |
| --- | --- | --- | --- | --- | --- |
| Cold boot to interactive readiness (managed, s) | 20 | 49.9 | 54.9 | `20261113-074512` | matched raw CLI overhead gate when both arms have 20 samples |
| Cold boot to interactive readiness (raw CLI, s) | 20 | 35.37 | 50.52 | `20261113-074512` | same-engine control |
| Cold boot (second managed profile, s) | 20 | 44.92 | 69.33 | `20261112-173642` | historical control |
| Historical restore reading (s) | 20 | 39.65 | 39.65 | `20261112-173642` | invalid endpoint |
| Verified warm restore (s) | 20 | 11.15 | 16.91 | `20261113-073629` | median at most 8 s, 20 samples |
| Restore attempt to readiness, including fallback (s) | 21 | 11.62 | 43.54 | `20261113-073629` | reports recovery cost |
| ADB tap to Logcat, single guest (ms) | 20 | 476.91 | 935.58 | `20261112-213629` | diagnostic |
| ADB tap to Logcat, two guests (ms) | 20 | 685.26 | 828.83 | `20261112-213629` | diagnostic |
| CLI one-shot PNG (ms) | 20 | 869.85 | 1155.98 | `20261112-213629` | diagnostic |

## Lifecycle

| Property | Value |
| --- | --- |
| Start/stop cycles | 30 |
| Failures | 0 |
| Cycle time p50 / p95 | 42.66 s / 50.66 s |
| Engine processes before / after | 0 / 0 |
| Free RAM before / after | 22310 MiB / 22464 MiB |
| Free virtual memory before / after | 23755 MiB / 23918 MiB |

## Four-hour WHPX soak

Run `20261112-213629` passed 200 paired probes over 14,411.36 seconds. Both original guest generations and their owned loopback listeners were verified throughout. Both guests were stopped and removed through the supervisor.

Minimum free physical RAM was 13,909 MiB. Minimum free virtual memory was 14,101 MiB.

| Guest | Private MiB first / last / peak | Handles first / last / peak |
| --- | --- | --- |
| `bench-e9eab70f3905` | 4,108.2 / 4,112.9 / 4,113.6 | 1545 / 1540 / 1546 |
| `bench2-52c863b183e4` | 4,128.5 / 4,125.6 / 4,128.5 | 1557 / 1537 / 1557 |

## WPF dispatch-to-composition

### Run `20261113-015728` (1 guest(s))

This run used desktop `0.1.0+526fb72cf42700abc505d46ea443c50bc23b9e7a`. Candidate payload scope was CLI, supervisor and sample APK from `8d40469154965e08eb2e511fb70b6a4fff7a927d`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 660.51 / 840.85 ms |
| Frame availability to composition p95 | 3.42 ms |
| Supervisor frame arrival interval p95 | 82.70 ms |
| Viewport throughput | 22.01 FPS at 540 × 1200 |
| Dropped sequences | 86 |

### Run `20261113-025638` (1 guest(s))

This run used desktop `0.1.2+76454c91a8a972eb43b2633cb7d69982ed73d90a`. Candidate payload scope was entire runtime payload from `76454c91a8a972eb43b2633cb7d69982ed73d90a`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 667.09 / 789.56 ms |
| Frame availability to composition p95 | 4.42 ms |
| Supervisor frame arrival interval p95 | 85.39 ms |
| Viewport throughput | 21.82 FPS at 540 × 1200 |
| Dropped sequences | 87 |
| Desktop dispatch to supervisor reply p95 | 546.72 ms |

Separate capture diagnostics measured 49.08 FPS in the direct controller RGB stream and 21.12 FPS in the guest H.264 stream.
Both used the same 540 × 1200 foreground fixture. Guest HWUI timing is in `guest-framestats.txt`.

### Run `20261113-040149` (1 guest(s))

This run used desktop `0.1.3+ad5e5a37ac2a8ddb1987cef0e949167b9d5b921d`. Candidate payload scope was entire runtime payload from `ad5e5a37ac2a8ddb1987cef0e949167b9d5b921d`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 547.22 / 719.67 ms |
| Frame availability to composition p95 | 2.50 ms |
| Supervisor frame arrival interval p95 | 53.60 ms |
| Viewport throughput | 27.04 FPS at 540 × 1200 |
| Dropped sequences | 292 |
| Desktop dispatch to supervisor reply p95 | 544.21 ms |

### Run `20261113-040351` (2 guest(s))

This run used desktop `0.1.3+ad5e5a37ac2a8ddb1987cef0e949167b9d5b921d`. Candidate payload scope was entire runtime payload from `ad5e5a37ac2a8ddb1987cef0e949167b9d5b921d`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 871.73 / 1147.53 ms |
| Frame availability to composition p95 | 1.76 ms |
| Supervisor frame arrival interval p95 | 86.62 ms |
| Viewport throughput | 15.82 FPS at 540 × 1200 |
| Dropped sequences | 349 |
| Desktop dispatch to supervisor reply p95 | 891.31 ms |

### Run `20261113-070554` (1 guest(s))

This run used desktop `0.1.4+5448d15df6c2fdbe67ca5c35532509391420e701`. Candidate payload scope was entire runtime payload from `5448d15df6c2fdbe67ca5c35532509391420e701`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 583.45 / 679.56 ms |
| Frame availability to composition p95 | 1.88 ms |
| Supervisor frame arrival interval p95 | 56.09 ms |
| Viewport throughput | 23.09 FPS at 540 × 1200 |
| Dropped sequences | 329 |
| Desktop dispatch to supervisor reply p95 | 533.56 ms |

### Run `20261113-070756` (2 guest(s))

This run used desktop `0.1.4+5448d15df6c2fdbe67ca5c35532509391420e701`. Candidate payload scope was entire runtime payload from `5448d15df6c2fdbe67ca5c35532509391420e701`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 882.98 / 1145.69 ms |
| Frame availability to composition p95 | 2.53 ms |
| Supervisor frame arrival interval p95 | 80.72 ms |
| Viewport throughput | 15.49 FPS at 540 × 1200 |
| Dropped sequences | 454 |
| Desktop dispatch to supervisor reply p95 | 846.15 ms |

### Run `20261113-095328` (1 guest(s), diagnostic)

This run used desktop `0.1.5+a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. Candidate payload scope was CLI, desktop and sample APK from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. The separately built supervisor was `0.1.0+148d584cb09179195cd5f5a0d820261a5749d25e`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 5 / 5 |
| Dispatch to composition p50 / p95 | 211.72 / 426.99 ms |
| Frame availability to composition p95 | 3.05 ms |
| Supervisor frame arrival interval p95 | 57.15 ms |
| Viewport throughput | 27.17 FPS at 540 × 1200 |
| Dropped sequences | 14 |
| Configured emulator GPU mode | `host` |
| Desktop dispatch to supervisor reply p95 | 318.23 ms |
| Supervisor controller setup p95 | 160.71 ms |
| Supervisor controller tap p95 | 125.49 ms |

### Run `20261113-100133` (1 guest(s), diagnostic)

This run used desktop `0.1.0+65c2f15a2dd1e169721b661d750618698eb1f967`. Candidate payload scope was CLI and sample APK from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. The separately built supervisor was `0.1.0+148d584cb09179195cd5f5a0d820261a5749d25e`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 5 / 5 |
| Dispatch to composition p50 / p95 | 246.2 / 474.69 ms |
| Frame availability to composition p95 | 4.62 ms |
| Supervisor frame arrival interval p95 | 42.92 ms |
| Viewport throughput | 32.38 FPS at 540 × 1200 |
| Dropped sequences | 30 |
| Configured emulator GPU mode | `host` |
| Desktop dispatch to supervisor reply p95 | 355.86 ms |
| Supervisor controller setup p95 | 188.48 ms |
| Supervisor controller tap p95 | 147.32 ms |
| Supervisor changed-frame write p95 | 11.82 ms |
| Desktop changed-frame decode and dispatch p95 | 47.87 ms |

### Run `20261113-100519` (1 guest(s), diagnostic)

This run used desktop `0.1.0+8c5bb6ca4d14ad70aa63ae34cf5cf35e9a57d7a6`. Candidate payload scope was CLI from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. The separately built supervisor was `0.1.0+148d584cb09179195cd5f5a0d820261a5749d25e`. The separately built fixture hash is in the evidence manifest.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 5 / 5 |
| Dispatch to composition p50 / p95 | 232.91 / 540.4 ms |
| Frame availability to composition p95 | 2.88 ms |
| Supervisor frame arrival interval p95 | 55.99 ms |
| Viewport throughput | 27.43 FPS at 540 × 1200 |
| Dropped sequences | 7 |
| Configured emulator GPU mode | `host` |
| Desktop dispatch to supervisor reply p95 | 386.73 ms |
| Supervisor controller setup p95 | 225.18 ms |
| Supervisor controller tap p95 | 146.82 ms |
| Dispatch to first observed changed controller frame p95 | 496.09 ms |
| Supervisor changed-frame write p95 | 7.19 ms |
| Desktop changed-frame decode and dispatch p95 | 81.26 ms |
| Guest touch release to draw p95 | 22.00 ms |

### Run `20261113-100819` (1 guest(s), diagnostic)

This run used desktop `0.1.0+8c5bb6ca4d14ad70aa63ae34cf5cf35e9a57d7a6`. Candidate payload scope was CLI from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. The separately built supervisor was `0.1.0+148d584cb09179195cd5f5a0d820261a5749d25e`. The separately built fixture hash is in the evidence manifest.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 5 / 5 |
| Dispatch to composition p50 / p95 | 362.02 / 506.56 ms |
| Frame availability to composition p95 | 1.71 ms |
| Supervisor frame arrival interval p95 | 117.77 ms |
| Viewport throughput | 15.91 FPS at 540 × 1200 |
| Dropped sequences | 12 |
| Configured emulator GPU mode | `swiftshader_indirect` |
| Desktop dispatch to supervisor reply p95 | 284.80 ms |
| Supervisor controller setup p95 | 166.64 ms |
| Supervisor controller tap p95 | 102.92 ms |
| Dispatch to first observed changed controller frame p95 | 477.10 ms |
| Supervisor changed-frame write p95 | 10.76 ms |
| Desktop changed-frame decode and dispatch p95 | 79.35 ms |
| Guest touch release to draw p95 | 9.00 ms |

### Run `20261113-101158` (1 guest(s), diagnostic)

This run used desktop `0.1.0+d9098e98f6eec6c226667706b8bf1f371903ed89`. Candidate payload scope was CLI from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`. The separately built supervisor was `0.1.0+148d584cb09179195cd5f5a0d820261a5749d25e`. The separately built fixture hash is in the evidence manifest.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 5 / 5 |
| Dispatch to composition p50 / p95 | 166.12 / 374.17 ms |
| Frame availability to composition p95 | 2.40 ms |
| Supervisor frame arrival interval p95 | 35.01 ms |
| Viewport throughput | 37.28 FPS at 270 × 600 |
| Dropped sequences | 43 |
| Configured emulator GPU mode | `host` |
| Desktop dispatch to supervisor reply p95 | 326.65 ms |
| Supervisor controller setup p95 | 165.07 ms |
| Supervisor controller tap p95 | 148.85 ms |
| Dispatch to first observed changed controller frame p95 | 349.76 ms |
| Supervisor changed-frame write p95 | 5.74 ms |
| Desktop changed-frame decode and dispatch p95 | 34.97 ms |
| Guest touch release to draw p95 | 11.00 ms |

## Matched cold-boot comparison `20261113-023049`

The managed arm verified and reused the 20 raw control rows from `20261113-021146`.
Both arms required Android boot completion and a current focused window.
Passed. Managed p50 44.36s vs raw p50 41.41s = 2.95s overhead (allowed 4.14s).

## Matched cold-boot comparison `20261113-074512`

Both arms ran in this benchmark on the same Windows host and pinned emulator.
Both arms required Android boot completion and a current focused window.
Missed the target. Managed p50 49.9s vs raw p50 35.37s = 14.53s overhead (allowed 3.54s).

## Bounded RGB publication experiment

Run `20261113-101843` used a separately built desktop and supervisor
from `d834bde`, the instrumented fixture and the verified 0.1.5 CLI.
It delivered 36.68 FPS at 270 × 600. All five marker trials passed and
the guest was removed. Median composition was 164.19 ms, with a
408.68 ms maximum. The preceding half-size run `20261113-101158`
delivered 37.28 FPS, with 166.12 ms median and 374.17 ms maximum.
The queue was reverted after this comparison.

## Direct RGB and RGBA capture

Run `20261113-101457` used the exact 0.1.5 payload, one host-GPU
guest and the foreground animated fixture at 270 × 600 capture.
Each arm used `wsctl capture-bench --width 270 --height 600 --seconds 8`.
These figures measure the direct controller stream. The WPF viewport
measured 37.28 FPS at this capture size in `20261113-101158`.

| Arm | Format | Frames | FPS | p95 frame interval |
| --- | --- | ---: | ---: | ---: |
| 1 | RGB | 437 | 54.33 | 23.14 ms |
| 2 | RGBA | 467 | 57.88 | 19.53 ms |
| 3 | RGBA | 468 | 58.20 | 21.46 ms |
| 4 | RGB | 469 | 58.22 | 25.91 ms |

[Source, installer hash and raw files](phase3-manifest.json)

## Reproduce the corrected benchmarks

```powershell
dotnet build
powershell -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root D:/WSBench -Samples 20 `
  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle

# Include restore trials by omitting -SkipRestore. For the four-hour
# soak, add -SoakMinutes 240 and monitor soak.jsonl through cleanup.
```
