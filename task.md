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
  `docs/compatibility-matrix.md` publishes the tested rows with the exact host, engine, image, ABI, snapshot, root and simulation support, and marks every untested combination as unverified. The Google APIs API 35 image is verified for the ADB-root workflow; the Google Play images and the other API levels are installed on the host but still unverified.
- [ ] Test same-user and external ADB trust boundaries, privileged scopes, injection, path escapes and reparse points, floods, partial operations, and exposed sockets. Verify retention, artifact deletion, and support-bundle preview.
  `docs/security-review.md` records the review with the code references, the seven fixed findings and the two open ones (the release signing identity and the four-hour soak).
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
  The release candidate is built and verified. `tools/package-release.ps1` publishes the desktop, the supervisor and the CLI self-contained for win-x64, harvests the payload into a **per-user WiX 5 MSI** and writes the release record, the SBOM, the notices and the provenance (commit, SDK versions, build commands, pins and the signing status). The candidate is `EmulatorWorkspace-0.1.0.msi`, 55 380 192 bytes, SHA-256 `8742367339f2b0b4daa65ba23ddf2e842ead707b52b9a344e3835d0d7f96fc5f`, **unsigned** (no user-owned certificate was supplied, and the provenance says so). `tools/installer-acceptance.ps1` passed **16 of 16 checks** in `artifacts/phase3/install/20261112-194153`: clean install, the installed files, the sample APK, the first launch, a boot through the installed CLI on the pinned image, offline reuse, an upgrade that preserves the operator profiles, the uninstall registration, the clean uninstall and the untouched profiles.
- [ ] Publish setup and recovery commands, architecture decisions, client setup and contract, permissions and compatibility matrices, and the reproducible lab build procedure. Attach first-run and failure screenshots, demo, test outputs, exact versions, benchmark samples, and the remaining gates with their next actions.
  `docs/release-notes.md` publishes the package layout, the pins, the first-run flow, the architecture, the recovery commands, the developer-tooling commands and the known limitations. The demo recording and the first-run/failure screenshots are still open.

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
| Phase 3 Windows installer | Verified | `artifacts/phase3/install/20261112-194153`: 16/16 checks; per-user MSI 55 380 192 bytes, SHA-256 `87423673â€¦`, unsigned | Sign with a user-owned certificate |
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
| The four-hour two-guest soak | The historical run omitted the soak. | Run `tools/benchmark.ps1 -Root D:/WSBench -Samples 20 -SkipBaseline -SkipColdBoot -SkipRestore -SkipLifecycle -SoakMinutes 240` and monitor `soak.jsonl` through cleanup. |
| Two-guest latency | WHPX run `20261112-205244` collected 20/20 fresh markers. ADB-to-Logcat p50 1,099.43 ms / p95 1,451.69 ms. Packaging overlapped the run. | Measure the same input workload through the WPF composition proxy on a quiet host. |
| The comparison with Android Studio, the raw Emulator CLI and an existing MCP server | Not run in this environment. | Run the same recipe through each of the three and record the measured difference in `docs/benchmark.md`. |
| The release signing identity | No user-owned code-signing certificate was supplied, so the candidate is unsigned. | Supply the certificate to `tools/package-release.ps1 -CertificateThumbprint` and rebuild; the provenance records the signing status either way. |
| A second API level and an unmodified Google Play image | The images are installed on the host but were not exercised. | Run `tools/phase3-acceptance.ps1` and the recipe harness against `system-images;android-36;google_apis_playstore;x86_64` and `system-images;android-35;google_apis_playstore_ps16k;x86_64` and add the rows to `docs/compatibility-matrix.md`. |
| 150% and mixed-DPI displays, paths with spaces and non-ASCII names | Only 100% and 125% scaling were exercised, and the workspace roots used plain paths. | Repeat the WPF click-through on a 150% display and run the harnesses with a root path that contains spaces and non-ASCII characters. |
| The demo recording and the first-run and failure screenshots | Not produced. | Record the WPF click-through and capture the first-run panel and a failure state as PNGs. |

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
- The rebuilt local candidate from `f375bde` is
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
