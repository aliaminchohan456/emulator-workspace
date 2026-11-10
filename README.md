# Emulator Workspace

Windows-first Android emulator workspace. Phase 0 proves the runtime: two real
WHPX x86_64 AOSP guests, exact ADB targeting, an authenticated loopback
controller, frame capture, input, named snapshots, and the Magisk boot input.

## What runs today

| Piece | Location | Status |
| --- | --- | --- |
| Persistent per-user supervisor (SQLite, pipe, lifecycle, adoption) | `src/EmulatorWorkspace.Supervisor` | Phase 1, verified |
| Versioned local contract | `src/EmulatorWorkspace.Contracts` | Protocol 1 |
| Supervisor core (instances, ports, journal, policy, engine adapter) | `src/EmulatorWorkspace.Core` | Phase 1 |
| Authenticated gRPC controller client (vendored engine protos) | `src/EmulatorWorkspace.Controller` | Verified against 37.2.12 |
| `wsctl` command line (supervisor client, `--direct` diagnostic fallback) | `src/EmulatorWorkspace.Cli` | Verified |
| WPF desktop (rail, embedded viewport, Inspector, Logcat, APK drop) | `src/EmulatorWorkspace.Desktop` | Phase 1 MVP |
| Deterministic sample APK | `sample/` + `artifacts/phase1/sample/ws-sample.apk` | Verified |
| Host baseline + Phase 0 harness | `tools/host-baseline.ps1`, `tools/phase0-spike.ps1` | 26/26 checks (run `run-20261110-152217`) |
| Phase 1 acceptance harness | `tools/phase1-acceptance.ps1` | 19 checks, 18 pass (run `20261110-172411`) |
| Unit tests | `tests/EmulatorWorkspace.Tests` | 21 passing |

The MCP server and root lab UI remain Phase 2 and Phase 4 work.

## Prerequisites

- Windows 11 x64 with hardware virtualization enabled in firmware.
- Windows Hypervisor Platform (`HypervisorPlatform` optional feature) enabled.
  Verify with `emulator-check accel`; it must print `WHPX ... is installed and usable`.
- .NET SDK 10.0.302 (pinned in `global.json`).
- Android SDK with:
  - `emulator` 37.2.12
  - `platform-tools`
  - `system-images;android-35;default;x86_64` revision 2 (AOSP, userdebug)
- Python 3 for the ramdisk tools.

`tools\host-baseline.ps1` checks all of this and prints the exact missing action.

## Build

```powershell
dotnet build
dotnet test
```

## Run

Runtime state (AVDs, instance records, logs, journal, SQLite) defaults to
`%LOCALAPPDATA%\EmulatorWorkspace`. Override it with `EMULATOR_WORKSPACE_HOME`
or `--root`. On this host it was relocated to `D:\EmulatorWorkspace` because the
system volume has limited free space:

```powershell
[Environment]::SetEnvironmentVariable('EMULATOR_WORKSPACE_HOME','D:\EmulatorWorkspace','User')
```

The desktop starts the supervisor on demand; the CLI does the same:

```powershell
src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe --root D:\Phase1Root
```

Two guests, end to end (all commands route through the supervisor):

```powershell
$ws = "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"

& $ws doctor --json artifacts\host-baseline.json --root D:\Phase1Root
& $ws create --name a --root D:\Phase1Root
& $ws start --name a --root D:\Phase1Root
& $ws wait --name a --root D:\Phase1Root
& $ws verify-target --name a --root D:\Phase1Root
& $ws controller-probe --name a --root D:\Phase1Root
& $ws frame --name a --via controller --out artifacts\a.png --root D:\Phase1Root
& $ws tap --name a --x 540 --y 720 --root D:\Phase1Root
& $ws snapshot-save --name a --tag baseline --root D:\Phase1Root
& $ws snapshot-load --name a --tag baseline --root D:\Phase1Root
& $ws stop --name a --root D:\Phase1Root
```

Supervisor diagnostics: `wsctl supervisor-status`, `wsctl supervisor-stop`.
`wsctl --direct <command>` keeps the pre-supervisor path for diagnostics only.

Sample APK and acceptance:

```powershell
powershell -ExecutionPolicy Bypass -File sample\build-sample-apk.ps1
powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1
```

Build output paths: CLI `net10.0`, supervisor and desktop `net10.0-windows`.

## Safety model

- Every command targets one managed `emulator-<port>` serial. There is no
  "first device" fallback and no global ADB shutdown.
- Mutations are serialized per instance with a named mutex and persisted
  atomically.
- The engine is launched with `-grpc-use-token`; the controller is bound to
  loopback and rejects missing or forged tokens.
- Agent callers are denied by default; every attempt is journaled at
  `journal\operations.jsonl`.
- Guest content is untrusted. Nothing from the guest is executed on the host.

## Documentation

- [Phase 0 evidence](docs/phase0-evidence.md)
- [Phase 1 evidence](docs/phase1-evidence.md)
- [Tools](tools/README.md)
- [Plan](plan.md)
- [Task checklist](task.md)
