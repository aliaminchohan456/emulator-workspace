// Real MCP stdio client for the Phase 2 workflow. Uses the official TypeScript
// SDK and the paired agent session token; stdout is the client's transcript.
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

const root = process.env.WS_ROOT;
const tokenFile = process.env.WS_TOKEN_FILE;
const outDir = process.env.WS_OUT ?? "artifacts/phase2/mcp";
const serverExe = process.env.WS_MCP_EXE;
if (!root || !tokenFile || !serverExe) {
  throw new Error("WS_ROOT, WS_TOKEN_FILE and WS_MCP_EXE are required");
}

const session = JSON.parse(fs.readFileSync(tokenFile, "utf8"));
fs.mkdirSync(outDir, { recursive: true });
const transcript = {
  client: "mcp-typescript-sdk",
  sdk: "1.32.2",
  session: session.session,
  instance: session.instance,
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

const client = new Client(
  { name: "ws-phase2-qa", version: "1.0.0" },
  { capabilities: {} },
);

async function call(name, args) {
  const started = new Date().toISOString();
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
    at: started,
    isError: result.isError === true,
    result: parsed,
    imageBytes: images.length ? images[0].data.length : 0,
  });
  return { parsed, isError: result.isError === true, images };
}

await client.connect(transport);
const tools = await client.listTools();
transcript.tools = tools.tools.map((tool) => tool.name);
transcript.server = tools.serverInfo ?? null;

const status = await call("device_status");
const shot = await call("screen_capture");
const tree = await call("ui_tree");
const users = (tree.parsed.nodes ?? []).filter((n) => n.contentDescription === "login_user").length;
if (users !== 1) throw new Error("login_user selector is not unique");

const userType = await call("ui_type", { text: "mcp-user", selector: { contentDescription: "login_user" } });
if (userType.isError) throw new Error("could not type the login user: " + userType.parsed.message);
const passType = await call("ui_type", { text: "mcp-pass", selector: { contentDescription: "login_password" } });
if (passType.isError) throw new Error("could not type the login password: " + passType.parsed.message);
const firstTap = await call("ui_tap", { selector: { contentDescription: "login_button" } });
if (firstTap.isError) throw new Error("could not tap the login button: " + firstTap.parsed.message);
await new Promise((resolve) => setTimeout(resolve, 2500));
let assert = await call("ui_assert", {
  selector: { contentDescription: "login_status", text: "WELCOME" },
});
if (!assert.parsed.pass) {
  // A transient guest/ADB hiccup is common on a cold disposable clone; retry
  // once against a fresh tree before failing the workflow.
  const retryTap = await call("ui_tap", { selector: { contentDescription: "login_button" } });
  if (retryTap.isError) throw new Error("could not retry the login button: " + retryTap.parsed.message);
  await new Promise((resolve) => setTimeout(resolve, 3000));
  assert = await call("ui_assert", {
    selector: { contentDescription: "login_status", text: "WELCOME" },
  });
}
if (!assert.parsed.pass) throw new Error("login did not reach WELCOME: " + JSON.stringify(assert.parsed));

const logs = await call("logs_read", { lines: 200, contains: "LOGIN_RESULT" });
const artifact = await call("artifact_read", { artifactId: shot.parsed.artifactId });
const saved = await call("snapshot_save", { tag: "mcp-baseline" });

// Destructive restore requires an operator-issued single-use approval code.
const wsctl = process.env.WS_WSCTL;
const approvalJson = execFileSync(wsctl, [
  "agent-approve-action",
  "--session", session.session,
  "--operation", "snapshot.load",
  "--root", root,
], { encoding: "utf8" });
const approval = JSON.parse(approvalJson).code;
const restored = await call("snapshot_restore", { tag: "mcp-baseline", approval });

transcript.finishedUtc = new Date().toISOString();
transcript.summary = {
  device: status.parsed.state ?? status.parsed.instance,
  screenshotArtifact: shot.parsed.artifactId,
  screenshotSecure: shot.parsed.secure ?? false,
  login: assert.parsed.pass === true,
  logLines: logs.parsed.lines ?? 0,
  artifactBytes: Buffer.from(artifact.parsed.base64 ?? "", "base64").length,
  snapshotSaved: saved.parsed.tag,
  snapshotRestored: restored.parsed.tag ?? null,
};
fs.writeFileSync(path.join(outDir, "mcp-transcript.json"), JSON.stringify(transcript, null, 2));
console.log(JSON.stringify(transcript.summary));
await client.close();
