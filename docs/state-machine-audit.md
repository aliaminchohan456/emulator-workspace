# Implicit state machines in the Emulator Workspace

Date: 2026-11-16. Scope: the whole workspace - the supervisor (Core), the
WPF desktop, the CLI, the MCP server and the agent surfaces.

Every state in this product is a word that travels over a pipe: `"Running"`,
`"Starting"`, `"prepared"`, `"pending"`, `"Paused"`. Most of those words are
*derived* state, and the derivation used to live in more than one place. This
is the inventory of those implicit state machines, the defects found inside
them, and what was changed.

**The rating rule used here:** every finding gets a *frequency* (1-10, how
often an operator hits it) and a *severity* (1-10, how bad it is when it
happens). The product decides what happens:

- product **>= 5** - fixed in this slice;
- product **< 5** - filed as an issue, the code is left alone.

## The inventory

| # | State machine | Where the state lives | Implicit in | Status |
| --- | --- | --- | --- | --- |
| SM-1 | Managed instance lifecycle | `InstanceRecord.State` (`InstanceState`), sent as a string | The supervisor (2 writers), the Studio, the device library, two converters, the agent table, the command palette | **Explicit now** (UX-18) |
| SM-2 | Guest readiness / boot stepper | Three `BootStageViewModel` steps (`done`/`active`/`pending`/`unknown`) | The Studio's status poll | Evidence-driven; now reads SM-1 |
| SM-3 | Frame stream | `_frameWaitSince`, `IsFrameStale`, `_captureRestricted`, `HasFrame` | The Studio | **Explicit now** (UX-18, continued) |
| SM-4 | Logcat stream | `_logcatSubscription`, `LogPaused`, `_logcatError`, the level chips | The Studio + `LogcatPresentation` | Already explicit and pure |
| SM-5 | Magisk lab transaction | 14 phase strings in the lab journal | `MagiskLabService`, the Studio's stage, the Root Lab card | **Explicit now** (UX-18) |
| SM-6 | Agent session / pairing / operation outcome | Session state, pairing status, outcome codes | `AgentStore`, `AgentSessionsPresentation` | Already explicit and pure |
| SM-7 | Overlay scope | `OverlayStack` | The shell | Already explicit and pure (UX-16) |
| SM-8 | Theme mode | `ThemeMode` + `ThemeSettingsStore` | The shell, the palette, the settings drawer | Already explicit and pure (UX-17) |
| SM-9 | Supervisor connection and recovery | `SupervisorWatchdog`, `IsConnected` | The shell | Already explicit and pure (UX-16) |
| SM-10 | Shell destination | `IsStudioVisible` / `IsLogcatPage` / `IsAgentsPage` | The shell | Two booleans that cannot both be true; left as is (see issue 4) |
| SM-11 | Busy / notice surfaces | `IsBusy` + `BusyText`, the error bar, the toast, the status line | The Studio | One owner, but one global busy flag (see issue 5) |
| SM-12 | Consent | `ConsentDialogModel` + the dialog result | The Studio, the Root Lab | One owner; the flow is already modelled (UX-09) |

The interesting ones were SM-1 and SM-5: both were *implicit* in the sense
that the transition rules were spread over several files, and both produced
defects.

## SM-1 - the managed instance lifecycle

### What the state machine actually was

The supervisor owns an `InstanceState` enum (`Defined`, `Starting`, `Running`,
`Stopping`, `Stopped`, `Failed`, `Unknown`) and sends it over the pipe as a
string. Before UX-18, the *transitions* were implicit and lived in five
places:

- `InstanceManager.StartAsync` wrote `Starting`, `WaitAsync` wrote `Running`,
  `StopAsync` wrote `Stopping`/`Stopped`, `AdoptAsync` wrote `Running`/
  `Starting`/`Stopped`/`Unknown`;
- `DeviceItemViewModel.IsRunning` read `"Running" or "Starting"` as *running*;
- `LibraryCard.StateKey` read the same word through a different table
  (`running`/`starting`/`stopping`/`failed`/`dead`/`stopped`);
- `Converters.StateToSoftBrush`/`StateToBrush` mapped the word to a colour
  through a third table;
