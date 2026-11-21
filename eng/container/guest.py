import fcntl
import io
import json
import mmap
import os
import selectors
import signal
import stat
import subprocess
import sys
import tempfile
import threading
import time


HEADER = 64 * 1024
MAX_ASSET = 128 * 1024 * 1024
LIMIT = 8 * 1024
USER = 1000
ENV = {"PATH": "/usr/local/bin:/usr/bin:/bin", "HOME": "/workspace", "USER": "agent", "LANG": "C.UTF-8"}
with open("/proc/cmdline") as boot:
    BOOT = boot.read().split()
MICROVM = "minirp.microvm=1" in BOOT
WORKSPACE_DISK = "/dev/minirp-workspace" if MICROVM else "/dev/vdb"
MAILBOX = "/dev/minirp-mailbox" if MICROVM else "/dev/vdc"
FREEZE = 0xC0045877
THAW = 0xC0045878
CGROUP = "/sys/fs/cgroup"


def privileged(*args, check=True):
    return subprocess.run(args, check=check)


def as_agent():
    os.setgroups([])
    os.setgid(USER)
    os.setuid(USER)


def copy(source, target, length):
    while length:
        data = source.read(min(65536, length))
        if not data:
            raise OSError("Incomplete Asset data.")
        target.write(data)
        length -= len(data)


def transfer(mailbox, request):
    # Open the mailbox as PID 1, but access guest paths with the same permissions as shell.
    as_agent()
    path = request["path"]
    if request["op"] == "export_asset":
        length = request["size"]
        if not 0 <= length <= MAX_ASSET:
            raise ValueError("Invalid Asset size.")
        with tempfile.NamedTemporaryFile(dir=os.path.dirname(path) or ".", delete=False) as out:
            temporary = out.name
            try:
                copy(mailbox, out, length)
                out.close()
                os.replace(temporary, path)
            finally:
                if os.path.lexists(temporary):
                    os.unlink(temporary)
        return {"size": length}
    with io.FileIO(os.open(path, os.O_RDONLY | os.O_NONBLOCK), "r") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_ASSET:
            raise ValueError("Use a regular guest file no larger than 128 MiB.")
        copy(source, mailbox, info.st_size)
        return {"size": info.st_size}


def discard_output(stream):
    with stream:
        while os.read(stream.fileno(), 65536):
            pass


