# Phase 2 evidence — local agent integration

Baseline: clean commit **7f1b0ed**. Build 0 errors; **23 tests pass**. Pins
unchanged (Emulator 37.2.12.0/16428233, vendored protos, AOSP API 35 x86_64 rev
2, WHPX). Historical Phase 1 runs remain: original **18/19**
(`20261110-172411`), stabilization **19/19** (`20261110-183709`), Phase 0
regression **26/26** (`run-20261110-174228`).

Test root: `D:\Phase2Root`. Template `tpl` stayed defined; disposable clone
`ag-f222eb` was created by pairing and deleted by cleanup. `QuickPastePremiumQA`
and the default AVD home were untouched.

## What was implemented

- **Agent store** (`AgentStore`): durable pairings, sessions, approvals,
  idempotency records, artifacts; WAL SQLite alongside supervisor state.
- **Pairing and scopes** (`AgentService`): short-lived single-use pairing code,
  operator approval that creates a fresh disposable AVD from a stopped template
  (never copying a running disk), revocable session token stored only as a
  SHA-256 hash, per-session scopes (`observe`, `input`, `app`, `lifecycle`,
  `network`, `restore`, `cleanup`, `artifact`), expiry, takeover that revokes
  sessions and bumps the guest generation.
- **Contract**: Protocol 1 envelopes extended with an opaque `auth` credential
  and `idempotencyKey`; typed errors for unauthorized, forbidden scope, expired,
  revoked, wrong instance, stale observation, ambiguous selector, approval
  required, idempotency conflict and unknown outcome. In-progress mutations are
  marked `unknown_outcome` on supervisor restart and replays refuse to re-execute.
- **Scoped operations**: disposable create, observation screenshot/tree,
  selector tap/type/assert, filtered logs, event poll, network profile, snapshot
  save/load, artifact read, scoped cleanup. Coordinate taps require a frame ID;
  age, guest identity, dimensions and generation are checked before dispatch.
  Input and mutations re-check session state immediately before dispatch.
- **MCP stdio server** (`EmulatorWorkspace.Mcp`): stdout carries JSON-RPC only;
  diagnostics go to stderr. Tools map 1:1 to supervisor ops. Token comes from
  `WS_AGENT_TOKEN_FILE`, never from tool arguments. Pairing tools are available;
  engine endpoints, controller tokens, ADB and `--direct` are not exposed.
- **CLI**: `--token-file`, `agent-pair`, `agent-pair-request/approve/complete`,
  `agent-sessions`, `agent-revoke`, `agent-takeover`, `agent-approve-action`,
  `call --op/--args-file/--idempotency`.
- **Sample fixture**: login form with semantic content descriptions, host HTTP
  login (350/1500 ms timeouts), FLAG_SECURE note screen, counter/marker/animated
  scene. Built APK 16,787 bytes, SHA-256
  `6B56060693294149F8A394E66AFB9579809DE81B2E7490E0567721416FBBF1C7`.

## Verified workflow

Operator pairing:

```powershell
wsctl create --name tpl --root D:\Phase2Root
wsctl agent-pair --client-name cli-agent --template tpl --out D:\Phase2Root\cli-session.json --root D:\Phase2Root
wsctl agent-pair-request ... / agent-approve ... / agent-complete ...   # same flow over the pipe
```

The paired clone booted (`76.92 s`, interactive `77.34 s`) and the sample APK
installed from the approved `<root>\apps\ws-sample.apk` path.

### Real MCP client

Official `@modelcontextprotocol/sdk` **1.32.2** (`clients/mcp-client/workflow.mjs`)
completed: initialize, tools/list, `device_status` → Running, `screen_capture`
(artifact `art-1aa5c0e0e2a7`, 168,868 bytes), `ui_tree`, `ui_type` into
`login_user`/`login_password`, `ui_tap` `login_button`, `ui_assert` `WELCOME`
(true), `logs_read` (1 `LOGIN_RESULT` line), `artifact_read`, `snapshot_save`
(`mcp-baseline`), operator-approved `snapshot_restore` (`mcp-baseline`).
Transcript: `artifacts/phase2/mcp/mcp-transcript.json`.

### CLI workflow (same session/scopes)

`wsctl call` with the same token completed screenshot, login typing/tap,
`ui_assert WELCOME` (true), filtered logs (1 line), snapshot save (`cli-baseline`)
and approved restore (`cli-baseline`). Transcript:
`artifacts/phase2/cli/cli-transcript.json`.

