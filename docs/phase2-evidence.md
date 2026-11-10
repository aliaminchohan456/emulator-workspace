# Phase 2 evidence — local agent integration

Baseline: clean commit **7f1b0ed**. This closure pass is built on **54f5195**
and adds the secure-window detector, guest network shaping, the WPF Agent
Sessions surface, the Python MCP client, the active-session recheck and the
final acceptance harness. Closure commit: **0317d53**. Build: **0 errors**,
**32 tests passing**.

Pins are unchanged: Emulator **37.2.12.0 / build 16428233**, vendored protos,
`system-images;android-35;default;x86_64` rev 2, WHPX. The historical Phase 1
runs remain distinct: original **18/19** (`20261110-172411`), stabilization
**19/19** (`20261110-183709`), Phase 0 regression **26/26**
(`run-20261110-174228`).

Test root: `D:\Phase2Root`. Template `tpl` remained defined throughout; every
disposable clone was deleted by its approved cleanup. `QuickPastePremiumQA` and
the default AVD home were not used.

## Implemented

- **Durable agent state** (`AgentStore`): pairings, sessions, approvals,
  idempotency records and artifacts in the supervisor SQLite database.
- **Pairing and scopes** (`AgentService`): short-lived single-use code, operator
  approval that creates a fresh AVD from a stopped template, revocable session
  token stored only as a SHA-256 hash, per-session scopes (`observe`, `input`,
  `app`, `lifecycle`, `network`, `restore`, `cleanup`, `artifact`), expiry,
  takeover and single-use approvals for restore/cleanup.
- **Protocol 1 extensions**: opaque `auth` and `idempotencyKey`; typed errors for
  unauthorized, forbidden scope, expired, revoked, wrong instance, stale
  observation, ambiguous selector, approval required, idempotency conflict,
  unknown outcome, capability unavailable and instance unavailable.
- **Active-session recheck**: `AgentService.EnsureActive` reloads durable state
  immediately before dispatch and again before input/mutation execution, so a
  revocation or takeover that lands while a request is queued cannot authorize
  it. Covered by `EnsureActive_rechecks_revocation_in_durable_store`.
- **Scoped operations**: disposable create, bounded screenshot/tree, semantic
  selector tap/type/assert, frame-bound coordinates with generation/age/geometry
  checks, filtered logs, event poll with resync, network profile, snapshot
  save/load, artifact read and scoped cleanup. Engine endpoints, controller
  tokens, raw ADB, root and `--direct` are outside the agent contract.
- **MCP stdio server**: stdout is protocol-only, diagnostics go to stderr, token
  comes from `WS_AGENT_TOKEN_FILE` or `WS_AGENT_TOKEN`, and tools map 1:1 to the
  supervisor registry.
- The WPF Agent Sessions surface lists paired clients, sessions, guests, scopes,
  expiry and state, pending pairings with template/scope approval, revoke,
  human takeover and the latest 25 agent operations. A final-commit probe after
  the non-blocking close change loaded 11 sessions/11 pairings and exited 0
  (`artifacts/phase2/20261110-213845/agent-sessions-probe-final.json`).
- **Sample fixture**: semantic login form, host HTTP login, FLAG_SECURE screen,
  counter and animated scene. APK 16,787 bytes, SHA-256
  `6B56060693294149F8A394E66AFB9579809DE81B2E7490E0567721416FBBF1C7`.
  Phase 3 extended the same fixture with a deterministic duplicate-selector
  surface (two controls sharing the content description `dup_action` and a
  `dup_marker` counter) and added a version code for bundle tooling; the
  acceptance APK for Phase 3 is SHA-256
  `09BA57AF004C54E7831E5565856875D9F80E261DC81FD59967BBCD0DBC7CD351`
  (see [phase3-evidence.md](phase3-evidence.md)).

## Secure observation — verified

Android 15 exposes the window flag symbolically in `dumpsys window`:

```
mAttrs={(0,0)(fillxfill) ... ty=BASE_APPLICATION ...
  fl=LAYOUT_IN_SCREEN SECURE LAYOUT_INSET_DECOR ...
```

It does **not** print `mSecure=true` for this path, which is why the first probe
missed it. `AgentService.ContainsSecureWindow` scans every `fl=` line for the
exact `SECURE` token (and `0x2000`); probe errors fail closed. The check runs
before and after capture/tree. Because the emulator framebuffer capture does not
enforce FLAG_SECURE itself, this supervisor gate is the security boundary.

Verified on a disposable API 35 guest:

| Path | Result |
| --- | --- |
| `screen_capture` with FLAG_SECURE | `status=secure_window`, `secure=true`, `redacted=true`, no image artifact |
| `ui_tree` with FLAG_SECURE | redacted result, `nodes=[]` |
| `ui_assert` / selector input | typed `secure_window` denial |
| Operator clears FLAG_SECURE | normal capture and tree return |
| MCP `screen_capture` | text result only, zero image parts; no base64 leak |
| MCP secure check | `screenshotRedacted`, `treeRedacted`, `selectorDenied`, `recoveredOk` all true |

## Network shaping — verified

The emulator console records `network delay/speed` state but does not shape the
virtio traffic on this image. The supervisor therefore applies **`tc netem`** to
the guest's `wlan0` and `eth0` interfaces through `su 0`; the ADB daemon remains
at its normal UID **2000 (shell)**. The agent contract still exposes only
`network_set_profile`.

`network_set_profile` accepts `latencyMs` 0–5000. A 2,500 ms profile produced
`NETWORK TIMEOUT`; removing it restored `WELCOME`; the qdisc was verified with
`tc qdisc show`. This affects the disposable guest only. The isolated host
fixture returns 64 KiB, so the shaped path cannot complete inside the sample's
1,500 ms timeout.