def shell(request, mailbox, cancelled):
    deadline = time.monotonic() + request["seconds"]
    size = 0
    prefix = bytearray()
    error = None

    def capture(data):
        nonlocal size
        prefix.extend(data[:max(0, LIMIT * 4 - len(prefix))])
        remaining = MAX_ASSET - size
        mailbox.write(data[:remaining])
        size += min(len(data), remaining)
        return len(data) <= remaining

    process = subprocess.Popen(
        ["/bin/bash", "-lc", request["command"]], cwd="/workspace", env=ENV,
        user=USER, group=USER, extra_groups=[], start_new_session=True,
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    if os.path.isfile(CGROUP + "/cgroup.subtree_control"):
        for name in os.listdir(CGROUP):
            if name.startswith("shell-"):
                try:
                    os.rmdir(CGROUP + "/" + name)
                except OSError:
                    pass
        group = CGROUP + "/shell-" + request["id"]
        try:
            os.mkdir(group)
            with open(group + "/memory.max", "w") as limit:
                limit.write(str(request.get("memory_mib", 2048) * 1024 * 1024))
            with open(group + "/cgroup.procs", "w") as members:
                members.write(str(process.pid))
        except (OSError, ProcessLookupError):
            pass
    with selectors.DefaultSelector() as ready:
        ready.register(process.stdout, selectors.EVENT_READ)
        try:
            exit_deadline = None
            while True:
                now = time.monotonic()
                if process.poll() is not None:
                    if exit_deadline is None:
                        exit_deadline = now + 0.025
                    if now >= exit_deadline or not ready.select(0):
                        break
                elif now >= deadline:
                    raise subprocess.TimeoutExpired(process.args, request["seconds"])
                elif cancelled():
                    error = "Guest command was interrupted."
                    break
                elif not ready.select(min(0.05, deadline - now)):
                    continue
                data = os.read(process.stdout.fileno(), 65536)
                if not data:
                    process.wait(timeout=max(0, deadline - time.monotonic()))
                    break
                if not capture(data):
                    error = "Guest command exceeded the 128 MiB output limit."
                    break
        except subprocess.TimeoutExpired:
            error = "Guest command timed out."
        finally:
            if error or process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.wait()
            if error:
                drain_until = time.monotonic() + 0.025
                while size < MAX_ASSET and time.monotonic() < drain_until and ready.select(0):
                    data = os.read(process.stdout.fileno(), 65536)
                    if not data or not capture(data):
                        break
            threading.Thread(target=discard_output, args=(process.stdout,), daemon=True).start()
    result = {"exit_code": process.returncode}
    if error:
        result["error"] = error
    if not request.get("asset_output") and size == len(prefix):
        text = prefix.decode(errors="replace")
        if len(text) <= LIMIT:
            return {**result, "output": text}
    return {**result, "size": size}


def operate(mailbox, request):
    fd = mailbox.fileno()
    result = subprocess.run([sys.executable, __file__, "--transfer", str(fd)],
                            pass_fds=(fd,), input=json.dumps(request), text=True,
                            stdout=subprocess.PIPE, check=True, timeout=30)
    return json.loads(result.stdout)


def reply(mailbox, response):
    data = json.dumps(response, ensure_ascii=False).encode()
    mailbox.seek(0)
    mailbox.write(data.ljust(HEADER, b"\x00"))
    os.fsync(mailbox.fileno())


def setup():
    privileged("mount", "-t", "tmpfs", "-o", "mode=1777,size=64m", "tmpfs", "/tmp")
    privileged("mount", "-t", "tmpfs", "-o", "mode=0755", "tmpfs", "/run")
    with open("/run/resolv.conf", "w") as resolver:
        resolver.write("nameserver 10.0.2.3\n")
    privileged("mount", "--bind", "/run/resolv.conf", "/etc/resolv.conf")
    os.chmod(MAILBOX, 0o600)
    if "minirp.initialize=1" in BOOT:
        privileged("mkfs.ext4", "-qF", "-m", "0", WORKSPACE_DISK)
    privileged("mount", "-t", "ext4", "-o", "nodev,nosuid", WORKSPACE_DISK, "/workspace")
    os.chown("/workspace", USER, USER)
    os.chdir("/workspace")
    privileged("ip", "link", "set", "lo", "up")
    privileged("ip", "link", "set", "eth0", "up")
    privileged("ip", "addr", "add", "10.0.2.15/24", "dev", "eth0")
    privileged("ip", "route", "add", "default", "via", "10.0.2.2")
    if privileged("mount", "-t", "cgroup2", "none", CGROUP, check=False).returncode == 0:
        with open(CGROUP + "/cgroup.controllers") as controllers:
            if "memory" in controllers.read().split():
                with open(CGROUP + "/cgroup.subtree_control", "w") as control:
                    control.write("+memory")


def serve(mailbox):
    direct = os.open(MAILBOX, os.O_RDONLY | os.O_DIRECT)
    workspace = os.open("/workspace", os.O_RDONLY | os.O_DIRECTORY)
    frozen = False
    try:
        with mmap.mmap(-1, 4096) as first, mmap.mmap(-1, HEADER) as header, mmap.mmap(-1, 4096) as control:
            def cancelled():
                os.preadv(direct, [control], HEADER + MAX_ASSET)
                return control[:12] == request["id"].encode("ascii")

            reply(mailbox, {"ready": True})
            while True:
                os.preadv(direct, [first], 0)
                if b'"op":' not in first[:]:
                    try:
                        while os.waitpid(-1, os.WNOHANG)[0]:
                            pass
                    except ChildProcessError:
                        pass
                    time.sleep(0.05)
                    continue
                os.preadv(direct, [header], 0)
                try:
                    request = json.loads(header[:].split(b"\x00", 1)[0])
                except (UnicodeError, ValueError):
                    request = {}
                op = request.get("op")
                if not op:
                    time.sleep(0.05)
                    continue
                response = {}
                try:
                    if frozen:
                        fcntl.ioctl(workspace, THAW, 0)
                        frozen = False
                    if op == "shutdown":
                        return
                    if op not in ("resume", "checkpoint"):
                        mailbox.seek(HEADER)
                        response = shell(request, mailbox, cancelled) if op == "shell" else operate(mailbox, request)
                    if op in ("checkpoint", "shell", "export_asset"):
                        fcntl.ioctl(workspace, FREEZE, 0)
                        frozen = True
                except Exception as error:
                    response = {**response, "error": str(error)}
                reply(mailbox, {**response, "id": request["id"], "frozen": frozen})
    finally:
        if frozen:
            fcntl.ioctl(workspace, THAW, 0)
        os.close(workspace)
        os.close(direct)


def main():
    with open(MAILBOX, "r+b", buffering=0) as mailbox:
        try:
            setup()
        except Exception as error:
            reply(mailbox, {"ready": False, "error": str(error)})
            return
        serve(mailbox)
    os.chdir("/")
    os.sync()


if len(sys.argv) > 1 and sys.argv[1] == "--transfer":
    with open(int(sys.argv[2]), "r+b", buffering=0) as inherited:
        try:
            print(json.dumps(transfer(inherited, json.load(sys.stdin))))
        except Exception as error:
            print(json.dumps({"error": str(error)}))
else:
    try:
        main()
    finally:
        os.sync()
        privileged("reboot" if MICROVM else "poweroff", "-f")
