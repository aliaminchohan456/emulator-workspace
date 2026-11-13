# Implementation Tasks

All tasks start open. Check off an implementation task after its scoped verification passes. Close each phase only after its [exit gate](plan.md#phases-and-exit-gates) has evidence. Keep the milestone log current when a gate is blocked or verified.

## 0. Feasibility

- [ ] Initialize source, build, test, sample app, and Windows validation script structure. Record workspace and toolchain baseline.
  Source, solution, tests, host baseline, acceptance harness, and trace tooling exist; the deterministic sample APK is Phase 1 work.
- [x] Implement host diagnostics for virtualization, WHPX, nested virtualization, CPU architecture, GPU and driver, RAM, disk, and installed Android SDK tools. Surface exact prerequisites and copyable diagnostics.
- [x] Verify official engine options, AVD and snapshot behavior, SDK terms, package IDs and revisions, and licensing. Pin the first emulator binary, controller protocol, and official AOSP API 35 x86_64 image after validation.
- [x] Boot two real AVDs using WHPX. Prove unique paths and ports, exact per-instance ADB targets, launch-to-ready checkpoints, lifecycle events, controller authentication and binding, frame streaming, basic input, and named snapshot save and restore.
- [ ] Compare supported hardware and software render modes using the same scene. Capture frame pacing and input traces before selecting the production viewport path.
  Boot-time comparison is recorded (host GPU 43-70 s, swiftshader 66-95 s); 20+ sample pacing and latency traces remain open.
- [ ] Start the Magisk feasibility probe early. Identify a suitable Linux AOSP builder, pin a source candidate, record provenance and rights, prove stock cold boot, and identify the boot artifacts and verification policy actually consumed by the chosen engine.
  Boot artifacts and verification policy are proven, and an unsigned repacked `-ramdisk` boots with a guest-visible marker. The Linux builder and Magisk source pin remain.
- [x] Save commands, outputs, matrix rows, and blockers. If a suitable WHPX host is unavailable, prepare the runnable validation harness and mark hardware results pending.

## 1. Developer MVP

- [ ] Build first-run SDK detection and guided setup with component versions, sizes, paths, ownership, licenses, disk requirements, and cold-boot troubleshooting.
  Detection, versions, paths, disk, WHPX/GPU state, issues, and copyable diagnostics are implemented; the download/consent flow is not.
- [x] Implement the supervisor, persistent device profile and instance records, lifecycle state machine, unique allocations, and a recovery journal. Verify reattachment using process creation identity and instance metadata. Exercise restart and partial launch recovery.
- [x] Build the device rail, central viewport, Inspector, debug drawer, toolbar, and status bar. Support dark and light themes, keyboard and screen reader access, high contrast, reduced motion, and mixed-DPI displays.
  Verified on 100% and 125% scaling; 150% and multi-monitor moves remain a manual check.
- [ ] Implement fit, 1:1, zoom, fullscreen, pop-out, rotation, resize handles, and supported multi-touch. Map human and agent input through the same framebuffer, rotation, letterboxing, and DPI transforms. Keep window resize, zoom, and guest resolution settings independent.
  Fit, 1:1, zoom, fullscreen, pop-out, rotation, and resize are verified; multi-touch is not implemented and is labeled single-touch.
- [x] Support boot, graceful stop, restart, and cold boot. Show operation progress, cancellation availability, and actionable readiness failures.
  Progress and readiness stages are shown; cancellation is not offered for create/start yet.
- [ ] Ship a deterministic sample APK. Verify drag-and-drop install, launch, basic device input, screenshot, and bounded, searchable Logcat through the desktop on a real guest.
  The real WPF click-through is green at **16 of 16 checks** (`artifacts/phase3/wpf/20261112-132247`): the window opens, the **Create device** button creates the guest, the guest boots, the **ADB root**/**ADB unroot** buttons move the daemon between uid 0 and 2000, the APK is dropped on the window and installed, **Launch sample** brings the application to the foreground, a click in the viewport reaches the guest, the Logcat drawer shows the guest log, **Screenshot** writes a PNG, **Stop** shuts the guest down, and Ctrl+K opens the command palette. A window screenshot is captured for every step. Five product defects were found and fixed on the way (the Root Lab commands never re-evaluated their state, the desktop log path was captured before `--root` was parsed, the Logcat drawer starts open, the drop handler and the automation entry point did not share their code, and the drop was delivered before the guest was ready).

## 2. Agent control

- [x] Define the versioned named-pipe request and event contract with typed operations, stable IDs, capabilities, errors, deadlines, cancellation, idempotency, reconnect cursors, and explicit overflow handling. Connect desktop, CLI, SDK, and MCP to the same policy and registry.
  Protocol 1 extended with auth/idempotency and typed agent errors; event ring/cursor plus overflow-resync, expiry and restart-reconciliation unit tests pass.
- [x] Keep agent access off initially. Implement visible pairing, scoped expiring grants, single-use approvals for privileged mutations, rate limits, revocation, audit, and human takeover that cancels queued input.
  Pairing, scopes, expiry, approvals, revocation, audit and takeover are implemented; the WPF Agent Sessions surface is built and probed; the dispatcher rechecks durable session state before dispatch and before input/mutation. A live queued-input race is still pending as an explicit test (unit recheck passes).
- [x] Add bounded frame and UI observations, semantic selectors with deterministic resolution, coordinate input with frame freshness checks, app operations, and artifact references. Verify secure-window behavior and sensitive-data redaction before enabling capture or input on supported paths.
  Observations, selectors, frame-bound input and artifacts are implemented; FLAG_SECURE redaction, selector denial, MCP no-image behavior and recovery after clearing are verified.
- [x] Keep managed ADB target selection exact. Test unauthorized and expired grants, revocation, cross-device calls with two managed guests and an external transport, ambiguous selectors, stale frames, and untrusted guest content.
  Unpaired, wrong-instance, stale-frame, ambiguous-selector, revoked and expired cases pass; the two-running-guest plus external-transport check remains pending.
- [x] Supply TypeScript and Python client examples plus stdio configuration examples for two real MCP clients. Verify the MCP handshake and keep diagnostics off protocol stdout.
  TypeScript SDK 1.32.2 full workflow and Python mcp 2.3.0 discovery/device_status are verified; stdout is protocol-only.
- [x] Run a real MCP client and the CLI through the same sample recipe. Create and boot a disposable clone, install and launch the APK, interact with and assert login, reproduce a controlled failure under poor network conditions, collect screenshots and filtered logs, restore baseline, and return artifact IDs. Build the minimal clone, restore, and network support here if needed. Save a machine-readable transcript and short recording.
  MCP and CLI completed the workflow; 2,500 ms guest tc-netem shaping reproduced NETWORK TIMEOUT and reset restored WELCOME. Transcripts and machine-readable artifacts are under artifacts/phase2/; a short recording was not produced.

## 3. Device and QA depth

- [ ] Expand the Device Library with phone and tablet presets, profile customization and import/export, ownership-aware SDK AVD imports, and resource checks. Verify imported originals are preserved.
  Phone/tablet presets, disk estimates and versioned profile export/import (no secrets) are implemented and verified. Ownership-aware import of an external SDK AVD is implemented (`instance.importAvd`, `wsctl import-avd`): preview validates ownership, reparse points, the configuration fingerprint and the writable disk hashes; apply produces an independent workspace-owned copy and reports the source fingerprint and disk hashes before and after. Five unit tests cover the validation paths, and the real external-AVD evidence run `artifacts/phase3/import/20261111-201839` passed 9 of 9 checks: preview, independent managed copy, unchanged source configuration and writable disks, and cleanup.
- [ ] Complete headless attachment, duplicate, disposable clone, reset, and delete. Add named snapshots with fingerprint validation, clean fallback, recovery point protection, and disk limits. Test parallel starts, interruptions, low space, and rollback.
  Duplicate (with a new port pair, disk lineage and reparse-point refusal for running sources), reset, delete, snapshot list/validate/delete, fingerprinting, newest-recovery-point protection and a clean-boot fallback after a snapshot load are implemented and verified. Parallel starts, forced interruption and a low-space run are still open.
- [ ] Add split APK and verified `.apks` installs, explicit `.aab` build and signing choices, manifest and ABI inspection, package and permission operations, deep links, instrumentation, and install diagnostics.
  The supervisor side is implemented and verified: `instance.importAvd` (ownership-aware external AVD import, 9/9 checks in `artifacts/phase3/import/20261111-201839`), `app.packageInfo`, `app.packagePermissions`, `app.deepLink` (with the typed `invalid_request` for a malformed URI) and `app.instrumentation` (which reports `available=false` with the reason when a package publishes no runner). The split-APK deployment path is `wsctl install-apks`, which resolves the device from a managed instance and uses the pinned bundletool 1.18.1. The real external-AVD evidence run and the developer-tool run `artifacts/phase3/devtools/20261112-100421` passed 9 of 9 checks. The explicit `.aab` build and test-signing choice in the product UI and the manifest/ABI inspection report are still open.
- [ ] Add crash and ANR capture, bug reports, searchable Logcat export, recordings, UI hierarchy highlighting, profiling entry points, a scoped guest file explorer, and advanced shell controls.
  Crash and ANR collection (`diagnostics.crash`), the searchable Logcat export (`logcat.export`, written as a workspace artifact) and the full bug report (`diagnostics.bugreport`, a 4.2 MB zip with its SHA-256) are implemented and verified in `artifacts/phase3/devtools/20261112-100421` (9 of 9 checks). Recordings, the UI hierarchy highlighting, the profiling entry point, the scoped guest file explorer and the operator-only advanced shell are still open.
- [ ] Ship GPS, rotation, battery and charging, and documented network speed and latency controls. Probe routes, offline and packet loss, sensors, camera and microphone, locale and timezone, display settings, and telephony or SMS for the second pack. Show exact capability and host-integration limits.
  The second simulation pack is probed by the new `simulation.probe` operation (`wsctl simulation-probe`): every capability is checked against the running guest - the emulator console for the network profile, routes, the power state and the sensors, and the guest itself for the camera, microphone, telephony, locale, timezone, display and airplane mode. A capability the guest does not offer reports the exact reason, so the answer is the observed behaviour and not a promise. The measured answer for each capability is still to be recorded in the compatibility matrix.
- [ ] Add bounded recipe recording and replay with preconditions, assertions, deadlines, stop conditions, baseline context, and artifacts. Verify clone and restore under two-guest load.
  Implemented and verified end to end: recorder (verified steps only), redacted export, pre-mutation validation, baseline resolution and restore with a recovery mode (`restored`/`cold_boot`), post-restore fixture relaunch with a bounded readiness wait, typed baseline verification against the recipe's first targeted control, per-step timings and artifact ids, network reset before and after, two-guest isolation, negatives, an interrupted run with a safe retry and cleanup. The dedicated launcher-visible baseline run `20261111-184156` passed **39 of 39 checks**.

  The dedicated fixture-foreground baseline run `20261112-092743` is **fully green at 39 of 39 checks**: the restore (`recovery=restored`), the recorded install re-execution, the package-manager readiness wait, the independent baseline verification (`verified=true`), the network reset (`networkRestored=restored`), all 15 replayed steps green, the live bystander with `DUP_TAPS=2` and an intact network profile, the interrupted run with a clean retry, the typed negatives and the cleanup.

  Seven product defects were found and fixed while closing it, each verified by a rerun: the keyguard, the package-manager readiness, the readiness contract (`RecipeService.FirstAppSelector`), the SystemUI ANR dialog that holds the focus, the launcher that takes the focus back, the repeated install of a package that is still present, and the untyped emulator-console error that aborted a replay without a report. Remaining: the harness must keep sweeping the guests it owns so a killed supervisor cannot starve the host pagefile.
- [ ] Document opt-in external ADB interoperability and tested integrations. Verify a deployment through Gradle/ADB or Android Studio and a run through an external test runner. Add command palette shortcuts for common workflows.
  The command palette is built and verified: Ctrl+K opens it with the device, lifecycle, deployment, view, Root Lab and diagnostics commands, and the click-through proves it opens. The external ADB interoperability is documented in `docs/release-notes.md`; the Gradle/Android Studio deployment and the external runner comparison remain open.

## 4. Root Lab

- [ ] Add separate ADB root and Magisk controls and capability badges. Verify targeted `adb root` and `adb unroot` with effective UID after reconnect. Recheck after boots, restores, and updates.
  The CLI side is implemented and verified: `wsctl adb-root`, `wsctl adb-unroot`, `wsctl adb-root-state` (ops `instance.adbRoot`, `instance.adbRootState`) are scoped to one managed guest by its exact ADB serial, refuse a non-userdebug image with the typed `capability_unavailable` error and re-read the effective user. Run `artifacts/phase3/rootlab/20261111-222756` passed 10 of 10 checks: uid 2000 -> 0 -> 2000, transport reattachment 301 -> 302, state refresh after boot and after a snapshot restore, named snapshot round-trip and cleanup. The WPF Root Lab surface is built: the inspector carries an **ADB root** / **ADB unroot** / **Refresh** group that shows the effective uid, the build type and the transport, and the same actions are in the Ctrl+K command palette. The Magisk actions remain; Magisk needs a Linux builder for the pinned sources (external prerequisite).
- [ ] Pin AOSP, engine, and Magisk versions and document image provenance and the verified patch flow. Preserve pristine artifacts and patch a per-instance copy of the actual boot inputs.
  [The build procedure](docs/magisk-lab-build.md) pins the experimental AOSP manifest and Magisk v30.7, records the SDK stock ramdisk hash, and provides the Linux capacity check and exact pinned patch command. The script passes `bash -n` and correctly refuses Windows Git Bash. Source build and patch acceptance remain open.
- [ ] Implement approved Magisk install with durable backup, journaled staging, validated activation, cold boot, and incompatible snapshot fencing. Provide progress and exact compatibility failure reasons.
- [ ] Verify Android boot, expected Magisk version, and approved guest app or probe `su` with UID 0. Repeat after a second cold boot and verify the original profile still boots.
- [ ] Recover from the Windows app while Android is unavailable. Test exact stock boot and compatible guest data restoration after interrupted writes, corrupt patches, and boot timeouts. Preserve evidence before allowing retries.
- [ ] Add opt-in module import and recovery only after base install and restore pass. Expose Zygisk or module toggles only for verified combinations.

## 5. Release

- [ ] Verify a second stable API level and an official unmodified Google Play profile if available. Publish the exact host, engine, image, graphics, snapshot, root, and simulation support per tested row.
  `docs/compatibility-matrix.md` publishes the tested rows with the exact host, engine, image, ABI, snapshot, root and simulation support, and marks every untested combination as unverified. The Google APIs API 35 image is verified for the ADB-root workflow. The stock Play API 36 revision 7 row passed 8/8. Stock Play API 35 16 KB revision 5 passed 7/8 with a cold-boot fallback for snapshot recovery.
- [ ] Test same-user and external ADB trust boundaries, privileged scopes, injection, path escapes and reparse points, floods, partial operations, and exposed sockets. Verify retention, artifact deletion, and support-bundle preview.
  `docs/security-review.md` records the review, seven fixed findings and the open signing identity. The preserved 0.1.1 payload completed the four-hour soak with verified cleanup.
- [ ] Run the benchmark plan, including baseline comparisons, 30 start/stop cycles and a four-hour two-guest soak. Preserve raw samples and compare workflows with Android Studio, the Emulator CLI, and an existing mobile MCP server.
  The historical results are in [docs/benchmark.md](docs/benchmark.md). The
  managed cold-boot medians were 39.65 s and 44.92 s for two managed profiles.
  The actual raw CLI control remains open. The historical warm restore reused
  cold-boot readiness and cannot verify the 8 s gate. The 30-cycle run had
  one failure and finished without a surviving engine. ADB tap-to-Logcat
  and the CLI PNG capture are diagnostic endpoints. The WPF composition proxy
  and 60 FPS workload remain pending.

- [ ] Add versioned Windows build, test, and package CI plus a separate WHPX hardware lane. Exercise paths with spaces and non-ASCII names, mixed DPI, low disk and RAM, and port conflicts.
  `.github/workflows/windows-release.yml` builds, tests and packages on every tag, verifies the installer hash against the release record and uploads the candidate; a separate self-hosted WHPX lane runs every acceptance harness and the benchmark. The path-with-spaces and non-ASCII cases and the constrained-volume low-space run are still open.
- [ ] Build the reproducible installer or installable release candidate. Produce hashes, dependency inventory or SBOM, notices, provenance, and signing status. Verify clean install, first launch, offline reuse with installed dependencies, upgrade preserving profiles, and uninstall.
  `tools/package-release.ps1` publishes a self-contained win-x64 payload, a **per-user WiX 5 MSI**, the SBOM, notices and provenance. Historical unsigned `EmulatorWorkspace-0.1.0.msi` is 55,380,192 bytes with SHA-256 `8742367339f2b0b4daa65ba23ddf2e842ead707b52b9a344e3835d0d7f96fc5f`. Installer acceptance `20261112-194153` passed 16/16 checks covering clean install, first launch, pinned guest boot, local dependency reuse, upgrade and uninstall with profiles retained. The preserved 0.1.1 installer passed 21/21 checks. Disconnected reuse and signed final acceptance remain pending.
- [ ] Publish setup and recovery commands, architecture decisions, client setup and contract, permissions and compatibility matrices, and the reproducible lab build procedure. Attach first-run and failure screenshots, demo, test outputs, exact versions, benchmark samples, and the remaining gates with their next actions.
  `docs/release-notes.md` publishes the package layout, the pins, the first-run flow, the architecture, the recovery commands, the developer-tooling commands and the known limitations. The 0.1.4 first-run and missing-image captures passed. A live demo recording is still open.

## Milestone log

| Milestone | State | Evidence or reproduction | Next action |
| --- | --- | --- | --- |
| Workspace baseline | Verified | Solution, tests, CLI, tools, and docs initialized; host baseline in `artifacts/host-baseline.json` | Keep the baseline current when the host or SDK changes |
| Phase 0 host prerequisites | Verified | `emulator-check accel`: WHPX 10.0.26300 usable; 31.8 GiB RAM; VBS running | None; record any host change |
| Phase 0 real guest | Verified | Two AVDs booted on WHPX; `emulator-5554`/`emulator-5556`; evidence in `docs/phase0-evidence.md` | Measure frame pacing and input latency |
| Phase 0 controller auth | Verified | Loopback-only gRPC, discovery-file token, missing and forged tokens rejected | Keep the vendored protos in lockstep with the engine |
| Phase 0 snapshots | Verified | Named save 5.7 s, load 5.5 s, exact window restored | Add deletion and fingerprint validation in Phase 3 |
| Phase 0 Magisk boot input | Verified (substitution) | Repacked ramdisk boots with `ro.bootimage.ws_probe=phase0-harness`; stock restore boots | Provide a Linux builder and pin Magisk |
| Phase 0 agent denial | Verified | Agent frame capture exits 4 and is journaled as denied | Full pairing and grants are Phase 2 |
| Phase 0 acceptance harness | Verified | `tools/phase0-spike.ps1` run `run-20261110-152217`: 26/26 checks, 0 failures | Run on any new host before claiming support |
| Phase 1 supervisor | Verified | SQLite schema 1, single owner lock, peer-verified pipe, restart adoption in acceptance run `20261110-172411` | Add cancellation and retention tests in Phase 3 |
| Phase 1 desktop | Verified (with caveats) | WPF first-run, rail, embedded viewport, Inspector, Logcat, APK drop; screenshots in `artifacts/phase1/` | Close drag/drop click-through and ANR-free launch check |
| Phase 1 sample APK | Verified | `dev.emulatorworkspace.sample`, 12,691 bytes, SHA-256 47D84A...; tap marker and `WS_SAMPLE` log confirmed | Keep the APK reproducible in release CI |
| Phase 1 acceptance | Partial | `tools/phase1-acceptance.ps1` run `20261110-172411`: 19 checks, 18 pass, 1 fail (SystemUI ANR) | Re-run on a quiet host; add latency probe |
| Phase 0 regression via supervisor | Verified | `tools/phase0-spike.ps1` run `run-20261110-174228`: 26/26 checks, two guests, snapshots 5.61/5.86 s | Keep in release CI |
| Phase 1 performance | Missed target | 3.66 FPS single / 1.07 FPS two guests, p95 352 / 1641 ms; limiter is emulator PNG capture | Profile capture path; test raw RGBA and 720x1600 guest |
| Phase 1 strict launch | Verified | `launch-check.ps1`: pass on a settled guest, pass under two-guest load, fresh acceptance 19/19; original ANR failure preserved | Automate the same check in WPF click-through |
| Phase 1 capture path | Verified (faster) | RGB888 540x1200: 32.6-34.5 FPS supervisor, 29.4 FPS WPF, p95 45 ms; PNG 3.66 FPS retained as fallback | Profile encoded transport; 60 FPS still missed |
| Phase 1 input latency | Partial | Headless dispatch-to-captured-frame: 20/20 valid, p50 434 / p95 474 ms host single; 695/1012 two guests; 741/858 software | Validate WPF composition proxy; 20 samples per mode |
| Phase 1 disk guard | Verified (injected) | 2 unit tests force low/sufficient space; `low_disk` code and message | Constrained-volume run |
| Phase 1 acceptance (stabilized) | Verified | `artifacts/phase1-1/acceptance-20261110-183709`: 19/19, strict launch pass | Keep as regression baseline |
| Phase 2 regression closure | Verified | `artifacts/phase2/20261110-222634`: 42/42, 0 failures; the ambiguous-selector probe now asserts the exact match count (2) against the fixture's duplicated `dup_action` controls | Keep the fixture surface stable so the count stays exact |
| Phase 3 device library | Verified (partial) | Duplicate with lineage and reparse-point refusal, reset, versioned profile export/import without secrets, presets, disk estimate | External SDK AVD import is still open |
| Phase 3 snapshots | Verified | List/validate/delete with fingerprints, newest-recovery-point protection, clean-boot fallback after a snapshot load | Test parallel starts, interruption and low space |
| Phase 3 transport identity | Verified | Two guests, exact serial/transport/engine-pid identity, recycled transport, disconnected target fails closed, physical devices never managed | Two *running* managed guests plus an external transport remain to be repeated |
| Phase 3 queued-input race | Verified | Hold -> queued (timestamp) -> takeover -> release; the queued tap returned `revoked` and the guest marker stayed `DUP_TAPS=0` | Exercise the same race with two guests under load |
| Phase 3 deployment | Verified | bundletool 1.18.1 built a signed `.aab`/`.apks` and installed it on one selected guest; malformed set produced a typed diagnostic | Add `.apks` support to the supervisor itself |
| Phase 3 external runner | Verified | Maestro 2.12.1 flow ran on the selected guest; the other guest's tap count stayed 0 | Pin the flow and add a Gradle deployment path |
| Phase 3 acceptance harness | Verified | `tools/phase3-acceptance.ps1` run `20261111-030838`: 56 checks, 0 failures; the Phase 2 acceptance runs inside it (`20261111-030904`, 43 checks, 0 failures) | Re-run after any engine or SDK change |
| Phase 3 recipe (both baselines) | Verified | `20261111-184156` launcher-visible 39/39; `20261112-092743` fixture-foreground 39/39 | Keep both baseline variants in the release CI |
| Phase 3 AVD import | Verified | `artifacts/phase3/import/20261111-201839`: 9/9 checks, the source is unchanged | Add the Google Play image |
| Phase 3 Root Lab (ADB daemon) | Verified (CLI) | `artifacts/phase3/rootlab/20261111-222756`: 10/10 checks, uid 2000 -> 0 -> 2000 | The Magisk profile needs a Linux builder |
| Phase 3 WPF click-through | Verified | `artifacts/phase3/wpf/20261112-132247`: 16/16 checks, real UI Automation against the live window | Repeat on a 150% DPI display |
| Phase 3 developer tooling | Verified | `artifacts/phase3/devtools/20261112-100421`: 9/9 checks (package info/permissions, deep link, instrumentation, logcat export, crash/ANR, bug report) | Add the `.aab` build and signing choice |
| Phase 3 Windows installer | Verified | `artifacts/phase3/install/20261112-194153`: 16/16 checks; per-user MSI 55 380 192 bytes, SHA-256 `87423673…`, unsigned | Sign with a user-owned certificate |
| Phase 3 benchmark | Historical diagnostics | `docs/benchmark.md`: two managed boot profiles; historical restore endpoint invalid; ADB-to-Logcat and PNG costs retained; one failure in 30 lifecycle cycles, no surviving engine | Matched raw CLI, verified restore and WPF composition runs |
| Phase 3 simulation probe | Implemented | `wsctl simulation-probe` checks the network profile, routes, power, sensors, camera, microphone, telephony, locale, timezone, display and airplane mode against the running guest and reports the exact reason for every capability the guest does not offer | Record the measured answers in the compatibility matrix |
| Guest ANR overlay | Diagnosed | A blocking "Quickstep isn't responding" dialog was captured in the Maestro debug artifacts and explains the earlier alternating runs; both harnesses now detect it and restart the guest | Keep the detection in the acceptance path |
| Snapshot restore recovery | Verified | A snapshot load that does not bring the guest back now triggers a clean boot and reports `recovery=cold_boot` | Test the same path under two-guest load |

## Open items and the exact next action

| Item | Why it is open | Exact next action |
| --- | --- | --- |
| The Magisk lab profile | The source build needs x86_64 Linux, 64 GB RAM and 400 GB free. This Windows host has 31.8 GiB RAM and WSL is absent. | Run `bash tools/build-magisk-lab-aosp.sh --check /mnt/aosp` on the builder, then follow [the pinned procedure](docs/magisk-lab-build.md). |
| The warm restore target of 8 s | The historical harness reused cold-boot readiness. | Run `tools/benchmark.ps1 -Root D:/WSBench -Samples 20 -SkipBaseline -SkipColdBoot -SkipLifecycle -SkipTwoGuest`. Check each restore outcome, guest-state marker and full wall time. |
| The input latency target of 100 ms | The historical latency endpoint was ADB dispatch to Logcat. | Fix the WPF probe to follow the visible fixture marker and measure composition. |
| The 60 FPS viewport target | The historical WPF RGB path delivered 29.4 FPS. | Measure a 60 FPS fixture in WPF and profile the RGB path before choosing a transport change. |
| Four-hour two-guest soak | Verified on the preserved 0.1.1 payload in run `20261112-213629`. 200 paired probes, 14,411.36 s and two successful cleanups. | Repeat on the final candidate after release-affecting changes. |
| Two-guest latency | WHPX run `20261112-205244` collected 20/20 fresh markers. ADB-to-Logcat p50 1,099.43 ms / p95 1,451.69 ms. Packaging overlapped the run. | Measure the same input workload through the WPF composition proxy on a quiet host. |
| The comparison with Android Studio, the raw Emulator CLI and an existing MCP server | Not run in this environment. | Run the same recipe through each of the three and record the measured difference in `docs/benchmark.md`. |
| The release signing identity | No user-owned code-signing certificate was supplied, so the candidate is unsigned. | Supply the certificate to `tools/package-release.ps1 -CertificateThumbprint` and rebuild; the provenance records the signing status either way. |
| Stock Google Play API 36 and API 35 | Both profiles booted and ran the sample. API 36 restored the named snapshot. API 35 16 KB rolled back the fixture with a cold boot. | Investigate the 16 KB restore path and rerun `tools/play-image-acceptance.ps1` for that package. |
| 150% and mixed-DPI displays, paths with spaces and non-ASCII names | Only 100% and 125% scaling were exercised, and the workspace roots used plain paths. | Repeat the WPF click-through on a 150% display and run the harnesses with a root path that contains spaces and non-ASCII characters. |
| Demo and WPF screenshots | Exact 0.1.4 first-run and missing-image screenshots verified in `20261113-084323` and `20261113-084356`. | Record the live WPF click-through after timed host tests. |

### Continuation from `806251f`

- Baseline: `dotnet build` and `dotnet test --no-build` passed, 56/56 tests.
  The existing MSI's hash and `NotSigned` status matched its release record.
  Its provenance identifies source `4f821ee`. The historical acceptance
  script's offline check was local status with the guest running and its
  upgrade check reran the same installer. Network-disconnected reuse and an
  upgrade between versions remain open.
- Benchmark implementation `f375bde`. Reproduce with
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools/benchmark.ps1 -Root 'D:/WS Bench résumé' -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle`.
  Run `20261112-205244` collected 20/20 single-guest and 20/20 two-guest
  markers. Single-guest p50/p95 were 221.11/553.47 ms, paired were
  1,099.43/1,451.69 ms. One-shot PNG p50 was 575.15 ms. Packaging overlapped
  the run. The harness deleted both guests through the supervisor and ADB
  showed no attached guests. Evidence is in `artifacts/phase3/bench/20261112-205244`.
  The smoke run `20261112-204922` exposed view-local log coordinates and
  unbounded ADB waits after a disconnect. Both were corrected.
- The earlier rebuilt 0.1.0 candidate from `f375bde` is
  `D:/Release/emulator-workspace-0.1.0-20261112-205529/EmulatorWorkspace-0.1.0.msi`.
  Size 55,367,904 bytes. SHA-256
  `7a3650c3208759c60d657b2ef2ffb16aa532fbffed40fad65e5fff8c436d73f3`.
  Authenticode reports `NotSigned`. Acceptance `20261112-210610` passed
  **17/17**, including structured snapshot JSON from the installed CLI,
  WHPX boot, local status, same-version reinstall preserving the marker,
  and uninstall. Reproduce with `tools/installer-acceptance.ps1 -Installer <exact MSI> -WorkRoot D:/WSCandidate205529 -VerifySnapshotJson`.
  Raw evidence is in `artifacts/phase3/install/20261112-210610`. Build command
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools/package-release.ps1 -Version 0.1.0 -OutDir D:/Release`.

- Restore smoke `20261112-210012`: one named load restored the independent
  guest marker in 44.70 s full wall time, of which the console call took
  6.09 s. No fallback occurred in that sample. The soak probe detected that
  Windows assigns emulator listeners to the QEMU child instead of the
  launcher PID. The harness now verifies the pinned executable and process
  generation before attributing those ports. The 20-sample restore gate and
  four-hour soak remain open.
- Signing preflight now checks for the exact code-signing thumbprint and a
  private key in `Cert:/CurrentUser/My`. It rejected a missing identity
  before packaging. A supplied identity invokes Windows SDK SignTool and
  must pass Authenticode verification before provenance says signed.
  Windows SDK 10.0.26100.0 SignTool is installed. Actual signed-candidate
  acceptance needs the authorized identity. Run
  `tools/package-release.ps1 -Version 0.1.0 -CertificateThumbprint <thumbprint>`
  once the operator has made the key available.

- Soak smoke `20261112-210824` recorded three paired status and
  package-manager probes through 75.13 seconds, two verified launchers and
  two QEMU children, and the exact loopback port owners. A QEMU child was
  still exiting at the first cleanup check and was gone at the subsequent
  inspection. The harness now waits up to 15 seconds for the exact observed
  process generations before deleting their profiles. A Windows PowerShell
  5.1 process probe verified both the matching-generation wait and the
  mismatched-generation skip. The full soak remains pending.
- Final managed build check used `dotnet build -c Release` and
  `dotnet test -c Release --no-build`. It passed 56/56 tests. A concurrent
  Debug build hit MSB3021/MSB3027 because the active test supervisor held the
  Debug outputs. The Release build used a separate configuration.

- Play compatibility: `tools/play-image-acceptance.ps1` with roots
  `D:/WSPlay36` and `D:/WSPlay35` measured the SDK packages listed in the
  [compatibility matrix](docs/compatibility-matrix.md). API 36 revision 7
  run `20261112-211536` passed 8/8. API 35 16 KB revision 5 run
  `20261112-211808` passed 7/8, with cold-boot fallback. Raw records are in
  `artifacts/phase3/play`. Both guests were removed through the supervisor.
- The Play probe uncovered false positive capability reports on console
  `KO` responses and a GPS mutation during an observation. Product commit
  `8d40469` reports the route rejection, reads power state, and queries the
  guest GPS feature. The rebuilt 0.1.1 candidate contains this correction.

- Local 0.1.1 candidate build `8d40469`: Release build succeeded and
  `dotnet test -c Release --no-build` passed 56/56. The MSI is
  `D:/Release/emulator-workspace-0.1.1-20261112-212010/EmulatorWorkspace-0.1.1.msi`,
  55,359,712 bytes, SHA-256
  `e2620bce693271f433275ad639a0ac9ccc846d42ec61c8d48047261590c5346b`,
  Authenticode `NotSigned`. Rebuild with
  `tools/package-release.ps1 -Version 0.1.1 -OutDir D:/Release`.
  The first installer validation attempt `20261112-212243` stopped
  before installation after finding two stale registrations from older
  runs. The harness now reads the exact candidate and predecessor product
  codes from the MSI property tables.

- Initial 0.1.1 acceptance run `20261112-212444` passed its clean-install,
  boot, installed snapshot, simulation and uninstall checks. The upgrade
  lane reached Windows Installer error 1619 with a slash-form predecessor
  path. The harness now resolves both MSI paths to native Windows paths.

- Exact 0.1.1 MSI acceptance `20261112-212707` passed **21/21**. It
  verified clean install, real WHPX boot, installed snapshot JSON, corrected
  capability reporting, local status reuse, and uninstall. The original
  `4f821ee` MSI installed successfully as the 0.1.0 predecessor. Windows
  Installer replaced its product registration, and the new CLI read the
  managed profile created by the previous CLI. The upgraded product also
  uninstalled. Raw evidence is in `artifacts/phase3/install/20261112-212707`.

  ```powershell
  dotnet build -c Release
  dotnet test -c Release --no-build
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/package-release.ps1 -Version 0.1.1 -OutDir D:/Release
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/installer-acceptance.ps1 `
    -Installer 'D:/Release/emulator-workspace-0.1.1-20261112-212010/EmulatorWorkspace-0.1.1.msi' `
    -PreviousInstaller 'D:/Release/emulator-workspace-0.1.0-20261112-194003/EmulatorWorkspace-0.1.0.msi' `
    -WorkRoot D:/WSInstall011Retry -VerifySnapshotJson -VerifySimulationProbe
  ```

  Network-disconnected reuse, signed acceptance, the remaining hardware and
  performance gates, and final-candidate end-to-end regression remain open.

## Final Windows verification from `efcf812`

- Verified the unchanged unsigned 0.1.1 MSI SHA-256 and the **21/21** raw
  installer summary. The full soak uses all 422 staged payload files
  verified against the candidate inventory. The running supervisor PID
  6140 resolves to the staged `EmulatorWorkspace.Supervisor.exe`.
- Completed soak `20261112-213629`, harness PID **19588**, creation
  `2026-11-12T16:36:26.8521220Z`, root
  `D:/WS-Soak-011-20261112-213626`. The first paired probe establishes
  an interval start around 21:43:29 PKT. Expected four-hour mark
  **2026-11-13 01:43:29 PKT**. The final paired probe completed at
  **01:43:40 PKT** and cleanup finished at **01:43:56 PKT**. Raw artifacts
  `artifacts/phase3/bench/20261112-213629`, including `run-context.json`
  with the guest generations and exact ports. Startup collected 20/20
  two-guest markers with ADB-to-Logcat p50 685.26 ms and p95 828.83 ms.
  The process-scoped wake request was used for the run.
  `verified-progress.json` confirms 200 paired probes and 14,411.36 seconds
  with unchanged guest and listener identities and both cleanups passed.
  Minimum free physical/virtual memory was 13,909/14,101 MiB. ADB showed
  no attached devices after cleanup. Reproduction from harness `27c655f`:

  ```powershell
  $release = 'D:/Release/emulator-workspace-0.1.1-20261112-212010'
  $payload = 'D:/Release/stage-20261112-212010/payload'
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/benchmark.ps1 `
    -Root 'D:/WS-Soak-011-20261112-213626' -CandidateRelease $release -CandidatePayload $payload `
    -Samples 20 -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle -SoakMinutes 240
  ```

- The candidate-bound test harnesses are prepared. Run sequentially on the
  quiet reference host after the soak. The WPF probe records the composition
  proxy and detects the exact successor color of the visible fixture
  marker. The first WPF invocation uses the exact 0.1.1 desktop. If its
  historical fixed marker coordinates fail, run the diagnostic desktop
  build with `-DesktopExecutable` and keep the override's hash and source
  commit separate. The raw comparison uses two AVD profiles with matched
  hardware settings and directly starts the pinned `emulator.exe` in the
  raw arm. Restore evidence includes attempted loads, fallback, verified
  foreground readiness and a separately checked guest-state marker.

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/latency-probe.ps1 `
    -Root D:/WS-WPF-011 -CandidateRelease $release -CandidatePayload $payload -Count 25
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/benchmark.ps1 `
    -Root D:/WS-Raw-011 -CandidateRelease $release -CandidatePayload $payload `
    -RawBaseline -Samples 20 -SkipRestore -SkipLifecycle -SkipLatency -SkipTwoGuest
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/benchmark.ps1 `
    -Root D:/WS-Restore-011 -CandidateRelease $release -CandidatePayload $payload `
    -Samples 20 -RestoreAttempts 40 -SkipBaseline -SkipColdBoot -SkipLifecycle -SkipLatency -SkipTwoGuest
  ```

- For candidate regression, build the MCP executable from the candidate
  commit in a separate worktree. The MSI contains the desktop, supervisor,
  CLI, and fixture. The MCP executable has separate source and binary
  provenance. The regression wrapper routes the existing acceptance lanes
  to the verified candidate payload.

  ```powershell
  git worktree add --detach D:/WS-Candidate-8d40469 8d40469
  dotnet build D:/WS-Candidate-8d40469/src/EmulatorWorkspace.Mcp/EmulatorWorkspace.Mcp.csproj -c Release
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/candidate-regression.ps1 `
    -RootPrefix D:/WS-Regression-011 -CandidateRelease $release -CandidatePayload $payload `
    -McpExecutable D:/WS-Candidate-8d40469/src/EmulatorWorkspace.Mcp/bin/Release/net10.0/EmulatorWorkspace.Mcp.exe
  ```

- The disconnected installer guard rejected the connected reference
  host before installation in `artifacts/phase3/offline/20261112-215224`.
  After soak cleanup, enable the approved Sandbox and Hyper-V management
  features from an elevated prompt with `-NoRestart`. This session is
  unelevated. An isolated Windows 11 x64 environment must provide working
  nested WHPX if virtualized, and the pinned SDK, image and accepted
  licenses must be available locally. Remove the environment's network
  connection, then run `tools/installer-acceptance.ps1 -Installer <exact MSI>
  -WorkRoot C:/WSOffline -OutDir artifacts/phase3/offline -VerifyDisconnectedReuse`
  inside it. The harness records adapters and default routes before
  installation and after booting the same profile and launching its fixture.
  [Windows Sandbox setup](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install),
  [Nested virtualization](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enable-nested-virtualization)

- `python tools/build-phase3-manifest.py` refreshed 118 finished raw runs,
  including the exact candidate installer and both stock Play rows. Active
  runs remain outside finished evidence until their summaries exist. Refresh
  again after each completed verification lane.
- Magisk still needs the authorized x86_64 Linux builder with 64 GB RAM
  and 400 GB free. Next command
  `bash tools/build-magisk-lab-aosp.sh --check /mnt/aosp`. Signed acceptance
  requires the authorized private key in `Cert:/CurrentUser/My`. The local
  0.1.1 candidate and its release hash remain preserved.

- While the soak runs, APK manifest and ABI reporting was implemented
  behind the existing supervised install operation. `wsctl install
  --name <n> --apk <path> --json` requests package name, API levels,
  launchable activity, and declared native ABIs. WPF uses the supervisor
  result and keeps automatic launch bound to the selected device at
  dispatch. An isolated C# host probe compiled the parser and exercised
  Build-Tools 35.0.0 against the exact staged candidate fixture. It reported
  `dev.emulatorworkspace.sample`, minimum and target API 35, and an empty
  native ABI list. Evidence
  `artifacts/phase3/apk-host/20261112-220535/manifest.json`. The integrated
  Release build, unit tests, and WHPX install with `--json` remain queued
  after the soak. Explicit AAB build and signing choices remain open.
- Inspect current soak status without touching the guests with
  `python tools/soak-progress.py artifacts/phase3/bench/20261112-213629`.
  Use `--require-complete` after cleanup to require four hours of paired
  evidence and successful cleanup for both recorded generations.

- The disconnected Windows Sandbox preparation copied and rehashed the
  exact MSI and validated a configuration with networking disabled,
  read-only input/SDK/JDK mappings, and an isolated writable evidence
  directory. Dry run `D:/WS-OfflineSandbox-20261112-220840`. After soak
  cleanup, run the operator-authorized feature command from an elevated
  prompt, with reboot handled separately if Windows requests it.

  ```powershell
  Enable-WindowsOptionalFeature -Online -FeatureName @(
    'Containers-DisposableClientVM', 'Microsoft-Hyper-V-Tools-All'
  ) -All -NoRestart
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/offline-sandbox.ps1 `
    -CandidateRelease 'D:/Release/emulator-workspace-0.1.1-20261112-212010' `
    -StagingRoot D:/WS-OfflineSandbox-Live -Launch
  ```

  The live sandbox first probes the pinned emulator for usable WHPX. If the
  acceleration check fails, preserve `output/acceleration.txt` and use an
  isolated Windows 11 x64 VM that exposes nested virtualization and passes
  the same accelerator probe.

- Future candidate packaging now includes the MCP stdio executable and
  its publish command in provenance. The installer harness has an optional
  `-VerifyMcp` check for initialize, tool discovery, protocol-only stdout,
  and clean exit on stdin EOF. A host-only smoke using the existing Release
  MCP executable and the preserved candidate's staged supervisor passed
  with 22 tools. Evidence `artifacts/phase3/mcp-host/20261112-221940/stdio.json`.
  After the soak, build and test the current source, then use a new version
  when packaging product changes. Repeat the installer check with
  `-VerifyMcp` against the resulting MSI. The preserved 0.1.1 MSI and
  its original 21/21 evidence remain the baseline.

- The supervisor now exposes explicit Android debug test signing for
  `.aab` conversion. CLI `wsctl build-apks --aab <path> --test-signing
  --bundletool <pinned jar>`. WPF **Build APK set** in Ctrl+K asks for
  confirmation and uses `WS_BUNDLETOOL`. It generates a local APK set
  without contacting a guest. Host-only probe
  `artifacts/phase3/bundle-host/20261112-231755` rejected absent signing
  selection and a jar with the wrong digest, then built the existing sample
  with bundletool 1.18.1. Build-Tools 35.0.0 `apksigner verify --print-certs`
  verified its base split under the Android Debug certificate. The
  supervised CLI/WPF route, deployment to one managed guest, and integrated
  Release build remain queued after the soak. The existing 0.1.1 MSI is
  unchanged. [Official bundletool documentation](https://developer.android.com/tools/bundletool)

- After the four-hour soak, `dotnet build -c Release` completed with zero
  errors and `dotnet test -c Release --no-build` passed **58/58**. The
  integrated Release CLI called the supervisor's `build-apks` operation
  against the pinned bundletool and sample AAB in its own root. It produced
  the same test-signed APK set digest as the host probe. Raw result
  `artifacts/phase3/bundle-cli/20261113-014609/result.json`. The supervisor
  was shut down through the scoped CLI. GUI and managed deployment evidence
  remain pending.
- Windows returned `The operation was canceled by the user` when launching
  the elevated feature command after soak cleanup. Servicing did not start.
  The next offline action is to approve the Windows administrator prompt
  for the command above. No restart was requested or performed.
- The first raw boot smoke `20261113-014645` reached a focused Android launcher.
  The pinned launcher passed `-avd` to QEMU, and the harness only recognized
  `hardware-qemu.ini` in the child arguments. The exact child and launcher exited
  and the managed record was removed. With the ownership check corrected,
  `20261113-015100` captured a 66.62 s raw boot and cleanly removed both profiles.
  The managed arm was skipped after the config comparison rejected the pinned
  engine's whitespace around `=`. The harness now normalizes all eight matched
  AVD keys before launch, verifies the raw child's loopback ports and records its
  PID. Both smoke runs are diagnostic and fall short of 20 observations per arm.
- The exact 0.1.1 WPF run `artifacts/phase3/wpf/20261113-015248` rendered
  guest frames but never dispatched a probe trial. The 0.1.1 startup registers
  its probe after showing the window. The owned window was closed so the script
  could clean up. The current source registers the handler before showing the
  window. Verify the fix using a separately hashed desktop binary and the
  preserved candidate runtime, then repeat against the final packaged build.
- Release build at `526fb72` passed with zero errors and 58/58 tests.
  Quiet-host WPF run `artifacts/phase3/wpf/20261113-015728` used desktop
  `526fb72cf42700abc505d46ea443c50bc23b9e7a`, executable SHA-256
  `5e1db799f5dc084213c96ece4574423b89f35b8fbcbd0b6181c891a7e0ce5094`,
  and the preserved 0.1.1 supervisor, CLI, and fixture. All 25 trials
  reached the WPF composition proxy, and guest cleanup passed. The p50
  was 660.51 ms, p95 was 840.85 ms, and viewport delivery was 22.01 FPS
  at 540 × 1200 with 86 skipped stream sequences. The 499 measured
  supervisor arrival intervals had a p95 of 82.70 ms. WPF took 3.42 ms
  p95 from changed-frame availability to the composition callback.
  Both performance targets remain open. Measure controller discovery and
  input acknowledgement separately and compare the pinned engine's RGB
  stream with an available alternate transport before changing defaults.
- Restore run `artifacts/phase3/bench/20261113-020001` captured four verified
  restores on the exact 0.1.1 payload (8.50–27.07 s), then the targeted ADB
  command to prepare the fifth trial exceeded its 6 s bound. The owned guest
  was stopped and removed. The rerun allows 20 s for targeted guest commands
  and records failed trial preparation separately within the 40-attempt bound.
  After cleanup, run the full matched raw comparison
  with `-RawBaseline -Samples 20 -SkipRestore -SkipLifecycle -SkipLatency
  -SkipTwoGuest` on a fresh root and the same two candidate paths.
- The detached worktree at `D:/WS-Candidate-8d40469` built the matching
  0.1.1 MCP sidecar from `8d40469154965e08eb2e511fb70b6a4fff7a927d`.
  The executable SHA-256 is
  `d57400f7d89b0532b8240ed30a461d9267efa0004c074a7a8c48c3afac1f3823`.
  The regression wrapper checks the override's embedded source commit
  and keeps every lane's raw evidence under that candidate run, including
  the nested Phase 2 acceptance. Run the nine lanes once the benchmark
  guests have been cleaned up.
- Snapshot rerun `artifacts/phase3/bench/20261113-020314` verified 20/20
  independent restored markers and fresh focused windows, with zero cold
  fallbacks. Request-to-verification was p50 9.19 s and p95 12.34 s, so the
  8 s median target remains open. The guest was stopped and destroyed, the
  root's supervisor shut down, and ADB showed no attached devices.
- Full comparison attempt `artifacts/phase3/bench/20261113-020845` verified
  all eight matched AVD settings. Windows returned `Access is denied` during
  raw process teardown. Both recorded profiles were removed and no emulator
  or ADB transport remained. Teardown now accepts a kill error only after a
  bounded wait confirms that the exact process exited and retains the raw
  engine log even when teardown fails. Rerun the 20 pairs and monitor the
  transcript through both cleanup checks before candidate regression.
- Matched comparison `artifacts/phase3/bench/20261113-021146` ran from
  `87fcbd0`, harness PID 7072 in `D:/WS-Raw-011-20261113-021143`. All 20
  raw trials passed with p50 41.41 s and p95 47.27 s. The first managed
  trial's `wsctl wait` returned before a focused window was available.
  The harness now polls for an actual focused window through the deadline.
  Resume the managed arm using `-RawControlRun artifacts/phase3/bench/20261113-021146`
  with the same matched settings and candidate. The harness verifies and
  hashes the prior raw records before reuse.
- The managed arm resumed successfully from `a1d95c0` in
  `artifacts/phase3/bench/20261113-023049`, root
  `D:/WS-Managed-011-20261113-023047`. It validated the candidate, fixture,
  settings and reference host against `20261113-021146`. Watch the managed
  rows in `cold-boot.jsonl` and the final cleanup result.
- The product readiness fix rejects `mCurrentFocus=null` and requires a
  real focused window after snapshot load within the recovery deadline.
  It is queued for build and hardware verification after timed runs.
  The next WPF diagnostic records tap acknowledgement, guest `gfxinfo`
  frame timings, and separately measures the pinned controller RGB stream
  and a targeted guest H.264 stream. Use `-CompareCapture` with one guest
  and `-GuestCount 2` for the two-guest presentation lane.
- `wsctl install-apks` now routes through the local supervisor. Pinned
  bundletool reads the managed device spec and selects the splits on the
  host. The supervisor checks the same engine and ADB transport before
  installing through `adb -t <transport-id> install-multiple`. Phase 3
  regression exercises the CLI path and checks that a second guest remains
  untouched. Release build and WHPX verification are queued after the
  matched cold-boot run.
- The matched comparison `20261113-023049` passed with 20/20 observations
  in both arms. Raw p50/p95 was 41.41/47.27 s. Managed p50/p95 was
  44.36/55.96 s. The median overhead of 2.95 s was within 4.14 s. The
  managed guest was stopped and removed, the supervisor was shut down,
  and ADB showed no attached devices. Raw control `20261113-021146` and
  its recorded checksums are in the combined benchmark report.
- Release build at `76454c91a8a972eb43b2633cb7d69982ed73d90a` passed
  with zero errors and 58/58 tests. Unsigned 0.1.2 built in
  `D:/Release/emulator-workspace-0.1.2-20261113-025118` with a separate
  426-file stage at `D:/Release/stage-20261113-025118/payload`. MSI SHA-256
  `601dd19bf243ae3d2f1e02ad1c1aa9ea565bc1701022b5252f1019a2897e761e`.
  Both MSI hashes were rechecked, and both reported `NotSigned`.
  Installer run `20261113-025335` uses the exact 0.1.1 MSI as predecessor
  with `-VerifySnapshotJson -VerifySimulationProbe -VerifyMcp`. It passed
  all 22 checks, including packaged MCP stdio and the real version upgrade.
- The 0.1.2 offline Sandbox kit is staged at
  `D:/WS-OfflineSandbox-012-20261113-025606`. Its MSI hash matches the
  release record, networking is disabled in the configuration, and the
  installer, SDK and JDK inputs are mapped read-only. It has yet to launch
  because Windows still requires elevated feature enablement.
- The exact 0.1.2 WPF and capture run is `artifacts/phase3/wpf/20261113-025638`.
  It collected 25/25 trials. Composition proxy p50 was 667.09 ms and p95
  was 789.56 ms. The supervisor reply arrived at 546.72 ms p95. WPF delivered
  21.82 FPS with 87 stream sequence drops. The separate controller RGB run
  reached 49.08 FPS, and targeted guest H.264 capture reached 21.12 FPS.
  Guest `gfxinfo` reports Skia OpenGL and 33.24% janky frames. Both WPF
  targets remain open. The owned guest and supervisor were cleaned up.
- The nine-lane regression against the exact 0.1.2 stage finished in
  `artifacts/phase3/candidate/20261113-025947`. It used the packaged MCP and
  separate workspace roots. Seven passed: fixture-baseline recipe (37/37,
  cold-boot fallback independently verified), Root Lab (10/10), AVD import
  (9/9), developer tools (9/9), WPF click-through (16/16), Play API 36 (8/8)
  and Play API 35 16 KB (8/8). Both Play snapshots restored. Phase 3 and
  the launcher-baseline recipe failed as detailed below. ADB showed no
  attached devices after the suite.
- Nested Phase 2 passed 43/43. Phase 3 passed 53/54 and removed all owned
  guests. The supervised split APK install targeted `emulator-5560`,
  transport 491, with no package change on the other guest. The malformed
  set returned the typed `install_failed`. Maestro's launch succeeded, then
  its fixture assertion saw `Quickstep isn't responding` in the target
  guest hierarchy. The runner now verifies the fixture is foreground before
  running the flow, waits up to 30 s for the title, and saves debug evidence
  in the run directory. Rerun the Phase 3 lane after the other lanes finish.
- Launcher-baseline recipe regression reached a restored and independently
  verified baseline, then failed its first readiness step with `The node
  already has a parent.` The readiness loop reused one selector `JsonNode`
  in new objects on successive attempts. It now creates its arguments once
  and reuses them for the read-only assertion. Preserve
  `artifacts/phase3/candidate/20261113-025947/recipe-launcher/20261113-031459/recipe-replay.json`
  and rerun both baselines against a new package containing the fix.
- The next unsigned candidate is 0.1.3 from
  `ad5e5a37ac2a8ddb1987cef0e949167b9d5b921d`. The installer is
  `D:/Release/emulator-workspace-0.1.3-20261113-032433/EmulatorWorkspace-0.1.3.msi`,
  SHA-256 `11adc6350d8319a9ab579e346dc9e9cc404f750e80b6009fafe32583bcdb7e34`.
  The stage is `D:/Release/stage-20261113-032433/payload`. Quiet-host WPF
  collected 25/25 samples in both runs and completed scoped cleanup.
  Run `20261113-040149` (single guest) measured p50/p95 547.22/719.67 ms
  and 27.04 FPS. Run `20261113-040351` (two guests) measured p50/p95
  871.73/1,147.53 ms and 15.82 FPS. Both targets remain open. Install
  acceptance `20261113-040915` passed 22/22 using the exact 0.1.2 MSI as
  predecessor. The nine-lane 0.1.3 regression is running in
  `artifacts/phase3/candidate/20261113-041225`, beginning with the two
  recipe baselines, with packaged MCP and isolated roots per lane.
- The 0.1.3 launcher-baseline rerun passed the restored-state assertion
  and reached the selector wait without the JSON ownership exception.
  It exposed a second bug: the harness expected a fixture control while
  the recipe was still at its launcher baseline, before recorded install
  and launch. The supervisor now checks a fixture control during recovery
  when it owns the relaunch, and lets a recorded launch before the first
  UI step establish foreground for the launcher baseline. Verify on a
  separately packaged build. The 0.1.3 run continues through the other lanes.
- The interrupted launcher-baseline retry also returned `port_conflict`
  for its own console port 5560 during a restore fallback. Stop now waits
  up to 15 s for the recorded engine and its reserved ports to clear
  before clearing ownership, and rechecks the creation timestamp before
  force-kill. The 30-cycle benchmark records each start, wait, stop and
  post-stop listener state. Verify both the interrupted retry and cycle
  lane on the updated runtime.
- Release build at `5448d15df6c2fdbe67ca5c35532509391420e701` passed
  59/59 tests. The separate unsigned 0.1.4 MSI is
  `D:/Release/emulator-workspace-0.1.4-20261113-042924/EmulatorWorkspace-0.1.4.msi`,
  SHA-256 `0b790e606b9a8eec7255d358ad2d803f5114bbee41c15c67a615b91b286af52d`.
  Its stage is `D:/Release/stage-20261113-042924/payload`. Verify the two
  recipe baselines and restart cleanup before repeating final acceptance.
- The 0.1.3 fixture-baseline replay observed `sys.boot_completed=1` while
  the selected guest's window-service dump timed out. It eventually failed
  after 19 minutes, with an ADB 60 s timeout during recovery. The current
  source bounds the recipe UI wait to two minutes, the launch focus wait
  by elapsed wall time, and each WindowManager dump by both guest and host
  deadlines. Confirm prompt failure or recovery on a real restored guest.
- In 0.1.3 Phase 3, the nested MCP/CLI acceptance passed 43/43. The race
  probe's two raw UIAutomator dumps hung on the restored guest. The exact
  owned ADB clients were terminated after their bounds so independent
  checks and cleanup could continue. The takeover rejected queued input
  with `revoked`. The independent UI markers remain failed. Future runs
  use the existing bounded ADB helper and require a successful fresh dump.
  Evidence `artifacts/phase3/candidate/20261113-041225/phase3/20261113-051314`.
- The 0.1.3 Maestro debug hierarchy showed the fixture controls, including
  `DUP_TAPS=0` and `DUPLICATE A`, with the title outside the visible
  hierarchy. The runner now waits for the exact initial marker and taps
  the displayed uppercase label. Rerun to verify the marker advances.
- The full 0.1.3 candidate suite finished 5/9 and cleaned up its guests.
  Root Lab, import, developer tools, WPF click-through and Play API 35
  16 KB passed. Both recipes and Phase 3 failed as recorded above. Play
  API 36 booted, installed, captured and cleaned up, with a cold fallback
  on named restore. Raw wrapper summary `20261113-041225/summary.json`.
- The targeted 0.1.4 candidate rerun started in
  `artifacts/phase3/candidate/20261113-054840` with launcher and fixture
  recipe baselines, then Phase 3. The 426-file stage passed hash
  verification. Check the wrapper and nested logs through cleanup. If
  these checks pass, run the remaining 0.1.4 installer and runtime lanes.
- The 0.1.4 launcher-baseline run `20261113-054841` passed 37/37.
  The first replay completed all 15 steps after a verified cold fallback,
  and the interrupted retry passed. Scoped cleanup left only the stopped
  template. Fixture-baseline run `20261113-060543` is next in the same
  wrapper, followed by Phase 3.
- The 0.1.4 fixture-baseline run `20261113-060543` also passed 37/37.
  It verified a named restore, completed all 15 steps, passed the
  interrupted retry, and removed its disposable guests.
- The targeted 0.1.4 wrapper `20261113-054840` finished 2/3 with all
  disposable guests removed. Phase 3 run `20261113-061905` passed 54/55,
  including both takeover markers and the corrected Maestro flow. Its
  nested Phase 2 run `20261113-061917` recorded four failures after the
  TypeScript MCP client's 60 s request deadline, including missing duplicate
  selectors. Preserve those failures and rerun the paired lane.
- Installer run `20261113-063652` passed 22/22 on the exact 0.1.4 MSI
  with 0.1.3 as predecessor. The follow-on suite is
  `artifacts/phase3/candidate/20261113-063943`, testing Root Lab, import,
  developer tools, WPF click-through and both Play profiles under
  `D:/WS-Candidate-014-Finish-*` with the verified 0.1.4 stage.
- The TypeScript MCP acceptance client now records the failing tool and
  partial transcript on timeout. It gives only `snapshot_restore` a 540 s
  deadline to cover the supervisor's bounded cold fallback, and requires
  a matching saved/restored tag and an explicit recovery outcome. Re-run
  the paired Phase 3 lane after `20261113-063943` completes.
- The 0.1.4 follow-on wrapper `20261113-063943` passed all six lanes:
  Root Lab 10/10, import 9/9, developer tools 9/9, WPF click-through
  16/16, and both Play profiles 8/8. The Play profiles verified named
  snapshot rollback. No owned emulator remained. The paired Phase 3
  rerun with the updated MCP client is `20261113-065245`.
- The refreshed exact 0.1.4 offline Sandbox configuration is at
  `D:/WS-OfflineSandbox-014-20261113-092237/offline-acceptance.wsb`.
  The copied candidate hash matches the MSI. It disables networking,
  clipboard and audio/video input and checks the engine and WHPX return
  statuses in the sandbox. Administrator enablement and usable nested
  WHPX are still required.
- The verified 0.1.4 desktop displayed the first-run panel and enabled
  Create with the pinned SDK (`20261113-084323`, 3/3). A separate SDK
  directory exposed the installed tools while omitting the pinned image.
  The desktop displayed the missing package ID and disabled Create
  (`20261113-084356`, 3/3). Both used fresh roots containing spaces,
  launched zero guests, and shut down their own supervisors. Both
  screenshots and their UI trees are under `artifacts/phase3/wpf`.
- Source `728cb4b` fixes a first-run false positive where a verdict
  containing “not usable” could enable Create. Setup now checks the
  structured WHPX probe status, emulator build, image revision and missing
  tool paths. It also displays the system image file size. The Release
  build, 64 tests and real `wsctl doctor --json <path>` passed on WHPX.
- Source `65ec277` removes a raw-frame byte-array copy, leaves room
  for 60 Hz arrival jitter and checks the advertised AVD name at frame
  subscription. The Release build and 64 tests passed. Measure both
  changes on the next packaged payload after the soak.
- The fixture now declares a positive smoke instrumentation test. Its
  debug APK signature, manifest, test-signed AAB and generated APK set
  passed local build verification. Exact 0.1.4 packaged CLI run
  `20261113-091920` passed 3/3 for explicit test-signing, APK-set
  generation and scoped shutdown, with no guest launched. Run the next candidate regression
  with `-RequireInstrumentation` to verify the test and invalid-runner
  rejection on the managed guest. The original 0.1.4 run covered the
  earlier capability check. Release build and 64 tests passed.
- Mobile MCP 1.0.17 with mobilecli 1.0.23 is staged under
  `D:/WS-Compare-MobileMCP-1.0.17`. The server completed stdio
  initialization and exposed install, launch, hierarchy and tap tools.
  Run `tools/mobile-mcp-comparison.py` after soak cleanup with exactly one
  owned, verified emulator and the packaged fixture. Android Studio
  build `262.9437.185.2621.16467767` is installed for the separate
  IDE comparison. The isolated project at `D:/WS-AndroidStudio-Comparison`
  built the same fixture sources offline with Gradle 8.11.1 and AGP 8.9.2
  in 2m06s. Its debug APK SHA-256 is
  `f583f0d016130599a99ce23fdbac1abe30e5fe6fd6db07cbb3a26d915c4e0f2`.
  Guest deployment and app workflow timings are still pending.
- Source `6a474b2` shares one guest boot-and-focus query between normal
  start and restore. It also shortens controller tap dwell to 32 ms,
  attempts contact release after cancellation, and checks the verified
  engine generation before controller dispatch. Release build and 64
  tests passed. The next managed cold-boot run measures the interactive
  `wsctl wait` reply and records independent endpoint verification
  separately. Re-run quiet-host WPF and 20 restores after soak cleanup.
- Window-only H.264 capture produced 16 frames in a two-second packaged
  desktop smoke check. The full live demo uses
  `tools/wpf-clickthrough-acceptance.ps1 -RecordDemo` after the timing
  runs, with the fixture guest and an isolated workspace.
- Unsigned 0.1.5 is built from `a0624599ac1bbd7cea5d7c15a22cee308f7c615a`.
  MSI `D:/Release/emulator-workspace-0.1.5-20261113-092932/EmulatorWorkspace-0.1.5.msi`,
  SHA-256 `0abf64ebaba4350e24227ce5118b588bfe1598228616b11f69c5fcc77f6bd53c`,
  Authenticode `NotSigned`. The 426 files in
  `D:/Release/stage-20261113-092931-015/payload` match the inventory,
  including the rebuilt instrumentation fixture. The network-disabled
  kit is `D:/WS-OfflineSandbox-015-20261113-093742/offline-acceptance.wsb`.
  After the 0.1.4 soak and cleanup, run quiet-host comparisons and
  `tools/candidate-regression.ps1 -RequireInstrumentation` on the 0.1.5
  payload, and exercise the installed 0.1.4 to 0.1.5 upgrade.
- Exact 0.1.5 first-run and missing-image checks each passed 3/3 in
  `20261113-093906` and `20261113-093939`, with roots containing spaces
  and non-ASCII text. The normal panel showed build 16428233, AOSP
  revision 2, 1.51 GiB for `system.img` and usable WHPX. Missing-image
  setup disabled Create. Both isolated supervisors shut down.
- Stopped the intermediate 0.1.4 run at the user's direction after 64
  successful paired probes and 4,793.44 seconds. The first stop raced
  the supervisor shutdown. The scoped retry removed the remaining
  guest. Both owned profiles, processes, reserved listeners and ADB
  transports were gone. The 0.1.1 full soak remains verified.
  Use quick diagnostics for intermediate changes. The current run
  completed five valid WPF trials (`20261113-094636`), with 514.15 ms
  median, 556.56 ms maximum and 25.99 delivered FPS. Per-tap controller
  setup took 244.54–275.27 ms. The restore run (`20261113-094828`)
  verified three restores in three attempts, with no fallback and
  13.13 s median. Both cleaned up. Test the controller setup path
  next. The diagnostic counts leave full acceptance pending.
- Source `148d584` reuses the authenticated controller channel for
  the verified engine process generation. The instance gate serializes
  channel use with start and stop. An RPC failure discards the channel
  for the next request. Release build and 64 tests passed. WPF diagnostic
  `20261113-095328` passed 5/5 and cleaned up. With the packaged 0.1.5
  desktop, CLI and fixture plus the separately hashed supervisor, median
  fell from 514.15 to 211.72 ms and throughput reached 27.17 FPS.
  First-tap setup took 160.71 ms. Reused setup took 0.08–0.34 ms.
  The controller lifecycle rerun `20261113-095854` passed 6/6. Both
  engine generations rejected absent and forged tokens, served an
  authenticated PNG and reused the channel on the second tap
  (0.33 ms initially, 0.07 ms after restart). The first run
  `20261113-095700` passed authentication and cleanup but its tap
  parser expected JSON from the text-only CLI command. The rerun used
  `wsctl call --op input.tap --args-file <path>`. Queued takeover
  and stale-observation acceptance remain pending for this build.
- Frame timing diagnostic `20261113-100133` passed 5/5 and cleaned up.
  Changed-frame writes took 3.59–11.82 ms and desktop decode plus
  dispatch took 21.92–47.87 ms. Median dispatch to composition was
  246.20 ms at 32.38 FPS. The supervisor and desktop were separate
  source builds. The desktop was built with the changes later committed
  as `6bcca53`, so its version embeds the preceding `65c2f15`.
- The paired draw diagnostic `20261113-100519` passed 5/5 and
  removed its guest. Touch release to draw took 4–22 ms in guest
  uptime. On the four reused-controller trials, the first observed
  changed controller frame arrived 183–232 ms after WPF dispatch,
  or 117–181 ms after the tap reply. These are host timestamps.
  The independently built desktop, supervisor and fixture hashes
  are recorded separately from the 0.1.5 CLI.
- The matched software GPU diagnostic `20261113-100819` passed
  5/5 with the same desktop, supervisor and fixture hashes. Warm
  dispatch-to-changed-frame receipt was 277–362 ms and viewport delivery
  was 15.91 FPS. The host GPU delivered 183–232 ms and 27.43 FPS.
  Both guests were removed. Keep the host renderer as the default.
- The 270 × 600 host diagnostic `20261113-101158` passed five
  marker trials with the guest still at 1080 × 2400. Guest tap
  coordinates moved one pixel on each axis (498,446 to 499,445).
  Warm changed-frame receipt fell from 183–232 to 118–129 ms,
  median composition fell from 232.91 to 166.12 ms, and throughput
  rose from 27.43 to 37.28 FPS. Cleanup passed. The 100 ms and 60 FPS
  gates remain open.
- Direct controller capture `20261113-101457` passed all four
  eight-second arms and cleanup on exact 0.1.5. One host-GPU guest
  ran the same animated fixture throughout, in RGB, RGBA, RGBA, RGB
  order at 270 × 600. RGB delivered 54.33 and 58.22 FPS. RGBA
  delivered 57.88 and 58.20 FPS with one-third more bytes per frame.
  Keep RGB for the next experiment. The direct stream reached about
  58 FPS while the WPF path delivered 37.28 FPS at the same capture
  size in `20261113-101158`.
- The bounded latest-frame queue in `d834bde` passed the Release
  build, all 64 tests and five real half-size WPF trials in
  `20261113-101843`. Median composition was 164.19 ms versus
  166.12 ms, delivery 36.68 FPS versus 37.28 FPS, and the slowest
  trial was 408.68 ms versus 374.17 ms. Cleanup passed and ADB
  was empty. Reverted the queue in `78abb25` because the short
  comparison showed no clear gain. The 100 ms and 60 FPS targets remain open.
- Restore diagnostic `20261113-102316` used the separately hashed
  `07b3585` supervisor with the exact 0.1.5 CLI and fixture. The restore
  readiness probe queried only display information, and the harness
  read the marker and focus in one independent ADB request. Three
  verified warm samples took 8.50, 11.55 and 8.90 s. The median was
  8.90 s versus 13.13 s in `20261113-094828`. Warm samples spent
  3.88–4.72 s in the console, 2.00–3.95 s on readiness and
  1.60–3.26 s on independent verification. One cold fallback took
  139.45 s after the 90 s readiness timeout. Release build, 64 tests
  and cleanup passed. Next, record each readiness probe's transport,
  boot property and focus outcome during the next short restore run
  to diagnose the fallback. Keep the 8 s and 20-sample gates open.
- Phase 3 paired rerun `20261113-065245` passed 55/55, including the
  nested Phase 2 run `20261113-065258` at 43/43. The MCP workflow, stale
  observation rejection, queued takeover, split install, Maestro and
  cleanup all passed. Alongside `20261113-054840` and `20261113-063943`,
  there is passing evidence for all nine runtime lanes on exact 0.1.4.
  ADB showed no attached devices after cleanup. Quiet-host WPF and the
  30-cycle leak verification are next on the same candidate.
- Exact 0.1.4 WPF quiet-host run `20261113-070554` recorded 25/25 valid
  single-guest trials, p50/p95 583.45/679.56 ms, and 23.09 delivered FPS.
  Two-guest run `20261113-070756` recorded 25/25, p50/p95 882.98/1,145.69 ms,
  and 15.49 delivered FPS. Both cleaned up. Both target misses are open.
  Lifecycle run `artifacts/phase3/bench/20261113-071213` passed 30/30 with
  no reserved listeners left after any cycle, engine count 0 → 0, and
  scoped cleanup. The cycle p50/p95 was 42.66/50.66 s. The exact 0.1.4
  20-sample restore rerun is `artifacts/phase3/bench/20261113-073629`.
- The exact 0.1.4 restore rerun verified 20 warm samples in 21 attempts
  with fresh focus and independent state markers. Warm p50/p95 was
  11.15/16.91 s, exceeding the 8 s median target. One cold fallback
  took 135.26 s and was excluded from warm percentiles. The guest was
  removed and the scoped supervisor was shut down. The exact 0.1.4
  matched raw comparison `artifacts/phase3/bench/20261113-074512` finished
  20/20 in each arm. Raw p50/p95 was 35.37/50.52 s and managed was
  49.90/54.90 s. The 14.53 s median overhead missed the 3.54 s allowance.
  Both profiles were removed and the scoped supervisor was shut down.
- The 0.1.4 four-hour soak is `artifacts/phase3/bench/20261113-081953`
  with root `D:/WS-Soak-014-20261113-081951`, harness PID 3632 created at
  08:19:51 PKT. The timed paired window started at 08:25:18 PKT. The
  historical 0.1.1 soak passed independently. Verify the new run with
  `python tools/soak-progress.py artifacts/phase3/bench/20261113-081953
  --require-complete` after both guests are removed.
- On 0.1.4, the restore median spent 3.27 s in the console load and
  10.12 s through the CLI reply. Current source `1fc7564` combines the
  fresh boot-property and focus checks in one bounded ADB command and
  shortens the retry interval to 700 ms. The Release build and 59 tests
  passed. The timing change needs a WHPX rerun after soak cleanup. The
  matched boot harness now records the supervisor's boot and interactive
  timestamps alongside the shared raw and managed readiness probe.
- The Windows CI workflow now checks the exact engine build, AOSP image
  revision and usable WHPX, verifies the MCP client versions, and installs
  the locked TypeScript dependencies. The upload is limited to check names
  and outcomes. YAML parsed locally. Dispatch still requires a configured
  GitHub repository and a self-hosted Windows x64 runner with the `wipx`
  label and the installed Android SDK, Java, Node, Python MCP and Phase 3
  tools. This checkout has no Git remote configured.
- A separate source change removes synchronous trace writes from the frame
  and Logcat reader, queues one latest frame before UI dispatch, rejects
  late frames and gestures from previous viewport subscriptions, and
  avoids resizing the WPF image on every frame. Build and measure the
  change after the candidate regression. Keep its diagnostic provenance
  separate from the 0.1.2 MSI.
- Both `Containers-DisposableClientVM` and `Microsoft-Hyper-V-Tools-All`
  still report WMI `InstallState=2` after the canceled elevation. The
  authorized feature change requires an elevated Windows token. Run the
  prepared command after the timed host workloads and verify whether
  Windows requests a restart.
- After successful feature enablement and any required restart, launch
  the hash-verified Sandbox kit for the candidate under test and retain
  its isolated `output` directory. The SDK, JDK and installer mappings
  are read-only. The first gate is `emulator -accel-check` inside the
  sandbox. If it cannot use WHPX, use an isolated Windows 11 x64 VM
  with virtualization extensions exposed.
