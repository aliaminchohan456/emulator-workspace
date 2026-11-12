# Release security review

Scope: the Emulator Workspace desktop, the `wsctl` CLI, the MCP server, the
local supervisor and the agent session surface, as they ship in the Windows
release candidate. Every finding below was checked against the code in this
repository or exercised by an acceptance run; the evidence run ids are given.

## 1. Trust boundaries

| Boundary | Mechanism | Evidence |
| --- | --- | --- |
| Host ↔ supervisor | A named pipe restricted to the current user (`PipeOptions.CurrentUserOnly`) in `src/EmulatorWorkspace.Core/Supervisor/SupervisorLocator.cs` and `SupervisorPipeServer.cs`. | The pipe refuses a connection from another user; the CLI can only talk to the supervisor of its own workspace root. |
| Supervisor ↔ engine controller | The engine's own gRPC controller on loopback, authenticated with the per-user console token (`Bearer`/`raw` metadata in `src/EmulatorWorkspace.Controller/EmulatorControlChannel.cs`). The engine is started with `-grpc-use-token`. | The controller identity check (`EmulatorDiscovery`) verifies the advertised AVD name, ADB port and gRPC port before any input is sent. |
| Supervisor ↔ guest | ADB against the **exact** managed serial. The serial is stored per instance and never resolved by a name pattern. | The identity check refuses input when the engine advertises a different identity (`controller_failure`). |
| Supervisor ↔ external ADB | Opt-in only. A session that is not paired to the workspace cannot reach the guest. | `OperationDispatcher` refuses unpaired observation and input with `unauthorized`. |
| Operator ↔ agent session | Pairing request, operator approval with an explicit scope list, a bounded expiry, revocation and human takeover. | The recipe lane verifies a revoked session (`revoked`) and a wrong instance (`wrong_instance`). |

## 2. Privileged scopes

The agent scopes are explicit and checked before the operation is dispatched
(`AgentService.AuthorizeAgent`):

| Scope | Grants |
| --- | --- |
| `observe` | Status, screenshots, hierarchy, log read, artifact read, and the new read-only diagnostics (`logcat.export`, `diagnostics.crash`). |
| `input` | Tap, text, key, swipe. |
| `app` | Install, launch, foreground, package info, **package permissions**, **deep link**, **instrumentation**. |
| `lifecycle` | Start, wait, stop, restart, disposable creation. |
| `network` | Network profile shaping. |
| `restore` | Snapshot save and load. |
| `cleanup` | Device cleanup. |
| `artifact` | Artifact retrieval. |

Privileged changes are separated from ordinary ones:

- `instance.adbRoot` (the Root Lab) is **not** in the agent scope list, so an
  agent session cannot elevate the guest's ADB daemon. It is an operator-only
  operation that must name the exact managed instance.
- The snapshot load and the recipe replay need an explicit approval code, which
  the operator issues in the desktop.
- Deleting the last verified recovery point needs `force` **and** an approval.

## 3. Injection and path escapes

| Risk | Control | Where |
| --- | --- | --- |
| Command injection through an ADB argument | Every ADB invocation goes through `ProcessRunner.RunAsync` with an **argument list**, never a command string. | `src/EmulatorWorkspace.Core/Engine/AdbClient.cs` |
| Path escape through the APK install argument | An agent session may only install the approved sample APK; the path is resolved and checked to be inside the workspace `apps` directory. A non-agent caller may pass any path, but the path is still resolved to a full path and the extension is checked. | `OperationDispatcher` (`app.install`), `InstanceManager.InstallAsync` |
| Path escape through the AVD import source | The source is resolved to a full path; a source inside the workspace is refused ("already belongs to this workspace"); a source without `config.ini` is refused; a source that contains **symlinks or Windows reparse points** is refused. | `DeviceLibraryService.ValidateAvdImport`, unit tests in `tests/EmulatorWorkspace.Tests/AvdImportTests.cs` |
| Path escape through the bundletool deployment | The `.apks` path is resolved, and the device is resolved from a **managed instance** so the external tool can never address an unrelated target. | `src/EmulatorWorkspace.Cli/SupervisorCommands.cs` (`install-apks`) |
| Argument injection through a deep link | The URI is parsed with `Uri.TryCreate(UriKind.Absolute)` before it reaches the guest's intent parser. | `InstanceManager.DeepLinkAsync` |
| Secret leakage through a recipe export | Secrets are replaced with `${runtime:name}` references; the export contains no absolute host paths. | The recipe lane verifies `redacted` and `no-host-paths`. |

## 4. Request floods and resource bounds

