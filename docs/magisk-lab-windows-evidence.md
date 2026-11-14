# Magisk Lab Windows evidence

## Base-only boot feasibility: passed

Run `artifacts/magisk-lab/windows-baseonly-20261114-131551/` passed
132 checks through the Release CLI and explicitly pinned Release supervisor.
This is the boot-input/provisioning probe, not transactional product acceptance.

Host: Windows 11 Pro 26H2 build 26300.9974, Intel i5-8265U, 31.8 GiB,
UHD 620 driver 31.0.101.2135. Engine 37.2.12.0 build 16428233 with WHPX.
SDK image `system-images;android-35;default;x86_64` revision 2, API 35,
x86_64 userdebug. SwiftShader indirect, 1080 x 2400, 4 vCPU, 3072 MiB.
The managed profile has more CPU/RAM than the Linux run (2 vCPU/2048 MiB).
Snapshot load and save disabled; all boots were cold.

The owner explicitly authorized temporary ADB root for provisioning.
The pinned 13-file environment was staged without the upstream test APK or
modules. Guest hashes matched. Provisioning returned adbd from UID 0 to 2000
before any Magisk proof. Every evidence request verified the managed process
generation, emulator serial, AVD name and transport ID.

Both post-provisioning cold boots reported Magisk 30700. Shell UID 2000 and
app `dev.emulatorworkspace.suprobe`, UID 10149, `untrusted_app`, independently
reached UID 0 with SELinux context `u:r:magisk:s0`. Adbd remained UID 2000.
Modules and boot-script directories were empty, Zygisk was explicitly off,
zygote had no Zygisk mapping, and there was no hosts mount.

Stock ramdisk restoration completed boot, preserved the guest marker and
app UID, removed Magisk elevation, and made the app probe report permission
denied (`error=13`). The original stock ramdisk hash remained unchanged.
Each cold boot followed `sync` and guest power-off; cleanup removed the
disposable guest and stopped its supervisor.

The generic instrumentation API reports `tests=0, passed=false` because
this probe returns identity fields rather than JUnit test counts. Acceptance
asserts the raw caller UID/context, root result and instrumentation output;
it does not reinterpret that generic JUnit flag as success.

Reproduction:

```powershell
python tools/magisk-baseonly-acceptance.py --root D:/WS-Magisk-NewProbe --allow-provisioning-adb-root
```

## Supervisor transaction verification

The initial run `artifacts/magisk-lab/transaction-20261114-132646/` failed
at a provisioning precondition after baseline boot and backup. Automatic
stock rollback completed, and cleanup removed the guest. This failure is
retained separately; it is not successful install evidence. The revised
adapter accepts an empty pre-existing environment directory, normalizes
Windows ADB line endings and records bounded command evidence. Rerun
`transaction-20261114-133402` passed all seven transaction assertions,
including two app-root cold boots, stock recovery and guest cleanup.
That iteration passed its Release build and 72 tests. The Grok-review revision
below adds stricter enrollment/probe checks and passes 89 tests.

New source exposes operator-only `magisk.prepare`, `magisk.status`,
`magisk.approve`, `magisk.install` and `magisk.recover` through the supervisor.
Approval binds the operator Windows logon session, instance generation, transaction and action, expires
in ten minutes and is consumed durably. Prepared inputs are copied into supervisor-managed transaction directories
and pinned; stock and data backups are preserved. Snapshots are conservatively
fenced for enrolled lab generations. Windows recovery does not require a
booted Android guest. A recovery failure keeps the journal and artifacts and
requires explicit recovery before other mutations.

Failure evidence:

- `transaction-20261114-135144-329361`: the actual supervisor process was
  terminated while the journal said `staging`. The harness held a Windows
  process handle after verifying the exact executable path and the PID
  returned by this isolated root. After restart, `staging` persisted, normal
  start was fenced, approved recovery passed without Android, and cleanup
  passed. This is process-crash evidence, not sudden host-power-loss testing.

