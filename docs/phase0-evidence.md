# Phase 0 evidence

Collected 2026-11-10 on the reference host. Every claim below was produced by a
real emulator binary and a real guest; nothing is simulated. Commands are the
exact ones that produced the output. Artifacts live under
`artifacts/phase0/` and the latest harness run directory.

## Host and pinned versions

| Item | Value |
| --- | --- |
| OS | Windows 11 Pro 26H2, build 26300.9818 (registry reports "Windows 10"; build number identifies 11) |
| CPU | Intel Core i5-8265U, 4 cores / 8 threads, 1.60 GHz |
| RAM | 31.8 GiB (9-20 GiB available during runs) |
| GPU | Intel UHD Graphics 620, driver 31.0.101.2135 |
| Security | VBS running (HVCI), a hypervisor is already present |
| Acceleration | `emulator-check accel` -> `WHPX(10.0.26300) is installed and usable.` |
| Emulator | 37.2.12.0, build_id 16428233 (`emulator` SDK package 37.2.12) |
| ADB | platform-tools 37.0.1, `Android Debug Bridge version 1.0.41` |
| System image | `system-images;android-35;default;x86_64` revision 2, API 35, ABI x86_64 |
| Guest build | `Android/sdk_phone64_x86_64/emu64x:15/AE3A.240806.019/12368160:userdebug/test-keys` |
| Guest root | userdebug, `ro.debuggable=1`, veritymode `enforcing` |
| Runtime root | `D:\EmulatorWorkspace` (`EMULATOR_WORKSPACE_HOME`), source repo on C: |
| .NET SDK | 10.0.302 (`global.json`) |

Full machine JSON: `artifacts/host-baseline.json` and each harness run's
`host-baseline.json`.

## Gate: WHPX boot-to-frame with visible input response

Instance A, cold boot with `-no-snapshot-load`, host GPU:

```
wsctl create --name a
wsctl start --name a
wsctl wait --name a
```

Observed: `boot_completed=1` in 43.0-69.8 s across runs. Two guests booting
concurrently took 69.8 s (A) and 105.5 s (B). Software rendering
(`-gpu swiftshader_indirect`) booted in 66-95 s.

Frame capture through the authenticated controller:

```
wsctl frame --name a --via controller --out artifacts/phase0/input-before.png
```

Observed: 1,818,250-byte PNG at 1080x2400 from `getScreenshot` (controller),
and a 1,199,010-byte frame on an earlier run. The controller also streams via
`streamScreenshot` (client implemented; single-frame calls used for artifacts).

Input:

```
wsctl tap --name a --x 332 --y 1820 --via controller
```

Observed focus change:

```
before: Window{eedd2ee u0 com.android.launcher3/...QuickstepLauncher}
after : Window{db024e6 u0 com.android.gallery3d/...GalleryActivity}
```

Frames before/after differ (1,818,250 vs 93,452 bytes; SHA-256 differs).
Gallery opened on the guest in response to the injected touch, toggling from
the launcher to `GalleryActivity`.

Implementation note: `sendTouch` needs a contact frame with pressure 1024, a
90 ms dwell, and a release with pressure 0 on the same identifier. Earlier
attempts with pressure 120 and no dwell only woke the display.

## Gate: exact ADB targeting

```
wsctl verify-target --name a
```

```
instance       : a
expected serial: emulator-5554 (console 5554, adb 5555)
engine alive   : True (pid 9612)
get-serialno   : emulator-5554
adb devices    : emulator-5554 device product:sdk_phone64_x86_64 ... transport_id:12
other emulators: emulator-5556 ... (excluded from this operation)
```

The CLI rejects any serial that is not `emulator-<4-5 digits>` (unit tests
cover physical serials, network serials, and injection attempts).

## Gate: authenticated controller access and binding

Engine startup line (from `logs/a.engine.log`):

```
Started GRPC server at 127.0.0.1:8554, security: Local, auth: +token
```

```
wsctl controller-probe --name a
```

```
controller port : 8554
discovery file  : %TEMP%\avd\running\pid_<qemu-pid>.ini
advertised id   : avd=ws_a adb=5555 build=16428233
engine bind line: 127.0.0.1:8554
socket endpoints: 127.0.0.1
token           : 88 chars from discovery (value not logged)
no-token call   : Unauthenticated
forged-token    : Unauthenticated
authed status   : version=37.2.12.0 (37.2.12-16428233) booted=True
authed frame    : 1,199,010 bytes (png)
identity match  : True
binding         : loopback-only
lan probe       : refused/unreachable
```

Findings:

- The gRPC credential is **not** `~/.emulator_console_auth_token`. The engine
  advertises a per-launch 88-character token in
  `%TEMP%\avd\running\pid_<qemu-pid>.ini` under `grpc.token`. The path is
  obtained from the instance's own console (`adb emu avd discoverypath`), so the
  token always belongs to that exact process.
