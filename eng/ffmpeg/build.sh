#!/bin/sh
set -eu
platform=${1:?android, linux or windows}
source=$(realpath "${2:?verified upstream FFmpeg 8.1.3 source directory}")
work=$(realpath -m "${3:?build directory}")
out=$(realpath -m "${4:?runtime output directory}")
x264=$(realpath "${5:?x264 source directory}")
scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
jobs=${MINIRP_BUILD_JOBS:-8}
test "$(cat "$source/RELEASE")" = 8.1.3 || { echo "Use FFmpeg 8.1.3 source." >&2; exit 2; }

case "$platform" in
    android)
        : "${ANDROID_NDK_ROOT:?point to a Linux-host Android NDK}"
        tools="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin"
        export PATH="$tools:$PATH"
        x264_host=aarch64-linux-android
        cc=aarch64-linux-android26-clang
        ar=llvm-ar
        ranlib=llvm-ranlib
        strip=llvm-strip
        set -- --enable-cross-compile --arch=aarch64 --target-os=android \
            --cc="$cc" --ar="$ar" --nm=llvm-nm --ranlib="$ranlib" --strip="$strip" \
            --extra-ldflags=-Wl,-z,max-page-size=16384 --enable-zlib
        ;;
    windows)
        cross=${CROSS_PREFIX:-x86_64-w64-mingw32-}
        x264_host=x86_64-w64-mingw32
        cc="${cross}gcc"
        ar="${cross}ar"
        ranlib="${cross}ranlib"
        strip="${cross}strip"
        mingw_notice=${MINIRP_MINGW_LICENSE:-/usr/share/doc/mingw-w64-x86-64-dev/copyright}
        test -f "$mingw_notice" || { echo "Set MINIRP_MINGW_LICENSE to the toolchain's runtime license notices." >&2; exit 2; }
        set -- --enable-cross-compile --arch=x86_64 --target-os=mingw32 --cross-prefix="$cross" \
            --extra-ldflags=-static
        ;;
    linux)
        x264_host="$(uname -m)-linux-gnu"
        cc=${CC:-cc}
        ar=${AR:-ar}
        ranlib=${RANLIB:-ranlib}
        strip=${STRIP:-strip}
        set -- --enable-zlib --cc="$cc" --ar="$ar" --ranlib="$ranlib" --strip="$strip"
        ;;
    *) echo "Unknown FFmpeg host platform: $platform" >&2; exit 2 ;;
esac
x264_asm=
if [ "${x264_host%%-*}" = x86_64 ] && ! command -v nasm >/dev/null 2>&1; then
    x264_asm=--disable-asm
    set -- "$@" --disable-x86asm
fi

mkdir -p "$work/build" "$work/x264-build" "$out"
if [ ! -d "$work/source" ]; then cp -R "$source" "$work/source"; fi
if [ ! -d "$work/x264-source" ]; then cp -R "$x264" "$work/x264-source"; fi
cp -R "$source/fftools/." "$work/source/fftools/"
cp "$x264/common/osdep.h" "$work/x264-source/common/osdep.h"
python3 "$scripts/prepare.py" "$work/source" "$work/x264-source"
(
    cd "$work/x264-build"
    CC="$cc" AR="$ar" RANLIB="$ranlib" STRIP="$strip" "$work/x264-source/configure" \
        --host="$x264_host" --prefix="$work/x264" --enable-static --enable-pic --disable-cli --disable-opencl $x264_asm
    make -j "$jobs"
    make install
)
cd "$work/build"

# The upstream libraries have no path-based protocols. Disable components that
# bypass AVIO and access host paths or devices directly.
filters=arnndn,firequalizer,loudnorm,asendcmd,sendcmd,curves,deshake,fieldhint,lut3d,haldclut,haldclutsrc,nnedi,paletteuse,psnr,selectivecolor,signature,ssim,ssim360,vmafmotion,xpsnr,cellauto,life
PKG_CONFIG_LIBDIR="$work/x264/lib/pkgconfig" PKG_CONFIG_PATH="" "$work/source/configure" "$@" \
    --pkg-config=pkg-config --pkg-config-flags=--static --enable-gpl --enable-libx264 --enable-shared --disable-static --enable-pic \
    --disable-autodetect --disable-doc --disable-debug --disable-ffplay --disable-network \
    --disable-devices --disable-hwaccels --disable-protocols --enable-protocol=pipe \
    --disable-demuxer=image2 --disable-muxer=hds,hls,smoothstreaming \
    --disable-decoder=dvdsub --disable-filter="$filters"
grep -qx 'CONFIG_LIBX264_ENCODER=yes' ffbuild/config.mak
if [ "$platform" = android ]; then
    make -j "$jobs" SLIBNAME_WITH_MAJOR='$(SLIBNAME)' SLIB_INSTALL_NAME='$(SLIBNAME)' SLIB_INSTALL_LINKS= all
    destination="$out/jniLibs/arm64-v8a"
    mkdir -p "$destination"
    install -m 755 ffmpeg "$destination/libminirp_ffmpeg.so"
    install -m 755 ffprobe "$destination/libminirp_ffprobe.so"
    for library in libav*/*.so libsw*/*.so; do install -m 755 "$library" "$destination"; done
else
    make -j "$jobs"
    if [ "$platform" = windows ]; then
        install -m 755 ffmpeg.exe ffprobe.exe libav*/*-*.dll libsw*/*-*.dll "$out"
        cp "$mingw_notice" "$out/mingw-runtime-license.txt"
    else
        install -m 755 ffmpeg ffprobe "$out"
        for library in libav*/*.so.* libsw*/*.so.*; do
            case "${library##*.so.}" in
                *.*) ;;
                *) install -m 755 "$library" "$out" ;;
            esac
        done
    fi
fi
cp "$source/COPYING.GPLv2" "$out/ffmpeg-license.txt"
tar -cJf "$out/.ffmpeg-source.tar.xz.tmp" --exclude-vcs \
    -C "$(dirname "$source")" "$(basename "$source")" -C "$(dirname "$x264")" "$(basename "$x264")" \
    -C "$scripts" build.sh prepare.py asset_io.c asset_io.h asset_paths.h x264_asset_io.h
mv -f "$out/.ffmpeg-source.tar.xz.tmp" "$out/ffmpeg-source.tar.xz"
touch "$out/LIBX264"
