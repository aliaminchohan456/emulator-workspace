#!/usr/bin/env python3
"""Builds the sanitized Phase 3 evidence manifest.

The manifest is committed; the raw artifacts stay in the gitignored artifacts
directory. It records run ids, commits, pins, commands, per-lane check counts
and statuses, and the name plus SHA-256 of every artifact file so the raw
evidence can be verified without committing it.
"""

import hashlib
import json
import pathlib
import subprocess
import sys


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def git(*args: str) -> str:
    try:
        return subprocess.run(
            ["git", *args], capture_output=True, text=True, check=False
        ).stdout.strip()
    except Exception:
        return ""


def main() -> int:
    repo = pathlib.Path(__file__).resolve().parent.parent
    artifacts = repo / "artifacts" / "phase3"
    runs = sorted([p for p in artifacts.iterdir() if p.is_dir()])
    if not runs:
        print("no phase 3 runs found", file=sys.stderr)
        return 2

    selected = runs[-1] if len(sys.argv) < 2 else artifacts / sys.argv[1]
    summary_path = selected / "summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    versions_path = selected / "versions.json"
    versions = json.loads(versions_path.read_text(encoding="utf-8-sig")) if versions_path.exists() else {}

    lanes: dict[str, dict] = {}
    for check in summary.get("checks", []):
        lane = lanes.setdefault(check["Lane"], {"passed": 0, "failed": 0, "checks": []})
        lane["passed" if check["Passed"] else "failed"] += 1
        lane["checks"].append({
            "check": check["Check"],
            "passed": check["Passed"],
            "detail": check["Detail"][:160],
            "atUtc": check["AtUtc"],
        })

    files = []
    for path in sorted(selected.rglob("*")):
        if path.is_file() and path.name != "summary.json":
            files.append({
                "name": str(path.relative_to(selected)).replace("\\", "/"),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            })

    manifest = {
        "schemaVersion": 1,
        "generatedUtc": summary.get("finishedUtc"),
        "runId": summary.get("runId"),
        "rawArtifactRoot": str(selected),
        "rawArtifactsAreGitignored": True,
        "commit": versions.get("commit"),
        "headCommit": git("rev-parse", "--short", "HEAD"),
        "workTree": git("status", "--porcelain") or "clean",
        "pins": {
            "emulator": versions.get("emulator"),
            "imageRevision": versions.get("imageRevision"),
            "dotnet": versions.get("dotnet"),
        },
        "root": versions.get("root"),
        "freeBytesAtStart": versions.get("freeBytes"),
        "failedChecks": summary.get("failed"),
        "totalChecks": len(summary.get("checks", [])),
        "lanes": lanes,
        "artifacts": files,
    }

    out = repo / "docs" / "phase3-manifest.json"
    out.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"{out} written for run {summary.get('runId')} "
          f"({manifest['failedChecks']} failed / {manifest['totalChecks']} checks)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
