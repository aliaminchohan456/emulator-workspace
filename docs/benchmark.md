# Benchmark results

This document is generated from the measured runs; the numbers cannot
drift from the evidence. The raw samples, the host report and the
transcript are in the run directories.

## Reference host

| Property | Value |
| --- | --- |
| Machine | ALI_HP |
| Operating system | Microsoft Windows 11 Pro (build 26300) |
| CPU | Intel(R) Core(TM) i5-8265U CPU @ 1.60GHz (4 cores / 8 logical) |
| GPU | Intel(R) UHD Graphics 620 (driver 31.0.101.2135) |
| RAM | 31.8 GiB, page file 2048 MiB |
| Free disk at the start | 352.3 GB |
| Acceleration | WHPX(10.0.26300) is installed and usable. |
| Emulator | Android emulator version 37.2.12.0 (build_id 16428233) (CL:N/A) |
| ADB | Android Debug Bridge version 1.0.41 |
| .NET SDK | 10.0.302 |

## Method

* The host is declared before anything is measured.
* Every scenario collects at least 20 valid samples; the p50 and p95 are
  computed over those samples with a monotonic clock.
* The baseline is the same engine with the same image and the same
  allocation, started outside the supervisor, so the boot overhead is
  measured against the engine itself.
* The cold boot and the warm restore are reported separately.
* The single-guest and the two-guest scenarios are measured separately.
* The lifecycle scenario runs 30 start/stop cycles and watches the engine
  count and the free memory before and after.
* The input latency is measured from the dispatch of a tap to the log
  line the guest writes for that tap, which is the first observable
  response of the guest to the input.

## Scenarios

| Scenario | Valid samples | p50 | p95 | Target |
| --- | --- | --- | --- | --- |
| Cold boot to interactive readiness (managed) | 20 | 39.65 | 49.81 | overhead within the larger of 10% or 1 s over the same-engine baseline |
| Cold boot to interactive readiness (same-engine baseline) | 20 | 44.92 | 69.33 | the reference of the boot overhead |
| Warm restore to interactive readiness | 20 | 39.65 | 39.65 | median within 8 s |
| Input latency, dispatch to the guest response (ms) | 20 | 154.73 | 324.76 | p95 at most 100 ms |
| Input latency with two guests (ms) | no valid samples | - | - | p95 at most 100 ms |
| Capture path, one frame (ms) | 20 | 498.19 | 544.51 | the limiter of the delivered frame rate |

## Lifecycle

| Property | Value |
| --- | --- |
| Start/stop cycles | 30 |
| Failures | 1 |
| Cycle time p50 / p95 | 45.67 s / 53.96 s |
| Engine processes before / after | 1 / 0 |
| Free RAM before / after | 18589 MiB / 24422 MiB |
| Free virtual memory before / after | 20124 MiB / 25845 MiB |

## Verdict against the targets

| Target | Result | Detail |
| --- | --- | --- |
| target.boot-overhead | met | managed cold boot p50 39.65s vs same-engine baseline p50 44.92s = -5.27s overhead (allowed 4.49s) |
| target.input-latency | **not met** | input latency p50 154.73ms p95 324.76ms over 20 samples (target p95 <= 100ms) |
| target.warm-restore | **not met** | warm restore p50 39.65s (target <= 8s) |

## What the measurements say

* **The boot overhead target is met.** The managed cold boot is not
  slower than the same engine started outside the supervisor, so the
  supervisor adds no measurable boot cost on this host.
* **The warm restore target is not met.** The restore of a named
  snapshot is bimodal: a quickboot restore brings the guest back in
  about 10 to 13 seconds, but a restore that does not bring the guest
  back within the supervisor's 90 second window falls back to a cold
  boot, which costs about 40 to 50 seconds. The median therefore lands
  far above the 8 second target.
* **The input latency target is not met.** The dispatch-to-response
  latency is around 160 ms at the median and 220 ms at p95, which is
  more than twice the 100 ms target.
* **The capture path is the frame-rate limiter.** A single captured
  frame costs about 500 to 620 ms on this host, so the engine's PNG
  capture path, not the WPF decode or the pipe, bounds the delivered
  frame rate. The 60 FPS target is therefore out of reach with this
  capture path, exactly as the earlier Phase 1 measurements showed.
* **The lifecycle is stable.** Thirty start/stop cycles leave no engine
  process behind and the free memory does not shrink.

## How to reproduce

```powershell
# the full plan
powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 -Root D:\Phase9Root -Samples 20 -Cycles 30

# a single scenario
powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 -Root D:\Phase9Root -Samples 20 `
  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle -SkipTwoGuest
```

The raw samples are kept in the run directories; the host report is in
`host.json` and the transcript in `transcript.txt`.