# MCP clients

All clients use the supervisor's paired session; none receive engine endpoints,
controller tokens, raw ADB, root or `--direct`. The MCP server is a thin stdio
bridge: stdout is JSON-RPC only and diagnostics go to stderr.

## TypeScript SDK client (verified)

`mcp-client/workflow.mjs` uses the official `@modelcontextprotocol/sdk`
**1.32.2** and performs the full Phase 2 workflow:

1. initialize and `tools/list`;
2. `device_status` and `screen_capture`;
3. `ui_tree` and semantic login field/button discovery;
4. `ui_type` user/password, `ui_tap` login, `ui_assert` `WELCOME`;
5. `logs_read` and `artifact_read`;
6. `snapshot_save` and operator-approved `snapshot_restore`.

`mcp-client/secure-check.mjs` verifies the FLAG_SECURE path: screenshot/tree are
redacted, selector input is denied, no image content is returned, and capture
recovers after the operator clears the flag.

```powershell
cd clients\mcp-client
npm install
$env:WS_ROOT="D:\Phase2Root"
$env:WS_TOKEN_FILE="D:\Phase2Root\agent.json"
$env:WS_OUT="C:\Users\aliam\OneDrive\Desktop\Emulator\artifacts\phase2\mcp"
$env:WS_MCP_EXE="...\src\EmulatorWorkspace.Mcp\bin\Debug\net10.0\EmulatorWorkspace.Mcp.exe"
$env:WS_WSCTL="...\src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
node workflow.mjs
```

For the secure check also set `WS_ADB` and `WS_SERIAL`; it uses the operator's
ADB only to clear the fixture's secure screen after verifying the denial.

## Python SDK client (verified)

`mcp-python/client.py` uses the official `mcp` **2.3.0** package. It negotiates
protocol `2024-11-05`, lists the 19 supervisor tools and calls the harmless
`device_status` tool.

```powershell
py -3 -m pip install --user mcp
cd clients\mcp-python
$env:WS_ROOT="D:\Phase2Root"
$env:WS_TOKEN_FILE="D:\Phase2Root\agent.json"
$env:WS_OUT="C:\Users\aliam\OneDrive\Desktop\Emulator\artifacts\phase2\python-mcp"
$env:WS_MCP_EXE="...\src\EmulatorWorkspace.Mcp\bin\Debug\net10.0\EmulatorWorkspace.Mcp.exe"
py -3 client.py
```

## stdio configuration

`../mcp-config.example.json` is a Claude Desktop style configuration. It points
at the MCP server executable and passes the workspace root plus a token file;
the token itself is never placed in arguments or the config.

The server accepts `WS_AGENT_TOKEN_FILE` (preferred) or `WS_AGENT_TOKEN` from the
environment. The token file is the JSON object written by
`wsctl agent-pair --out ...`; only its `token` field is read.
