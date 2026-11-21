#!/bin/sh
# Build QEMU from an extracted release. Android uses the corresponding Termux
# patches and Android NDK compiler plus the Termux dependency prefix.
set -eu
platform=${1:?android or windows}
source=$(realpath "${2:?QEMU source directory}")
build=$(realpath -m "${3:?build directory}")
scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$platform" in
    android)
        executable=qemu-system-x86_64
        size_cflags='-ffunction-sections -fdata-sections'
        : "${CC:?point CC at the aarch64 Android compiler}"
        set -- --cross-prefix= --cpu=aarch64 --cc="$CC" --disable-kvm --enable-fdt=system
        if [ -n "${CXX:-}" ]; then set -- "$@" --cxx="$CXX"; fi
        ;;
    windows)
        executable=qemu-system-x86_64.exe
        size_cflags=''
        set -- --cross-prefix="${CROSS_PREFIX:-x86_64-w64-mingw32-}" --enable-whpx --enable-fdt=internal
        ;;
    *) echo "Unknown QEMU host platform: $platform" >&2; exit 2 ;;
esac
mkdir -p "$build"
cp "$scripts/qemu-devices.mak" "$source/configs/devices/x86_64-softmmu/minirp.mak"
cd "$build"
"$source/configure" "$@" \
    --target-list=x86_64-softmmu --without-default-features --enable-tcg --enable-slirp \
    --without-default-devices --with-devices-x86_64=minirp \
    --disable-docs --disable-tools --disable-guest-agent --disable-debug-info \
    --extra-cflags="${CFLAGS:-} -Os $size_cflags" \
    --extra-ldflags="${LDFLAGS:-} -Wl,--gc-sections"
ninja -j "${JOBS:-8}" "$executable"