- `transaction-20261114-134311-642159`: staging failed after a partial copy
  because the manager APK was missing. After supervisor shutdown/restart,
  the durable `staging` phase fenced normal start and approved recovery
  completed without booting Android. This is partial staging/restart
  evidence, not a power-loss durability claim.
- `transaction-20261114-134827-108536`: a deliberately corrupted private
  patch was rejected before guest mutation; restart, recovery and cleanup passed.
- `transaction-20261114-134330-858968`: patched boot exceeded its approved
  one-second deadline. Automatic stock rollback returned `recovered`, with
  boot complete and adbd UID 2000. The shell `su -c` failure was a syntax error, not root denial. The original harness
  expected the words "timed out" rather than the actual "readiness within
  1s" message; its failed summary is retained alongside the independent
  `corrected-assertion-review.json` against the saved raw response.
- Corrupt-patch attempt `transaction-20261114-134253` hit a shutdown/restart
  pipe race; attempt `transaction-20261114-134347-650783` passed the safety
  checks but cleanup encountered ports used by the concurrent timeout run.
  The harness now waits for the supervisor lock to release. Run guest lanes
  sequentially; later isolated cleanup and the corrupt-patch rerun passed.

`transaction-20261114-134857-299370` passed the full install and two
cold-boot proofs again. The harness then replaced only its stopped disposable
userdata overlay with invalid bytes. The live WPF Recover stock control
presented the exact guest/transaction and submitted approval and recovery.
The first stock attempt failed on the invalid overlay; the supervisor
preserved failed userdata/encryption overlays, restored all hash-verified
baseline files, booted stock and proved adbd UID 2000,
then booted stock again with no private ramdisk override. The UI reported
`Magisk lab: recovered`; normal supervisor deletion and empty-list cleanup
passed. Raw approval/result UI trees, a screenshot, failed overlays and
payload hashes are retained in that run. Following Grok review, the actual
summary.json label was corrected to `stock-recovery-from-compatible-data-backup`,
with a correction note. After baseline restoration the probe app is absent,
so this lane proves baseline data recovery, not shell root denial. App denial
on preserved installed data was proved by `transaction-20261114-133402`.
The final screenshot's pipe error follows the harness's intentional
supervisor shutdown after successful recovery.

Two small fixes followed that hardware run: status reads no longer wait
behind the transaction mutation lease, and journal retirement creates its
directory if a crash preceded directory creation. Build/unit verification
and final process-crash smoke `transaction-20261114-140455-708817`
(passed, including restart/recovery/deletion) cover those changes; the full hardware run
above identifies its earlier exact binaries in `payloads.json`.
Source-built AOSP image capacity and redistribution/signing reviews remain
separate gates. No release-readiness claim is made.

## Operator entry points

The shared supervisor locator now prefers repository Release over Debug
(including a Release DLL over a Debug executable). A packaged adjacent
supervisor or explicit path retains precedence. An invalid explicit path
fails closed instead of silently selecting another binary. For reproducible
acceptance, keep `EMULATORWORKSPACE_SUPERVISOR_EXE` explicitly set. Send JSON using
`wsctl call --op OP --args-file FILE --root ROOT`.

1. `magisk.prepare`: `name`, `patch`, `apk`, `probe`, and
   `allowProvisioningAdbRoot: true`. The guest must be stopped and stock.
   An optional `bootTimeoutSeconds` (1–300, default 300) is bound into
   the prepared transaction; the short setting exercises timeout recovery.
2. Review `magisk.status` and its exact `transaction`.
3. `magisk.approve`: `name`, `transaction`, `action: "install"` or `"recover"`.
4. Call `magisk.install` or `magisk.recover` with `name`, `transaction`
   and the returned single-use `approval`. Do not publish the credential.

The WPF Root Lab panel exposes separate Install Magisk lab and Recover stock
controls. It selects pinned inputs, prepares them, and displays the concrete
transaction and provisioning/recovery effects before approval. Recovery is
available for a stopped or unbootable enrolled guest. Agent credentials
cannot call these operator-only operations; MCP remains off by default.
Snapshots stay fenced for this lab generation, even after stock recovery;
use a fresh stock profile for snapshot work. Existing snapshots are retained.

