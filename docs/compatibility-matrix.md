# Compatibility matrix

This matrix records only what was actually exercised on the reference host. A
cell that was not tested says so; nothing is marked compatible on the strength
of documentation alone.

## Reference host

| Property | Value |
| --- | --- |
| Machine | DESKTOP-EMULATOR (Windows 11 Pro 10.0.26300, build 26300) |
| CPU | Intel Core i5-8265U @ 1.60 GHz, 4 cores / 8 logical |
| GPU | Intel UHD Graphics 620, driver 31.0.101.2135 |
| RAM | 31.8 GB, page file 14.1 GB (system managed) |
| Acceleration | WHPX (Windows Hypervisor Platform), hardware virtualization enabled in firmware |
| Android SDK | `%LOCALAPPDATA%\Android\Sdk` |
| Emulator | 37.2.12.0, build 16428233 (`Pkg.Revision 37.2.12.0`) |
| ADB | 1.0.41 (platform-tools 37.0.1-15733141) |
| .NET SDK | 10.0.302 (pinned in `global.json`) |

## Guest images

| System image | ABI | Verified | Notes |
| --- | --- | --- | --- |
| `system-images;android-35;default;x86_64` (AOSP API 35, revision 2) | x86_64 | **Yes** | The regression baseline. Every Phase 1, 2 and 3 acceptance run uses it. |
| `system-images;android-35;google_apis;x86_64` | x86_64 | **Yes** | The `adb root` workflow needs a userdebug image; the Google APIs image is userdebug and was verified for the ADB daemon transitions. |
| `system-images;android-35;google_apis_playstore_ps16k;x86_64` revision 5 | x86_64 | **Partial** | Boot, APK, foreground and PNG verified. Snapshot state rolled back with cold-boot recovery. |
| `system-images;android-36;google_apis_playstore;x86_64` revision 7 | x86_64 | **Yes, tested workflows** | Boot, APK, foreground, PNG and named snapshot rollback verified. |
| `system-images;android-36.1;google_apis_playstore;x86_64` | x86_64 | **No** | Installed but not exercised. |
| `system-images;android-37.1;google_apis_playstore_ps16k;x86_64` | x86_64 | **No** | Installed but not exercised. |

The stock Play runs used Windows 11 Pro build 26300 on the i5-8265U with
UHD 620 driver 31.0.101.2135, Emulator 37.2.12.0 build 16428233, WHPX, and
headless host GPU. Both images booted with `ro.build.type=user` and effective
ADB daemon UID 2000. Their stock ramdisk hashes matched before and after.

| Stock image | Run | Guest build | Named snapshot result |
| --- | --- | --- | --- |
| API 36 revision 7 | `20261112-211536`, 8/8 | `BE2A.250530.026.D1/13818094` | `recovery=restored`, fixture absent after load |
| API 35 16 KB revision 5 | `20261112-211808`, 7/8 | `AE3A.240806.041/12890756` | `recovery=cold_boot`, fixture absent after fallback |

Raw status, probe, snapshot and cleanup records are in `artifacts/phase3/play/`.
The original simulation output exposed a false-positive console status for
`route` and `power capacity`. The corrected probe distinguishes console
rejections, reads the battery state using `power display`, and reads the
advertised GPS feature without injecting a position. Camera, microphone and
telephony entries are feature advertisements. The Play tests cover snapshot
rollback and APK QA. Recipe replay, split installs, live viewport streaming,
and simulation mutations require separate checks on those profiles.

## Capabilities

| Capability | AOSP API 35 x86_64 | Google APIs API 35 x86_64 | Google Play image |
| --- | --- | --- | --- |
| Cold boot to interactive readiness | Verified (see `docs/phase3-evidence.md`) | Verified | Verified on both tested images |
| Warm snapshot restore | Verified | Not verified | API 36 restored. API 35 16 KB used cold-boot recovery |
| Frame capture and streaming | Verified | Not verified | One-shot PNG verified on both tested images |
| Input (tap, text, key, swipe) | Verified | Not verified | Not verified |
| ADB daemon as root (`adb root`) | Verified (`ro.build.type=userdebug`) | Verified | Both tested images report user build and root unavailable |
| Magisk | Not available (needs a Linux builder) | Not available | Out of scope by policy |
| App install (`.apk`) | Verified | Not verified | Verified on both tested images |
| Split APK set (`.apks`, bundletool 1.18.1) | Verified | Not verified | Not verified |
| Recipe record and replay | Verified | Not verified | Not verified |
| GPS, rotation, battery, network shaping | Verified | Not verified | Not verified |

## Snapshots

| Snapshot kind | Verified | Notes |
| --- | --- | --- |
| Cold snapshot (engine `avd snapshot save`) | Yes | Named snapshots round-trip on a lab guest; the fingerprint is bound to the image. |
| Warm snapshot (in-memory quickboot) | Yes | The recipe lane restores with `recovery=restored`. |
| Snapshot of a running guest after a restore | Yes | The restore reverts userdata, so a recorded install is re-executed by the runner. |
| Cross-image snapshot | No | Refused with the typed `recipe_incompatible_baseline` error. |
| Snapshot of the last verified recovery point | Protected | Deleting it needs `force` plus an approval. |

## Root Lab

| Profile | Verified | Notes |
| --- | --- | --- |
| AOSP API 35 x86_64 userdebug | Yes | `adb root` / `adb unroot` with the UID transition 2000 → 0 → 2000. |
| Magisk lab profile | **Blocked** | Needs a Linux builder for the pinned AOSP/Magisk sources, or a stock Magisk ramdisk proven compatible with the pinned image. The ramdisk substitution itself is already proven (Phase 0). |

## Host architectures and GPUs

| Host | Verified | Notes |
| --- | --- | --- |
| Windows 11 x64, Intel UHD 620, WHPX | Yes | The reference host above. |
| Windows 11 x64, discrete NVIDIA/AMD GPU | No | No such host was available in this environment. |
| Windows 11 ARM64 | No | No such host was available. |
| Windows 10 x64 | No | No such host was available. |

## Two-guest behaviour

| Host size | Verified | Notes |
| --- | --- | --- |
| 31.8 GB RAM (reference) | Yes | Two guests are booted and driven concurrently in the recipe lane; the bystander keeps its own marker and network profile. |
| 16 GB host | No | The goal asks for a documented 16 GB host; none was available. The reference host is documented instead. |

## Known host limits found during verification

- Concurrent engine starts are unreliable on this host: the hypervisor is handed
  to one guest only, so the acceptance harnesses start the guests sequentially by
  default and record the concurrent attempt as a separate, explicit option.
- Seven orphaned guests once held about 16 GB and exhausted the pagefile, which
  produced misleading boot and ADB failures. The harnesses now sweep the guests
  they own, matched by their own AVD name.
