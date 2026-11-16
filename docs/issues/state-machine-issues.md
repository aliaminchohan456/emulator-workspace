# Filed issues: implicit state machines

Date: 2026-11-16. Companion to [the state machine audit](state-machine-audit.md).

Findings whose *frequency x severity* is below 5 are filed here and their code
is left alone, per the owner's rule. Nothing in this file is a guess about a
future case: every entry was observed in the current code or in the current
repository state.

| # | Issue | Where | Frequency | Severity | Product | Why it is not fixed here |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | The Magisk lab's phase word is printed raw in the inspector row and in the rail dot's tooltip ("verifying_boot_1", "baseline_boot"). | `MainWindow.xaml` (the Diagnostics tab), `MainViewModel.MagiskState` | 4 | 1 | 4 | A wording change in a surface that was reviewed by a human; the value is still exact and it is in a tooltip. |
| 2 | A runtime state root named `System.Windows.Automation.AutomationElement/` is tracked in the repository (`supervisor.db`, `supervisor.lock`, the WAL files). It was created when a probe passed a .NET type name as a workspace root. | repository root | 1 | 3 | 3 | Untracking other people's files is the owner's call. `WorkspacePaths` already documents that runtime state must live outside the source tree; the fix is `git rm -r --cached System.Windows.Automation.AutomationElement` plus a `.gitignore` entry. |
| 3 | Two state converters are declared as XAML resources but are never bound: `StateToSoftBrushConverter` and `StateToBrushConverter`. They are now consistent with the shared lifecycle, but nothing uses them. | `MainWindow.xaml`, `CommandPaletteWindow.xaml` | 1 | 1 | 1 | Dead code. Removing a declared resource is a cosmetic change with no user-visible effect; keeping them costs nothing now that they agree with the lifecycle. |
| 4 | The shell's destination is two independent booleans (`IsLogcatPage`, `IsAgentsPage`) instead of one destination enum. They are kept mutually exclusive by hand in `OpenLogcatPage`/`OpenAgentsPage`. | `MainViewModel` | 1 | 3 | 3 | The hand-kept exclusion is correct today; the risk is a future edit that forgets it. An enum would be a refactor of the shell's page switching for no user-visible gain. |
| 5 | One global busy flag covers every operation. A long operation on one guest (an install, a cold boot) disables every device command in the window, including the ones for another guest. | `MainViewModel.IsBusy` | 2 | 2 | 4 | The busy flag is also what protects the serialised mutations. Making it per guest is a real change to the command surface, not a state-machine fix. |
| 6 | The emulator's engine process and, once, the supervisor exited **silently** on the reference host: no Windows application or system event, no crash record, no log line. The engine log ended in the renderer's `Failed to find ColorBuffer: 121`, right after the guest's Gallery app opened. The product recovers (the reconciliation reports the guest as stopped, the watchdog restarts the supervisor, the viewport re-attaches), but the cause is upstream in the emulator/host pair, not in this workspace. | engine process, supervisor process | 2 | 3 | 6 | It is not this workspace's code: the engine and the supervisor are separate processes that exited without a trace. The product's side of it is fixed and proven (the guest reads as stopped, the supervisor comes back, the viewport re-attaches). |

## Pending hardware evidence

These are not defects; they are the runtime proofs that still need a real
WHPX guest. They are listed so the next run knows what to look at.

| # | What to prove | How | Where | State |
| --- | --- | --- | --- | --- |
| A | A guest started **without** a waiter reaches `Running` in durable state and the Studio attaches to it. | `wsctl start --name <guest> --root <root>`, then `wsctl list --root <root>` (the state must not stay "Starting"), then open the Studio on that guest (the viewport must attach). | `artifacts/ux18/` | **Proved** (a real guest: `wsctl start` with no waiter, then `wsctl status` alone drove Starting -> Running in ~90 s, the record persisted `Running`, the Studio attached and showed the live guest) |
| B | A guest whose engine is gone reads "Engine gone" in both the Studio and the device library, and the library offers Start. | Kill the engine process for a running guest, then read the Studio's state pill and the library card. | `artifacts/ux18/` | **Proved** on a real guest (the engine was killed: `wsctl status` printed `state=Stopped engineAlive=false`, the Studio read "Engine gone" and the stage "The guest's engine is gone") |
| C | A Magisk install in flight reports "install in progress", not "Lab recovery required"; an interrupted install reports that a stock recovery is due. | Run `magisk.prepare` + `magisk.install` from the CLI while the Studio watches the guest. | `artifacts/ux18/` | **Open**: it needs the disposable Magisk lab (a patched API 35 ramdisk), which is not built on this host. The phase rules are unit-tested; the runtime wording is not proven yet. |
| D | The supervisor's `instance.status` still answers every agent scope the same way after the reconciliation change (no new write in a read scope, no scope change). | The Phase 2 agent acceptance run. | `artifacts/phase2/` | **Open**: the Phase 2 acceptance needs its own root (a template instance, a paired session, the sample APK and the two MCP clients). |

### B was first proved without a guest

The "engine gone" path was proved against a disposable supervisor root with a
record whose engine process was gone: `wsctl list` printed `Stopped`,
`wsctl status` printed `state=Stopped engineAlive=false pid=999999`, the Studio
showed "Engine gone" and the stage "The guest's engine is gone"
(`artifacts/ui/ux18-engine-gone.png`), and the Device Manager showed the same
chip, a Start button and "Needs attention 1 · guest to review"
(`artifacts/ui/ux18-engine-gone-library.png`). The row is now closed by the
run on a real guest described above.