Reproduce failure lanes with a new root for each:

```powershell
python tools/magisk-transaction-acceptance.py --root D:/WS-Magisk-NewRun --allow-provisioning-adb-root --mode normal
# Other modes: corrupt, interrupted-staging, crash-staging, timeout, data-recovery.
# --desktop-recovery waits for recovery through the live WPF controls.
```

## Grok review follow-up (2026-11-14)

Yes: `magisk.install` calls `MagiskGuestAdapter.Provision`, which requests
ADB root. This is limited to the explicitly opted-in, enrolled SDK lab
transaction; it is not a general product/agent entitlement. Before starting
provisioning and immediately before requesting root, the supervisor rereads
the journal, verifies its instance-generation binding, exact transaction,
`activating` phase, opted-in flag, pinned image, private transaction directory
and active patched ramdisk. The adapter provisioning method is internal.
The narrow owner-approved provisioning exception is documented in agents.md;
normal Magisk verification does not rely on a rooted adbd.

Every new journal starts with `adbProvisioning.rootRequested=false` and
`unrootAttempted=false`. Provisioning durably records intent before requesting
root, the root result, the unroot attempt, the unroot result (including exact
transport, observed time and `uidAfter`), and `adbdUid2000Verified` before
root proofs proceed. Cleanup attempts unroot even when the root request or
provisioning fails. Recovery independently saves `recoveryAdbUnroot` and
requires UID 2000. A process crash can leave an incomplete attempt in the
journal; it cannot fabricate a successful unroot check, and the generation
remains fenced for explicit stock recovery.

`ls -A` includes hidden files and symlink entries when determining whether
the environment is empty; command failure is fatal. The adapter rejects
symlinks at `/data/adb` and `/data/adb/magisk`, requires root:root ownership,
and validates exact path/SELinux-label pairs: `/data/adb` must be
`adb_data_file`; `/data/adb/magisk` may be `system_file` (observed for the
pinned Magisk-created empty directory), `adb_data_file` (newly created
under the data directory), or `magisk_file`. Other labels are refused. Directory ownership, permissions and labels are captured in the
provisioning output. A populated or unexpectedly labeled environment is
refused, not wiped.

The pinned probe APK is unchanged. It returns a valid instrumentation result
bundle and completion code, but no JUnit tests. `MagiskProbeEvidence` parses
that exact bundle, requires all eight fields once, the exact package/UID,
a valid app PID/context, a matching caller identity, successful completion,
and explicit su output. Missing, truncated, duplicate, malformed, wrong-UID
or failed output throws `adb_failure`. The transaction returns a separate
`appProof` with verified caller/root results; generic JUnit `passed=false`
remains untouched. Regression tests cover the failure cases and Windows ADB
line endings. Acceptance requires both parsed app proofs and journaled
provisioning unroot verification.

Linux passed on Emulator 37.2.13.0 (build 16740729, KVM); Windows passed on
37.2.12.0 (build 16428233, WHPX). These are distinct tested rows, not a claim
that the engine builds are identical. Source-built AOSP, signing,
redistribution and sudden host-power-loss testing remain outside this result.

Final Release hardware rerun **passed**:
`transaction-20261114-142606-818906`, with eight acceptance assertions,
including install, two parsed app-root cold-boot proofs, journaled unroot,
approval replay rejection, snapshot fencing, stock recovery with app-root
denial and guest cleanup. Release build passed (21 existing warnings on the
final incremental build; no new warnings) and all 89 tests passed. Both the
source manifest and payload hashes were rechecked unchanged after completion.
No code was changed after this successful full run.

[Committed compact evidence, payload hashes and corrected prior summary](evidence/magisk-windows-review-20261114.json)

The running supervisor path and creation time were checked against the
Release payload. A concurrent magisk.status request completed during
baseline_boot in 0.219 seconds. A separate live CLI launch, without an
explicit environment override, selected the Release supervisor. Raw logs,
journals and binaries remain local; the committed JSON provides reviewable
results and hashes without redistributing SDK or Magisk assets.

