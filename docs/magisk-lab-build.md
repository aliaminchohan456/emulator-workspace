# Magisk Lab build inputs

The experimental source profile pins AOSP `android-15.0.0_r1` (manifest
`2f91a1ccd8a14ab34358ef141bfb1fc68d200bb4`), target
`sdk_phone64_x86_64-trunk_staging-userdebug`, and Magisk v30.7
(`e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac`). The Windows test engine remains
37.2.12.0 build 16428233. The new source profile needs its own image provenance
and stock boot evidence. The SDK regression profile remains API 35 AOSP x86_64
revision 2. [Pinned AOSP product definitions](https://android.googlesource.com/device/generic/goldfish/+/refs/tags/android-15.0.0_r1/AndroidProducts.mk),
[Magisk v30.7](https://github.com/topjohnwu/Magisk/releases/tag/v30.7)

## Build the stock image

Use an x86_64 Linux builder with glibc 2.17 or later, 64 GB RAM, and 400 GB
free on the checkout volume. Install the official Android build prerequisites
and Repo, then run:

```bash
bash tools/build-magisk-lab-aosp.sh --check /mnt/aosp
bash tools/build-magisk-lab-aosp.sh --build /mnt/aosp
```

The build writes a resolved Repo manifest and stock SHA-256 inventory beside
its output. Preserve the image together with its kernel, vendor image, notices,
and build provenance. The Windows reference host has 31.8 GiB RAM and WSL is
absent. The preflight exits 2 under Windows Git Bash. [AOSP build host requirements](https://source.android.com/docs/setup/start/requirements)

Linux preflight on 2026-11-13 also stopped at capacity: the builder has 8 CPU
threads, 16,779,689,984 bytes of RAM and 114,026,356,736 bytes free on
`/workspace/aosp`, below the required 64 GB and 400 GB. Both `--check` and
`--build` exited 2, so no source-built image or resolved Repo manifest exists.
The build-volume path was `/workspace/aosp` because `/mnt/aosp` is absent.

## Prepare the Magisk patch

On the build host, configure the SDK and JDK from the pinned Magisk guide.
Verify the checkout commit before building:

```bash
git clone --branch v30.7 --recurse-submodules https://github.com/topjohnwu/Magisk.git
cd Magisk
test "$(git rev-parse HEAD)" = e8a58776f1d7bdf852072ad0baa6eceb9a1e4aac
python3 build.py ndk
python3 build.py -r all
```

For v30.7, the patch command is `python3 build.py avd_patch INPUT OUTPUT --apk APK`.
The helper uses ADB to stage files and run the patch. Before invoking it, use an
isolated ADB server with one disposable lab guest and verify that `ANDROID_SERIAL`
matches that guest. Pass a copy of the verified stock ramdisk as `INPUT`.
Record the APK and ramdisk hashes and inspect both input and output with
`tools/probe_ramdisk.py`. [Pinned Magisk build guide](https://github.com/topjohnwu/Magisk/blob/v30.7/docs/build.md),
[Pinned patch implementation](https://github.com/topjohnwu/Magisk/blob/v30.7/build.py)

The SDK baseline stock ramdisk SHA-256 is
`60eb16b9c894514f3792c5bef9f58ea478bd1356f6e0fdb8045dca01ab903a30`. Its
two legacy LZ4 frames carry the generic init and first-stage files respectively.
The Phase 0 substitution used the engine's `-ramdisk` option and restored
stock boot. Verify the actual source profile's frame layout and boot behavior
before accepting a patch. Persistent app-level `su`, stock rollback,
interrupted staging, bad patch, and boot timeout recovery require WHPX evidence
on the dedicated lab profile. [Phase 0 boot-input evidence](phase0-evidence.md)

## Linux verification

On 2026-11-13 the pinned Magisk v30.7 APK built on Linux and the pinned
`avd_patch` helper patched a copy of the SDK regression ramdisk. The patched
ramdisk booted twice with snapshot load/save disabled; both cold boots returned
`boot_completed=1`, `magisk -V` reported `30700`, and `su -c id` from an
unprivileged UID 2000 caller returned UID 0 with `context=u:r:magisk:s0`. The
pinned Magisk test app instrumentation (`MagiskAppTest#testSuRequest`) also
passed. A subsequent stock ramdisk cold boot kept the guest-data marker,
removed Magisk, and made `su -c id` fail. The ADB daemon stayed at UID 2000, so
the ADB-root and Magisk paths remain independent.

The Linux runner used Emulator 37.2.13.0 build 16740729 with KVM and the SDK
image, not the source-built AOSP image. The Windows agent must repeat the boot
and `su` checks on Emulator 37.2.12.0 build 16428233 with WHPX and then verify
the scoped supervisor install and recovery lifecycle. Raw Linux evidence and
hashes are in `artifacts/magisk-lab/linux-20261113-110018/` (gitignored).
[Linux run details](magisk-lab-linux-evidence.md)
