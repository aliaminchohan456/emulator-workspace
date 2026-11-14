"""Verify the extracted Linux handoff without executing its binaries or scripts."""
import argparse
import hashlib
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    root = args.bundle.resolve(strict=True)
    checks = []
    seen = set()
    for line in (root / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        expected, name = line.split(maxsplit=1)
        if len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
            raise ValueError("Invalid SHA-256 entry")
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts or ":" in name:
            raise ValueError("Unsafe manifest path: " + name)
        path = (root / relative).resolve(strict=True)
        if not path.is_relative_to(root) or str(path).casefold() in seen:
            raise ValueError("Escaping or duplicate manifest path: " + name)
        seen.add(str(path).casefold())
        with path.open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        checks.append({"path": name, "expected": expected, "actual": actual,
                       "passed": actual == expected})
    if not checks:
        raise ValueError("Empty inventory")
    report = {"bundle": str(root), "files": len(checks),
              "passed": all(c["passed"] for c in checks), "checks": checks}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"{len(checks)} files; passed={report['passed']}; report={args.report}")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