| Final Release file | SHA-256 |
| --- | --- |
| `wsctl.exe` | `b95e32aea6fe5e84f235e8c2cf3bf4a24fa0ce8341fb925e647ad9f41d25ec75` |
| `EmulatorWorkspace.Supervisor.exe` | `0f039b61c5ba308ac2a5ca0b7dcaae2f5fdce06d4681502e8d5a0e8791e43ad2` |
| `EmulatorWorkspace.Supervisor.dll` | `61144dc3d34469a6422a989d64372ad2bdde13a55585a0f2ba47ab637cee5857` |
| `EmulatorWorkspace.Core.dll` | `3be84449838941e7689efb80c765ee288ca6bfbb3a8fe76175caccb29a83eb62` |

The first Grok-review hardware run `transaction-20261114-141840-349692`
exposed the pinned Magisk-created empty directory's `system_file` context.
The initially narrower check refused provisioning. The journal recorded
`rootRequested=true`, `unrootAttempted=true`, `adbdUid2000Verified=true`,
and the exact ownership/label trace. Automatic stock rollback completed
with `recoveryAdbUnroot.uidAfter=2000`; guest deletion and supervisor shutdown
completed. This failed attempt is retained separately. The corrected check permits `system_file` only for the exact
`/data/adb/magisk` path; it does not relax the parent directory check.

## Second review corrections (2026-11-14)

M1: stock verification now requires no magiskd, a failing/absent `magisk -V`,
and absence of `/debug_ramdisk/magisk` (including a dangling symlink).
It independently requires adbd UID 2000 and, when the installed probe survives,
app-level EACCES. AOSP `su 0 id` is recorded separately; stock userdebug is not
a promise of shell root denial. Historical JSON retains the old source hashes
as historical data, adds reviewed commit blob IDs, and retracts shellSuDenied.
The non-reproducing SupervisorLocator hash is explicitly disclosed.

M2 decision: active labs expose observation only to paired agents. Both outer
and nested dispatcher guards reject agent mutations, including app launches,
instrumentation, input, recipe replay and lifecycle control. This also applies
to already verified journals. Operator control remains available. We chose the
supervisor fence, so no unverified Magisk auto-response setting is used.
The permanent shell UID 2000 allow row grants Magisk root to any host process
that can use ADB shell against this patched lab.

L1: installation holds a FileShare.Read handle on the patch through boot and
provisioning on Windows; both provisioning enrollment checks also re-hash it.
Transaction directories are supervisor-managed, not protected from same-user
processes by a special ACL. Host power-loss durability remains untested:
WriteAtomic flushes the temporary file but does not fsync the rename/directory.
L2: the provisioning journal preserves provisioningError alongside unrootError;
a double failure throws an aggregate containing both original exceptions.
L3: approval binding is the operator Windows logon session, not client PID;
the secret single-use token remains required across separate CLI invocations.
L4: desktop consent precedes prepare, includes exact input paths and temporary
ADB-root permission. Cancel makes no journal. A recovered generation remains
single-use; create a fresh disposable instance for another installation.
L5: desktop install/recovery deadline is 60 minutes, covering the configured
boot/power-off waits with room for backup and rollback. Disk-copy time has no
fixed upper bound; the journal remains authoritative after any timeout.

The payload hash comparison now checks the exact regular-file set. Shell output
normalization removes all CR characters. `/data/local/tmp/ws-magisk-env` and
`/data/adb/magisk` are intentionally retained as evidence; the latter is inert
on stock. No cleanup of unrelated guest files is attempted.

Verification: Release build passed with 22 existing warnings; 114/114 tests
passed (including real dispatcher code with simulated sessions). Fresh WHPX
transaction `transaction-20261114-150136-508009` passed 8/8, including both
app-root cold boots, stock app denial and cleanup. Stock shell `su 0 id`
returned UID 0 with `u:r:su:s0`, confirming the review's inference; meanwhile
`magisk -V` returned 127, `/debug_ramdisk/magisk` was absent, no magiskd ran,
and adbd remained UID 2000. A concurrent write-open on the leased patch was
denied by Windows. Payload hashes remained unchanged. Compact results and
source blob IDs: [second review evidence](evidence/magisk-windows-review2-20261114.json).
Fresh WPF Cancel click-through and fake-ADB cancellation/timeout tests remain
pending; no release-readiness claim follows from this scoped run.

