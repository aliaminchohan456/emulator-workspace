#!/usr/bin/env python3
"""Read paired WHPX soak progress without changing the running guests."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def progress(run: Path) -> dict:
    summary_path = run / "summary.json"
    summary = load(summary_path) if summary_path.exists() else None
    heartbeats = run / "soak.jsonl"
    observations = []
    if heartbeats.exists():
        lines = heartbeats.read_text(encoding="utf-8-sig").splitlines()
        for index, line in enumerate(lines):
            try:
                observations.append(json.loads(line))
            except json.JSONDecodeError:
                if summary is not None or index != len(lines) - 1:
                    raise

    details = {"runId": run.name, "state": "starting", "pairedProbes": len(observations)}
    if not observations:
        if summary:
            details["state"] = "ended-before-first-probe"
        return details

    elapsed = [row["elapsedSeconds"] for row in observations]
    if elapsed != sorted(elapsed):
        raise ValueError("Soak elapsed times are out of order")
    by_guest = {}
    generations = {}
    for row in observations:
        if len(row["guests"]) != 2:
            raise ValueError("A heartbeat did not observe two guests")
        for guest in row["guests"]:
            identity = guest["identity"]
            name = identity["name"]
            generation = (
                identity["identity"],
                sorted((process["pid"], process["startUtc"]) for process in guest["processes"]),
            )
            if generations.setdefault(name, generation) != generation:
                raise ValueError(f"Guest generation changed during the soak: {name}")
            owned_pids = {process["pid"] for process in guest["processes"]}
            ports = {
                listener["LocalPort"] for listener in guest["listeners"]
                if listener["OwningProcess"] in owned_pids and listener["LocalAddress"] in {"127.0.0.1", "::1"}
            }
            if not {identity["consolePort"], identity["consolePort"] + 1, identity["grpcPort"]} <= ports:
                raise ValueError(f"Missing owned loopback listener for {name}")
            by_guest.setdefault(name, []).append({
                "privateBytes": sum(process["privateBytes"] for process in guest["processes"]),
                "workingSetBytes": sum(process["workingSetBytes"] for process in guest["processes"]),
                "handles": sum(process["handles"] for process in guest["processes"]),
            })
    if len(generations) != 2:
        raise ValueError("The soak did not observe two distinct guest identities")

    cleanups = {
        check["Check"].removeprefix("cleanup."): check["Passed"]
        for check in (summary or {}).get("checks", [])
        if check["Check"].startswith("cleanup.")
    }
    expected = ((summary or {}).get("benchmark", {}).get("soak") or {}).get("minutes", 0)
    complete = bool(
        summary and not summary["failed"] and expected == 240 and elapsed[-1] >= 14_400
        and all(cleanups.get(name) is True for name in generations)
    )
    memory = [row["memory"]["freePhysicalMB"] for row in observations]
    virtual = [row["memory"]["freeVirtualMB"] for row in observations]
    details.update({
        "state": "complete" if complete else ("ended" if summary else "active"),
        "elapsedSeconds": elapsed[-1],
        "lastProbeUtc": observations[-1]["atUtc"],
        "freePhysicalMB": {"minimum": min(memory), "latest": memory[-1]},
        "freeVirtualMB": {"minimum": min(virtual), "latest": virtual[-1]},
        "guests": {
            name: {
                "first": values[0],
                "last": values[-1],
                "peaks": {metric: max(row[metric] for row in values) for metric in values[0]},
                "cleanupPassed": cleanups.get(name),
            }
            for name, values in by_guest.items()
        },
    })
    return details


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path, help="benchmark evidence directory")
    parser.add_argument("--require-complete", action="store_true")
    args = parser.parse_args()
    if not args.run.is_dir():
        parser.error(f"missing evidence directory: {args.run}")
    result = progress(args.run)
    print(json.dumps(result, indent=2))
    return int(args.require_complete and result["state"] != "complete")


if __name__ == "__main__":
    raise SystemExit(main())