- `AgentSessionsPresentation.InstanceLabel` mapped it to a word through a
  fourth.

### The findings

| # | Finding | Frequency | Severity | Product | Decision |
| --- | --- | --- | --- | --- | --- |
| 1 | **The record only reached `Running` as a side effect of somebody waiting.** `InstanceState.Running` was assigned in exactly two places: `AdoptAsync` (supervisor restart) and `WaitAsync`. A guest started by `wsctl start`, by an MCP client, by an agent or by the device library stayed `"Starting"` in durable state until a waiter happened to look at it. `wsctl list` printed "Starting" for a guest that had been up for an hour. | 7 | 7 | **49** | **Fixed** |
| 2 | **The Studio could not attach to a guest it did not start.** The frame subscription was gated on `device.State == "Running"`, so a guest started elsewhere stayed on the "Starting Android" stage with no picture and no way to recover but restarting it. | 7 | 7 | **49** | **Fixed** |
| 3 | **The record never learned that the engine died.** The supervisor has no exit watcher (the legacy CLI engine had one), so a crashed guest kept saying "Running" until the supervisor restarted. The device library corrected it with `engineAlive`; the Studio did not, so it offered input for a guest that was gone. | 5 | 6 | **30** | **Fixed** |
| 4 | **"Starting" counted as "running".** Input, the app keys, rotate, screenshot, install and launch were all live while Android was still booting, and every one of them came back with a refusal from the supervisor. | 5 | 3 | **15** | **Fixed** |
| 5 | **The primary button said "Working..." and stopped the guest.** Its label was computed from the raw word while its action was computed from `IsRunning`, so during a boot the button claimed to be working and stopped the boot. | 3 | 4 | **12** | **Fixed** |
| 6 | **The device library offered Start and Stop at the same time** for a guest whose engine was gone: `StopCommand` compared the raw word, `CanExecuteStart` compared the derived key. | 4 | 2 | **8** | **Fixed** |
| 7 | **"Start device" stayed enabled for a running guest**, and the refusal came back as an error bar. | 3 | 2 | **6** | **Fixed** |
| 8 | **The command states were never re-evaluated when the guest's state changed.** They were refreshed on a selection change and on the busy flag only, so a guest stopped from the device library left the Studio offering a stop. | 6 | 3 | **18** | **Fixed** |
| 9 | **The card's state tooltip never refreshed** (the live evidence changed, the tooltip did not). | 5 | 1 | **5** | **Fixed** |
| 10 | **The state word was spelled in four places**, so a new word would have been added four times or missed once. | 8 | 2 | **16** | **Fixed** |
| 11 | **The device library's "Needs attention" counter only counted `Failed`**, so a guest that lost its engine was not counted. | 4 | 1 | **4** | **Fixed** (the counter now reads the same two states the card's chip does) |
| 12 | **A guest in the `Unknown` state offered a "Start"** that the supervisor refuses with `already_running` (the process is alive, it just could not be verified). | 2 | 3 | **6** | **Fixed** (the state now offers no mutation and says "Unknown state") |
| 13 | **The `Stopping` state left the primary action enabled** - the operator could press it and get a typed refusal while the guest was shutting down. | 3 | 2 | **6** | **Fixed** |
| 22 | **A guest that outlived its engine process held the instance's ports.** The engine is a wrapper around the real emulator process. When the wrapper dies on its own, the guest it started keeps running and keeps the console, ADB and controller ports. The supervisor saw an instance that was gone while the ports it needs were still busy, and the next start refused with "Port 5554 is already in use. Another emulator or process owns it" - a message that named a stranger, and the operator had to find the leftover guest by hand. | 3 | 4 | **12** | **Fixed** |

### What changed

**Supervisor (`Core`)**

- `Core/Instances/InstanceLifecycle.cs` (new): the transition table, in one
  place and pure. `Reconcile(current, evidence)` is monotonic: evidence moves a
  record forward (`Starting` -> `Running` when the guest reports
  `sys.boot_completed=1`) or reports that the process is gone (-> `Stopped`).
  It never invents a state and never overwrites a recorded failure.
