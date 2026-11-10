#!/usr/bin/env python3
"""Real MCP stdio client using the official Python SDK (mcp 2.3.0).

Verifies initialize, tools/list and a harmless device_status call against the
same local supervisor. Writes a machine-readable transcript to WS_OUT.
"""

import asyncio
import json
import os
import pathlib
import sys

from mcp import ClientSession, Implementation, StdioServerParameters
from mcp.client.stdio import stdio_client


async def main() -> int:
    root = os.environ["WS_ROOT"]
    token_file = os.environ["WS_TOKEN_FILE"]
    server_exe = os.environ["WS_MCP_EXE"]
    out_dir = pathlib.Path(os.environ.get("WS_OUT", "artifacts/phase2/python-mcp"))
    out_dir.mkdir(parents=True, exist_ok=True)

    session_record = json.loads(pathlib.Path(token_file).read_text(encoding="utf-8"))
    env = dict(os.environ)
    env["EMULATOR_WORKSPACE_HOME"] = root
    env["WS_AGENT_TOKEN"] = session_record["token"]

    params = StdioServerParameters(command=server_exe, args=[], env=env)
    transcript = {
        "client": "mcp-python-sdk",
        "sdk": "mcp 2.3.0",
        "session": session_record["session"],
        "instance": session_record["instance"],
        "clientName": "ws-phase2-python",
        "calls": [],
    }

    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write, client_info=Implementation(name="ws-phase2-python", version="1.0.0")) as client:
            init = await client.initialize()
            transcript["negotiatedProtocol"] = init.protocol_version
            transcript["serverInfo"] = (
                init.server_info.model_dump() if hasattr(init.server_info, "model_dump") else str(init.server_info)
            )
            tools = await client.list_tools()
            transcript["tools"] = [tool.name for tool in tools.tools]

            result = await client.call_tool("device_status", {})
            text = "\n".join(
                item.text for item in result.content if getattr(item, "type", None) == "text"
            )
            try:
                parsed = json.loads(text)
            except json.JSONDecodeError:
                parsed = {"raw": text}
            transcript["calls"].append(
                {
                    "tool": "device_status",
                    "isError": bool(result.is_error),
                    "result": parsed,
                }
            )

    transcript["ok"] = (
        transcript["negotiatedProtocol"] is not None
        and "device_status" in transcript["tools"]
        and not transcript["calls"][0]["isError"]
        and transcript["calls"][0]["result"].get("state") == "Running"
    )
    (out_dir / "python-mcp.json").write_text(json.dumps(transcript, indent=2), encoding="utf-8")
    print(json.dumps({"ok": transcript["ok"], "protocol": transcript["negotiatedProtocol"], "tools": len(transcript["tools"])}))
    return 0 if transcript["ok"] else 2


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
