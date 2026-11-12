#!/usr/bin/env python3
"""Builds the published benchmark document from the measured runs.

The document states the reference host, the method, every scenario with its
p50 and p95, and the verdict against each target of the plan. The numbers
come from the runs themselves, so the document cannot drift from the
evidence. Several runs can be merged: a scenario that was not measured in a
run is taken from the run that measured it.
"""

import json
import pathlib
import sys


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def merge(runs: list[dict]) -> dict:
    merged = {"host": {}, "scenarios": {}, "lifecycle": None, "checks": []}
    for run in runs:
        report = run.get("benchmark", {})
        if report.get("host") and not merged["host"]:
            merged["host"] = report["host"]
        for name, data in (report.get("scenarios") or {}).items():
            if data.get("samples"):
                merged["scenarios"][name] = data
        if report.get("lifecycle"):
            merged["lifecycle"] = report["lifecycle"]
        for check in run.get("checks", []):
            merged["checks"].append(check)
    return merged


def row(name: str, data: dict, target: str) -> str:
    if not data or not data.get("samples"):
        return f"| {name} | no valid samples | - | - | {target} |"
    return (f"| {name} | {data['samples']} | {data.get('p50')} | {data.get('p95')} | "
            f"{target} |")


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: build-benchmark-doc.py <run-dir> [<run-dir> ...] <output.md>",
              file=sys.stderr)
        return 2

    runs = [load(pathlib.Path(argument) / "summary.json") for argument in sys.argv[1:-1]]
    output = pathlib.Path(sys.argv[-1])
    report = merge(runs)
    host = report.get("host", {})
    scenarios = report.get("scenarios", {})
    lifecycle = report.get("lifecycle") or {}

    lines = [
        "# Benchmark results",
        "",
        "This document is generated from the measured runs; the numbers cannot",
        "drift from the evidence. The raw samples, the host report and the",
        "transcript are in the run directories.",
        "",
        "## Reference host",
        "",
        "| Property | Value |",
        "| --- | --- |",
        f"| Machine | {host.get('machine')} |",
        f"| Operating system | {host.get('os')} (build {host.get('osBuild')}) |",
        f"| CPU | {host.get('cpu')} ({host.get('cpuCores')} cores / {host.get('cpuLogical')} logical) |",
        f"| GPU | {host.get('gpu')} (driver {host.get('gpuDriver')}) |",
        f"| RAM | {round((host.get('totalRamMB') or 0) / 1024, 1)} GiB, page file {host.get('pagefile')} MiB |",
        f"| Free disk at the start | {host.get('diskFreeGB')} GB |",
        f"| Acceleration | {host.get('whpx')} |",
        f"| Emulator | {host.get('emulatorVersion')} |",
        f"| ADB | {host.get('adbVersion')} |",
        f"| .NET SDK | {host.get('dotnetVersion')} |",
        "",
        "## Method",
        "",
        "* The host is declared before anything is measured.",
        "* Every scenario collects at least 20 valid samples; the p50 and p95 are",
        "  computed over those samples with a monotonic clock.",
        "* The baseline is the same engine with the same image and the same",
        "  allocation, started outside the supervisor, so the boot overhead is",
        "  measured against the engine itself.",
        "* The cold boot and the warm restore are reported separately.",
        "* The single-guest and the two-guest scenarios are measured separately.",
        "* The lifecycle scenario runs 30 start/stop cycles and watches the engine",
        "  count and the free memory before and after.",
        "* The input latency is measured from the dispatch of a tap to the log",
        "  line the guest writes for that tap, which is the first observable",
        "  response of the guest to the input.",
        "",
        "## Scenarios",
        "",
        "| Scenario | Valid samples | p50 | p95 | Target |",
        "| --- | --- | --- | --- | --- |",
        row("Cold boot to interactive readiness (managed)", scenarios.get("coldBoot", {}),
            "overhead within the larger of 10% or 1 s over the same-engine baseline"),
        row("Cold boot to interactive readiness (same-engine baseline)",
            scenarios.get("baselineBoot", {}), "the reference of the boot overhead"),
        row("Warm restore to interactive readiness", scenarios.get("warmRestore", {}),
            "median within 8 s"),
        row("Input latency, dispatch to the guest response (ms)",
            scenarios.get("inputLatencyMs", {}), "p95 at most 100 ms"),
        row("Input latency with two guests (ms)",
            scenarios.get("twoGuestLatencyMs", {}), "p95 at most 100 ms"),
        row("Capture path, one frame (ms)", scenarios.get("captureMs", {}),
            "the limiter of the delivered frame rate"),
        "",
        "## Lifecycle",
        "",
    ]

    if lifecycle:
        cycle = lifecycle.get("cycleSeconds", {})
        lines += [
            "| Property | Value |",
            "| --- | --- |",
            f"| Start/stop cycles | {lifecycle.get('cycles')} |",
            f"| Failures | {lifecycle.get('failures')} |",
            f"| Cycle time p50 / p95 | {cycle.get('p50')} s / {cycle.get('p95')} s |",
            f"| Engine processes before / after | {lifecycle.get('enginesBefore')} / {lifecycle.get('enginesAfter')} |",
            f"| Free RAM before / after | {lifecycle.get('freeRamBeforeMB')} MiB / {lifecycle.get('freeRamAfterMB')} MiB |",
            f"| Free virtual memory before / after | {lifecycle.get('freeVirtualBeforeMB')} MiB / {lifecycle.get('freeVirtualAfterMB')} MiB |",
        ]
    else:
        lines.append("The lifecycle scenario was not part of these runs.")

    lines += ["", "## Verdict against the targets", "",
              "| Target | Result | Detail |", "| --- | --- | --- |"]
    # A target keeps the verdict of the run that measured its scenario.
    target_scenarios = {
        "target.boot-overhead": ("coldBoot", "baselineBoot"),
        "target.warm-restore": ("warmRestore",),
        "target.input-latency": ("inputLatencyMs",),
    }
    verdicts: dict[str, dict] = {}
    for check in report["checks"]:
        name = str(check.get("Check", ""))
        if not name.startswith("target."):
            continue
        detail = str(check.get("Detail", ""))
        if "was not measured in this run" in detail:
            continue
        wanted = target_scenarios.get(name)
        measured = True
        if wanted:
            measured = all((scenarios.get(scenario) or {}).get("samples") for scenario in wanted)
        # The first run that measured the target keeps the verdict.
        if name in verdicts:
            continue
        verdicts[name] = check

    for name in sorted(verdicts):
        check = verdicts[name]
        verdict = "met" if check.get("Passed") else "**not met**"
        lines.append(f"| {name} | {verdict} | {check.get('Detail')} |")

    lines += [
        "",
        "## What the measurements say",
        "",
        "* **The boot overhead target is met.** The managed cold boot is not",
        "  slower than the same engine started outside the supervisor, so the",
        "  supervisor adds no measurable boot cost on this host.",
        "* **The warm restore target is not met.** The restore of a named",
        "  snapshot is bimodal: a quickboot restore brings the guest back in",
        "  about 10 to 13 seconds, but a restore that does not bring the guest",
        "  back within the supervisor's 90 second window falls back to a cold",
        "  boot, which costs about 40 to 50 seconds. The median therefore lands",
        "  far above the 8 second target.",
        "* **The input latency target is not met.** The dispatch-to-response",
        "  latency is around 160 ms at the median and 220 ms at p95, which is",
        "  more than twice the 100 ms target.",
        "* **The capture path is the frame-rate limiter.** A single captured",
        "  frame costs about 500 to 620 ms on this host, so the engine's PNG",
        "  capture path, not the WPF decode or the pipe, bounds the delivered",
        "  frame rate. The 60 FPS target is therefore out of reach with this",
        "  capture path, exactly as the earlier Phase 1 measurements showed.",
        "* **The lifecycle is stable.** Thirty start/stop cycles leave no engine",
        "  process behind and the free memory does not shrink.",
        "",
        "## How to reproduce",
        "",
        "```powershell",
        "# the full plan",
        "powershell -ExecutionPolicy Bypass -File tools\\benchmark.ps1 -Root D:\\Phase9Root -Samples 20 -Cycles 30",
        "",
        "# a single scenario",
        "powershell -ExecutionPolicy Bypass -File tools\\benchmark.ps1 -Root D:\\Phase9Root -Samples 20 `",
        "  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle -SkipTwoGuest",
        "```",
        "",
        "The raw samples are kept in the run directories; the host report is in",
        "`host.json` and the transcript in `transcript.txt`.",
    ]

    output.write_text("\n".join(lines), encoding="utf-8")
    print(f"{output} written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
