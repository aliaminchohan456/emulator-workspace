# Emulator Workspace 0.1.1 candidate

A Windows-first Android emulator workspace for development and QA: a WPF
desktop, a scoped local CLI and MCP workflow, verified device and recipe
recovery, an ADB root workflow, and an installable Windows release candidate.

## What is in the package

| Component | Path after install | Purpose |
| --- | --- | --- |
| Desktop | `%LOCALAPPDATA%\Programs\Emulator Workspace\EmulatorWorkspace.Desktop.exe` | The WPF application: device rail, embedded viewport, inspector, Logcat drawer, APK drop, agent sessions, Root Lab panel and the Ctrl+K command palette. |
| Supervisor | `%LOCALAPPDATA%\Programs\Emulator Workspace\EmulatorWorkspace.Supervisor.exe` | The persistent per-user supervisor: named pipe, SQLite state, lifecycle, snapshots, recipes, journal. Started on demand. |
| CLI | `%LOCALAPPDATA%\Programs\Emulator Workspace\wsctl.exe` | The local command line client. |
| Sample APK | `%LOCALAPPDATA%\Programs\Emulator Workspace\ws-sample.apk` | The deterministic QA fixture that the desktop installs and launches. |

The package contains **no** Android SDK, emulator or system image. Those are
obtained through the guided first-run setup, which uses the official SDK
Manager and the pinned versions.

## Local build

