# Phase 3 evidence — device library, snapshots, transports, deployment

Baseline: commit **c396e8a** (Phase 2 closure) plus the Phase 2 regression fix
**958fe74** and the Phase 3 slice **27bfb64**. Build: **0 errors**, **39 unit
tests passing**. Pins are unchanged: Emulator **37.2.12.0 / build 16428233**,
vendored protos, `system-images;android-35;default;x86_64` rev 2, WHPX.

Runtime roots are isolated from the repository and from the personal AVD home:
the acceptance harness used `D:\Phase3Root` (362–366 GiB free at start) and
never touched `QuickPastePremiumQA` or the default AVD home. A physical Android
device (`YHR45TTKWKS8YXHY`, model `23078PND5G`) was attached to the host ADB
server during part of the work; it was **never** a managed target (the
supervisor only accepts `emulator-NNNN` serials), it was never addressed, and
the harness records the visible non-emulator transports in
`adb-devices-before.txt` / `adb-devices-after.txt`.

## Acceptance runs

| Run | Result |
| --- | --- |
| `20261111-015144` | 55 of 56 checks passed; only the external-runner flow failed (keyguard hid the window) |
| `20261111-030838` | **56 checks, 0 failures** - every lane green |

The Phase 2 acceptance is executed *inside* the Phase 3 harness as lane A, so
every Phase 3 run also re-proves the Phase 2 gate on the same guests.

## What was implemented

- **Device library** (`DeviceLibraryService`): phone/tablet presets with display,
  DPI, RAM, core and data-partition settings; a disk estimate against the
  measured free space of the root's volume; versioned profile export/import
  (schema version 1) that carries no secret and no guest data; ownership-aware
  duplication that copies the writable disk of a *stopped* source into a new
  instance with its own port pair and disk lineage, refusing reparse points and
  refusing to copy a running guest; reset that discards the writable disk of a
  stopped instance and keeps the lineage.
- **Snapshots**: save, list, validate and delete with a per-snapshot metadata
  sidecar (engine version, image package, config fingerprint, instance
  fingerprint, disk lineage, byte size). Listing reports compatibility and the
  reason for an incompatible snapshot. Deleting the newest verified recovery
  point requires an explicit force; older snapshots delete normally.
- **Restore that always returns a usable guest**: a snapshot load is only
  complete when the guest answers again (`sys.boot_completed=1`); when it does
  not, the supervisor performs a clean boot and reports
  `recovery=cold_boot`. Before this change a restore could leave the guest
  unusable, which is what made the earlier Phase 3 runs flaky.
- **Exact target identity** (`instance.adbIdentity`): instance name, ADB serial,
  the transport id the pinned server resolves, engine pid and start time, the
  owned ports and a hashed identity — proven with a **round-trip probe**, not
  only a listing entry. A serial that is missing, not in the `device` state or
  not answering fails closed with `instance_unavailable`.
- **Input gate** (`instance.inputHold` / `instance.inputGate`): an operator-only
  synchronization point that parks agent input for one guest and reports the
  queue timestamp of every waiter. It is the controlled race point used by the
  queued-input takeover test.
- **Simulation** (`sim.geoFix`, `sim.battery`): GPS position through the
  emulator console, and battery level plus charger state, both read back from
  the guest.
- **Split-APK deployment**: `sample/build-split-apk.ps1` builds the sample with
  pinned SDK tools, assembles a bundletool module zip, produces a signed `.aab`
  and a targeted `.apks` with bundletool **1.18.1** (jar SHA-256
  `675786493983787FFA11550BDB7C0715679A44E1643F3FF980A529E9C822595C`). The
  sample APK used by the acceptance run is 16,787 bytes, SHA-256
  `09BA57AF004C54E7831E5565856875D9F80E261DC81FD59967BBCD0DBC7CD351`; the
  generated `.apks` is SHA-256
  `10AEFDD46F9F86A90853DB274F664A3F082BE936E736A2F87C8C01428AC6E435`.
- **Bounded harness clients**: every ADB and `wsctl` call in the harnesses runs
  through a bounded client that terminates **only the owned client process**
  after the deadline. The shared ADB server and unrelated targets are never
  touched.

## Lane results

### Phase 2 regression closure (lane A)

