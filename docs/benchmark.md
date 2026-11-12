# Benchmark results

Raw evidence is under `artifacts/phase3/bench/` and `artifacts/phase3/wpf/`.

Runs used for this report: `20261112-173642`, `20261112-190940`, `20261112-210012`, `20261112-205244`, `20261112-213629`, `20261113-020314`, `20261113-023049`, `20261113-015728`, `20261113-025638`.

Guest profile: AOSP API 35 x86_64 revision 2, headless host GPU on WHPX.

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

| Scenario | Valid samples | p50 | p95 | Target |
| --- | --- | --- | --- | --- |
| Cold boot to interactive readiness (managed, s) | 20 | 44.36 | 55.96 | matched raw CLI overhead gate when both arms have 20 samples |
| Cold boot to interactive readiness (raw CLI, s) | 20 | 41.41 | 47.27 | same-engine control |
| Cold boot (second managed profile, s) | 20 | 44.92 | 69.33 | historical control |
| Historical restore reading (s) | 20 | 39.65 | 39.65 | invalid endpoint |
| Verified warm restore (s) | 20 | 9.19 | 12.34 | median at most 8 s, 20 samples |
| Restore attempt to readiness, including fallback (s) | 20 | 9.19 | 12.34 | reports recovery cost |
| ADB tap to Logcat, single guest (ms) | 20 | 476.91 | 935.58 | diagnostic |
| ADB tap to Logcat, two guests (ms) | 20 | 685.26 | 828.83 | diagnostic |
| CLI one-shot PNG (ms) | 20 | 869.85 | 1155.98 | diagnostic |

## Lifecycle

| Property | Value |
| --- | --- |
| Start/stop cycles | 30 |
| Failures | 1 |
| Cycle time p50 / p95 | 45.67 s / 53.96 s |
| Engine processes before / after | 1 / 0 |
| Free RAM before / after | 18589 MiB / 24422 MiB |
| Free virtual memory before / after | 20124 MiB / 25845 MiB |

## Four-hour WHPX soak

Run `20261112-213629` passed 200 paired probes over 14,411.36 seconds. Both original guest generations and their owned loopback listeners were verified throughout. Both guests were stopped and removed through the supervisor.

Minimum free physical RAM was 13,909 MiB. Minimum free virtual memory was 14,101 MiB.

| Guest | Private MiB first / last / peak | Handles first / last / peak |
| --- | --- | --- |
| `bench-e9eab70f3905` | 4,108.2 / 4,112.9 / 4,113.6 | 1545 / 1540 / 1546 |
| `bench2-52c863b183e4` | 4,128.5 / 4,125.6 / 4,128.5 | 1557 / 1537 / 1557 |

## WPF dispatch-to-composition

### Run `20261113-015728`

This run used desktop `0.1.0+526fb72cf42700abc505d46ea443c50bc23b9e7a` with the hash-verified CLI, supervisor and fixture from `8d40469154965e08eb2e511fb70b6a4fff7a927d`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 660.51 / 840.85 ms |
| Frame availability to composition p95 | 3.42 ms |
| Supervisor frame arrival interval p95 | 82.70 ms |
| Viewport throughput | 22.01 FPS at 540 × 1200 |
| Dropped stream sequences | 86 |

### Run `20261113-025638`

This run used desktop `0.1.2+76454c91a8a972eb43b2633cb7d69982ed73d90a` with the hash-verified CLI, supervisor and fixture from `76454c91a8a972eb43b2633cb7d69982ed73d90a`.
The endpoint is the WPF composition callback after the changed frame became available.

| Metric | Result |
| --- | --- |
| Valid trials | 25 / 25 |
| Dispatch to composition p50 / p95 | 667.09 / 789.56 ms |
| Frame availability to composition p95 | 4.42 ms |
| Supervisor frame arrival interval p95 | 85.39 ms |
| Viewport throughput | 21.82 FPS at 540 × 1200 |
| Dropped stream sequences | 87 |
| Desktop dispatch to supervisor reply p95 | 546.72 ms |

Separate capture diagnostics measured 49.08 FPS in the direct controller RGB stream and 21.12 FPS in the guest H.264 stream.
Both used the same 540 × 1200 foreground fixture. Guest HWUI timing is in `guest-framestats.txt`.

## Matched cold-boot comparison

Run `20261113-023049` verified the 20 raw control rows from `20261113-021146` against the same Windows host, emulator, fixture, MSI and AVD settings before running the managed arm.
Both arms required Android boot completion and a current focused window.
Managed p50 44.36s vs raw p50 41.41s = 2.95s overhead (allowed 4.14s).

## Reproduce the corrected benchmarks

```powershell
dotnet build
powershell -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root D:/WSBench -Samples 20 `
  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle

# Include restore trials by omitting -SkipRestore. For the four-hour
# soak, add -SoakMinutes 240 and monitor soak.jsonl through cleanup.
```
