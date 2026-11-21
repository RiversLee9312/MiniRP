#!/bin/sh
# Rebuild the x86-64 loader from the exact Alpine revision installed in the image.
set -eu
root=$(realpath "${1:?Alpine rootfs directory}")
loader="$root/lib/ld-musl-x86_64.so.1"
test -f "$loader" && test ! -L "$loader"
source_info=$(awk 'BEGIN { RS=""; FS="\n" }
    /(^|\n)P:musl(\n|$)/ {
        for (i=1; i<=NF; i++) {
            if ($i ~ /^V:/) version=substr($i, 3)
            if ($i ~ /^c:/) commit=substr($i, 3)
        }
        print version, commit
    }' "$root/lib/apk/db/installed")
set -- $source_info
package=${1:?musl package version}
revision=${2:?musl aports commit}
version=${package%-r*}
series=$(cut -d. -f1,2 "$root/etc/alpine-release")
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cd "$work"
aports="https://raw.githubusercontent.com/alpinelinux/aports/$revision/main/musl"
curl -fsSL --retry 2 "$aports/APKBUILD" -o APKBUILD
grep -qx "pkgver=$version" APKBUILD
grep -qx "pkgrel=${package##*-r}" APKBUILD
archive="musl-$version.tar.gz"
curl -fsSLO --retry 2 "https://distfiles.alpinelinux.org/distfiles/v$series/$archive"
awk -v archive="$archive" '$2 == archive || $2 ~ /\.patch$/ { print $1 "  " $2 }' APKBUILD > SHA512SUMS
grep -q "  $archive\$" SHA512SUMS
patches=$(sed -n '/^source="/,/^[[:space:]]*"/p' APKBUILD | awk '$1 ~ /\.patch$/ { print $1 }')
for name in $patches; do
    curl -fsSLO --retry 2 "$aports/$name"
done
sha512sum -c SHA512SUMS
tar -xf "$archive"
cd "musl-$version"
for name in $patches; do patch -p1 < "$work/$name"; done
printf '%s\n' "$version" > VERSION
rm src/string/x86_64/memcpy.s src/string/x86_64/memmove.s
sed -i -E 's/^(#define[[:blank:]]+DEFAULT_STACK_SIZE)[[:blank:]]+131072$/\1 (8 * 1024 * 1024)/' src/internal/pthread_impl.h
grep -qE '^#define[[:blank:]]+DEFAULT_STACK_SIZE[[:blank:]]+\(8 \* 1024 \* 1024\)$' src/internal/pthread_impl.h
LDFLAGS="${LDFLAGS:-} -Wl,-soname,libc.musl-x86_64.so.1" ./configure --prefix=/usr --sysconfdir=/etc
make -j "${JOBS:-8}" lib/libc.so
strip --strip-unneeded lib/libc.so
install -m 755 lib/libc.so "$loader"
mkdir -p "$root/usr/share/minirp"
printf 'musl %s, aports %s, DEFAULT_STACK_SIZE=8388608\n' "$package" "$revision" > "$root/usr/share/minirp/musl-build.txt"
