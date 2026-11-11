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


def read_json(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def lanes_of(summary: dict) -> dict:
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
    return lanes


def files_of(directory: pathlib.Path) -> list:
    files = []
    for path in sorted(directory.rglob("*")):
        if path.is_file() and path.name != "summary.json":
            files.append({
                "name": str(path.relative_to(directory)).replace("\\", "/"),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            })
    return files


def main() -> int:
    repo = pathlib.Path(__file__).resolve().parent.parent
    artifacts = repo / "artifacts" / "phase3"
    runs = []
    for directory in sorted([p for p in artifacts.iterdir() if p.is_dir()]):
        summary_path = directory / "summary.json"
        if not summary_path.exists():
            continue
        summary = read_json(summary_path)
        versions_path = directory / "versions.json"
        versions = read_json(versions_path) if versions_path.exists() else {}
        runs.append({
            "kind": "phase3",
            "runId": summary.get("runId"),
            "rawArtifactRoot": str(directory),
            "rawArtifactsAreGitignored": True,
            "commit": versions.get("commit"),
            "pins": {
                "emulator": versions.get("emulator"),
                "imageRevision": versions.get("imageRevision"),
                "dotnet": versions.get("dotnet"),
            },
            "root": versions.get("root"),
            "freeBytesAtStart": versions.get("freeBytes"),
            "failedChecks": summary.get("failed"),
            "totalChecks": len(summary.get("checks", [])),
            "lanes": lanes_of(summary),
            "artifacts": files_of(directory),
        })

    recipe_root = artifacts / "recipe"
    if recipe_root.exists():
        for directory in sorted([p for p in recipe_root.iterdir() if p.is_dir()]):
            summary_path = directory / "summary.json"
            if not summary_path.exists():
                continue
            summary = read_json(summary_path)
            versions_path = directory / "versions.json"
            versions = read_json(versions_path) if versions_path.exists() else {}
            runs.append({
                "kind": "recipe",
                "runId": summary.get("runId"),
                "rawArtifactRoot": str(directory),
                "rawArtifactsAreGitignored": True,
                "commit": versions.get("commit"),
                "pins": {"dotnet": versions.get("dotnet")},
                "root": versions.get("root"),
                "freeBytesAtStart": versions.get("freeBytes"),
                "failedChecks": summary.get("failed"),
                "totalChecks": len(summary.get("checks", [])),
                "lanes": lanes_of(summary),
                "artifacts": files_of(directory),
            })

    if not runs:
        print("no phase 3 runs found", file=sys.stderr)
        return 2

    manifest = {
        "schemaVersion": 1,
        "generatedUtc": runs[-1].get("finishedUtc"),
        "headCommit": git("rev-parse", "--short", "HEAD"),
        "workTree": git("status", "--porcelain") or "clean",
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