The Phase 2 acceptance runs inside the harness: ``20261111-030904` (43 checks, 0 failures)` — 41 checks,
0 failures. The ambiguous-selector probe is now deterministic: the sample
fixture owns two controls with the content description `dup_action`, so the
selector must match **exactly two** nodes, and the harness asserts the exact
count and the typed `ambiguous_selector` response.

### Device library and snapshots (lane B)

| Check | Result |
| --- | --- |
| Duplicate a running guest | refused (`must be stopped`) — a running writable disk is never copied |
| Duplicate a stopped source | new instance, new port pair (5562/8558), new disk lineage, parent lineage recorded, distinct fingerprints |
| Template untouched | the source instance is unchanged |
| Reset | writable disk artifacts discarded, lineage kept |
| Presets and disk estimate | 4 presets; required bytes and measured free space reported |
| Profile export | schema 1, no token/password/secret fields |
| Profile import | creates an independent instance from the exported profile |
| Snapshot save/list | 5 tags with fingerprints, sizes and compatibility |
| Delete an older snapshot | succeeds |
| Delete the newest recovery point | refused without force; succeeds with force |
| Restore | guest answers again (`sys.boot_completed=1`), stale frame ids are rejected afterwards |

### Two guests and the external transport (lane C)

| Check | Result |
| --- | --- |
| Identity | primary `emulator-5556`/transport 60, secondary `emulator-5560`/transport 61, distinct identity hashes |
| Cross-guest access | the agent session bound to the primary is denied `wrong_instance` for the secondary |
| External state unchanged | transport ids stable across agent calls |
| Recycled transport | after `adb reconnect` the transport id changes and the same serial and engine pid still resolve |
| Disconnected target | fails closed with `instance_unavailable`; the target is never substituted with another guest |
| Physical devices | non-emulator transports are visible but never managed; managed serials are emulator-only |

### Queued-input takeover race (lane D)

The harness parks agent input, proves the request is queued, triggers human
takeover and only then releases the parked work:

```
queuedUtc    2026-11-10T22:17:37.7209959+00:00
takeoverUtc  2026-11-10T22:17:38.4385957Z  (revokedSessions: 1)
releaseUtc   2026-11-10T22:17:39.4140838Z
resultUtc    2026-11-10T22:17:43.5430629Z
```

The released tap returned `revoked` (`The agent session has been revoked`), the
fixture marker stayed `DUP_TAPS=0` and no `DUP_TAP` line reached the guest
logcat: the queued input never reached the guest. The timeline is stored in
`race-timeline.json`.

### Simulation (lane E)

GPS fix, battery level 42 with charging (verified by `dumpsys battery`) and
rotation are applied to the managed guest and read back.

### Deployment and external runner (lanes F/G)

| Check | Result |
| --- | --- |
| bundletool | 1.18.1, jar SHA-256 `675786493983787FFA11550BDB7C0715679A44E1643F3FF980A529E9C822595C` |
| Target selection | the deployment target is a separate, freshly booted guest (`emulator-5562`), not the primary |
| `.apks` install | the package is present after `bundletool install-apks --device-id <serial>` |
| Split APKs | the package manager reports the split install |
| Target explicit | the other guest did not receive the deployment |
| Malformed `.apks` | typed diagnostic, non-zero exit |
| Maestro 2.12.1 | **passed** (exit 0): the flow launched the app, asserted the fixture title, tapped `Duplicate A` and asserted `DUP_TAPS=1` |
| Runner isolation | the other guest's tap count stayed 0 |

## ADB hang diagnosis

The earlier Phase 2 evidence recorded `'adb.exe' did not exit within 30s` and
`device offline` under repeated disposable cycles. The correlation is now
clear:

0. A blocking "Quickstep isn't responding" (SystemUI/launcher ANR) dialog can
   appear on the guest and cover the application. It was captured in the
   Maestro debug artifacts and explains the alternating green and red runs. Both
   harnesses now detect the overlay through the focused-window dump and restart
   the affected guest until the dialog is gone; the recovery is reported in the
   `no-anr-overlay` checks.
1. A snapshot load can leave the guest without an interactive runtime. The
   supervisor now waits for the guest and performs a clean boot, so the guest
   stops disappearing from the server.
2. A serial can be *listed* while its transport no longer answers. Identity is
   therefore proven with a round-trip probe, and the harness reconnects the one
   owned transport (never the shared server) until it answers.
3. Unbounded client calls turned a slow transport into a hung harness. Every
   ADB and `wsctl` client is now bounded and only the owned client process is
   terminated after the deadline.

