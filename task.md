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
  APK, install, launch, tap, screenshot, and Logcat verified through the supervisor and captured in the desktop; the drag/drop click-through and the launch foreground check under an ANR remain open.

## 2. Agent control

- [ ] Define the versioned named-pipe request and event contract with typed operations, stable IDs, capabilities, errors, deadlines, cancellation, idempotency, reconnect cursors, and explicit overflow handling. Connect desktop, CLI, SDK, and MCP to the same policy and registry.
- [ ] Keep agent access off initially. Implement visible pairing, scoped expiring grants, single-use approvals for privileged mutations, rate limits, revocation, audit, and human takeover that cancels queued input.
- [ ] Add bounded frame and UI observations, semantic selectors with deterministic resolution, coordinate input with frame freshness checks, app operations, and artifact references. Verify secure-window behavior and sensitive-data redaction before enabling capture or input on supported paths.
- [ ] Keep managed ADB target selection exact. Test unauthorized and expired grants, revocation, cross-device calls with two managed guests and an external transport, ambiguous selectors, stale frames, and untrusted guest content.
- [ ] Supply TypeScript and Python client examples plus stdio configuration examples for two real MCP clients. Verify the MCP handshake and keep diagnostics off protocol stdout.
- [ ] Run a real MCP client and the CLI through the same sample recipe. Create and boot a disposable clone, install and launch the APK, interact with and assert login, reproduce a controlled failure under poor network conditions, collect screenshots and filtered logs, restore baseline, and return artifact IDs. Build the minimal clone, restore, and network support here if needed. Save a machine-readable transcript and short recording.

## 3. Device and QA depth

- [ ] Expand the Device Library with phone and tablet presets, profile customization and import/export, ownership-aware SDK AVD imports, and resource checks. Verify imported originals are preserved.
- [ ] Complete headless attachment, duplicate, disposable clone, reset, and delete. Add named snapshots with fingerprint validation, clean fallback, recovery point protection, and disk limits. Test parallel starts, interruptions, low space, and rollback.
- [ ] Add split APK and verified `.apks` installs, explicit `.aab` build and signing choices, manifest and ABI inspection, package and permission operations, deep links, instrumentation, and install diagnostics.
- [ ] Add crash and ANR capture, bug reports, searchable Logcat export, recordings, UI hierarchy highlighting, profiling entry points, a scoped guest file explorer, and advanced shell controls.
- [ ] Ship GPS, rotation, battery and charging, and documented network speed and latency controls. Probe routes, offline and packet loss, sensors, camera and microphone, locale and timezone, display settings, and telephony or SMS for the second pack. Show exact capability and host-integration limits.
- [ ] Add bounded recipe recording and replay with preconditions, assertions, deadlines, stop conditions, baseline context, and artifacts. Verify clone and restore under two-guest load.
- [ ] Document opt-in external ADB interoperability and tested integrations. Verify a deployment through Gradle/ADB or Android Studio and a run through an external test runner. Add command palette shortcuts for common workflows.

## 4. Root Lab

- [ ] Add separate ADB root and Magisk controls and capability badges. Verify targeted `adb root` and `adb unroot` with effective UID after reconnect. Recheck after boots, restores, and updates.
- [ ] Pin AOSP, engine, and Magisk versions and document image provenance and the verified patch flow. Preserve pristine artifacts and patch a per-instance copy of the actual boot inputs.
- [ ] Implement approved Magisk install with durable backup, journaled staging, validated activation, cold boot, and incompatible snapshot fencing. Provide progress and exact compatibility failure reasons.
- [ ] Verify Android boot, expected Magisk version, and approved guest app or probe `su` with UID 0. Repeat after a second cold boot and verify the original profile still boots.
- [ ] Recover from the Windows app while Android is unavailable. Test exact stock boot and compatible guest data restoration after interrupted writes, corrupt patches, and boot timeouts. Preserve evidence before allowing retries.
- [ ] Add opt-in module import and recovery only after base install and restore pass. Expose Zygisk or module toggles only for verified combinations.

## 5. Release

- [ ] Verify a second stable API level and an official unmodified Google Play profile if available. Publish the exact host, engine, image, graphics, snapshot, root, and simulation support per tested row.
- [ ] Test same-user and external ADB trust boundaries, privileged scopes, injection, path escapes and reparse points, floods, partial operations, and exposed sockets. Verify retention, artifact deletion, and support-bundle preview.
- [ ] Run the benchmark plan, including baseline comparisons, 30 start/stop cycles and a four-hour two-guest soak. Preserve raw samples and compare workflows with Android Studio, the Emulator CLI, and an existing mobile MCP server.
- [ ] Add versioned Windows build, test, and package CI plus a separate WHPX hardware lane. Exercise paths with spaces and non-ASCII names, mixed DPI, low disk and RAM, and port conflicts.
- [ ] Build the reproducible installer or installable release candidate. Produce hashes, dependency inventory or SBOM, notices, provenance, and signing status. Verify clean install, first launch, offline reuse with installed dependencies, upgrade preserving profiles, and uninstall.
- [ ] Publish setup and recovery commands, architecture decisions, client setup and contract, permissions and compatibility matrices, and the reproducible lab build procedure. Attach first-run and failure screenshots, demo, test outputs, exact versions, benchmark samples, and the remaining gates with their next actions.

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
| Phase 1 performance | Missed target | 3.66 FPS single / 1.07 FPS two guests, p95 352 / 1641 ms; limiter is emulator PNG capture | Profile capture path; test raw RGBA and 720x1600 guest |