The candidate built from `8d40469` is at
`D:/Release/emulator-workspace-0.1.1-20261112-212010/EmulatorWorkspace-0.1.1.msi`.
It contains 422 payload files, is 55,359,712 bytes, and reports
`NotSigned` in Authenticode. SHA-256
`e2620bce693271f433275ad639a0ac9ccc846d42ec61c8d48047261590c5346b`.
The release directory contains the SBOM, notices, hash inventory and build
provenance. Installer run `20261112-212707` passed 21/21 checks, including
WHPX boot, the installed snapshot and simulation commands, upgrade from the
original 0.1.0 MSI with its managed profile preserved, and uninstall.
[Reproduction and remaining gates](../task.md#continuation-from-806251f)

The 0.1.1 payload completed a four-hour WHPX soak with 200 successful
two-guest probes and verified cleanup. It restored the named snapshot in
all 20 measured trials, with p50 9.19 s and p95 12.34 s. The 8 s median
target and disconnected Windows reuse remain pending.
[Raw benchmark index](phase3-manifest.json)

```powershell
powershell -ExecutionPolicy Bypass -File tools/package-release.ps1 -Version 0.1.1 -OutDir D:/Release
```

An authorized signing certificate with its private key must be available in
`Cert:/CurrentUser/My` for `-CertificateThumbprint`. The build uses Windows
SDK SignTool to verify the MSI before writing signed provenance.
[SignTool documentation](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)

## Pins

| Component | Version |
| --- | --- |
| Android Emulator | 37.2.12.0, build 16428233 |
| System image | `system-images;android-35;default;x86_64`, revision 2 |
| ADB | platform-tools 37.0.1-15733141 |
| .NET | 10.0.302 (SDK, pinned in `global.json`) |
| Runtime | `win-x64`, self-contained |

## First run

1. Install the package for your Windows user.
2. Start **Emulator Workspace** from the Start menu.
3. The first-run panel reports the SDK state. Install the official SDK
   components and accept the licenses if the panel asks for them; the pinned
   emulator and the pinned image are never substituted automatically.
4. Press **Create device**. The guest boots from the pinned image.

The workspace state lives in `%LOCALAPPDATA%\EmulatorWorkspace` (instances,
AVDs, journals, evidence, recipes). It is deliberately outside the program
files so that an uninstall or an upgrade never touches the operator's
profiles.

## Architecture

```
 WPF desktop  ──┐                        (named pipe, current user only)
 MCP server   ──┼──▶ supervisor ──▶ emulator engine (loopback gRPC + token)
 wsctl CLI    ──┘         │
                          ├──▶ ADB (exact managed serial only)
                          └──▶ SQLite state + JSONL operation journal
```

- The supervisor owns the named pipe (`PipeOptions.CurrentUserOnly`), the
  SQLite state and the durable journal. It is started on demand by the first
  client and lives for the session.
- The engine is started with `-grpc-use-token`; every controller call carries
  the per-user console token.
- Every operation is journaled with its actor, arguments and outcome. A
  mutation whose outcome is unknown after a restart is reported as
  `unknown_outcome` so the operator reconciles instead of blindly retrying.
- An agent session is a separate identity: it is paired, scoped, time-limited,
  revocable, and it can be taken over by the operator.

## Recovery commands

```powershell
# What is running?
wsctl list --root D:\Phase4Root

# Is a guest usable again after an interruption?
wsctl stop   --name dev --force --root D:\Phase4Root
wsctl start  --name dev --root D:\Phase4Root
wsctl wait   --name dev --timeout 600 --root D:\Phase4Root

# Snapshots
wsctl snapshot-save --name dev --tag baseline --root D:\Phase4Root
wsctl snapshot-load --name dev --tag baseline --root D:\Phase4Root

# Recipes
wsctl recipe-start --name dev --root D:\Phase4Root
wsctl recipe-export --recipe <id> --out recipe.json --root D:\Phase4Root
wsctl recipe-replay --recipe recipe.json --token-file session.json --root D:\Phase4Root

# Root Lab
wsctl adb-root       --name dev --root D:\Phase4Root
wsctl adb-root-state --name dev --root D:\Phase4Root
wsctl adb-unroot     --name dev --root D:\Phase4Root
```

## Development tooling

```powershell
wsctl package-info        --name dev --package dev.emulatorworkspace.sample --root D:\Phase4Root
wsctl package-permissions --name dev --package dev.emulatorworkspace.sample --root D:\Phase4Root
wsctl deep-link           --name dev --uri "https://example.test/login" --root D:\Phase4Root
wsctl instrumentation     --name dev --package dev.emulatorworkspace.sample --root D:\Phase4Root
wsctl logcat-export       --name dev --lines 500 --root D:\Phase4Root
wsctl crash-report        --name dev --package dev.emulatorworkspace.sample --root D:\Phase4Root
wsctl bugreport           --name dev --root D:\Phase4Root
wsctl install-apks        --name dev --apks D:\Phase3Tools\apks\ws-sample.apks --root D:\Phase4Root
wsctl import-avd          --source C:\Users\me\.android\avd\other.avd --target imported --root D:\Phase4Root
```

The external ADB interoperability is opt-in: `wsctl` never starts or stops a
global ADB server, and it only ever addresses the exact serial of a managed
instance.

## Security summary

The full review is in `docs/security-review.md`. The short version:

- The supervisor listens on a current-user named pipe, not a TCP port.
- The engine controller is loopback-only and token-authenticated.
- An agent session can never install an arbitrary APK, never elevate the ADB
  daemon and never address a guest other than the one it is bound to.
- Paths are resolved and checked; an AVD import refuses a source that already
  belongs to the workspace or that contains links or reparse points.
- The release candidate is **unsigned** unless a user-owned code-signing
  certificate is supplied to the packaging job; the provenance record states
  the signing status truthfully.

## Known limitations

- The quiet-host WPF diagnostic used the 0.1.1 supervisor and fixture with a
  separately built desktop. It measured 22.01 delivered FPS and 840.85 ms
  p95 dispatch to the WPF composition callback over 25 valid trials. The
  60 FPS and 100 ms p95 targets remain open. [Benchmark results](benchmark.md)
- The source-built Magisk Lab profile needs an x86_64 Linux builder. The
  pinned inputs and preparation commands are in
  [magisk-lab-build.md](magisk-lab-build.md).
- Concurrent engine starts are unreliable on this host, so the acceptance
  harnesses start the guests sequentially and record the concurrent attempt as
  an explicit option.
- Only one host, one GPU and one guest ABI were exercised. The compatibility
  matrix marks every other combination as unverified.
