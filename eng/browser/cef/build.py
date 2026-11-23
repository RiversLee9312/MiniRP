"""Build the MiniRP helper against the same pinned CEF ABI as the desktop installer."""

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import tarfile
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


SOURCE = Path(__file__).resolve().parent
HOST = "https://cef-builds.spotifycdn.com/"
CHUNK_SIZE = 8 * 1024 * 1024


def pin(target):
    release = json.loads((SOURCE / "releases.json").read_text(encoding="utf-8"))
    filename = f"cef_binary_{release['version']}_{target}_minimal.tar.bz2"
    return filename, release["platforms"][target]


def fetch_range(url, offset, length, size):
    end = offset + length - 1
    request = Request(url, headers={"Range": f"bytes={offset}-{end}"})
    for attempt in range(3):
        try:
            with urlopen(request, timeout=60) as response:
                expected = f"bytes {offset}-{end}/{size}"
                if response.status != 206 or response.headers.get("Content-Range") != expected:
                    raise OSError("CEF publisher returned an unexpected byte range.")
                content = response.read(length + 1)
                if len(content) != length:
                    raise OSError("CEF publisher returned an incomplete byte range.")
                return offset, content
        except (OSError, URLError):
            if attempt == 2:
                raise


def check_archive(file, metadata):
    if not file.is_file() or file.stat().st_size != metadata["size"]:
        return False
    with file.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256" if metadata.get("sha256") else "sha1").hexdigest()
    return digest == metadata.get("sha256", metadata["sha1"])


def download(cache, target):
    filename, metadata = pin(target)
    archive = cache / filename
    if check_archive(archive, metadata):
        return archive
    print(f"Downloading pinned CEF SDK for {target} ({metadata['size'] // 1_048_576} MiB)", flush=True)
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="download-", dir=cache) as staging:
        temporary = Path(staging) / filename
        url = HOST + quote(filename, safe="+")
        size = metadata["size"]
        with ThreadPoolExecutor(max_workers=8) as pool, temporary.open("w+b") as output:
            offsets = list(range(0, size, CHUNK_SIZE))
            for start in range(0, len(offsets), 8):
                pending = offsets[start:start + 8]
                for offset, content in pool.map(
                    lambda offset: fetch_range(url, offset, min(CHUNK_SIZE, size - offset), size),
                    pending,
                ):
                    output.seek(offset)
                    output.write(content)
        if not check_archive(temporary, metadata):
            raise OSError("CEF SDK checksum does not match the pinned release.")
        temporary.replace(archive)
    return archive


def build(archive, cache, target, output):
    distribution = archive.name.removesuffix(".tar.bz2")
    sdk = cache / distribution
    if not (sdk / "include/cef_version.h").is_file():
        print(f"Extracting the CEF SDK for {target}", flush=True)
        with tempfile.TemporaryDirectory(prefix="extract-", dir=cache) as staging:
            with tarfile.open(archive, mode="r:bz2") as source:
                source.extractall(staging, filter="data")
            (Path(staging) / distribution).replace(sdk)

    intermediates = cache / f"build-{distribution}"
    cmake = ["cmake", "-S", str(SOURCE), "-B", str(intermediates), f"-DCEF_ROOT={sdk.as_posix()}"]
    if target.startswith("windows"):
        cmake += ["-A", "ARM64" if target == "windowsarm64" else "x64"]
        helper = intermediates / "Release" / "minirp-cef.dll"
    else:
        cmake += ["-DCMAKE_BUILD_TYPE=Release"]
        helper = intermediates / "minirp-cef"
    subprocess.run(cmake, check=True)
    jobs = str(min(12, os.cpu_count() or 1))
    subprocess.run(["cmake", "--build", str(intermediates), "--config", "Release", "--parallel", jobs], check=True)
    output.mkdir(parents=True, exist_ok=True)
    shutil.copy2(helper, output / helper.name)
    shutil.copy2(sdk / "LICENSE.txt", output / "cef-LICENSE.txt")
    if target.startswith("windows"):
        shutil.copy2(sdk / "Release/bootstrap.exe", output / "minirp-cef.exe")


def main():
    system = {"Windows": "windows", "Linux": "linux"}.get(platform.system(), "")
    machine = {"amd64": "64", "x86_64": "64", "arm64": "arm64", "aarch64": "arm64"}.get(
        platform.machine().lower(), ""
    )
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--platform", choices=("windows64", "windowsarm64", "linux64", "linuxarm64"),
                        default=system + machine)
    parser.add_argument("--download-only", action="store_true")
    args = parser.parse_args()
    if args.platform not in ("windows64", "windowsarm64", "linux64", "linuxarm64"):
        parser.error("No CEF SDK is pinned for this architecture.")
    default_cache = SOURCE.parents[2] / "artifacts/browser-sdk" / args.platform
    cache = Path(os.environ.get("MINIRP_CEF_SDK_CACHE", default_cache)).resolve()
    archive = download(cache, args.platform)
    if not args.download_only:
        build(archive, cache, args.platform, args.output.resolve())


if __name__ == "__main__":
    main()