## Third Magisk review follow-up (2026-11-14)

N1: Install now uses LeaseVerified: open FileShare.Read, hash that same handle,
rewind it, and retain it through install/rollback. Hash failure disposes the
handle. Tests reject replacement bytes, verify rewind/disposal, and on Windows
verify write, rename and delete exclusion. The previous implementation also
re-hashed patched.img in the pins loop after acquiring the lease; that check
already preceded every boot. The new helper makes the handle/hash relationship
explicit rather than depending on that second path-based check.

N2 accepted/documented: later operator starts of an already verified lab still
hash by path without retaining a lease. They do not request provisioning ADB
root. Same-user host processes are not isolated by this lab; that remaining
operator-start TOCTOU is not claimed closed.

N3: expanded real-dispatcher/simulated-session and direct guard tests across
all 12 active phases. Recipe replay targeting the lab is denied at the outer
entry; the nested internal dispatch entry is separately exercised with that
same agent session. This is simulated authorization evidence, not guest replay.
N4: renamed review2 reviewedCommit to baseCommit. Preserve historical
magisk_absent exit markers: 0 means the absence-test succeeded, 1 means it
failed. No historical proof output or source blob ID was changed.

Recommended fake-ADB cancellation/root-timeout/double-failure tests and WPF
Cancel click-through remain open, as does the boot-completed substring nit.
Power-loss, source-built AOSP capacity, signing and redistribution remain open.
The unrelated readiness patch stays excluded. No new hardware run is claimed;
review2's passing 8/8 applies to its recorded binaries only.

Verification: `dotnet build -c Release --no-restore` passed with 22 existing
warnings; `dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj
-c Release --no-restore` passed 388/388 on Windows. Logs:
`artifacts/magisk-review3-build.log` and `artifacts/magisk-review3-tests.log`.
`git diff --check` passed. These are build/unit checks, not a fresh WHPX run.

## Fourth Magisk review follow-up (2026-11-14)

Grok independently passed the Linux Release build and 388 tests at 8eace58,
with no merge blockers. The original N1 swapped-boot finding was withdrawn:
the prior pins loop already re-hashed the patch under the lease before boot.

F1: split rewind/release coverage from Windows write/delete/rename exclusion.
WindowsFact reports an explicit skip on non-Windows runners; platform-specific
assertions no longer silently disappear inside a passing portable test.
F2: the 60 dispatcher magisk.* phase cases prove end-to-end scope denial, not
phase-guard reach: AuthorizeAgent rejects them first. The direct Guard theory
covers all 144 phase/operation pairs, including the magisk.* early-return fence.
F3: removed the File.Exists precheck from LeaseVerified. IOException and
UnauthorizedAccessException during open now return invalid_request, including
missing/deleted and sharing-denied files. Hash/seek failures still dispose the
opened handle. Tests cover missing files and Windows sharing denial.

No new WHPX or WPF run is claimed. Fake-ADB cleanup tests, WPF Cancel,
boot-completed exact markers, later operator-start lease and replay-loop
session-forwarding regression coverage remain optional follow-ups. Power-loss,
source-built AOSP, signing and redistribution gates remain open. Readiness
changes remain excluded. Merge approval is not release-readiness approval.

Verification: Release build passed with 22 existing warnings; Windows tests
passed 391/391, zero skipped. The two WindowsFact cases executed on this host.
Linux skip reporting is implemented but was not run here; expected Linux
result is 389 passed / 2 skipped, subject to independent verification.
Commands: `dotnet build -c Release --no-restore` and
`dotnet test -c Release --no-build --no-restore`. Logs:
`artifacts/magisk-review4-build.log`, `artifacts/magisk-review4-tests.log`.
`git diff --check` passed. No fresh hardware evidence is attached to this change.
