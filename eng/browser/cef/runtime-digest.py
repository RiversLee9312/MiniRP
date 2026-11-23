"""Generate digests for a pinned CEF minimal distribution and its runtime payload."""

import argparse
import hashlib
from pathlib import Path, PurePosixPath
import tarfile


def runtime_digest(path: Path) -> str:
    archive_root = path.name.removesuffix(".tar.bz2")
    digests = {}
    with tarfile.open(path, "r|bz2") as archive:
        for member in archive:
            if member.isdir():
                continue
            if not member.isfile():
                raise ValueError(f"Unexpected tar entry type: {member.name}")
            relative = PurePosixPath(member.name).relative_to(archive_root)
            if relative.parts[0] in ("Release", "Resources"):
                relative = PurePosixPath(*relative.parts[1:])
                if relative.as_posix() in ("bootstrap.exe", "bootstrapc.exe") or relative.suffix in (".a", ".lib", ".pdb", ".debug"):
                    continue
            elif relative.as_posix() != "LICENSE.txt":
                continue
            name = relative.as_posix()
            if name in digests:
                raise ValueError(f"Duplicate runtime file: {name}")
            with archive.extractfile(member) as stream:
                digests[name] = hashlib.file_digest(stream, "sha256").hexdigest()
    digest = hashlib.sha256()
    for name, file_hash in sorted(digests.items()):
        digest.update(f"{file_hash}\t{name}\n".encode())
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--sha1", required=True, help="SHA-1 from the CEF build catalog")
    args = parser.parse_args()
    with args.archive.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha1").hexdigest()
    if actual != args.sha1.lower():
        raise SystemExit("Archive does not match the CEF catalog.")
    with args.archive.open("rb") as stream:
        print("sha256", hashlib.file_digest(stream, "sha256").hexdigest(), flush=True)
    print("runtimeSha256", runtime_digest(args.archive))


if __name__ == "__main__":
    main()
