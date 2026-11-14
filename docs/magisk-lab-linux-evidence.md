# Magisk Lab Linux evidence

Collected 2026-11-13 on the Linux builder. Raw artifacts are under
`artifacts/magisk-lab/linux-20261113-110018/` (gitignored). The Linux guest is
the pinned **SDK regression image**, not a source-built AOSP image.

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
- Signing: the APK is signed with the Android debug certificate
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

## Boot evidence

Linux engine: `37.2.13.0`, build `16740729`, KVM, software GPU
(`swiftshader_indirect`), 2 vCPU, 2048 MiB RAM, no snapshot load/save.

| Boot | Result |
| --- | --- |
| Stock | `boot_completed=1`; launcher focused; `adbd` UID 2000; no Magisk |
| Patched 1 | `boot_completed=1`; `magiskd` `e8a58776(30700)` started; environment incomplete until app setup |
| Patched 2 (cold) | `su -c id` from caller UID 2000 -> `uid=0(root)`, `context=u:r:magisk:s0`; `MagiskAppTest#testSuRequest` passed; `adbd` UID 2000 |
| Patched 3 (cold) | repeat cold boot; `su -c id` from UID 2000 -> UID 0 |
| Stock restore (cold) | `boot_completed=1`; marker persisted; Magisk binaries gone; stock `su -c id` rejected; `adbd` UID 2000 |

`su` resolved to the Magisk-injected `/system_ext/bin/su -> ./magisk` on patched
boots; the stock `/system/xbin/su` only supports its legacy `su <uid> <cmd>`
syntax and rejects `-c`. ADB daemon root was not used, so the two root paths
remain independent.

## Pending Windows acceptance

The Windows agent must repeat the same inputs on Emulator `37.2.12.0` build
`16428233` with WHPX and then finish the scoped supervisor Magisk install and
recovery flow: exact approval, durable stock backup, journaled staging,
validated atomic activation, incompatible snapshot fencing, interrupted
staging, corrupt patch, boot-timeout rollback, two cold boots with app-level
`su`, and compatible guest-data recovery. Those checks are not covered by this
Linux evidence.
