// MCP secure-window check. Activates the sample FLAG_SECURE screen through the
// agent, verifies that MCP returns a redacted typed result with no image
// content, then clears the flag as the local operator (raw ADB) and verifies
// that capture recovers. Writes a machine-readable transcript.
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.env.WS_ROOT;
const tokenFile = process.env.WS_TOKEN_FILE;
const serverExe = process.env.WS_MCP_EXE;
const adb = process.env.WS_ADB;
const serial = process.env.WS_SERIAL;
const outDir = process.env.WS_OUT ?? "artifacts/phase2/secure";
if (!root || !tokenFile || !serverExe || !adb || !serial) {
  throw new Error("WS_ROOT, WS_TOKEN_FILE, WS_MCP_EXE, WS_ADB and WS_SERIAL are required");
}

const session = JSON.parse(fs.readFileSync(tokenFile, "utf8"));
fs.mkdirSync(outDir, { recursive: true });
const transcript = {
  client: "mcp-typescript-sdk",
  sdk: "1.32.2",
  session: session.session,
  instance: session.instance,
  serial,
  startedUtc: new Date().toISOString(),
  calls: [],
};

const transport = new StdioClientTransport({
  command: serverExe,
  args: [],
  env: {
    ...process.env,
    EMULATOR_WORKSPACE_HOME: root,
    WS_AGENT_TOKEN: session.token,
  },
  stderr: "inherit",
});

const client = new Client({ name: "ws-phase2-secure", version: "1.0.0" }, { capabilities: {} });

async function call(name, args) {
  const result = await client.callTool({ name, arguments: args ?? {} });
  const text = (result.content ?? [])
    .filter((item) => item.type === "text")
    .map((item) => item.text)
    .join("\n");
  const images = (result.content ?? []).filter((item) => item.type === "image");
  let parsed = null;
  try {
    parsed = JSON.parse(text);
  } catch {
    parsed = { raw: text };
  }
  transcript.calls.push({
    tool: name,
    args,
    at: new Date().toISOString(),
    isError: result.isError === true,
    result: parsed,
    imageParts: images.length,
    imageBytes: images.length ? images[0].data.length : 0,
  });
  return { parsed, isError: result.isError === true, images };
}

function operatorTap(x, y) {
  execFileSync(adb, ["-s", serial, "shell", "input", "tap", String(x), String(y)], {
    encoding: "utf8",
  });
}

await client.connect(transport);
const tools = await client.listTools();
transcript.tools = tools.tools.map((tool) => tool.name);

const tap = await call("ui_tap", { selector: { contentDescription: "secure_button" } });
if (tap.isError) throw new Error("could not activate the secure fixture");
await new Promise((resolve) => setTimeout(resolve, 500));

const secureShot = await call("screen_capture");
const secureTree = await call("ui_tree");
const secureAssert = await call("ui_assert", {
  selector: { contentDescription: "login_status" },
});

const screenshotRedacted =
  secureShot.parsed.status === "secure_window" &&
  secureShot.parsed.secure === true &&
  secureShot.images.length === 0;
const treeRedacted =
  secureTree.parsed.status === "secure_window" &&
  secureTree.parsed.secure === true &&
  Array.isArray(secureTree.parsed.nodes) &&
  secureTree.parsed.nodes.length === 0;
const selectorDenied =
  secureAssert.isError === true && secureAssert.parsed.code === "secure_window";

// The operator clears the secure flag; the agent path is intentionally unable
// to observe or target the protected screen.
operatorTap(502, 581);
await new Promise((resolve) => setTimeout(resolve, 800));
const recovered = await call("screen_capture");
const recoveredOk =
  recovered.parsed.secure === false &&
  recovered.parsed.status !== "secure_window" &&
  recovered.images.length === 1;

transcript.checks = {
  screenshotRedacted,
  treeRedacted,
  selectorDenied,
  recoveredOk,
};
transcript.finishedUtc = new Date().toISOString();
fs.writeFileSync(path.join(outDir, "mcp-secure.json"), JSON.stringify(transcript, null, 2));
console.log(JSON.stringify(transcript.checks));
await client.close();
if (!screenshotRedacted || !treeRedacted || !selectorDenied || !recoveredOk) {
  process.exitCode = 2;
}
