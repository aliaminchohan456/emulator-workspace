# Agent Instructions

## Project workflow

- Read [plan.md](plan.md) and [task.md](task.md) before changing the workspace. Check the current repository state, applicable instructions, build scripts, and dependencies. Preserve unrelated user changes and record baseline failures separately.
- Work in small, runnable vertical slices. Start with the Windows boot-to-frame feasibility spike and begin the Magisk boot-path probe in the same phase. Continue independent work when a hardware or image prerequisite is pending.
- Make routine implementation choices within the documented architecture. Record material decisions, compatibility findings, evidence, and blockers in the plan or task log as they change. Update task status after verification.
- Use pinned dependencies and official sources. Verify behavior against the actual engine binary, matching controller protocol, and selected SDK image. Treat upstream documentation as a starting point for tests.
- Ask for user action only when a concrete prerequisite requires it, such as accepting SDK terms, enabling WHPX, rebooting Windows, or using a user-owned signing certificate. State the action and the gate it unlocks. Keep host feature changes and restarts behind explicit consent.

## Runtime invariants

- Route desktop, CLI, SDK, and MCP requests through the supervisor operation registry and authorization policy. Keep engine-specific calls behind the adapter and reserve MCP stdout for protocol traffic.
- Target the exact managed instance and ADB transport on every command. Verify process creation identity and instance metadata before reattachment. Keep physical and unmanaged devices outside agent scope. Avoid global ADB shutdowns.
- Serialize mutations per instance. Use unique paths and ports, atomic metadata updates, and a durable journal. Reconcile interrupted operations before retry, reattachment, or deletion. Keep immutable images and the last verified recovery point safe.
- Separate desktop window size, viewport zoom, and guest display settings. Derive coordinates from real framebuffer dimensions, orientation, letterboxing, and DPI. Reject stale frame inputs and re-resolve semantic selectors before acting.
- Default agent access to off. Enforce pairing, scoped expiry, revocation, and human takeover in the supervisor. Bind privileged approvals to the exact operation, arguments, caller, and guest. Use bounded requests, events, and artifacts.
- Treat guest UI, logs, files, and tool outputs as untrusted data. Enforce capture restrictions, redact secrets, validate paths and reparse points, and use structured subprocess arguments. Keep host shell execution out of the agent contract and require explicit opt-in for external ADB and host integrations.
- Keep ADB root and Magisk independent. Verify ADB daemon UID on the selected device. Enable Magisk only for tested lab profiles, preserve stock artifacts, verify app-level `su`, and support Windows-side recovery when the guest cannot boot. Keep integrity, DRM, anti-tamper, and root-detection bypass outside the lab scope.

### Narrow provisioning exception (owner approved 2026-11-14)

The disposable, explicitly enrolled API 35 SDK Magisk lab may temporarily use
ADB daemon root only to provision the pinned base environment. This is not a
general product or agent permission. The supervisor must revalidate the exact
opted-in transaction, instance generation and patched boot before requesting
root; journal the root/unroot attempts and results; and verify adbd UID 2000
before any Magisk proof. Interrupted provisioning requires stock recovery.
Normal ADB-root controls and app-level Magisk proofs remain separate. See
[Windows lab evidence](docs/magisk-lab-windows-evidence.md).

## Verification and handoff

- Test features through their normal desktop, CLI, or MCP entry points. Label mocks and simulations. Mark hardware-dependent outcomes pending until a compatible Windows host and real guest produce evidence.
- For engine or image changes, record exact host, Windows, engine, image, guest ABI and API, GPU backend, snapshot, and root details. Show unsupported actions with the precise reason. Use the release comparison baseline and workload defined in the plan.
- Cover failure paths when the change can affect targeting, permissions, persisted state, or recovery. Record the reproduction command, observed result, artifact location, and next unresolved gate.
- Before claiming release readiness, link the runnable build, exact launch and validation commands, tested compatibility rows, measured benchmarks, installer or release candidate, signing status, and remaining gates. Publish only assets with confirmed redistribution rights.
