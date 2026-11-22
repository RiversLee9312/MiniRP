#!/bin/sh
# Build the upstream curl CLI against a static Mbed TLS installation.
set -eu
platform=${1:?android, linux or windows}
source=$(realpath "${2:?curl 8.23.0 source directory}")
tls=$(realpath "${3:?Mbed TLS 3.6.7 source directory}")
ca=$(realpath "${4:?Mozilla CA bundle from curl.se}")
work=$(realpath -m "${5:?build directory}")
out=$(realpath -m "${6:?output directory}")
ssh=$(realpath "${7:?libssh2 source directory}")
http2=$(realpath "${8:?nghttp2 source directory}")
zlib=$(realpath "${9:?zlib source directory}")
scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
case "$platform" in
    android)
        : "${ANDROID_NDK_ROOT:?point to a Linux-host Android NDK}"
        ldap=$(realpath "${10:?OpenLDAP source directory}")
        set -- "-DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake" \
            -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=26 -DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON
        binary=jniLibs/arm64-v8a/libminirp_curl.so
        built=curl
        ;;
    windows)
        set -- -DCMAKE_SYSTEM_NAME=Windows "-DCMAKE_C_COMPILER=${CROSS_PREFIX:-x86_64-w64-mingw32-}gcc"
        binary=minirp-curl.exe
        built=curl.exe
        ;;
    linux)
        set --
        binary=minirp-curl
        built=curl
        ;;
    *) echo "Unknown curl host platform: $platform" >&2; exit 2 ;;
esac
mkdir -p "$work" "$out/$(dirname "$binary")"
python3 "$scripts/prepare.py" "$source"
cmake -S "$tls" -B "$work/tls" -G Ninja "$@" -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_INSTALL_PREFIX="$work/prefix" \
    -DENABLE_PROGRAMS=OFF -DENABLE_TESTING=OFF -DUSE_SHARED_MBEDTLS_LIBRARY=OFF
cmake --build "$work/tls" --parallel 8
cmake --install "$work/tls"
cmake -S "$ssh" -B "$work/ssh" -G Ninja "$@" -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_INSTALL_PREFIX="$work/prefix" \
    -DCMAKE_PREFIX_PATH="$work/prefix" -DCRYPTO_BACKEND=mbedTLS -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_EXAMPLES=OFF -DBUILD_TESTING=OFF
cmake --build "$work/ssh" --parallel 8
cmake --install "$work/ssh"
cmake -S "$http2" -B "$work/http2" -G Ninja "$@" -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_INSTALL_PREFIX="$work/prefix" \
    -DENABLE_LIB_ONLY=ON -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DBUILD_TESTING=OFF
cmake --build "$work/http2" --parallel 8
cmake --install "$work/http2"
cmake -S "$zlib" -B "$work/zlib" -G Ninja "$@" -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_INSTALL_PREFIX="$work/prefix" \
    -DZLIB_BUILD_SHARED=OFF -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_STATIC=ON
cmake --build "$work/zlib" --parallel 8
cmake --install "$work/zlib"
if [ "$platform" = android ]; then
    cc="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/linux-x86_64/bin"
    mkdir -p "$work/ldap"
    if [ ! -f "$work/prefix/lib/libldap.a" ] || [ ! -f "$work/prefix/include/ldap.h" ]; then (
        cd "$work/ldap"
        CC="$cc/aarch64-linux-android26-clang" AR="$cc/llvm-ar" RANLIB="$cc/llvm-ranlib" \
            "$ldap/configure" --host=aarch64-linux-android --prefix="$work/prefix" --disable-shared --enable-static \
            --disable-slapd --without-tls --without-cyrus-sasl --without-systemd --without-threads
        make depend -j 8
        make -j 8 -C libraries
        make -C libraries install
        make -C include install
    ); fi
    cp "$ldap/LICENSE" "$out/openldap-license.txt"
    set -- "$@" "-DLDAP_INCLUDE_DIR=$work/prefix/include" "-DLDAP_LIBRARY=$work/prefix/lib/libldap.a" \
        "-DLDAP_LBER_LIBRARY=$work/prefix/lib/liblber.a;$work/ldap/libraries/liblutil/liblutil.a"
    zlib_archive=libz.a
elif [ "$platform" = windows ]; then
    zlib_archive=libzs.a
else
    zlib_archive=libz.a
fi
export PKG_CONFIG_PATH="$work/prefix/lib/pkgconfig"
cmake -S "$source" -B "$work/curl" -G Ninja "$@" -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_PREFIX_PATH="$work/prefix" \
    -DMbedTLS_DIR="$work/prefix/lib/cmake/MbedTLS" -DMBEDTLS_INCLUDE_DIR="$work/prefix/include" \
    -DMBEDTLS_LIBRARY="$work/prefix/lib/libmbedtls.a" -DMBEDX509_LIBRARY="$work/prefix/lib/libmbedx509.a" \
    -DMBEDCRYPTO_LIBRARY="$work/prefix/lib/libmbedcrypto.a" -DNGHTTP2_INCLUDE_DIR="$work/prefix/include" \
    -DNGHTTP2_LIBRARY="$work/prefix/lib/libnghttp2.a" -DLIBSSH2_INCLUDE_DIR="$work/prefix/include" \
    -DLIBSSH2_LIBRARY="$work/prefix/lib/libssh2.a" -DZLIB_INCLUDE_DIR="$work/prefix/include" \
    -DZLIB_LIBRARY_RELEASE="$work/prefix/lib/$zlib_archive" -DZLIB_LIBRARY_DEBUG="$work/prefix/lib/$zlib_archive" \
    -DZLIB_USE_STATIC_LIBS=ON \
    -DCURL_USE_MBEDTLS=ON -DCURL_USE_OPENSSL=OFF -DCURL_USE_SCHANNEL=OFF -DCURL_USE_LIBPSL=OFF \
    -DCURL_USE_LIBSSH2=ON -DCURL_USE_LIBSSH=OFF -DUSE_NGHTTP2=ON -DCURL_DISABLE_LDAP=OFF -DCURL_DISABLE_LDAPS=OFF \
    -DENABLE_UNIX_SOCKETS=OFF \
    -DCURL_CA_EMBED="$ca" -DCURL_CA_BUNDLE=none -DCURL_CA_PATH=none \
    -DBUILD_TESTING=OFF -DBUILD_EXAMPLES=OFF -DBUILD_LIBCURL_DOCS=OFF -DBUILD_MISC_DOCS=OFF \
    -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DBUILD_STATIC_CURL=ON \
    '-DCMAKE_C_FLAGS=-ffunction-sections -fdata-sections -DNGHTTP2_STATICLIB' '-DCMAKE_EXE_LINKER_FLAGS=-Wl,--gc-sections -s'
cmake --build "$work/curl" --parallel 8 --target curl
install -m 755 "$work/curl/src/$built" "$out/$binary"
cp "$source/COPYING" "$out/curl-license.txt"
cp "$tls/LICENSE" "$out/mbedtls-license.txt"
cp "$ssh/COPYING" "$out/libssh2-license.txt"
cp "$http2/COPYING" "$out/nghttp2-license.txt"
cp "$zlib/LICENSE" "$out/zlib-license.txt"
