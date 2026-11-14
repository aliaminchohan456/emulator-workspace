# Questions for Grok: base-only Magisk setup

## Response reviewed (2026-11-14)

Grok answered the questions and supplied corrected docs in `485b093`, now
fast-forwarded into local master with the Windows review notes preserved.
The original setup did install Shamiko/test modules and enabled Zygisk by
default. The corrected docs distinguish that run from the new base-only run.

The new tar `linux-20261114-124924-baseonly.tar` matches SHA-256
`5b212869df8170b9bf7933eeacb384d287670649a4e4e765adf8ddf43305775d`.
Its inventory matches
`d974791c87209f10ae39311a33dc6a35a14602bb06f5c5754483080ce6d75a9a`,
and all 48 inventoried files passed verification. The 13 base payload files
were independently extracted from the pinned release APK to
`artifacts/magisk-lab/windows-baseonly-staged/` and match `staged.sha256`.
The app probe signature verifies with the reported certificate SHA-256
`65950e78ccaf1883ed05cf288fdcef0596ad7077dc35ac55aa339dcf0d3febd1`.
The probe source, setup scripts, second cold-boot and stock-restore evidence
were reviewed. No bundled host setup or cleanup script was executed.

The verified Linux procedure uses temporary **ADB daemon root for
provisioning**, explicitly disables Zygisk and grants shell/probe UID policies.
It then restores adbd UID 2000 before testing independent Magisk elevation.
Grok explicitly left acceptance of this provisioning dependency to the owner.
The owner explicitly approved temporary ADB root for provisioning. Windows
base-only and supervisor transaction probes subsequently passed; see
[Windows evidence](magisk-lab-windows-evidence.md). The alternative app-UI
setup remains unverified.
Do not execute the old full-environment instrumentation command.

The questions below are retained as historical context, not unanswered
requests to repeat the Linux investigation.

Windows review, 2026-11-14. Please resolve the following before we repeat the
setup command or implement the supervisor install flow.

The docs branch was fast-forwarded locally from `3200d3e` to `378d5a9`.
All eight parts, the uncompressed tar (`c6c30d11…5035c`), and all 55 entries in
the extracted `SHA256SUMS` match. The installed Windows SDK ramdisk, kernel,
system and vendor also match the transferred image. Release build and 64 tests
pass. The unrelated readiness-probe patch has not been applied.

## Conflict found in the pinned source

The handoff calls this first-boot command environment setup:

```text
am instrument -w --user 0 -e class '.Environment#setupEnvironment'
  com.topjohnwu.magisk.test/com.topjohnwu.magisk.test.AppTestRunner
```

At the exact pinned Magisk commit, that method invokes the base emulator
installer **and also installs Shamiko on API >=27**, prepares mount-test,
SELinux, systemless-hosts, invalid/empty Zygisk and other test modules.
This conflicts with this repository's exclusion of root-detection bypass and
its requirement to defer modules until base install and restore pass.

Source:
[Environment.kt at e8a58776](https://github.com/topjohnwu/Magisk/blob/e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac/app/core/src/main/java/com/topjohnwu/magisk/test/Environment.kt).
Downloaded source SHA-256:
`baef7616c3e1ae396b1740343f5d35b09ab0620c2ee60af14836037d8538c421`.

1. Were the transferred APKs built from unmodified pinned sources? If not,
   provide the exact diff, build configuration and corresponding hashes.
2. Did the recorded Linux run install these modules? Please provide the
   retained module inventory, Zygisk setting, setup logs and any deviations
   from the upstream method. The existing `setup-environment.txt` only says
   `OK (1 test)`. Please correct the evidence description where necessary.
3. Provide an exact **base-only** setup procedure for this ramdisk/APK pair,
   without Shamiko, other modules, hiding or Zygisk changes. If a reduced
   instrumentation test is needed, provide its minimal source diff and
   build/signing provenance. Please verify it on a fresh disposable Linux
   guest, with two cold boots, an empty module inventory and stock recovery.
4. `MagiskAppTest#testSuRequest` executes `su -c id` through
   `uiAutomation.executeShellCommand`, which tests UID 2000 shell elevation.
   `BaseTest.prerequisite` separately asserts an app root shell, but the
   handoff does not record an app caller UID and its resulting UID/context.
   Please supply that explicit app-level evidence or label the existing
   results precisely and provide a base-only app probe for Windows.

Related pinned sources:
[MagiskAppTest.kt](https://github.com/topjohnwu/Magisk/blob/e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac/app/core/src/main/java/com/topjohnwu/magisk/test/MagiskAppTest.kt),
[BaseTest.kt](https://github.com/topjohnwu/Magisk/blob/e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac/app/core/src/main/java/com/topjohnwu/magisk/test/BaseTest.kt).

## Windows state

The preflight verified Windows 11 build 26300.9974, i5-8265U, 31.8 GiB,
UHD 620 driver 31.0.101.2135, engine 37.2.12.0 build 16428233 and usable WHPX.
The disposable probe was stopped during its **stock** boot wait after this
source conflict was found. No patched boot, APK install or instrumentation
setup was executed. Stock boot completion was not established.

The guest was stopped and deleted through wsctl; both probe-owned supervisors
were shut down; ADB listed no attached devices. Raw evidence and the pinned
source copies are in `artifacts/magisk-lab/windows-20261114-123747/`.
This interrupted probe used the Release CLI with an automatically resolved
Debug supervisor, so it is not Release runtime acceptance. Explicitly pin the
supervisor binary on the next run. No host feature changes were made.
