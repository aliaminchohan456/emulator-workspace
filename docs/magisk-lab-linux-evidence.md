# Magisk Lab Linux evidence

Collected 2026-11-13 on the Linux builder, with a base-only rerun and a
correction on 2026-11-14. Raw artifacts are under
`artifacts/magisk-lab/linux-20261113-110018/` (first run) and
`artifacts/magisk-lab/linux-20261114-124924-baseonly/` (base-only run), both
gitignored. The Linux guest is the pinned **SDK regression image**, not a
source-built AOSP image.

> **Correction (2026-11-14).** The 2026-11-13 run used upstream
> `Environment#setupEnvironment`, the full Magisk CI test environment. It
> installed Shamiko and several test modules, and Zygisk was enabled through
> the emulator default. That run is therefore **not** a base-only install, and
> its `su` proof covers only shell UID 2000. The base-only procedure and its
> evidence are in [Base-only run](#base-only-run-2026-11-14).

## Host and preflight

| Item | Value |
| --- | --- |
| OS / kernel | Debian GNU/Linux 13 (trixie), Linux 6.12.113+, x86_64 |
| glibc | 2.41 |
| CPU threads | 8 |
| RAM | 16,779,689,984 bytes (15.6 GiB) |
| Free on build volume | 114,026,356,736 bytes (106.2 GiB) at preflight |
| Build workspace | `/workspace/aosp` (`/mnt/aosp` does not exist) |
| KVM | `/dev/kvm` accessible; `emulator-check accel` -> `KVM (version 12) is installed and usable.` |
| Git / Python | 2.47.3 / 3.13.5 |
| Repo / JDK | `repo` absent; JDK 21.0.13+9 installed from Temurin because Debian apt egress failed |

The AOSP preflight failed capacity exactly as expected:

```text
$ bash tools/build-magisk-lab-aosp.sh --check /workspace/aosp
CPU threads: 8
RAM bytes: 16779689984
Free workspace bytes: 114026356736
The source build requires 64 GB RAM and 400 GB free on its workspace volume.
check_exit=2

$ bash tools/build-magisk-lab-aosp.sh --build /workspace/aosp
... same capacity error ...
build_exit=2
```

No `manifest-pinned.xml`, source-build `provenance.txt`, or source stock image
was produced. The source-built lab image remains blocked.

## Magisk v30.7 build

```bash
git clone --branch v30.7 --recurse-submodules https://github.com/topjohnwu/Magisk.git
git rev-parse HEAD   # e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac
python3 build.py ndk
python3 build.py -r all
```

- APK version: `e8a58776`, version code `30700`.
- APK: `out/app-release.apk`, SHA-256
  `d820319e907a16eecd3239be360def9299b351da1155736ac9a28966092dfd7e`.
- Test APK: SHA-256
  `82e82c3d237823feb3489b27bc49ee9742d4596784dd655af9d298ee0e4290a6`.
- x86_64 components present: `libbusybox.so`, `libinit-ld.so`, `libmagisk.so`,
  `libmagiskboot.so`, `libmagiskinit.so`, `libmagiskpolicy.so`.
- Toolchain: Temurin JDK 21.0.13+9, ONDK r29.5, Android SDK build-tools
  36.1.0, platform android-36.1, Gradle 9.3.0, ADB 37.0.1.
- Source state: the checkout at `e8a58776` has no tracked changes, no stash, no
  `config.prop`, and no `~/.gradle/gradle.properties`. All seven submodules
  are at their recorded commits and are clean. Only build outputs are
  untracked (ignored). `out/app-release.apk` and `out/test.apk` hash to the
  transferred `d820319e…` and `82e82c3d…`. The APKs were not rebuilt to prove
  bit-for-bit reproducibility.
- Signing: with no `config.prop`, `Setup.kt` falls back to
  `signingConfigs["debug"]`, which is this builder's `~/.android/debug.keystore`
  (`androiddebugkey`, created 2026-11-13 10:56 PKT; certificate SHA-256
  `a8e2fa1e8689d7de11cd5a26373273cd98a84bbdaa1df97bffeca5b3a92dd28a`). Both APKs
  carry that certificate. It is the Android debug certificate
  (`CN=Android Debug`), not the official Magisk release key. Treat it as a lab
  build, use this exact APK for both patching and installation, and do not
  redistribute it without a rights and signing review.

## Stock profile and patch

- SDK package: `system-images;android-35;default;x86_64` revision 2.
- Guest fingerprint: `Android/sdk_phone64_x86_64/emu64x:15/AE3A.240806.019/12368160:userdebug/test-keys`.
- Stock ramdisk SHA-256 matches the pin:
  `60eb16b9c894514f3792c5bef9f58ea478bd1356f6e0fdb8045dca01ab903a30`.
- Frame layout: `lz4-legacy(2 frames)`, 17 cpio entries each; frame 0 owns
  `/init`, frame 1 owns `first_stage_ramdisk/fstab.ranchu` and kernel modules.

Patch command:

```bash
export ANDROID_ADB_SERVER_PORT=5038 ANDROID_SERIAL=emulator-5554
python3 build.py avd_patch \
  "$RUN/stock-ramdisk-copy.img" "$RUN/patched-ramdisk.img" \
  --apk /workspace/magisk-lab/Magisk/out/app-release.apk
```

The pinned helper completed with `PATCH_EXIT=0`. It produced a single gzip cpio
with `magiskinit` as `/init`, `overlay.d/sbin/{magisk,stub,init-ld}.xz`, and
`.backup/*`. Patched ramdisk SHA-256
`d5b47070ee8a63bcb80e7851887831e371d55762cec388195552ac09426faf8f`. The stock
input hash was unchanged after patching.

## Boot evidence (2026-11-13 run, full upstream test environment)

Linux engine: `37.2.13.0`, build `16740729`, KVM, software GPU
(`swiftshader_indirect`), 2 vCPU, 2048 MiB RAM, no snapshot load/save.

| Boot | Result |
| --- | --- |
| Stock | `boot_completed=1`; launcher focused; `adbd` UID 2000; no Magisk |
| Patched 1 | `boot_completed=1`; `magiskd` `e8a58776(30700)` started; log `Magisk environment incomplete, abort`. Before setup, `MagiskAppTest#testSuRequest` failed (`SuRequestActivity is not launched`). The full upstream test environment was then installed (see below) |
| Patched 2 (cold) | `su -c id` from **shell** UID 2000 -> `uid=0(root)`, `context=u:r:magisk:s0`; `MagiskAppTest#testSuRequest` passed; `adbd` UID 2000. Shamiko and the test modules were installed (see the inspection below) |
| Patched 3 (cold) | Repeat cold boot; `su -c id` from shell UID 2000 -> UID 0 |
| Stock restore (cold) | `boot_completed=1`; marker persisted; Magisk binaries gone; stock `su -c id` rejected; `adbd` UID 2000. The modules stayed in `/data/adb/modules` but were inert (no Magisk) |

`su` resolved to the Magisk-injected `/system_ext/bin/su -> ./magisk` on patched
boots. The stock `/system/xbin/su` supports only its legacy `su <uid> <cmd>`
syntax and rejects `-c`. ADB daemon root was not used in this run.

### What the first-boot setup actually installed

Setup command (upstream `scripts/test_common.sh` `run_setup`, unmodified):

```bash
adb install -r -g apk/Magisk-v30.7-e8a58776-release.apk
adb install -r -g apk/Magisk-v30.7-test.apk
adb shell am instrument -w --user 0 -e class '.Environment#setupEnvironment' \
  com.topjohnwu.magisk.test/com.topjohnwu.magisk.test.AppTestRunner
```

At `e8a58776`, `Environment#setupEnvironment` (`Environment.kt` SHA-256
`baef7616…c421`) runs `MagiskInstaller.Emulator` (`fixEnv()`: `extractFiles()`
+ `fix_env`). It then flashes Shamiko (API >= 27) and skips LSPosed (API 27..34
only; this guest is API 35). It also creates `mount_test`, `sepolicy_rule`
(the preinit device resolved), the systemless `hosts` module, `empty_zygisk`,
`invalid_zygisk`, `remove_test` and `upgrade_test`. The retained
`setup-environment.txt` shows only `OK (1 test)`.

A later inspection booted a **copy** of the run's AVD with the patched ramdisk;
the original userdata hash was unchanged afterwards (raw output in
`linux-20261114-124924-baseonly/inspect-original/`). It found:

| Item | Observed |
| --- | --- |
| `/data/adb/modules` | `empty_zygisk`, `hosts`, `invalid_zygisk`, `mount_test`, `sepolicy_rule`, `upgrade_test` (has `disable`), `zygisk_shamiko` (Shamiko v1.2.1 (383), description `[❌ Unsupported environment]`); `remove_test` already removed; no `modules_update` |
| Other `/data/adb` | `shamiko/`, `post-fs-data.d/.shamiko_cleanup.sh` (executed at boot) |
| Zygisk | no `zygisk` row in `settings` (only `bootloop=0`), so the daemon default applied: `zygisk = is_emulator` (`native/src/core/db.rs`). zygote64 started with `-XX:NativeBridge=libzygisk.so` and mapped `/system/lib64/libzygisk.so`; `zygiskd64` launched; Shamiko `post-fs-data.sh`/`service.sh` ran |
| Denylist | not enforced, empty table |
| Policies | `uid=2000 policy=2 (allow) until=0`, written by `testSuRequest` |
| Mounts | Magisk tmpfs over `/system/bin`, `/system/fonts`, `/system/app/EasterEgg`; `/system/etc/hosts` bind mount |
| Packages | `com.topjohnwu.magisk`, `com.topjohnwu.magisk.test` |
| `/data/adb/magisk` | 13 files, byte-identical to the base-only payload below |

Deviations from upstream: none in sources or commands. The run ran only the
`setupEnvironment` and `MagiskAppTest#testSuRequest` steps of upstream
`run_tests`; `AdditionalTest`, hiding/restore and `testZygisk` were not run.

### App-level evidence in the 2026-11-13 run

`testSuRequest` runs `su -c id` through `UiAutomation.executeShellCommand`, so
it proves **shell UID 2000** elevation only. `BaseTest.prerequisite` asserts
that the Magisk app's own libsu shell is root, inside the
`com.topjohnwu.magisk` process. It passed implicitly, but that run recorded no
caller UID or resulting context, so it is not explicit app-level evidence. The
base-only run below adds that evidence.

## Base-only run (2026-11-14)

Fresh AVD `ws_magisk_baseonly` with the same image, engine and `config.ini`,
new userdata, the same patched ramdisk `d5b47070…` and release APK
`d820319e…`. The test APK was not installed. Guests were powered off with
`adb shell sync; adb shell reboot -p` between boots. A first attempt used
`adb emu kill`, which lost the app install and marker contents; it was
discarded and redone (`attempt1-unclean-shutdown/`).

### Procedure

```bash
# Host: stage exactly what MagiskInstaller.extractFiles() provides
# (non-stub app, 64-bit-only x86_64 guest => no magisk32). 13 files; their
# hashes equal the /data/adb/magisk produced by the upstream installer.
python3 stage_env.py Magisk-v30.7-e8a58776-release.apk stage

# Patched cold boot 1 of a fresh guest; wait for sys.boot_completed=1
adb install -r -g Magisk-v30.7-e8a58776-release.apk      # manager only
adb push stage/. /data/local/tmp/ws-magisk-env/
adb root                                                 # provisioning only
adb shell 'set -e
MAGISKBIN=/data/adb/magisk
rm -rf $MAGISKBIN/*; mkdir -p $MAGISKBIN; chmod 700 /data/adb
cp -r /data/local/tmp/ws-magisk-env/. $MAGISKBIN/
chmod -R 755 $MAGISKBIN; chown -R 0:0 $MAGISKBIN
rm -rf /data/local/tmp/ws-magisk-env
/debug_ramdisk/magisk --sqlite "REPLACE INTO settings (key,value) VALUES(\"zygisk\",0)"
/debug_ramdisk/magisk --sqlite "REPLACE INTO policies (uid,policy,until,logging,notification) VALUES(2000,2,0,0,0)"'
adb unroot                                               # back to UID 2000
adb shell sync; adb shell reboot -p                      # graceful power-off
# Then cold boot with -ramdisk patched-ramdisk.img -no-snapshot
```

This is the `fix_env` body from `scripts/app_functions.sh`, run from adbd root
instead of the Magisk app's root shell. It needs no modified APK and no test
APK, and runs no module, hosts, denylist, hiding, Shamiko or LSPosed step. Two
explicit choices differ from upstream defaults:

- **Zygisk is set off** (`zygisk=0`). At this commit the emulator default is on.
- **The UID 2000 allow policy is written directly.** This is the same row
  `testSuRequest` produced, without its auto-response trick.

`adb root` is used only during provisioning and is reverted before any
evidence. All later boots show `adbd` UID 2000 and elevation through Magisk
`su`. Using adbd root as a provisioning tool sits awkwardly with the
`agents.md` rule "Keep ADB root and Magisk independent" and needs an owner
decision. The alternative is the Magisk app's own "Requires Additional Setup"
dialog (`EnvFixDialog` -> `MagiskInstaller.FixEnv`, the same `fixEnv()`),
followed by in-app Zygisk and Superuser changes. That path needs UI taps and
was **not** verified here.

A headless no-adbd-root attempt also failed. It requested root from the shell
(`/debug_ramdisk/su -c id`) during patched boot 1, but `uiautomator dump`
never captured the Magisk prompt. The prompt timed out after about 10 s, and a
**permanent deny** for UID 2000 was stored
(`uid=2000 policy=1 until=0`, `variant-noadbroot/`). Automation must never
trigger a `su` request that nothing will answer.

### Evidence

| Boot | Result |
| --- | --- |
| Patched 1 (fresh) | daemon `e8a58776(30700)`, `/data/adb is not present, abort`; setup applied; `/data/adb/magisk` hashes equal the staged set; DB `zygisk=0`, `uid=2000 policy=2` |
| Patched 2 (cold) | `boot_completed=1`; `magisk -V` 30700; shell UID 2000 `su -c id` -> `uid=0(root) … context=u:r:magisk:s0`; `/data/adb/modules` empty (`module_count=0`), no `modules_update`, empty `post-fs-data.d`/`service.d`; settings `zygisk=0`, zygote64 libzygisk mappings 0, `ro.dalvik.vm.native.bridge=0`; denylist not enforced and empty; no hosts bind mount; only `/system_ext/bin` Magisk tmpfs; `adbd` UID 2000 |
| Patched 3 (cold) | Same results again |
| Stock restore (cold) | `boot_completed=1`; no `magiskd`, no `/system_ext/bin/{magisk,su}`, 0 Magisk mounts; shell `su -c id` rejected; app probe `error=13, Permission denied`; marker `ws-magisk-baseonly-marker-v1` preserved; `/data/adb` inert with empty `modules` |

### App-level probe

`ws-suprobe.apk` (SHA-256
`a6b4837f101c2423d32f801ca6951901f3fb66e2584ad0d59296956bed1e23b1`, package
`dev.emulatorworkspace.suprobe`) is a single `Instrumentation` class that runs
in its own app process. It records its package, UID, PID and SELinux context,
then runs `su -c 'id; echo context=$(cat /proc/self/attr/current)'`. It does
not install, hide or change anything. It is built from the SDK only
(`javac`, `d8`, `aapt2`, `zipalign`, `apksigner`; build-tools 36.1.0,
android-36.1). It is signed with a dedicated lab key, certificate SHA-256
`65950e78ccaf1883ed05cf288fdcef0596ad7077dc35ac55aa339dcf0d3febd1`. Source and
`build.sh` are in the run's `suprobe/` directory.

```bash
adb install -r ws-suprobe.apk
UID_=$(adb shell pm list packages -U dev.emulatorworkspace.suprobe | sed -n 's/.*uid:\([0-9]*\).*/\1/p')
adb shell "su -c 'magisk --sqlite \"REPLACE INTO policies (uid,policy,until,logging,notification) VALUES($UID_,2,0,0,0)\"'"
adb shell am instrument -w dev.emulatorworkspace.suprobe/.SuProbe
```

| Case | Caller | Result |
| --- | --- | --- |
| Boot 2, no policy (prompt unanswered) | `dev.emulatorworkspace.suprobe`, UID 10149, `u:r:untrusted_app:s0:c149,c256,c512,c768` | `su_exit=13`, `Permission denied`; daemon `su: request rejected (10149)`; a deny row `uid=10149 policy=1 until=0` was then stored |
| Boot 2, explicit allow row | same | `uid=0(root) gid=0(root) groups=0(root) context=u:r:magisk:s0`, `su_exit=0` |
| Boot 3 (cold) | same | `uid=0(root) … context=u:r:magisk:s0`, `su_exit=0` |
| Stock restore | same | `Cannot run program "su": error=13, Permission denied` |

The Magisk app's own root shell, the one `BaseTest.prerequisite` asserts, was
also observed. The app process `com.topjohnwu.magisk` ran as UID 10148
`u:r:untrusted_app:s0:c148,…`, and its root service
`com.topjohnwu.magisk:root:0` ran as UID 0 `u:r:magisk:s0` under `magiskd`.

## Pending Windows acceptance

The Windows agent must repeat the **base-only** procedure on Emulator
`37.2.12.0` build `16428233` with WHPX. It must not use
`Environment#setupEnvironment`. It must then finish the scoped supervisor
Magisk install and recovery flow: exact approval, durable stock backup,
journaled staging, validated atomic activation, incompatible snapshot fencing,
interrupted staging, corrupt patch, boot-timeout rollback, two cold boots with
the app-level probe, and compatible guest-data recovery. Linux evidence does
not cover these checks.
