#!/usr/bin/env python3
"""Builds the sanitized Phase 3 evidence manifest.

The manifest is committed; the raw artifacts stay in the gitignored artifacts
directory. It records run ids, commits, pins, per-lane check counts and the
name plus SHA-256 of every artifact file so the raw evidence can be verified
without committing it.
"""

import hashlib
import json
import pathlib
import subprocess
import sys
from functools import lru_cache


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


def read_json(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def lanes_of(summary: dict) -> dict:
    lanes: dict[str, dict] = {}
    for check in summary.get("checks", []):
        name = check.get("Check") or check.get("name")
        passed = check.get("Passed", check.get("passed"))
        lane_name = check.get("Lane") or name or "check"
        lane = lanes.setdefault(lane_name, {"passed": 0, "failed": 0, "checks": []})
        lane["passed" if passed else "failed"] += 1
        lane["checks"].append({
            "check": name,
            "passed": passed,
            "detail": (check.get("Detail") or check.get("detail") or "")[:160],
            "atUtc": check.get("AtUtc"),
        })
    return lanes


def files_of(directory: pathlib.Path) -> list:
    files = []
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            files.append({
                "name": str(path.relative_to(directory)).replace("\\", "/"),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            })
    return files


@lru_cache(maxsize=None)
def installer_provenance(installer: pathlib.Path, expected_hash: str) -> dict | None:
    release_path = installer.with_name("release.json")
    if not installer.is_file() or not release_path.is_file():
        return None
    release = read_json(release_path)
    if release.get("installerSha256") != expected_hash or sha256(installer) != expected_hash:
        return None
    return {
        "sourceCommit": release.get("commit"),
        "installerSha256": expected_hash,
        "signingStatus": release.get("signingStatus"),
        "evidence": "verified MSI and matching release record",
    }


def verified_harness_commit(directory: pathlib.Path, record: dict, script: str) -> str | None:
    context_path = directory / "run-context.json"
    expected = record.get("harnessSha256")
    if not expected:
        return None
    context = read_json(context_path) if context_path.is_file() else {}
    commit = context.get("harnessCommit") or record.get("sourceCommit")
    if not commit or not all(c in "0123456789abcdef" for c in commit) or len(commit) < 7:
        return None
    result = subprocess.run(["git", "show", f"{commit}:{script}"], capture_output=True, check=False)
    if result.returncode != 0:
        return None
    content = result.stdout
    windows_checkout = content.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    if any(hashlib.sha256(variant).hexdigest() == expected.lower() for variant in (content, windows_checkout)):
        return commit
    return None


def collect(directory: pathlib.Path, kind: str, repo: pathlib.Path) -> dict | None:
    summary_path = directory / "summary.json"
    if not summary_path.exists():
        return None
    summary = read_json(summary_path)
    versions_path = directory / "versions.json"
    versions = read_json(versions_path) if versions_path.exists() else {}
    benchmark = summary.get("benchmark") or {}
    host = benchmark.get("host") or summary.get("host") or {}
    lanes = lanes_of(summary)
    candidate = benchmark.get("candidate") or summary.get("candidate")
    candidate_path = directory / "candidate.json"
    if not candidate and candidate_path.exists():
        candidate = read_json(candidate_path)
    if summary.get("installer") and summary.get("installerSha256"):
        installed_hash = summary["installerSha256"]
        release = installer_provenance(pathlib.Path(summary["installer"]), installed_hash)
        if release:
            candidate = release
        elif not candidate or candidate.get("installerSha256") != installed_hash:
            candidate = {
                "installerSha256": installed_hash,
                "evidence": "recorded in acceptance summary",
            }
    source_commit = versions.get("commit") or benchmark.get("sourceCommit") or summary.get("sourceCommit")
    harness_commit = source_commit
    if benchmark.get("harnessSha256"):
        harness_commit = verified_harness_commit(directory, benchmark, "tools/benchmark.ps1")
    elif kind == "wpf" and summary.get("harnessSha256"):
        harness_commit = verified_harness_commit(directory, summary, "tools/latency-probe.ps1")
    elif kind == "candidate" and summary.get("harnessSha256"):
        harness_commit = verified_harness_commit(directory, summary, "tools/candidate-regression.ps1")
    return {
        "kind": kind,
        "runId": summary.get("runId"),
        "rawArtifactRoot": directory.relative_to(repo).as_posix(),
        "rawArtifactsAreGitignored": True,
        "commit": (candidate or {}).get("sourceCommit") or source_commit,
        "harnessCommit": harness_commit,
        "harnessSha256": benchmark.get("harnessSha256") or summary.get("harnessSha256"),
        "candidate": candidate,
        **({"desktop": {"sha256": summary["desktopSha256"], "assemblySha256": summary.get("desktopAssemblySha256"),
                        "version": summary.get("desktopVersion"),
                        "override": summary.get("desktopOverride")}} if summary.get("desktopSha256") else {}),
        "finishedUtc": summary.get("finishedUtc"),
        "pins": {
            "emulator": versions.get("emulator") or host.get("emulatorVersion") or host.get("emulator"),
            "image": summary.get("image"),
            "imageRevision": versions.get("imageRevision") or summary.get("revision"),
            "dotnet": versions.get("dotnet") or host.get("dotnetVersion"),
        },
        "root": versions.get("root") or summary.get("root"),
        "freeBytesAtStart": versions.get("freeBytes"),
        "failedChecks": sum(lane["failed"] for lane in lanes.values()),
        "totalChecks": len(summary.get("checks", [])),
        "lanes": lanes,
        "artifacts": files_of(directory),
    }


def main() -> int:
    repo = pathlib.Path(__file__).resolve().parent.parent
    artifacts = repo / "artifacts" / "phase3"
    runs = []

    for directory in sorted([p for p in artifacts.iterdir() if p.is_dir()]):
        entry = collect(directory, "phase3", repo)
        if entry is not None:
            runs.append(entry)

    for kind in ("recipe", "import", "rootlab", "wpf", "devtools", "install", "bench", "play", "offline", "candidate"):
        root = artifacts / kind
        if not root.exists():
            continue
        for directory in sorted([p for p in root.iterdir() if p.is_dir()]):
            entry = collect(directory, kind, repo)
            if entry is not None:
                runs.append(entry)

    if not runs:
        print("no phase 3 runs found", file=sys.stderr)
        return 2

    manifest = {
        "schemaVersion": 1,
        "latestFinishedUtc": max((run["finishedUtc"] for run in runs if run["finishedUtc"]), default=None),
        "headCommit": git("rev-parse", "--short", "HEAD"),
        "trackedWorkTree": git("status", "--porcelain", "--untracked-files=no") or "clean",
        "runs": runs,
    }
    out = repo / "docs" / "phase3-manifest.json"
    out.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for run in runs:
        print(f"{run['kind']} {run['runId']}: {run['failedChecks']} failed / {run['totalChecks']} checks")
    print(f"{out} written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