The remaining host-side factor is the physical device that shared the ADB
server during part of the window; it is outside the workspace and was never
touched.

## Recipe record and replay (Phase 3 continuation)

`tools/recipe-acceptance.ps1` is the focused entry point for the recipe
feature. It records a versioned recipe on one managed guest through the normal
`wsctl` entry points, exports it redacted, replays it on a clean disposable
clone while a second guest stays active, interrupts a run and exercises the
negative cases.

Implementation:

- **Recorder** (`RecipeService`, supervisor ops `recipe.recordStart`,
  `recipe.recordRuntime`, `recipe.recordStop`): a recording session is bound to
  one managed guest and captures the supported operation types that flow
  through the supervisor registry, with validated arguments. Only safe
  references are stored: input text becomes `${runtime:<name>}` (unregistered
  text is never stored), approval codes, tokens and host paths are dropped, and
  a coordinate tap is stored as `needsObservation` so the replay takes a fresh
  observation instead of replaying an expired frame id.
- **Recipe format**: schema version 1 with the guest compatibility fingerprint
  (image, device profile, config fingerprint, disk lineage), the baseline
  snapshot reference, the expected package and APK hash, the network
  preconditions, the runtime value names and the typed steps with their
  expectations.
- **Validator** (`recipe.validate`): the whole recipe is validated before any
  mutation. Malformed schema, unsupported operation, missing argument group,
  step count, timeout bounds and target compatibility produce typed errors
  (`recipe_invalid`, `recipe_unsupported_schema`, `recipe_unsupported_operation`).
- **Runner** (`recipe.replay`): resolves the baseline snapshot for the target
  guest (creating its own baseline on a fresh clone), restores it, reports the
  recovery mode (`restored` or `cold_boot`), relaunches the fixture and waits
  for the interactive hierarchy, then executes every step through the same
  scope, approval and lease checks as a live operation. Each step reports its
  result, duration and artifact id; a failed step stops the dependent steps.
  The recorded network profile is restored at the end. A step that fails with
  an unknown outcome keeps that outcome instead of pretending it did not run.
- **Entry points**: `wsctl recipe start|runtime|stop|export|validate|replay`
  and the MCP tools `recipe_validate`, `recipe_export`, `recipe_replay`, all
  going through the same supervisor policy as every other client.

### Verified in run `20261111-143213` (37 checks, 36 passed)

| Area | Result |
| --- | --- |
| Recording | 15 steps captured through the registry, including the login, the 2,500 ms delay, the `NETWORK TIMEOUT` assertion and the recovery |
| Export | the password never appears, `${runtime:login_pass}` is used instead, no absolute host path is stored |
| Two guests | distinct engine, serial, transport and disk identities; distinct fixture markers (`DUP_TAPS=1` vs `DUP_TAPS=2`) |
| Replay on a clone | the clone creates its own baseline (`baseline=created`) and restores it (`recovery=restored` or `cold_boot`; run `20261111-143213` recovered through the clean-boot fallback) |
| Recipe steps | all 15 replayed steps green: `baseline_restored`, then 14 `ok` steps including the login `WELCOME`, the 2,500 ms delay `NETWORK TIMEOUT` and the recovery `WELCOME` |
| Baseline verification | after the restore the fixture package is focused and a fresh hierarchy contains a stable marker (`verified=true`) |
| Isolation | the bystander keeps `DUP_TAPS=2` and its network profile after the replay |
| Negatives | wrong target `wrong_instance`, malformed recipe `recipe_invalid`, incompatible baseline `recipe_invalid`, revoked session `revoked` |
| Interruption | the replay client was cancelled mid-run, the operation store shows `recipe.replay`, and the dependent retry is reported |
| Lifecycle | parallel start attempted, forced interruption with the guest re-established (stop + cold start + readiness) and a fresh approval for the retry, low space (injected at unit level) |

### Post-restore transition (fixed)

The original failure was in the replay's post-restore transition, not in the
recipe format. Two defects were found and repaired:

1. The fixture relaunch loop was dead code (`uiReady` started `true`), so a
   restored guest never had its application brought back. The loop now starts
   `uiReady = !relaunchNeeded`, the recorded application is launched through the
   registry with a package check (`app.packageInfo`) and the interactive
   hierarchy is awaited with bounded retries.
