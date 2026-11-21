#!/bin/sh
# Run as root on an x86-64 Linux builder with curl, cpio, kmod and squashfs-tools.
set -eu
destination=$(realpath -m "${1:?output directory}")
scripts=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
cd "$temporary"
base=https://dl-cdn.alpinelinux.org/alpine
release=3.24.4
series=v3.24
archive=alpine-minirootfs-$release-x86_64.tar.gz
curl -fsSLO "$base/$series/releases/x86_64/$archive"
curl -fsSLO "$base/$series/releases/x86_64/$archive.sha256"
sha256sum -c "$archive.sha256"
mkdir root initrd
tar -xzf "$archive" -C root
cp -L /etc/resolv.conf root/etc/resolv.conf
printf '%s\n' "$base/$series/main" "$base/$series/community" >root/etc/apk/repositories
chroot root /sbin/apk add --no-cache \
    apk-tools linux-virt busybox-static bash curl ffmpeg nodejs python3 e2fsprogs \
    jq ripgrep sqlite py3-pip npm git poppler-utils unzip zstd diffutils
chroot root /sbin/apk --version
sh "$scripts/build-musl.sh" "$temporary/root"
chroot root /usr/sbin/adduser -D -H -u 1000 -h /workspace -s /bin/bash agent
rm root/etc/resolv.conf
touch root/etc/resolv.conf
kernel=$(basename root/lib/modules/*)
mkdir -p initrd/bin initrd/dev initrd/proc initrd/sys initrd/root initrd/modules
cp root/bin/busybox.static initrd/bin/busybox
cp "$scripts/init" initrd/init
chmod 755 initrd/init
for driver in virtio_pci virtio_mmio virtio_blk virtio_net squashfs ext4; do
    modprobe -d "$temporary/root" -S "$kernel" --show-depends "$driver"
done > module-deps
awk '/^insmod / && !seen[$2]++ {print $2}' module-deps > module-paths
while read -r module; do
    name=$(basename "$module" .gz)
    gzip -dc "$module" >"initrd/modules/$name"
    printf '%s\n' "$name"
done < module-paths > initrd/modules/load-order
mkdir -p "$destination"
cp root/boot/vmlinuz-virt "$destination/kernel"
cp "$scripts/guest.py" root/minirp-guest.py
mkdir -p root/workspace root/run root/tmp root/proc root/sys root/dev
rm -rf root/lib/modules root/boot root/usr/lib/firmware
rm -f root/bin/busybox.static
(cd initrd && find . -print0 | cpio --null -o -H newc --quiet | gzip -9) >"$destination/initrd.gz"
mksquashfs root "$destination/root.squashfs" -noappend -comp xz -b 256K -Xbcj x86 -processors 2 -quiet
touch "$destination/MICROVM"
touch "$destination/ROOM_VM"
(
    cd "$destination"
    sha256sum kernel initrd.gz root.squashfs > SHA256SUMS
)
