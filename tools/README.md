# Tools

## `host-baseline.ps1`

Collects the host facts Phase 0 depends on and fails on a missing prerequisite.

```powershell
powershell -ExecutionPolicy Bypass -File tools\host-baseline.ps1
```

Checks: WHPX verdict from `emulator-check accel`, RAM, free disk, the AOSP API 35
image, and the emulator binary. Writes `artifacts/host-baseline.json` and
`.md`, prints the exact action for each failure, exits 2 on failure.

## `phase0-spike.ps1`

Runs the real acceptance scenario through `wsctl` only. Boots two guests, checks
targeting, controller auth/binding, input response, snapshot round-trip, agent
denial, ramdisk substitution, and independent input.

```powershell
powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1 -Root D:\EmulatorWorkspace
```

Options: `-InstanceA`, `-InstanceB`, `-TapX`, `-TapY`, `-BootTimeoutSeconds`,
`-Sdk`, `-Root`, `-OutDir`, `-KeepRunning`, `-SkipMagiskProbe`.

Artifacts per run: `summary.json`, `transcript.txt`, `host-baseline.json`,
`target-*.txt`, `controller-*.txt`, `input-before.png`, `input-after.png`,
`restored.png`, `snapshot-*.txt`, `ramdisk-probe.img`. Exit code 2 means at
least one check failed.

The harness stops running instances at the start (verified engine identity) and
at the end unless `-KeepRunning` is passed.

## `probe_ramdisk.py`

Decodes the emulator ramdisk container and lists cpio members.

```powershell
python tools\probe_ramdisk.py "$env:ANDROID_HOME\system-images\android-35\default\x86_64\ramdisk.img"
```

The container is concatenated LZ4 legacy frames; each frame wraps a newc cpio
archive. Frame 0 is the generic ramdisk (owns `/init`), frame 1 carries
`first_stage_ramdisk`. No external packages are required.

## `repack_ramdisk.py`

Rebuilds the ramdisk with an extra file or an appended line and re-encodes every
frame as a literal LZ4 block.

```powershell
python tools\repack_ramdisk.py <in.img> <out.img> --add "ws-probe.txt" --content "hello"
python tools\repack_ramdisk.py <in.img> <out.img> --append-line "system/etc/ramdisk/build.prop=ro.bootimage.ws_probe=1"
```

Then boot it with `wsctl start --name <n> --ramdisk <out.img>` and return to the
stock input with `--ramdisk stock`.

Known constraints: every LZ4 frame must be preserved, cpio data offsets must be
4-byte aligned after the name, and the engine appends its own bootconfig to the
per-AVD `initrd` (do not patch that generated file; it is rebuilt each launch).

## `phase1-acceptance.ps1`

Runs the Phase 1 non-visual gate on a fresh root: supervisor, doctor, create,
boot, exact ADB target, controller auth, continuous frame trace, APK install and
launch, screenshot, tap marker in Logcat, agent denial, snapshot round trip, a
second guest, port-conflict error, and supervisor restart reattachment.

```powershell
powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1
```

Artifacts: `artifacts/phase1/<run-id>/` with `summary.json`, `transcript.txt`,
`doctor.json`, `frame-trace.json`, `screenshot.png`, and `logcat-marker.txt`.
Exit code 2 means at least one check failed; failures stay visible in the
summary instead of being retried away.

## `phase2-acceptance.ps1`

Runs the Phase 2 agent gate against a paired root: build/test, disposable clone
boot, CLI and TypeScript/Python MCP workflows, guest network shaping, FLAG_SECURE
redaction and recovery, WPF Agent Sessions probe, authorization failures,
idempotent replay, approved cleanup, and the revoked-credential check.

```powershell
powershell -ExecutionPolicy Bypass -File tools\phase2-acceptance.ps1 `
  -Root D:\Phase2Root -TokenFile D:\Phase2Root\agent.json -Template tpl
```

Options: `-Root`, `-TokenFile`, `-Template`, `-OutDir`, `-FixtureBytes`,
`-SkipBuild`, `-SkipMcp`, `-SkipDesktop`, `-KeepRunning`. The script starts the
fixture if needed; a running supervisor locks its binaries, so build before
starting it or pass `-SkipBuild`.

Artifacts: `artifacts/phase2/<run-id>/` with `summary.json`, `versions.json`,
`build.txt`, `tests.txt`, `cli-transcript.json`, `mcp/`, `network.json`,
`secure-cli.json`, `agent-sessions-probe.json`, `security.json`, `cleanup.txt`
and the fixture request log. Exit code 2 means at least one check failed.

## `fixtures/login_fixture.py`

Isolated HTTP fixture for the login workflow. Listens on `0.0.0.0:8765` by
default and returns a 64 KiB `/login` body. Environment: `FIXTURE_PORT`,
`FIXTURE_BODY_BYTES`, `FIXTURE_LOG`. It logs request timestamps and response
sizes so the shaped path can be distinguished from the baseline.

```powershell
py -3 tools\fixtures\login_fixture.py
```

## Release hardware probes

Build the Debug binaries and fixture once before starting the hardware runs.
Each run creates disposable guests through the supervisor and stores its raw
results under `artifacts/phase3`.

```powershell
dotnet build
powershell -ExecutionPolicy Bypass -File sample/build-sample-apk.ps1
powershell -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root D:/WSBench -Samples 20 `
  -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle
powershell -ExecutionPolicy Bypass -File tools/play-image-acceptance.ps1 -Root D:/WSPlay36 `
  -Image 'system-images;android-36;google_apis_playstore;x86_64'
```

Omit `-SkipRestore` for named snapshot trials. The benchmark records the full
wall time, console time, recovery mode, and saved marker. Add `-SoakMinutes
240` for the four-hour paired run and follow `soak.jsonl` through supervisor
cleanup. See [benchmark results](../docs/benchmark.md) for the endpoints.

The Play harness records interactive boot, stock ADB capabilities, APK
deployment, capture, snapshot state, and observed simulation capabilities.
Run it separately for `system-images;android-35;google_apis_playstore_ps16k;x86_64`.

The Linux source-profile build and Windows signing gates have separate
prerequisites. [Magisk Lab build procedure](../docs/magisk-lab-build.md),
[Windows release notes](../docs/release-notes.md)
