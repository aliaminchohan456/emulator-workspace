# MCP clients

## TypeScript SDK client (verified)

`mcp-client/workflow.mjs` uses the official `@modelcontextprotocol/sdk`
**1.32.2** and performs the Phase 2 workflow over stdio:

1. initialize and tools/list;
2. device_status and screen_capture;
3. ui_tree and login field/button selectors;
4. ui_type user/password, ui_tap login, ui_assert `WELCOME`;
5. logs_read and artifact_read;
6. snapshot_save and snapshot_restore with an operator approval code.

Run:

```powershell
cd clients\mcp-client
npm install
$env:WS_ROOT="D:\Phase2Root"
$env:WS_TOKEN_FILE="D:\Phase2Root\cli-session.json"
$env:WS_OUT="C:\Users\aliam\OneDrive\Desktop\Emulator\artifacts\phase2\mcp"
$env:WS_MCP_EXE="C:\Users\aliam\OneDrive\Desktop\Emulator\src\EmulatorWorkspace.Mcp\bin\Debug\net10.0\EmulatorWorkspace.Mcp.exe"
$env:WS_WSCTL="C:\Users\aliam\OneDrive\Desktop\Emulator\src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
node workflow.mjs
```

The transcript is written to `mcp-transcript.json` in `WS_OUT`.

## stdio configuration

`../mcp-config.example.json` is a Claude Desktop style configuration. It points
at the MCP server executable and passes the workspace root plus a token file;
the token itself is never placed in arguments or the config.

## Python client

Not delivered in this pass. The supervisor contract is language-neutral
(length-prefixed JSON over the local pipe) and the MCP server is stdio JSON-RPC,
so a Python client can use the official `mcp` package or a small JSON-RPC
driver. This remains an open acceptance item.
