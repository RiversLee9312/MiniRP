#!/bin/sh
set -eu
workspace=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$workspace"
case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    aarch64) runtime=linux-arm64 ;;
    *) echo "Unsupported Linux architecture." >&2; exit 2 ;;
esac
output=$(realpath -m "${1:-$workspace/artifacts/desktop/$runtime}")
native="$workspace/artifacts/native/$runtime"
build_intermediates="$workspace/artifacts/dotnet-$runtime"
set --
if [ "${MINIRP_DESKTOP_NO_SYMBOLS:-}" = 1 ]; then
    build_intermediates="$build_intermediates-package"
    set -- -p:DebugType=none "-p:PathMap=$workspace=/_/MiniRP"
fi
dotnet publish src/MiniRP.Core -r "$runtime" -c Release -o "$native" --artifacts-path "$build_intermediates" "$@"
dotnet publish src/MiniRP.Cli -r "$runtime" -c Release --self-contained true -o "$output" --artifacts-path "$build_intermediates" "$@"
cp "$native/libminirpcore.so" "$output/"
nuget_root=${NUGET_PACKAGES:-"$HOME/.nuget/packages"}
cp "$nuget_root/sqlitepclraw.lib.e_sqlite3/3.53.3/runtimes/$runtime/native/libe_sqlite3.so" "$output/"

if [ -n "${MINIRP_WEB_PREBUILT:-}" ]; then
    test -f "$MINIRP_WEB_PREBUILT/index.html"
    test -f "$MINIRP_WEB_PREBUILT/openui/index.html"
    mkdir -p "$output/wwwroot"
    cp -R "$MINIRP_WEB_PREBUILT/." "$output/wwwroot/"
else
    web="$workspace/apps/desktop-web"
    openui="$workspace/apps/android/genui"
    npm ci --prefix "$web" --ignore-scripts --no-audit --no-fund
    npm ci --prefix "$openui" --ignore-scripts --no-audit --no-fund
    node "$web/node_modules/vite/bin/vite.js" build "$web" --outDir "$output/wwwroot"
    node "$openui/build.mjs" "$output/wwwroot" --desktop
fi

cp package.json package-lock.json .env.example README.md LICENSE THIRD_PARTY_NOTICES.md "$output/"
npm ci --prefix "$output" --omit=dev --ignore-scripts --no-audit --no-fund
python3 eng/browser/cef/build.py "$output/browser"

curl_runtime=${MINIRP_CURL_RUNTIME:-"$workspace/artifacts/curl-runtime/linux"}
if [ -f "$curl_runtime/minirp-curl" ]; then
    install -D -m 755 "$curl_runtime/minirp-curl" "$output/curl/minirp-curl"
    find "$curl_runtime" -maxdepth 1 -name '*-license.txt' -exec cp '{}' "$output/curl/" ';'
fi
container_image=${MINIRP_CONTAINER_IMAGE:-"$workspace/artifacts/container-image"}
if [ -f "$container_image/ROOM_VM" ]; then
    mkdir -p "$output/container-image"
    for name in kernel initrd.gz root.squashfs SHA256SUMS ROOM_VM; do
        cp "$container_image/$name" "$output/container-image/"
    done
fi
if [ -n "${MINIRP_QEMU_RUNTIME:-}" ]; then
    mkdir -p "$output/qemu"
    cp -R "$MINIRP_QEMU_RUNTIME/." "$output/qemu/"
fi
