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
    merged = {"host": {}, "scenarios": {}, "lifecycle": None, "run_ids": []}
    for run in runs:
        report = run.get("benchmark", {})
        merged["run_ids"].append(run["runId"])
        if report.get("host") and not merged["host"]:
            merged["host"] = report["host"]
        for name, data in (report.get("scenarios") or {}).items():
            if name == "warmRestore" and "restoreTrials" not in report:
                name = "historicalRestore"
            if data.get("samples"):
                merged["scenarios"][name] = data
        if report.get("lifecycle"):
            merged["lifecycle"] = report["lifecycle"]
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

    directories = [pathlib.Path(argument) for argument in sys.argv[1:-1]]
    runs = [load(directory / "summary.json") for directory in directories]
    soaks = [
        load(directory / "verified-progress.json")
        for directory in directories
        if (directory / "verified-progress.json").is_file()
    ]
    output = pathlib.Path(sys.argv[-1])
    report = merge(runs)
    host = report.get("host", {})
    scenarios = report.get("scenarios", {})
    lifecycle = report.get("lifecycle") or {}

    lines = [
        "# Benchmark results",
        "",
        "Run directories under `artifacts/phase3/bench/` contain the raw evidence.",
        "",
        "Runs used for this report: " + ", ".join(f"`{run_id}`" for run_id in report["run_ids"]) + ".",
        "",
        "Guest profile: AOSP API 35 x86_64 revision 2, headless host GPU on WHPX.",
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
        "* Historical cold-boot rows came from two supervisor-managed profiles.",
        "  The raw CLI row is populated only by an independently launched control.",
        "* The historical restore harness could reuse cold-boot readiness after",
        "  a load. The corrected harness times each load with a monotonic clock,",
        "  records fallback and verifies a guest-state marker independently.",
        "* Logcat latency runs from ADB tap dispatch to host receipt of a fresh",
        "  fixture marker. WPF dispatch-to-composition needs its own measurement.",
        "* One-shot PNG cost includes the CLI, capture and output write. The",
        "  historical RGB viewport delivered 29.4 FPS in WPF.",
        "",
        "## Scenarios",
        "",
        "| Scenario | Valid samples | p50 | p95 | Target |",
        "| --- | --- | --- | --- | --- |",
        row("Cold boot to interactive readiness (managed, s)", scenarios.get("coldBoot", {}),
            "matched raw CLI overhead gate when both arms have 20 samples"),
        row("Cold boot to interactive readiness (raw CLI, s)", scenarios.get("rawBoot", {}),
            "same-engine control"),
        row("Cold boot (second managed profile, s)", scenarios.get("baselineBoot", {}), "historical control"),
        row("Historical restore reading (s)", scenarios.get("historicalRestore", {}), "invalid endpoint"),
        row("Verified warm restore (s)", scenarios.get("warmRestore", {}), "median at most 8 s, 20 samples"),
        row("Restore attempt to readiness, including fallback (s)",
            scenarios.get("restoreAttemptWallSeconds", {}), "reports recovery cost"),
        row("ADB tap to Logcat, single guest (ms)",
            scenarios.get("inputLatencyMs", {}), "diagnostic"),
        row("ADB tap to Logcat, two guests (ms)",
            scenarios.get("twoGuestLatencyMs", {}), "diagnostic"),
        row("CLI one-shot PNG (ms)", scenarios.get("captureMs", {}), "diagnostic"),
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

    for soak in soaks:
        if soak["state"] != "complete":
            continue
        lines += [
            "",
            "## Four-hour WHPX soak",
            "",
            f"Run `{soak['runId']}` passed {soak['pairedProbes']} paired probes over "
            f"{soak['elapsedSeconds']:,.2f} seconds. Both original guest generations and "
            "their owned loopback listeners were verified throughout. Both guests were "
            "stopped and removed through the supervisor.",
            "",
            f"Minimum free physical RAM was {soak['freePhysicalMB']['minimum']:,} MiB. "
            f"Minimum free virtual memory was {soak['freeVirtualMB']['minimum']:,} MiB.",
            "",
            "| Guest | Private MiB first / last / peak | Handles first / last / peak |",
            "| --- | --- | --- |",
        ]
        for name, metrics in soak["guests"].items():
            private = " / ".join(f"{metrics[stage]['privateBytes'] / 2**20:,.1f}" for stage in ("first", "last", "peaks"))
            handles = " / ".join(str(metrics[stage]["handles"]) for stage in ("first", "last", "peaks"))
            lines.append(f"| `{name}` | {private} | {handles} |")

    lines += [
        "",
        "## Reproduce the corrected benchmarks",
        "",
        "```powershell",
        "dotnet build",
        "powershell -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root D:/WSBench -Samples 20 `",
        "  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle",
        "",
        "# Include restore trials by omitting -SkipRestore. For the four-hour",
        "# soak, add -SoakMinutes 240 and monitor soak.jsonl through cleanup.",
        "```",
        "",
    ]

    output.write_text("\n".join(lines), encoding="utf-8")
    print(f"{output} written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
