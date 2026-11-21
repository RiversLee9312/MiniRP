"""Assemble the headless QEMU native files from an unpacked aarch64 Termux prefix.

Use packages verified by the Termux APT signing key. Needs readelf and patchelf.
"""

import collections
import argparse
import pathlib
import re
import shutil
import subprocess


def output(*args):
    return subprocess.check_output(args, text=True)


def main(prefix, target, executable=None):
    native = target / "jniLibs/arm64-v8a"
    firmware = target / "assets/container-firmware"
    native.mkdir(parents=True, exist_ok=True)
    firmware.mkdir(parents=True, exist_ok=True)
    executable = executable or prefix / "bin/qemu-system-x86_64"
    system = {"libc.so", "libm.so", "libdl.so", "liblog.so", "libandroid.so", "libz.so"}
    files = collections.deque([(executable, "libminirp_qemu.so")])
    packaged = set()

    def jni_name(name):
        if not re.fullmatch(r"lib[A-Za-z0-9.+_-]+\.so(?:\.[0-9.]+)?", name):
            raise ValueError("Unexpected dependency: " + name)
        return name if name.endswith(".so") else name.replace(".so.", "_") + ".so"

    while files:
        source, name = files.popleft()
        if name in packaged:
            continue
        destination = native / name
        shutil.copyfile(source, destination)
        if source.name == "libslirp.so":
            # Termux patches libslirp's resolver path to its own app prefix.
            # MiniRP gives QEMU a private per-run working directory.
            termux = b"/data/data/com.termux/files/usr/etc/resolv.conf\0"
            binary = destination.read_bytes()
            if binary.count(termux) != 1:
                raise ValueError("Review the path used by this libslirp build.")
            destination.write_bytes(binary.replace(termux, b"./resolv.conf\0".ljust(len(termux), b"\0")))
        needed = re.findall(r"\(NEEDED\).*?\[(.*?)\]", output("readelf", "-d", str(source)))
        for dependency in needed:
            if dependency in system:
                continue
            android_name = jni_name(dependency)
            if android_name != dependency:
                subprocess.check_call(["patchelf", "--page-size", "16384", "--replace-needed", dependency, android_name, destination])
            files.append((prefix / "lib" / dependency, android_name))
        edit = ["patchelf", "--page-size", "16384", "--remove-rpath"]
        if source != executable:
            edit += ["--set-soname", name]
        subprocess.check_call([*edit, destination])
        packaged.add(name)

    qemu_data = prefix / "share/qemu"
    for name in ("bios-256k.bin", "kvmvapic.bin", "linuxboot_dma.bin", "qboot.rom"):
        shutil.copyfile(qemu_data / name, firmware / name)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefix", type=pathlib.Path)
    parser.add_argument("target", type=pathlib.Path)
    parser.add_argument("--executable", type=pathlib.Path)
    args = parser.parse_args()
    main(args.prefix, args.target, args.executable)
