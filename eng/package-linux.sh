#!/bin/sh
set -eu
workspace=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$workspace"
format=${1:-deb}
output=$(realpath -m "${2:-artifacts/installers}")
case "$format" in
    deb) command -v dpkg-deb >/dev/null ;;
    rpm) command -v rpmbuild >/dev/null ;;
    *) echo "Usage: sh eng/package-linux.sh [deb|rpm] [output directory]" >&2; exit 2 ;;
esac
case "$(uname -m)" in
    x86_64) deb_arch=amd64; rpm_arch=x86_64; node_arch=x64 ;;
    aarch64) deb_arch=arm64; rpm_arch=aarch64; node_arch=arm64 ;;
    *) echo "Unsupported Linux architecture." >&2; exit 2 ;;
esac
if [ -z "${MINIRP_NODE_RUNTIME:-}" ]; then
    node_path=$(node -p 'process.execPath')
    MINIRP_NODE_RUNTIME=$(dirname "$(dirname "$node_path")")
fi
MINIRP_NODE_RUNTIME=$(realpath -m "$MINIRP_NODE_RUNTIME")
node_binary="$MINIRP_NODE_RUNTIME/bin/node"
node_license="$MINIRP_NODE_RUNTIME/LICENSE"
if ! test -x "$node_binary" || ! test -f "$node_license" ||
    ! "$node_binary" -e 'if (process.platform !== "linux" || process.arch !== process.argv[1] ||
        Number(process.versions.node.split(".")[0]) < 24) process.exit(1)' "$node_arch"; then
    echo "Set MINIRP_NODE_RUNTIME to an official Linux Node.js 24+ distribution for this architecture with bin/node and LICENSE." >&2
    exit 2
fi
version=$(dotnet msbuild src/MiniRP.Cli/MiniRP.Cli.csproj -getProperty:Version)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
payload="$stage/payload"
PATH="$MINIRP_NODE_RUNTIME/bin:$PATH" MINIRP_DESKTOP_NO_SYMBOLS=1 sh eng/build-desktop.sh "$payload/usr/lib/minirp"
if ! test -x "$payload/usr/lib/minirp/ffmpeg/ffmpeg" || ! test -x "$payload/usr/lib/minirp/ffmpeg/ffprobe"; then
    echo "Build the shared FFmpeg runtime before packaging MiniRP." >&2
    exit 2
fi
install -m 755 "$node_binary" "$payload/usr/lib/minirp/browser/node"
install -m 644 "$node_license" "$payload/usr/lib/minirp/browser/node-LICENSE.txt"
find "$payload" -name '*.pdb' -delete
install -D -m 755 eng/packaging/minirp "$payload/usr/bin/minirp"
install -D -m 644 eng/packaging/minirp.desktop "$payload/usr/share/applications/minirp.desktop"
install -D -m 644 assets/branding/launcher.png "$payload/usr/share/icons/hicolor/256x256/apps/minirp.png"
mkdir -p "$output"

if [ "$format" = deb ]; then
    install -D -m 644 LICENSE "$payload/usr/share/doc/minirp/copyright"
    mkdir "$payload/DEBIAN"
    cat > "$payload/DEBIAN/control" <<EOF
Package: minirp
Version: $version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: MiniRP
Depends: libc6 (>= 2.34), libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70
Recommends: xdg-utils, zenity, libgtk-3-0t64 | libgtk-3-0, libnss3, libnspr4, libcups2t64 | libcups2, libgbm1, libasound2t64 | libasound2, ffmpeg, poppler-utils, img2pdf
Description: MiniRP chat workspace
 Local terminal and browser interfaces for MiniRP.
EOF
    dpkg-deb --root-owner-group --build "$payload" "$output/minirp_${version}_${deb_arch}.deb"
else
    install -D -m 644 LICENSE "$payload/usr/share/licenses/minirp/LICENSE"
    rpmbuild -bb --target "$rpm_arch" --buildroot "$stage/rpm-root" \
        --define "_topdir $stage/rpm" --define "_rpmdir $output" \
        --define "minirp_version $version" --define "minirp_payload $payload" eng/packaging/MiniRP.spec
fi
