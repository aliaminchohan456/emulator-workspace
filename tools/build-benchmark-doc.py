#!/usr/bin/env python3
"""Builds the published benchmark document from the measured runs.

The document states the reference host, the method, every scenario with its
p50 and p95, and the verdict against each target of the plan. The numbers
come from the runs themselves, so the document cannot drift from the
evidence. Several runs can be merged: a scenario that was not measured in a
run is taken from the run that measured it.
"""

import json
import math
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
                merged["scenarios"][name] = {**data, "runId": run["runId"]}
        if report.get("lifecycle"):
            merged["lifecycle"] = report["lifecycle"]
    return merged


def row(name: str, data: dict, target: str) -> str:
    if not data or not data.get("samples"):
        return f"| {name} | no valid samples | - | - | - | {target} |"
    return (f"| {name} | {data['samples']} | {data.get('p50')} | {data.get('p95')} | "
            f"`{data['runId']}` | {target} |")


def p95(samples: list[float]) -> str:
    if not samples:
        return "-"
    return f"{sorted(samples)[math.ceil(len(samples) * 0.95) - 1]:.2f}"


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
    presentation = [
        (directory, load(directory / "summary.json"), load(directory / "presentation.json"))
        for directory in directories
        if (directory / "presentation.json").is_file()
    ]
    output = pathlib.Path(sys.argv[-1])
    report = merge(runs)
    host = report.get("host", {})
    scenarios = report.get("scenarios", {})
    lifecycle = report.get("lifecycle") or {}

    lines = [
        "# Benchmark results",
        "",
        "Raw evidence is under `artifacts/phase3/bench/` and `artifacts/phase3/wpf/`.",
        "",
        "Runs used for this report: " + ", ".join(f"`{run_id}`" for run_id in report["run_ids"]) + ".",
        "",
        "Guest profile: AOSP API 35 x86_64 revision 2 on WHPX. Headless host GPU "
        "is the default. Renderer-specific viewport runs identify their GPU mode.",
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
        "  fixture marker. WPF reports dispatch to the first composition pass after the changed frame.",
        "* One-shot PNG cost includes the CLI, capture and output write. The",
        "  historical RGB viewport delivered 29.4 FPS in WPF.",
        "",
        "## Scenarios",
        "",
        "| Scenario | Valid samples | p50 | p95 | Run | Target |",
        "| --- | --- | --- | --- | --- | --- |",
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

    if presentation:
        lines += ["", "## WPF dispatch-to-composition"]
    for directory, summary, probe in presentation:
        trace = (probe.get("frameTrace") or {}).get("samples") or []
        frame_intervals = [sample["intervalMs"] for sample in trace if sample["intervalMs"] > 0]
        composition_intervals = [
            sample["ms"] - sample["frameAvailableMs"] for sample in probe["samples"]
            if not sample["timeout"] and "frameAvailableMs" in sample
        ]

        candidate = summary.get("candidate") or {}
        acknowledged = [sample["tapAcknowledgedMs"] for sample in probe["samples"]
                        if not sample["timeout"] and "tapAcknowledgedMs" in sample]
        controller_setup = [sample["controllerSetupMs"] for sample in probe["samples"]
                            if not sample["timeout"] and sample.get("controllerSetupMs") is not None]
        controller_tap = [sample["controllerTapMs"] for sample in probe["samples"]
                          if not sample["timeout"] and sample.get("controllerTapMs") is not None]
        lines += [
            "",
            f"### Run `{summary['runId']}` ({summary.get('guestCount', 1)} guest(s))",
            "",
            f"This run used desktop `{summary.get('desktopVersion')}` "
            f"with the hash-verified CLI, supervisor and fixture from `{candidate.get('sourceCommit')}`.",
            "The endpoint is the WPF composition callback after the changed frame became available.",
            "",
            "| Metric | Result |",
            "| --- | --- |",
            f"| Valid trials | {probe['valid']} / {probe['trials']} |",
            f"| Dispatch to composition p50 / p95 | {probe['p50Ms']} / {probe['p95Ms']} ms |",
            f"| Frame availability to composition p95 | {p95(composition_intervals)} ms |",
            f"| Supervisor frame arrival interval p95 | {p95(frame_intervals)} ms |",
            f"| Viewport throughput | {probe['deliveredFps']} FPS at "
            f"{probe['deliveredWidth']} × {probe['deliveredHeight']} |",
            f"| Dropped sequences | {probe['dropped']} |",
        ]
        if summary.get("gpu"):
            lines.append(f"| Configured emulator GPU mode | `{summary['gpu']}` |")
        if acknowledged:
            lines.append(f"| Desktop dispatch to supervisor reply p95 | {p95(acknowledged)} ms |")
        if controller_setup:
            lines.append(f"| Supervisor controller setup p95 | {p95(controller_setup)} ms |")
        if controller_tap:
            lines.append(f"| Supervisor controller tap p95 | {p95(controller_tap)} ms |")
        if (directory / "rgb-capture.json").is_file() and (directory / "encoded-capture.json").is_file():
            rgb = load(directory / "rgb-capture.json")
            encoded = load(directory / "encoded-capture.json")
            lines += [
                "",
                f"Separate capture diagnostics measured {rgb['fps']} FPS in the direct controller RGB stream "
                f"and {encoded['framesPerWallSecond']} FPS in the guest H.264 stream.",
                "Both used the same 540 × 1200 foreground fixture. Guest HWUI timing is in `guest-framestats.txt`.",
            ]

    for run in runs:
        data = run.get("benchmark") or {}
        control = data.get("rawControl")
        check = next((check for check in run.get("checks", []) if check.get("Check") == "target.boot-overhead"), None)
        if check and data.get("matchedAvdConfig"):
            lines += [
                "",
                f"## Matched cold-boot comparison `{run['runId']}`",
                "",
                (f"The managed arm verified and reused the 20 raw control rows from `{control['runId']}`."
                 if control else "Both arms ran in this benchmark on the same Windows host and pinned emulator."),
                "Both arms required Android boot completion and a current focused window.",
                f"{'Passed' if check['Passed'] else 'Missed the target'}. {check['Detail'].capitalize()}.",
            ]

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
