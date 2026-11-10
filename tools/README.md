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