- The engine help text for `-grpc-use-token` says "use the emulator console
  token", which is misleading for build 16428233. This is recorded as a
  compatibility finding, not followed as documentation.
- Sending `authorization: Bearer <console-token>` yields
  `Unauthenticated`; the discovery token with the `Bearer` scheme succeeds.
- A TCP connect to the host's LAN address on the controller port is refused.

## Gate: named snapshot save and restore

```
wsctl snapshot-save --name a --tag phase0-152633
# ... press Home on the guest ...
wsctl snapshot-load --name a --tag phase0-152633
```

Harness run `run-20261110-152217`:

| Measurement | Value |
| --- | --- |
| Save | 5.7 s |
| Load | 5.5 s |
| Saved focus | `GalleryActivity` window `db024e6` |
| Focus after Home | `QuickstepLauncher` window `eedd2ee` |
| Focus after load | `GalleryActivity` window `db024e6` (same window) |

Earlier manual runs measured save 5.0-5.2 s and load 5.5-6.1 s. Disk cost is
about 1.6-1.7 GiB per named snapshot at 2 GiB guest RAM.

## Gate: two independent AVDs

```
wsctl create --name b
wsctl start --name b
wsctl wait --name b
```

| | A | B |
| --- | --- | --- |
| AVD | `ws_a` | `ws_b` |
| Console / ADB | 5554 / 5555 | 5556 / 5557 |
| Controller | 127.0.0.1:8554 | 127.0.0.1:8555 |
| Discovery token | separate, per launch | separate, per launch |
| Guest data | `D:\EmulatorWorkspace\avd\ws_a.avd` | `...\ws_b.avd` |

Independent input check: tapping Gallery on B changed B's focus while A stayed
on the same `GalleryActivity` window hash. Both `controller-probe` runs show
distinct tokens, ports, and `avd=` identities.

## Gate: agent access is off by default

```
wsctl frame --name a --agent --agent-name phase0-harness --out denied.png
# exit code 4
DENIED [agent_access_disabled] Agent access is disabled. Pair a client from the
desktop to create a scoped grant.
```

Journal entry (`journal/operations.jsonl`):

```json
{"phase":"denied","operation":"frame.capture","callerKind":"Agent",
 "callerName":"phase0-harness","arguments":{"instance":"a","tag":null}}
```

The same policy denies an agent `instance.start` even when a grant carries the
control scope, because the operation requires single-use human approval
(unit-tested).

## Gate: Magisk boot-path probe

### Boot artifacts the engine actually consumes

QEMU argv from the engine log:

```
-kernel <image>\kernel-ranchu
-initrd <avd>\initrd                 # regenerated per launch
-drive ... file=<image>\system.img,read-only
-drive ... file=<image>\vendor.img,read-only
-drive ... file=<avd>\encryptionkey.img.qcow2
```

There is no `boot.img`/`vendor_boot.img` in the image. The engine builds the
per-AVD `initrd` from `ramdisk.img` and appends an Android bootconfig section
(~1,913 bytes, `#BOOTCONFIG`, `androidboot.*`, ending in the `++f` trailer).

`ramdisk.img` format (tools/probe_ramdisk.py):

```
container       : lz4-legacy(2 frames)
frame 0: 4,135,424 bytes, 17 cpio entries   # generic ramdisk, owns /init
frame 1:   265,472 bytes, 17 cpio entries   # first_stage_ramdisk/fstab.ranchu
init    : 4,132,440-byte ELF at /init
```

### Substitution result

The engine exposes `-ramdisk <file>` (`ramdisk image (default <system>/ramdisk.img)`).
A locally repacked ramdisk appended one line to
`system/etc/ramdisk/build.prop`:

```
ro.bootimage.ws_probe=phase0-harness
```

Boot with `wsctl start --name b --ramdisk <repacked.img>`:

```
boot_completed=1 in 93.8 s
adb -s emulator-5556 shell getprop ro.bootimage.ws_probe
phase0-harness
```

Then `wsctl start --name b --ramdisk stock` restores the stock input and boots
again (146.0 s in the harness because it cold-boots with the second guest
still running).

Conclusions:

- The ramdisk is an **unsigned boot input** for this engine. No signature or
  AVB check rejected the modified archive; only the system/vendor vbmeta digest
  is passed to the guest as `androidboot.vbmeta.*`.
- Replacing files in the generic ramdisk frame is sufficient to change guest
  boot behavior, which is exactly the insertion point Magisk needs.
- Two framing details cost a failed boot and are now handled: the ramdisk is a
  concatenation of LZ4 frames (dropping the first-stage frame hangs boot), and
  cpio data offsets must be 4-byte aligned after the name.

### Named blocker

A real Magisk build is still required. Magisk's installer and `magiskinit` are
built on Linux; there is no supported Windows build. This host has no WSL
distribution installed (`wsl -l -v` reports that the subsystem is not
installed), so the probe stops here.