- `InstanceManager.StatusAsync` reconciles the record with what it just
  observed (the engine process, the ADB transport, the guest's own boot
  property) and persists the change. The readiness stages are stamped from the
  same evidence, so a guest that booted while nobody was waiting still reports
  when it booted.
- `InstanceManager.List` reconciles the liveness of every record, so
  `instance.list` and `wsctl list` no longer print "Running" for a guest whose
  engine is gone.
- `Core/Instances/OrphanedGuest.cs` (new, pure): the guest that outlived the
  engine process. The console on the instance's serial says whose guest it
  is, and it is only ever addressed when it answers as *this* instance's AVD.
  `StartAsync` then names the instance instead of pointing at a stranger, and
  `StopAsync` ends the leftover guest and waits for the ports to be released.

**Desktop**

- `Desktop/ViewModels/DeviceLifecycle.cs` (new): the one model every surface
  reads. It takes the supervisor's word **and** the evidence that came with it
  and returns an explicit phase plus the capabilities that phase allows
  (`CanStart`, `CanStop`, `CanSendInput`, `CanAttachFrames`, `CanInstall`).
  The rule that matters: **the evidence outranks the record**. A guest that
  reported boot completion is running even when the record still says
  "Starting"; a record that points at an engine process which is gone is a
  guest that died, not a guest that is running.
- The Studio, the device library, the two state converters, the agent table and
  the command palette all resolve a guest through it now, so a control is
  enabled exactly when the supervisor would accept the action and two panels
  can no longer disagree about the same guest.
- The Studio gained an "engine gone" stage, the same word the device library
  already used, and the boot stepper reads the same lifecycle.

### What did not change

- The wire contract: `instance.status` and `instance.list` keep every field
  they had. `magisk.status` gained one additive field (`installInProgress`).
- The supervisor's authorization policy, scopes, leases and journal.
- The engine, the image, the ADB targeting and the viewport geometry.

## SM-3 - the frame stream

### What the state machine actually was