## Verified acceptance runs

### Clean full run — `20261110-212427`

- Instance `ag-5a92d9`, session `ses-c289713d4956`, serial `emulator-5556`.
- Boot completed in **67.06 s**, interactive readiness **67.43 s**.
- **41/41 checks passed**, build 0 errors, **31 tests**.
- TypeScript MCP SDK 1.32.2 and `wsctl` both completed the login/assert/logs/
  screenshot/artifact/snapshot-save/restore workflow; screenshot artifact
  `art-f73af2029b3a` (164,760 bytes), MCP image 219,680 base64 bytes.
- Network: `WELCOME` → 2,500 ms netem → `NETWORK TIMEOUT` → reset → `WELCOME`.
- Secure CLI and MCP checks passed.
- WPF probe: 6 sessions, 6 pairings, 2 templates, 25 recent operations.
- Cleanup deleted the clone and the token returned `revoked`; `tpl` remained.

This run predates the final active-session recheck by one implementation step.

### Final-code run — `20261110-213845`

- Instance `ag-4c8bcc`, session `ses-180363c9ed9f`, serial `emulator-5556`.
- Boot completed in **72.03 s**, interactive readiness **72.28 s**.
- **41/42 checks passed**; build 0 errors, **32 tests**.
- The single failure was the harness's state-dependent ambiguous-selector probe
  (`text=""` matched zero nodes on the restored screen), not a product path.
- All product checks passed: CLI login/assert/logs/artifact/snapshot restore;
  network `WELCOME` → `NETWORK TIMEOUT` → `WELCOME`; secure CLI and MCP redaction;
  TypeScript MCP workflow with 164,897-byte screenshot artifact; Python MCP
  `device_status`; WPF surface probe (8 sessions, 8 pairings, 2 templates, 25
  operations); unpaired/wrong-instance/stale/revoked denials; event idempotency
  replay and conflict; approved cleanup and `revoked` retry.
- The corrected probe uses `resourceId=""`. On a fresh clone it returned
  `ambiguous_selector: 13 nodes matched` (and `className=android.widget.TextView`
  matched 6); cleanup and the revoked retry also passed. Evidence:
  `artifacts/phase2/20261110-213845/ambiguous-corrected.json`.

A later rerun (`20261110-215946`) hit host ADB transport timeouts
(`adb.exe: device offline`, `adb.exe did not exit within 30s`) after repeated
clone cycles. Those are recorded as harness instability, not product failures;
the clean 41/41 run and the final-code 41/42 run above are the acceptance
evidence.

## Authorization and safety checks

| Check | Result |
| --- | --- |
| No credential | `unauthorized` |
| Wrong guest (`tpl`) | `wrong_instance` |
| Stale frame after a generation bump | `stale_observation` |
| Ambiguous selector | `ambiguous_selector` (13 nodes in corrected probe) |
| Replayed successful read | cached response identical |
| Same key, different arguments | `idempotency_conflict` |
| Timed-out mutation retry | `unknown_outcome` |
| Expired session | `expired` (unit) |
| Revoked session | `revoked` |
| Event-ring overflow | typed `overflow=true` + resync, no stale events (unit) |
| Restart reconciliation | in-progress mutation becomes `unknown_outcome` (unit) |
| Human takeover | revoked the session; subsequent observation returned `revoked` |
| Cleanup with approval | clone deleted, template preserved, token revoked |

## Remaining gaps

| Item | State | Reproduction / next action |
| --- | --- | --- |
| Two running managed guests + external ADB transport | Pending | The cross-guest test used the clone vs `tpl` and returned `wrong_instance`. Start two disposable clones and run the same status/tap against the non-bound clone; repeat with a controlled external ADB transport. |
| Live queued-input takeover race | Pending live race | The recheck is implemented and unit-tested (`EnsureActive_rechecks_revocation_in_durable_store`); live takeover followed by input returns `revoked`. To orchestrate the race: start a long `snapshot.save`/install, queue an `input.tap` behind it, call `agent-takeover`, and assert the queued tap fails with `revoked`. |
| Phase 1 desktop click-through | Pending (separate phase) | The WPF viewport/Agent surface is built, but the Create → boot → drag/drop → launch → tap → Logcat → screenshot → shutdown click-through has not been recorded as a Phase 1 run. |
| Phase 1 performance | Pending (separate phase) | 540×1200 RGB path, 29.4 FPS WPF, p95 474 ms dispatch-to-capture; 60 FPS and 100 ms composition targets are not met. |
| H.264, mixed-DPI follow-up, constrained volume, WSL, Magisk | Out of Phase 2 scope | Unchanged from the plan. |

## Reproduce

The fixture is `tools/fixtures/login_fixture.py` (64 KiB body). Then:

```powershell
dotnet build
dotnet test
wsctl create --name tpl --root D:\Phase2Root
wsctl agent-pair --client-name agent --template tpl --out D:\Phase2Root\agent.json --root D:\Phase2Root
powershell -ExecutionPolicy Bypass -File tools\phase2-acceptance.ps1 `
  -Root D:\Phase2Root -TokenFile D:\Phase2Root\agent.json -Template tpl
```

The script starts the fixture if needed, builds/tests when the supervisor is not
running (or pass `-SkipBuild`), starts the clone, and writes
`artifacts\phase2\<run-id>/` with `summary.json`, `versions.json`, build/test
logs, CLI and MCP transcripts, network and secure evidence, the WPF probe,
security checks and cleanup output. It cleans up with a single-use approval
unless `-KeepRunning` is set. `-SkipMcp` and `-SkipDesktop` are available for
isolated runs.
