#!/usr/bin/env python3
"""Time the fixture workflow on a single, explicitly authorized test emulator."""

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import time

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def compare(args: argparse.Namespace, result: dict) -> None:
    def verify_target() -> None:
        listing = subprocess.run(
            [str(args.adb), "devices", "-l"], capture_output=True, text=True, check=True, timeout=15,
        ).stdout
        devices = [row.split()[:2] for row in listing.splitlines()[1:] if row.strip()]
        if devices != [[args.serial, "device"]]:
            raise RuntimeError("Expected the owned test emulator to be the only attached device")
        avd = subprocess.run(
            [str(args.adb), "-s", args.serial, "emu", "avd", "name"],
            capture_output=True, text=True, check=True, timeout=15,
        ).stdout.splitlines()
        if not avd or avd[0].strip() != args.avd:
            raise RuntimeError("The selected serial no longer identifies the expected test AVD")

    verify_target()
    server = StdioServerParameters(
        command=shutil.which("node") or "node",
        args=[str(args.server), "--stdio"],
        env={
            "ANDROID_HOME": str(args.adb.parent.parent),
            "ANDROID_SDK_ROOT": str(args.adb.parent.parent),
            "MOBILEMCP_DISABLE_TELEMETRY": "1",
            "MOBILEWRIGHT_DISABLE_TELEMETRY": "1",
        },
    )
    async with stdio_client(server) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            initialized = await asyncio.wait_for(session.initialize(), 20)
            result["server"] = initialized.serverInfo.model_dump()
            if initialized.serverInfo.version != "1.0.17":
                raise RuntimeError("The comparison requires Mobile MCP 1.0.17")

            async def call(name: str, **arguments) -> str:
                verify_target()
                started = time.perf_counter()
                row = {"tool": name, "passed": False}
                result["calls"].append(row)
                try:
                    reply = await asyncio.wait_for(
                        session.call_tool(name, {"device": args.serial, **arguments}), 60,
                    )
                    content = "\n".join(item.text for item in reply.content if item.type == "text")
                    row["result"] = content
                    if reply.isError:
                        raise RuntimeError(f"{name} failed")
                    row["passed"] = True
                    return content
                finally:
                    row["milliseconds"] = round((time.perf_counter() - started) * 1000, 2)

            await call("mobile_install_app", path=str(args.apk))
            await call("mobile_launch_app", packageName="dev.emulatorworkspace.sample")
            prefix = "Found these elements on screen: "

            async def hierarchy() -> list[dict]:
                tree = await call("mobile_list_elements_on_screen", format="json")
                if not tree.startswith(prefix):
                    raise RuntimeError("Unexpected hierarchy response")
                return json.loads(tree[len(prefix):])

            for attempt in range(6):
                initial = await hierarchy()
                if any(node.get("text") == "DUP_TAPS=0" for node in initial):
                    break
                if attempt < 5:
                    await asyncio.sleep(0.5)
            else:
                raise RuntimeError("Fixture never displayed the expected initial marker")

            buttons = [node for node in initial if str(node.get("text")).upper() == "DUPLICATE A"]
            if len(buttons) != 1 or not re.fullmatch(r"@e\d+", buttons[0].get("ref") or ""):
                raise RuntimeError("Could not uniquely identify the fixture button")
            await call("mobile_click_on_screen_at_coordinates", ref=buttons[0]["ref"])
            updated = await hierarchy()
            if not any(node.get("text") == "DUP_TAPS=1" for node in updated):
                raise RuntimeError("Fixture did not display the expected post-tap marker")
            result["marker"] = {"before": "DUP_TAPS=0", "after": "DUP_TAPS=1"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--apk", required=True, type=Path)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--avd", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r"emulator-[0-9]{4,5}", args.serial):
        parser.error("--serial must identify an emulator")
    for key in ("adb", "server", "apk"):
        setattr(args, key, getattr(args, key).resolve(strict=True))
    if args.output.exists():
        parser.error("--output already exists")
    lock = args.server.parents[4] / "package-lock.json"
    version = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=Path(__file__).resolve().parent.parent,
        capture_output=True, text=True, check=True, timeout=10,
    ).stdout.strip()
    result = {
        "harnessCommit": version,
        "harnessSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "serial": args.serial,
        "avd": args.avd,
        "fixtureSha256": hashlib.sha256(args.apk.read_bytes()).hexdigest(),
        "lockSha256": hashlib.sha256(lock.read_bytes()).hexdigest(),
        "calls": [],
        "passed": False,
    }
    try:
        asyncio.run(compare(args, result))
        result["passed"] = True
    except Exception as error:
        result["error"] = str(error)
    finally:
        with args.output.open("x", encoding="utf-8") as output:
            json.dump(result, output, indent=2)
    return 0 if result["passed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
