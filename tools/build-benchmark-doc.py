#!/usr/bin/env python3
"""Builds the published benchmark document from a measured benchmark run.

The document states the reference host, the method, every scenario with its
p50 and p95, and the verdict against each target of the plan. The numbers
come from the run itself, so the document cannot drift from the evidence.
"""

import json
import pathlib
import sys


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def scenario_row(name: str, data: dict, target: str) -> str:
    samples = data.get("samples", 0)
    if samples == 0:
        return f"| {name} | no valid samples | — | — | {target} | not measured |"
    return (f"| {name} | {samples} | {data.get('p50')} | {data.get('p95')} | "
            f"{target} | see the verdict |")


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: build-benchmark-doc.py <run-dir> <output.md>", file=sys.stderr)
        return 2

    run = pathlib.Path(sys.argv[1])
    output = pathlib.Path(sys.argv[2])

    summary = load(run / "summary.json")
    report = summary.get("benchmark", {})
    host = report.get("host", {})
    scenarios = report.get("scenarios", {})
    lifecycle = report.get("lifecycle") or {}
    checks = summary.get("checks", [])

    lines = [
        "# Benchmark results",
        "",
        "This document is generated from the measured run; the numbers cannot",
        "drift from the evidence. The raw samples, the host report and the",
        "transcript are in the run directory.",
        "",
        "## Reference host",
        "",
        "| Property | Value |",
        "| --- | --- |",
        f"| Run | `{summary.get('runId')}` |",
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
        "",
        "## Scenarios",
        "",
        "| Scenario | Valid samples | p50 | p95 | Target | Verdict |",
        "| --- | --- | --- | --- | --- | --- |",
    ]

    lines.append(scenario_row("Cold boot to interactive readiness (managed)",
                              scenarios.get("coldBoot", {}),
                              "overhead within the larger of 10% or 1 s over the same-engine baseline"))
    lines.append(scenario_row("Cold boot to interactive readiness (same-engine baseline)",
                              scenarios.get("baselineBoot", {}),
                              "the reference of the boot overhead"))
    lines.append(scenario_row("Warm restore to interactive readiness",
                              scenarios.get("warmRestore", {}),
                              "median within 8 s"))
    lines.append(scenario_row("Input latency, dispatch to the guest response (ms)",
                              scenarios.get("inputLatencyMs", {}),
                              "p95 at most 100 ms"))
    lines.append(scenario_row("Input latency with two guests (ms)",
                              scenarios.get("twoGuestLatencyMs", {}),
                              "p95 at most 100 ms"))
    lines.append(scenario_row("Capture path, one frame (ms)",
                              scenarios.get("captureMs", {}),
                              "the limiter of the delivered frame rate"))

    lines += [
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
        lines.append("The lifecycle scenario was not part of this run.")

    lines += ["", "## Verdict against the targets", "",
              "| Target | Result | Detail |", "| --- | --- | --- |"]
    for check in checks:
        if not str(check.get("Check", "")).startswith("target."):
            continue
        verdict = "met" if check.get("Passed") else "not met"
        lines.append(f"| {check.get('Check')} | {verdict} | {check.get('Detail')} |")

    lines += [
        "",
        "## How to reproduce",
        "",
        "```powershell",
        "powershell -ExecutionPolicy Bypass -File tools\\benchmark.ps1 -Root D:\\Phase9Root -Samples 20 -Cycles 30",
        "```",
        "",
        "The raw samples are kept in the run directory; the host report is in",
        "`host.json` and the transcript in `transcript.txt`.",
    ]

    output.write_text("\n".join(lines), encoding="utf-8")
    print(f"{output} written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
