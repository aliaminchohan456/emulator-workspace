# Emulator Workspace

Windows-first Android emulator workspace. Phase 0 proves the runtime: two real
WHPX x86_64 AOSP guests, exact ADB targeting, an authenticated loopback
controller, frame capture, input, named snapshots, and the Magisk boot input.

## What runs today

| Piece | Location | Status |
| --- | --- | --- |
| Supervisor core (instances, ports, journal, policy, engine adapter) | `src/EmulatorWorkspace.Core` | Phase 0 vertical slice |
| Authenticated gRPC controller client (vendored engine protos) | `src/EmulatorWorkspace.Controller` | Verified against 37.2.12 |
| `wsctl` command line | `src/EmulatorWorkspace.Cli` | Verified |
| Host baseline + Phase 0 acceptance harness | `tools/host-baseline.ps1`, `tools/phase0-spike.ps1` | Verified, 28/28 checks |
| Ramdisk probe/repack for the Magisk boot path | `tools/probe_ramdisk.py`, `tools/repack_ramdisk.py` | Verified |
| Unit tests | `tests/EmulatorWorkspace.Tests` | 21 passing |

The WPF desktop, sample APK, MCP server, and root lab UI are Phase 1+ work.

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

Runtime state (AVDs, instance records, logs, journal) defaults to
`%LOCALAPPDATA%\EmulatorWorkspace`. Override it with `EMULATOR_WORKSPACE_HOME`
or `--root`. On this host it was relocated to `D:\EmulatorWorkspace` because the
system volume has limited free space:

```powershell
[Environment]::SetEnvironmentVariable('EMULATOR_WORKSPACE_HOME','D:\EmulatorWorkspace','User')
```

Two guests, end to end:

```powershell
$ws = "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"

& $ws doctor --json artifacts\host-baseline.json
& $ws create --name a
& $ws start --name a
& $ws wait --name a
& $ws verify-target --name a
& $ws controller-probe --name a
& $ws frame --name a --via controller --out artifacts\a.png
& $ws tap --name a --x 332 --y 1820 --via controller
& $ws snapshot-save --name a --tag baseline
& $ws snapshot-load --name a --tag baseline
& $ws stop --name a
```

Second guest: `create --name b`, then start/wait it. Ports and controller ports
are allocated automatically and recorded per instance.

Full acceptance run (boots two guests, measures everything, writes artifacts):

```powershell
powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1 -Root D:\EmulatorWorkspace
```

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
- [Tools](tools/README.md)
- [Plan](plan.md)
- [Task checklist](task.md)