Next action (requires the operator): install a Linux builder, for example
`wsl --install -d Ubuntu` from an elevated prompt, which needs a reboot, **or**
provide a Linux machine/CI runner. That unlocks: pin a Magisk release and commit,
build `magiskinit`, and replace `/init` in the patched ramdisk as the first real
root-lab step. Stock restoration is already proven by `--ramdisk stock`.

## Render-mode comparison (partial)

| Mode | Boot to `boot_completed` | Notes |
| --- | --- | --- |
| `-gpu host` (Intel UHD 620) | 43-70 s single, 69.8/105.5 s for two concurrent | Selected default; responsive UI |
| `-gpu swiftshader_indirect` | 66-95 s | Boots and renders; SystemUI ANR risk under sustained interaction on this CPU |

Frame pacing and dispatch-to-visible latency were not measured with 20+
samples, so the viewport metrics remain open (Phase 1).

## Compatibility matrix rows

| Candidate | Host | Engine / image | Guest | Graphics / snapshots | Root | Status |
| --- | --- | --- | --- | --- | --- | --- |
| First developer profile | Win 11 26H2 26300.9818, i5-8265U, 31.8 GiB, UHD 620 | 37.2.12 (16428233) / `android-35;default;x86_64` r2 | API 35, x86_64, userdebug | host GPU verified; swiftshader verified; named save 5.7 s / load 5.5 s | `adb root` uid 0 verified, `adb unroot` returns 2000 | Verified (Phase 0) |
| Second developer profile | same | same | same | second AVD, own ports/token/data | same image | Verified concurrently |
| Google Play profile | same | official Play image installed (API 35/36) | not booted | not tested | unmodified | Planned |
| Magisk Lab | same | boot input proven; Magisk build pending | API 35 | ramdisk substitution boots | needs Linux Magisk build | In progress |

Also verified on the same image: `adb root` restarts adbd as `uid=0(root)` and
`adb unroot` returns `uid=2000(shell)`; this is image-specific and does not
imply Magisk support.

## Material deviations from `plan.md`

1. **Runtime state outside the repo and off the system volume.** AVDs, logs,
   journal, and snapshots live in `D:\EmulatorWorkspace` via
   `EMULATOR_WORKSPACE_HOME`. The default remains
   `%LOCALAPPDATA%\EmulatorWorkspace`. Reason: the C: volume had 5-14 GiB free
   during testing; two AVDs plus snapshots plus a 2.5 GiB quickboot image per
   AVD exhausted it. The emulator itself refuses snapshot saves when free space
   is low ("Not enough disk space").
2. **GPU default is `-gpu host`, not `swiftshader_indirect`.** Host rendering
   booted 20-30 s faster and avoided SystemUI ANRs. Software rendering remains
   selectable with `--gpu swiftshader_indirect` and is recorded separately.
3. **Interim engine host process.** Phase 0 launches `wsctl engine-host`, which
   owns the engine's stdio and records PID plus creation time. The Phase 1
   supervisor replaces it; the CLI already reconciles orphaned hosts by verified
   identity.
4. **Lab profile** for both AVDs: 2 vCPU, 2 GiB RAM, 2 GiB data partition, no SD
   card, no audio, cold boot, `-no-snapshot-save`. Animations are disabled and
   the screen is woken by the harness only to make checks deterministic.
5. **AVD home is workspace-local** (`ANDROID_AVD_HOME=<root>\avd`), so the
   user's existing `~/.android/avd` and the `QuickPastePremiumQA` AVD are never
   touched.
6. **The gRPC token comes from the engine discovery file**, not the console
   token file. See the finding above.
7. **Quickboot snapshot cost.** Despite `-no-snapshot-save` and
   `firstboot.saveToLocalSnapshot=no`, the engine writes
   `snapshots\default_boot\ram.img` (~2.5 GiB at 2 GiB guest RAM) during a cold
   boot. Disk budgets must include it per AVD.

## Reproduce

```powershell
powershell -ExecutionPolicy Bypass -File tools\host-baseline.ps1
powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1 -Root D:\EmulatorWorkspace
```

The harness exits non-zero if any check fails and writes `summary.json`,
`transcript.txt`, frames, target/controller reports, and the ramdisk probe into
a timestamped run directory.

Latest full run: `artifacts/phase0/run-20261110-152217` - 26/26 checks passed,
0 failures.

## Open gates

| Gate | State | Next action |
| --- | --- | --- |
| Phase 0 exit gate | Verified except frame-pacing measurements | Measure 20+ frame intervals and input latencies per the benchmark plan |
| Magisk boot input | Verified substitution; Magisk itself pending | Provide a Linux builder (WSL2) and pin a Magisk build |
| Phase 1 first-run SDK setup | Not started | Begin desktop/setup work; the engine and target contract are now fixed |