### Authorization and safety checks

| Check | Result |
| --- | --- |
| No credential | `unauthorized — Pairing is required` |
| Wrong guest (`tpl`) | `wrong_instance — bound to 'ag-f222eb'` |
| Stale frame after generation bump | `stale_observation — guest generation changed` |
| Ambiguous selector (`text=""`) | `ambiguous_selector — 7 nodes matched` |
| Replayed timed-out mutation | `unknown_outcome — reconcile before retrying` |
| Same idempotency key, different args | `idempotency_conflict` |
| Cleanup with approval | `cleaned: true`; instance removed, template kept |
| Credential after cleanup | `revoked` |

Raw: `artifacts/phase2/security/security-checks.json`.

### Cleanup

`agent-approve-action --operation device.cleanup` issued a single-use code; the
agent cleanup deleted `ag-f222eb`, its AVD and artifacts, revoked the session and
bumped the generation. `wsctl list` afterwards showed only the template.

## Not verified / blockers

| Item | State | Reproduction and next action |
| --- | --- | --- |
| FLAG_SECURE observation | **Unverified** | The first secure attempt returned a normal screenshot/tree (secure=false). Detection was changed to scan the whole `dumpsys window` output and fail closed on probe errors, but the guest wedged before retest. Re-run on a fresh clone; if still not detected, disable agent `screen_capture`/`ui_tree` until an independently verified secure path exists. This blocks Phase 2 closure. |
| Network shaping / failed login | **Failed to prove** | Emulator console `network delay gprs` + `network speed gsm` had no effect on guest eth0 traffic (login still WELCOME). `tc qdisc netem delay 2000ms` on `wlan0`/`eth0` also left ping at 5-12 ms. The host fixture outage attempt did not produce a timeout within the window. Need a supported qdisc/kernel path or a pinned proxy; next action is to verify `tc` class/qdisc support and a deterministic endpoint, then re-run the fail/succeed pair. |
| WPF Agent Sessions surface | **Not built** | Pairing/approval is CLI-only in this pass. Add a desktop window listing sessions/pairings with client identity, scopes, expiry, revoke, takeover and approve. |
| Second real MCP client | **Not tested** | Only the TypeScript SDK client was exercised. Add the Python `mcp` client and record its transcript. |
| Takeover during queued input | **Not tested** | Takeover revokes sessions and bumps generation; run a queued tap then takeover and assert no post-revoke input. |
| Event overflow / resync | **Not tested** | Poll with a cursor older than the 500-event ring and assert the typed resync response. |
| Successful idempotent replay | **Not observed** | The attempted save timed out first (correctly yielding `unknown_outcome`). Run a successful mutation twice with one key and compare cached response. |
| WPF Phase 1 click-through | Still pending | Record Create → drag/drop → launch → tap → Logcat → screenshot → stop with UI Automation. |
| Performance targets | Unchanged | 540x1200 RGB path, 29.4 FPS WPF, p95 474 ms dispatch-to-capture; 60 FPS / 100 ms composition not met. |

## Reproduce

Host fixture (64 KiB body, so a throttled path exceeds the app timeout):

```powershell
# terminal 1: TCP listener on 127.0.0.1:8765 returning HTTP 200 and 64 KiB
```

Then:

```powershell
dotnet build
dotnet test
wsctl create --name tpl --root D:\Phase2Root
wsctl agent-pair --client-name cli-agent --template tpl --out D:\Phase2Root\cli-session.json --root D:\Phase2Root
# start/install/launch and the CLI/MCP workflow commands are listed above
powershell -ExecutionPolicy Bypass -File tools\phase2-acceptance.ps1 -Root D:\Phase2Root -TokenFile D:\Phase2Root\cli-session.json
```

`tools/phase2-acceptance.ps1` automates the same checks against a paired root and
writes `artifacts/phase2/<run-id>/`. It was not re-run end to end after the manual
session was cleaned up; the manual transcript above is the acceptance evidence.

## Gate status

Phase 2 is **not closed**. The MCP+CLI workflow, authorisation failures, scoped
observation, snapshot restore and cleanup are verified on WHPX. Secure
observation, network shaping, the WPF agent surface and the second client remain
open, and the secure-capture gap is a blocker rather than a documentation gap.
Phase 1's desktop click-through and performance targets also remain separate open
items.