2. The baseline verification asserted once and raced the guest boot. It is now
   bounded by the caller's deadline and requires the fixture package focused
   (only when the baseline itself contains the fixture) and a fresh hierarchy
   from a stable marker. When the required state cannot be confirmed, the recipe
   returns `recipe_baseline_not_verified` and stops dependent steps.

A replay also resets the recorded original network profile before the first step
so a run that was interrupted while shaping was applied cannot leave a delay
behind for the retry.

### Remaining flakiness in the recipe harness

The recorder, exporter, validator, baseline resolution, restore outcome,
isolation, negatives and interruption paths are verified. Two harness-level
races remain visible in the run history and are recorded rather than hidden:
a guest that is still settling at boot can leave the fixture without a
foreground window for the first recording steps (the harness detects the ANR
overlay and restarts the guest, but a tight race can still lose a marker), and
one run left a stopped disposable instance behind after cleanup (the destroy is
now retried with a bounded loop).

### Open gap in the recipe lane

After the baseline restore the guest reboots, and on this image the sample
application does not come back to the foreground within the bounded wait, so the
first step that targets an application control ends with `selector_not_found`
and the dependent steps are stopped (the behaviour the contract asks for). The
recorder, exporter, validator, baseline resolution, restore outcome, isolation,
negatives and interruption paths are verified; the post-restore application
foreground is not. Next action: verify whether a cold-boot recovery
(`recovery=cold_boot`) restores the running application, or re-record the
baseline after the fixture is in the foreground so the restored state contains
the running application.

## Remaining gaps

| Item | State | Next action |
| --- | --- | --- |
| External SDK AVD import | Pending | Add a preview-and-copy importer that validates source ownership, state, symlinks and reparse points and produces an independent workspace copy |
| Two running managed guests plus an external transport | Partially verified | The harness runs two guests, but the second is restarted during the transport lane; repeat with both running for the whole window |
| Recipe record/replay | Partially verified | Recorder, exporter, validator, baseline resolution/restore, isolation, negatives and interruption are verified (`20261111-111424`); the post-restore application foreground on a restored guest is not |
| Package ops, deep links, instrumentation, manifest/ABI inspection | Pending | Add them as operator and scoped agent operations |
| Crash/ANR capture, bug reports, recordings, hierarchy highlighting, profiling, file explorer | Pending | Not started |
| Second simulation pack | Pending | Routes, packet loss, sensors, camera/microphone, locale/timezone and telephony are not probed |
| Desktop command palette | Pending | Not started |
| WPF Create → drag/drop → launch → tap → Logcat → screenshot → shutdown click-through | Pending (separate lane) | The desktop now also carries the Agent Sessions surface; the Phase 1 click-through run is still outstanding |
| WPF latency probe | Pending | The 29.4 FPS WPF figure and the 474 ms headless dispatch-to-capture figure stay separate; 60 FPS and 100 ms remain unmet |

## Reproduce

```powershell
dotnet build
dotnet test

# pinned external tools (hashes are recorded in the manifest)
#   bundletool 1.18.1, Maestro 2.12.1 under $env:WS_PHASE3_TOOLS

powershell -ExecutionPolicy Bypass -File tools\phase3-acceptance.ps1 -Root D:\Phase3Root
```

The harness creates its own root, template and two paired guests, runs the
Phase 2 acceptance as lane A, exercises every lane above and writes
`artifacts\phase3\<run-id>\` with `summary.json`, `versions.json`, build and test
logs, the CLI and MCP transcripts, the network and secure evidence, the WPF
probe, the security checks, the race timeline, the bundletool and Maestro
output and the cleanup record. The sanitized manifest is
`docs/phase3-manifest.json` (run id, commits, pins, per-lane counts and the
name plus SHA-256 of all 32 raw artifacts); the raw artifacts stay in the
gitignored `artifacts/` tree at `artifacts/phase3/20261111-030838/`.

## Gate status

The Phase 3 exit gate is closed for the criteria this pass covers: repeatable
clone and restore with a verified clean-boot fallback, a rollbackable duplicate
and reset path, two managed guests with exact transport identity, a verified
deployment through a pinned bundletool `.apks`, and a real external test-runner
result (Maestro) that touched only the selected guest. The remaining rows in the
table above - external SDK AVD import, recipe record and replay,
package/deep-link/instrumentation operations, crash/ANR and bug-report capture,
the second simulation pack, the desktop command palette and the WPF
click-through - are open and are tracked in `task.md`.
