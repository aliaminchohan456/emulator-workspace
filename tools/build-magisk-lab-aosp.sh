#!/usr/bin/env bash
set -eo pipefail

# Build the experimental stock source profile. The Windows SDK baseline uses
# a separate vendor-provided image and must keep its existing provenance.
tag=android-15.0.0_r1
manifest_commit=2f91a1ccd8a14ab34358ef141bfb1fc68d200bb4
target=sdk_phone64_x86_64-trunk_staging-userdebug
mode=${1:-}
if [[ ( "$mode" != --check && "$mode" != --build ) || $# != 2 ]]; then
    echo "Usage: bash $0 --check|--build /absolute/path/to/builder-workspace" >&2
    exit 2
fi

workspace=$2
if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo "The AOSP builder requires Linux x86_64." >&2
    exit 2
fi
if [[ "$workspace" != /* || ! -d "$workspace" ]]; then
    echo "Pass an existing absolute workspace directory on the Linux build volume." >&2
    exit 2
fi

ram_bytes=$(awk '/^MemTotal:/ { printf "%.0f", $2 * 1024 }' /proc/meminfo)
free_bytes=$(df -PB1 "$workspace" | awk 'NR==2 { print $4 }')
printf 'CPU threads: %s\nRAM bytes: %s\nFree workspace bytes: %s\n' "$(nproc)" "$ram_bytes" "$free_bytes"
if (( ram_bytes < 64000000000 || free_bytes < 400000000000 )); then
    echo "The source build requires 64 GB RAM and 400 GB free on its workspace volume." >&2
    exit 2
fi
for command in git repo python3; do
    command -v "$command" >/dev/null || { echo "Missing $command." >&2; exit 2; }
done
if [[ "$mode" == --check ]]; then
    exit 0
fi

# A fresh directory keeps existing checkouts and outputs outside this build.
build_dir=$(mktemp -d "$workspace/ws-magisk-lab.XXXXXXXX")
cd "$build_dir"
printf 'Build directory: %s\n' "$build_dir"
repo init -u https://android.googlesource.com/platform/manifest -b "refs/tags/$tag"
actual=$(git -C .repo/manifests rev-parse HEAD)
if [[ "$actual" != "$manifest_commit" ]]; then
    echo "Expected manifest $manifest_commit, got $actual." >&2
    exit 2
fi
repo sync -c -j8
repo manifest -r -o "$build_dir/manifest-pinned.xml"
source build/envsetup.sh
lunch "$target"
m -j8

product_out=$(get_build_var PRODUCT_OUT)
printf 'AOSP tag: %s\nManifest commit: %s\nTarget: %s\n' "$tag" "$actual" "$target" > "$build_dir/provenance.txt"
(
    cd "$product_out"
    sha256sum ramdisk.img kernel-ranchu system.img vendor.img
) > "$build_dir/stock-SHA256SUMS.txt"
printf 'Stock product: %s/%s\n' "$build_dir" "$product_out"