The Studio's viewport had no stream state at all. It kept four unrelated
facts - `_frameWaitSince` (the first-frame wait), `IsFrameStale` (a five
second budget), `_captureRestricted` (a supervised operation holding the
guest's ADB channel) and `HasFrame` - and the chip, the input gate and the
stage each read a different one of them. The supervisor's side was worse: the
frame subscription lived in the hub's in-memory dictionary, and the window
kept the id it was given. Nothing ever said "that subscription is over".

### The findings

| # | Finding | Frequency | Severity | Product | Decision |
| --- | --- | --- | --- | --- | --- |
| 17 | **An idle guest was reported as a broken stream.** The viewport read "no new frame for five seconds" as a fault: it showed an amber "Stalled 16 s" chip over a perfectly current picture and dropped every tap and swipe. The emulator's image stream is *change driven* - a guest whose screen does not change sends no frames at all. The whole idle guest looked broken and unusable. | 7 | 6 | **42** | **Fixed** |
| 18 | **A closed subscription was never forgotten.** When the guest's engine died, the supervisor closed the frame subscription, but the window kept the closed id: the status poll read "already subscribed" and never opened a new one. The viewport stayed dead for the rest of the session, even after the guest came back. | 5 | 6 | **30** | **Fixed** |
| 19 | **A dead capture left its subscriptions counted as live.** The hub kept the subscriber after the pump died, so `instance.status` counted a stream that was closed, and the capture of a guest nobody watched any more kept running (the unsubscribe only stops a capture when no subscriber is left). | 4 | 4 | **16** | **Fixed** |
| 20 | **A lost supervisor left the subscription id pointing at nothing.** A subscription lives in the supervisor's in-memory hub. When the pipe closed (a supervisor restart), the window kept the id, so after the supervisor came back the viewport never re-attached and the operator was left with a dead picture and a "Running" guest. | 4 | 6 | **24** | **Fixed** |

### What changed

**Supervisor (`Core`)**

- `FramePump` publishes its own health: `PublishedFrames` (how many frames it
  delivered) and `LastFrameUtc` (when it delivered the last one).
- `instance.status` reports it in an additive `frameStream` object
  (`running`, `subscribers`, `lastFrameUtc`, `frames`). The existing fields are
  untouched.
- A capture that dies now closes the subscriptions it owned: they leave the
  hub and the unsubscribe bookkeeping, so the count is honest and the next
  subscriber starts from a clean slate. A pump that was already replaced never
  closes the stream that replaced it.

**Desktop**

- `Desktop/ViewModels/FrameStreamHealth.cs` (new, pure): the stream's four
  states - **Live**, **Idle** (the screen did not change, so no frame came;
  the picture is current and the operator keeps the input), **Stalled** (the
  capture is not running or the guest stopped answering; the input waits) and
  **Held** (a supervised operation owns the guest's ADB channel; the input
  waits). Only a stalled stream and a held channel pause the input.
- The Studio reads the capture's health from `instance.status` and the
  guest's own transport answer, and the chip says "Stalled <age>" only when
  the stream really is broken.
- A closed subscription is recognised (`Desktop/ViewModels/SubscriptionClose.cs`,
  pure): the window clears the id, so the status poll re-attaches as soon as
  the guest is up again. The log stream is re-attached the same way.
- A lost supervisor drops both subscription ids: the ids belonged to a hub
  that no longer exists.

### What did not change

- The wire contract: `instance.status` gained one additive object
  (`frameStream`); every other field is unchanged.
- The viewport geometry, the capture format and the supervisor's
  authorization policy.

## SM-5 - the Magisk lab transaction

### The finding

| # | Finding | Frequency | Severity | Product | Decision |
| --- | --- | --- | --- | --- | --- |
| 14 | **`recoveryRequired` was true through the whole successful install.** The lab journal reported "recovery required" for every phase that was not `prepared`, `verified` or `recovered` - so `staging`, `baseline_boot`, `activating`, `provisioned` and `verifying_boot_N` all claimed that the guest needed a stock recovery. The Studio turned that into a "Lab recovery required" stage over the viewport, and told the operator to recover stock while the install was doing exactly what it was asked to do. | 6 | 5 | **30** | **Fixed** |
| 15 | **An interrupted install was indistinguishable from a running one.** The phase was the only signal, so a transaction that died mid-`activating` looked like a transaction that was still running. | 4 | 4 | **16** | **Fixed** (a journal that has not moved for 10 minutes is an interrupted transaction, and it needs a stock recovery) |
| 16 | **The lab's phase word was printed raw** ("verifying_boot_1") in the inspector and in the rail dot's tooltip. | 4 | 1 | **4** | Filed (issue 2) |
| 21 | **"prepared" counted as an install in flight.** `InProgress` listed `prepared`, so a lab with the patch staged and nothing running claimed that an install was in progress - the chip said "install in progress" between two operator actions. | 4 | 2 | **8** | **Fixed** (the phase is a stable state between two operator actions) |
| 23 | **The Studio's lab state was frozen at selection time.** `magisk.status` was read once, when the guest was selected, so the window kept saying "Prepared" through a whole install and the stage never said that a lab was running. The operator had no live view of the transaction in the product at all. | 5 | 6 | **30** | **Fixed** |
| 24 | **A guest that went away raised an error band for something the window did on its own.** A lab transaction reboots its guest several times; the desktop's automatic frame subscription was refused with `instance_not_running` and showed the operator "Instance 'lab' is not running. Start the device, then repeat the action." for an action the operator never took. | 4 | 3 | **12** | **Fixed** (the window waits in the status line and attaches again when the guest is up) |
| 25 | **A transaction that nobody was driving still said "install in progress".** The journal keeps the last phase it wrote, so a transaction whose supervisor died still said "activating" - and the phase word was read as work in progress. The window waited for an install that was dead, and the stage did not say that a stock recovery was due. | 4 | 5 | **20** | **Fixed** (the transaction is running only while the supervisor that owns it is driving it and its journal still moves) |

### What changed

- `Core/Supervisor/MagiskLabPhase.cs` (new): the phases are classified once -
  failed, running, or interrupted. `RequiresRecovery` is true for a failed
  transaction and for a transaction that was interrupted after the guest had
  already booted with the patched ramdisk; `InProgress` is true while the
  transaction is really being driven. **A phase word alone proves nothing**:
  the journal keeps the last phase it wrote, so a transaction whose supervisor
  died still says "activating". Two facts decide it - whether this supervisor
  is driving the transaction (it holds the lab's lease while the operation
  runs) and whether the journal still moves.
- `magisk.status` reports `recoveryRequired` (now honest) and the new
  `installInProgress`.
- The Studio shows "Magisk lab in progress" while the transaction runs, and
  keeps "Lab recovery required" for the case that really needs a stock
  recovery. **The state follows the transaction**: it is read by the status
  poll, not once at selection, so the window says what the lab is doing while
  it is doing it. A subscription that is refused because the guest is not
  running (a lab reboots its guest) waits in the status line instead of
  raising an error band for an action the operator never took.
  The agent fence in the supervisor (`Guard`) is unchanged: an active lab
  still permits observation only until the guest is back on stock.

## Verification

- `dotnet build -c Release --no-incremental`: 0 errors, 22 warnings (the
  recorded baseline).
- `dotnet test -c Release --no-build`: **617/617** (577 before this slice,
  40 new).
- New unit tests: `DeviceLifecycleTests` (14), `InstanceLifecycleTests` (10),
  `MagiskLabPhaseTests` (8, one rewritten), `FrameStreamHealthTests` (8),
  `SubscriptionCloseTests` (5). They cover the transition table, the capability
  table, the phase classification, the stream's four states and the closed
  subscription.
- `git diff --check`: clean.

### Runtime evidence (a disposable workspace root, no guest)

A managed record whose engine process was gone was written straight into a
throwaway supervisor database (state `Running`, a dead pid) and then read
through the normal entries:

- `wsctl list --root <root>` printed `ghost  emulator-5594  grpc:8584  Stopped`.
  Before the change it printed `Running` for a guest with no engine process.
- `wsctl status --name ghost --root <root>` printed
  `state=Stopped engineAlive=false pid=999999`: the truth, with the pid kept
  as the evidence that the guest had a process and lost it.
- The Studio showed the same guest as one word - the state pill read
  **"Engine gone"** and the stage read "The guest's engine is gone / The engine
  process for ghost is no longer running. / The guest stopped outside the
  supervisor. Start it again to bring it back."
  (`artifacts/ui/ux18-engine-gone.png`).
- The Device Manager agreed: the card's chip read **"Engine gone"**, the card
  offered **Start** (and no Stop), "Running 0 of 1" and "Needs attention 1 ·
  guest to review" (`artifacts/ui/ux18-engine-gone-library.png`).

Two surfaces, one word and one set of capabilities - which is the point of the
slice.

### Runtime evidence (a real guest, `D:\EmulatorWorkspace`, API 35 x86_64)

The rest of the proofs need a guest that really boots, so they were run
against one (`ux18`, `system-images;android-35;default;x86_64`, engine
37.2.12.0, GPU `host`, 1080 x 2400).

- **A guest started without a waiter reaches Running in durable state.**
  `wsctl start --name ux18` with no `wait` and then `wsctl status` alone
  drove `Starting` -> `Running` in about 90 s; the SQLite record persisted
  `Running` and `wsctl list` printed it.
- **The Studio attaches to it and shows the live guest**
  (`artifacts/ui/ux18-attaches-without-waiter.png`): the picture, the boot
  stepper's three reconciled stages ("Boot 93.2 s") and the nav cluster.
- **An idle guest is not a stalled stream.** With the guest up and its screen
  unchanged, the chip reads "Guest display - 1080 x 2400" and *no* "Stalled
  Ns" suffix (`artifacts/ui/ux18-idle-guest-2.png`).
- **A tap in the viewport reaches the guest.** A click on the Gallery icon in
  the viewport opened the Gallery app in the guest
  (`foreground: com.android.gallery3d/...GalleryActivity`), and the Studio's
  own Recents command showed the guest's recents screen
  (`artifacts/ui/ux18-recover-9-gallery.png`).
- **An engine that dies is reported, not pretended away.** The engine process
  was killed: `wsctl status` printed `state=Stopped engineAlive=false`, the
  Studio's pill read **"Engine gone"** and the stage read "The guest's engine
  is gone" (`artifacts/ui/ux18-recover-1-engine-gone.png`).
- **The viewport re-attaches by itself when the guest comes back.** After the
  guest was started again, the desktop opened a new subscription on its own
  (`frameSubscription` 0 -> 1, `frameStream.running` true, frames flowing)
  with no operator action (`artifacts/ui/ux18-recover-2-reattached.png`).
- **A supervisor that dies is survived.** The supervisor process was killed:
  the watchdog restarted it, the Studio reconnected ("supervisor pipe closed"
  -> "supervisor connected") and re-attached to the guest
  (`artifacts/ui/ux18-supervisor-restart-reattached.png`).
- **A guest that outlived its engine process is named, then cleared.** The
  engine wrapper was killed while its guest stayed alive. The next start
  refused with "Port 5554 is held by the previous guest of instance 'ux18':
  it still answers on emulator-5554 as this instance's AVD, although its
  engine process is gone. Stop the instance first - that clears the leftover
  guest: wsctl stop --name ux18 --force." The stop answered
  `{"stopped":true,"forced":false,"orphanStopped":true}`, the leftover guest
  was gone, and the next start worked.

### Runtime evidence (a real Magisk lab, `D:\WS-Magisk-UX18`)

The lab transaction needs a patched ramdisk, the pinned Magisk manager and
the pinned root probe. All three are on this host
(`artifacts/magisk-lab/linux-20261113-110018` and
`.../linux-20261114-124924-baseonly`), so the whole transaction was run
against a disposable guest ("lab") on the API 35 x86_64 image, engine
37.2.12.0, WHPX, SwiftShader - under the owner-approved lab exception in
`agents.md` (the journaled provisioning asks for ADB root once and verifies
adbd back at UID 2000).

- **Prepared is not an install.** `magisk.prepare` reported
  `phase=prepared, installInProgress=false, recoveryRequired=false`, and the
  Studio showed "Magisk lab: prepared" with the guest's own stage - not
  "Lab recovery required" (`artifacts/ui/magisk-ux18-1-prepared.png`).
- **An install in flight says so.** The phases ran
  `baseline_boot -> activating -> verifying_boot_2`, and every read reported
  `installInProgress=true, recoveryRequired=false`. The Studio's stage read
  **"Magisk lab in progress"** with "Magisk lab: verifying_boot_2 (install in
  progress)", and the inspector read **"Install in progress"**
  (`artifacts/ui/magisk-ux18-3-install-in-progress-live.png`).
