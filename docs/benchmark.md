# Benchmark results

Run directories under `artifacts/phase3/bench/` contain the raw evidence.

Runs used for this report: `20261112-173642`, `20261112-190940`.

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

* Cold-boot rows come from two supervisor-managed profiles. The raw
  Android Emulator control and the boot overhead gate remain open.
* The historical restore harness could reuse cold-boot readiness after
  a load. The corrected harness times each load with a monotonic clock,
  records fallback and verifies a guest-state marker independently.
* Logcat latency runs from ADB tap dispatch to host receipt of a fresh
  fixture marker. WPF dispatch-to-composition needs its own measurement.
* One-shot PNG cost includes the CLI, capture and output write. The
  historical RGB viewport delivered 29.4 FPS in WPF.

## Scenarios

| Scenario | Valid samples | p50 | p95 | Target |
| --- | --- | --- | --- | --- |
| Cold boot to interactive readiness (managed, s) | 20 | 39.65 | 49.81 | raw CLI comparison pending |
| Cold boot (second managed profile, s) | 20 | 44.92 | 69.33 | historical control |
| Historical restore reading (s) | 20 | 39.65 | 39.65 | invalid endpoint |
| Verified warm restore (s) | no valid samples | - | - | median at most 8 s, 20 samples |
| Restore attempt to readiness, including fallback (s) | no valid samples | - | - | reports recovery cost |
| ADB tap to Logcat, single guest (ms) | 20 | 154.73 | 324.76 | diagnostic |
| ADB tap to Logcat, two guests (ms) | no valid samples | - | - | diagnostic |
| CLI one-shot PNG (ms) | 20 | 629.63 | 685.88 | diagnostic |

## Lifecycle

| Property | Value |
| --- | --- |
| Start/stop cycles | 30 |
| Failures | 1 |
| Cycle time p50 / p95 | 45.67 s / 53.96 s |
| Engine processes before / after | 1 / 0 |
| Free RAM before / after | 18589 MiB / 24422 MiB |
| Free virtual memory before / after | 20124 MiB / 25845 MiB |

## Reproduce the corrected benchmarks

```powershell
dotnet build
powershell -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root D:/WSBench -Samples 20 `
  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle

# Include restore trials by omitting -SkipRestore. For the four-hour
# soak, add -SoakMinutes 240 and monitor soak.jsonl through cleanup.
```