| Control | Detail |
| --- | --- |
| Bounded requests | Every operation has a deadline; the CLI uses two minutes by default and raises it per operation. |
| Bounded artifacts | The log read is clamped to 2000 lines, the logcat export to 20 000 lines, the frame payload to 16 MB, the crash excerpt to 2000 characters. |
| Bounded waits | The boot, restore, launch focus and UI-readiness waits all have an explicit attempt or time budget. |
| Bounded input | The tap, text and swipe paths reject a stale frame and re-resolve the semantic selector before acting. |
| Bounded guest text | The package, UID and hierarchy readers are bounded so a chatty guest cannot stall the supervisor. |
| Idempotency | A mutation with an idempotency key is recorded, and an interrupted mutation is reported as `unknown_outcome` so the operator reconciles instead of blindly retrying. |

## 5. Stale input and interrupted operations

- A stale frame is rejected by sequence number before it is presented.
- The frame and log subscriptions are invalidated on boot and on restore.
- A cancelled client leaves the operation in the journal; the recipe lane
  interrupts a replay on purpose, observes the journal entry and then retries
  the recipe cleanly after the guest is re-established.
- The ADB daemon identity is re-read after `adb root`/`adb unroot` because
  adbd restarts and the transport changes (observed: transport 301 → 302).

## 6. Exposed sockets

- The supervisor listens on a **named pipe**, not a TCP port, and the pipe is
  restricted to the current user.
- The engine's gRPC controller binds to loopback and requires the console
  token.
- The MCP server speaks over stdio; stdout carries protocol traffic only.

## 7. Credential and secret handling

| Item | Handling |
| --- | --- |
| Console / gRPC token | Read from the engine's own token file, sent as request metadata, never written to a log. |
| Agent session token | Written to a session file with the workspace's own permissions; the CLI reads it from `--token-file` and sends it as the request authentication. |
| Recipe secrets | Replaced by runtime references on export. |
| Diagnostics bundle | The export path goes through the same redaction as the log entries. |

## 8. Retention and deletion

- Artifacts are stored per session under the workspace and are removed by
  `device.cleanup`.
- The snapshot of the last verified recovery point is protected against
  deletion without `force` and an approval.
- The AVD of a managed instance is removed with a bounded retry, because the
  engine keeps a few files (for example `pstore.bin`) open for a moment after
  it was stopped. The removal clears read-only attributes first.
- The uninstall of the Windows package removes the installed files only; the
  operator's profiles live in `%LOCALAPPDATA%\EmulatorWorkspace` and are never
  touched by the installer.

## 9. Findings and their status

| # | Finding | Status |
| --- | --- | --- |
| 1 | A restart could leave the operation outcome unknown, so a retry could double-apply a mutation. | Fixed: `unknown_outcome` is reported and the operation journal is consulted before a retry. |
| 2 | An AVD import could follow a link out of the source directory. | Fixed: reparse points and symlinks in the source are refused; five unit tests cover the validation paths. |
| 3 | A recipe could store a step whose assertion had failed. | Fixed: the recorder stores verified steps only. |
| 4 | An agent session could install an arbitrary APK. | Fixed: an agent session may only install the approved sample APK. |
| 5 | A caller could declare itself an agent to bypass pairing. | Fixed: a caller-declared agent role is refused with `denied`. |
| 6 | A stale device could receive input after the engine was replaced. | Fixed: the engine identity (AVD name, ADB port, gRPC port) is verified before input is sent. |
| 7 | The privileged ADB-root operation was reachable from an agent scope. | Fixed: `instance.adbRoot` and `instance.adbRootState` are outside the agent scopes. |
| 8 | The code-signing certificate of the release candidate. | **Open**: no user-owned signing identity was supplied, so the release candidate is produced unsigned and the provenance records `signing.status = "unsigned"`. Supplying a certificate is an operator action. |
| 9 | The four-hour two-guest soak. | **Verified on 0.1.1**: WHPX soak `20261112-213629` recorded 200 paired probes over 14,411.36 seconds and removed both guests. Repeat on the final candidate after release-affecting changes. |

## 10. How to reproduce the checks

```
dotnet test                                              # 56 unit tests
powershell -ExecutionPolicy Bypass -File tools\recipe-acceptance.ps1 -Root D:\Phase4Root -Baseline FixtureForeground
powershell -ExecutionPolicy Bypass -File tools\root-lab-acceptance.ps1 -Root D:\Phase4Root
powershell -ExecutionPolicy Bypass -File tools\avd-import-acceptance.ps1 -Root D:\Phase4Root
powershell -ExecutionPolicy Bypass -File tools\phase3-acceptance.ps1 -Root D:\Phase4Root
```

The run ids and the per-lane results are in `docs/phase3-manifest.json` and in
`docs/phase3-evidence.md`.