- **The install really worked.** The transaction finished at `verified` with
  two cold-boot app-root proofs: the probe app (uid 10149, context
  `u:r:untrusted_app:s0`) reached `uid=0(root)` in `u:r:magisk:s0`; Magisk
  reported 30700, Zygisk off, no modules; and the journaled provisioning
  recorded the root request (UID 2000 -> 0) and the unroot (0 -> 2000) with
  `adbdUid2000Verified`.
- **Stock recovery removed it.** The recovery finished at `recovered` and the
  stock app probe was denied `su` with `error=13` (permission denied); the
  inspector read "Recovered"
  (`artifacts/ui/magisk-ux18-6-recovered-diagnostics.png`).
- **An interrupted install says a recovery is due.** A second run
  (`D:\WS-Magisk-UX18b`) had the supervisor killed in the `provisioned` phase
  - the guest had already booted with the patched ramdisk. The next read
  reported `phase=verifying_boot_1, installInProgress=false,
  recoveryRequired=true`, and the Studio's stage read **"Lab recovery
  required"** with the inspector row "Stock recovery required"
  (`artifacts/ui/magisk-ux18-7-interrupted-recovery-required.png`). The
  approved stock recovery then completed (the app probe denied `su` with
  `error=13`, Magisk absent, adbd back at UID 2000). Before this, the same
  state said "install in progress" and the operator waited for a dead
  transaction.

The local evidence (the lab journals with their provisioning records, the
install and recover results) is under `artifacts/magisk-lab/ux18-20261116`
and `artifacts/magisk-lab/ux18-20261116-crash`; both lab roots, their guests
and their supervisors were removed afterwards.

Every hardware proof this audit asked for is now closed: the guest that boots
without a waiter, the engine that dies, the guest that outlives its engine,
the Magisk install in flight and the Phase 2 agent acceptance.
