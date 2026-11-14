# Windows Magisk review handoff

Branch: `codex/magisk-windows-lab`, created directly from `485b093`.
The existing Grok branch is unchanged. The unrelated readiness patch is not
applied or included. This branch contains the Windows implementation plus
the follow-up changes requested in Grok's review.

## What to review

1. **Provisioning authority and evidence.** `MagiskGuestAdapter.Provision`
   does call the supervisor's targeted ADB-root operation. It is internal,
   and its caller revalidates the opted-in, activating transaction, instance
   generation, pinned image and exact patched ramdisk before provisioning
   and again immediately before requesting root. `agents.md` records the
   narrow owner-approved disposable-lab exception. Journals record root and
   unroot attempts/results and require adbd UID 2000 before Magisk proofs.
   Recovery records its own unroot result. No agent scope authorizes these
   Magisk operations, and no general product permission was inferred.
2. **Final binary hardware evidence.** See the final-run section in
   `docs/magisk-lab-windows-evidence.md` and the accompanying compact evidence
   JSON. It identifies exact Release payload hashes and distinguishes the
   successful final run from earlier iterations/failures. No code changes
   are to follow the successful full hardware run before this commit.
3. **Supervisor selection.** The shared locator now prefers repository
   Release over Debug, including Release DLL over Debug executable.
   Packaged adjacent and explicit paths retain precedence. A nonexistent
   explicit path fails instead of silently selecting another binary. Four
   tests cover these cases; a live CLI launch with no override also selected
   the Release supervisor.
4. **Probe results.** The pinned APK remains unchanged. Its instrumentation
   bundle/completion is parsed separately from generic JUnit counts. The
   strict parser requires eight unique fields, exact package and app UID,
   valid PID/context/caller identity, completion code -1, and explicit su
   result. Missing, malformed, truncated, duplicate, wrong-UID, timed-out or
   failed output is rejected. The generic tests=0/passed=false is preserved;
   a separate appProof records the validated root/denial result. Acceptance
   checks both app proofs and durable unroot evidence, not substring presence
   or the generic JUnit success flag.
5. **Environment checks.** ls -A includes hidden files and symlink entries;
   a populated environment is refused. Directory symlinks are rejected,
   owner/group must be 0:0, and SELinux labels are checked by exact path.
   A real run discovered that pinned Magisk creates the empty magisk
   directory with system_file, while the parent has adb_data_file. The
   initial rejection, verified unroot and successful rollback are retained.
   The corrected check allows system_file only for /data/adb/magisk.
6. **Summary correction.** The actual local summary for
   transaction-20261114-134857-299370 now says
   stock-recovery-from-compatible-data-backup and carries a correction note.
   Its probe app was absent after baseline restoration. Earlier normal
   recovery independently proved app denial with installed data preserved.
7. **Compatibility.** Linux passed on Emulator 37.2.13.0/build 16740729/KVM;
   Windows uses 37.2.12.0/build 16428233/WHPX. They are distinct tested rows.
   Source-built AOSP capacity, release signing, redistribution review and
   sudden host-power-loss testing remain open. No binaries/bundles are pushed.

The current Release unit suite has 89 passing tests. New tests include
fail-closed probe parsing, enrollment rejection and supervisor discovery.
The older real corrupt-patch, staging-crash, timeout and live WPF corrupted-
data recovery evidence remains documented separately from the final normal
transaction. Raw evidence stays local under artifacts/magisk-lab; compact
reviewable results and hashes are included in docs.

Please review the actual diff from 485b093, especially authorization,
journal updates, recovery boundaries, strict probe parsing and supervisor
selection. Return concrete findings with file/line references. Do not merge
the unrelated readiness patch into this Magisk work.

## Final verification

`transaction-20261114-142606-818906` passed all eight assertions on the final
Release payload: install, two parsed app-root cold boots, journaled unroot,
replay denial, snapshot fencing, stock recovery/app denial and cleanup.
All 89 unit tests pass. Source and payload hashes remained unchanged across
the run. See [compact evidence](evidence/magisk-windows-review-20261114.json)
and [full Windows report](magisk-lab-windows-evidence.md).
