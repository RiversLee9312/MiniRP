# Agent 容器

Android 和 Windows x64 的安装包包含同一份 x86-64 Alpine 镜像和各平台的无界面 QEMU。安装后，ChatAgent 可以激活 `container` 技能。

- `shell(command, seconds?, memory_mib?, async?)` 在 `/workspace` 执行 Bash，返回退出码和合并输出。超过 8192 个 Unicode 码点的输出使用临时 `asset_url`，可用 `view_asset` 查看。`async: true` 立即返回输出的 `asset_url` 和 `db_key`，完成后可用同一 URL 读取输出。通过 `db_watch` 订阅下一轮模型调用前的更新，用 `db_read` 读取 `running`、`failed` 或 `success`。非零退出码、超时、输出达到 128 MiB 上限以及执行中断都会标记为 `failed`，可读取已捕获的输出。

`shell` 和 `curl` 的 `seconds` 参数用于设置可选的执行期限，留空时运行到完成或取消。
- 镜像内含 Alpine 的 `apk`、BusyBox、Bash、curl、FFmpeg、Node.js、npm、Python 3、pip、Git、jq、ripgrep（`rg`）、`sqlite3`、`pdftotext`、`unzip`、`zstd` 和 GNU diffutils。两端提供相同的用户态网络和工具参数。
- `export_asset(url, path)` 将当前 Agent 会话已授权的 Asset 写入客户机文件。
- `import_asset(url, path)` 将客户机普通文件写入当前会话已授权且声明为可写的 Asset。成功提交后复用现有 Asset 作用域和原子替换机制。
- Agent 可单独激活 `curl` Skill，使用 `curl(args, read_assets?, write_assets?, stdin_asset?, asset_url?, seconds?)`。`args` 是原始 curl 参数数组，省略命令名。把命令里的文件参数写成 Asset URL，并在 `read_assets` 或 `write_assets` 中声明。`-o` 等显式目的地只在 curl 成功后提交。标准输入可来自 `stdin_asset`，标准输出保存为 Asset。可用 `asset_url` 指定已有的可写文件。单个 Asset 上限为 128 MiB。宿主内置单独从 curl 源码构建的工具，启动 Skill 时无需等待 Room 虚拟机。

系统盘是使用 XZ 压缩的只读 SquashFS，`/workspace` 是独立的 ext4 日志盘，也用作 Agent 的主目录。打开 Room 时异步引导虚拟机。后续调用复用来宾内核、进程和临时目录。后台进程的输出可重定向到 `/workspace`，供后续命令读取。离开 Room 后保存工作盘检查点并退出虚拟机。保存检查点期间短暂冻结 ext4，完成后恢复后台写入。

每个 Room 的来宾内存上限为 2 GiB。QEMU 使用按需分配的内存后端。来宾支持 cgroup v2 内存控制时，可用 `memory_mib` 为单条命令及其子进程设置更低的上限。镜像重建与 Alpine 包对应的 musl，将默认 pthread 栈设为 8 MiB。Android ARM64 使用 microVM 和 TCG。Windows x64 优先使用可用的 WHPX，并可使用 TCG。

Linux user-mode 的 `-R` 参数预留来宾虚拟地址区间。x86-64 的高地址 vsyscall 页超出 `-R 4G` 的范围。Android ARM64 真机 上的 PRoot 原型使用这个参数时会在启动阶段失败。 [QEMU user-mode 参数](https://www.qemu.org/docs/master/user/main.html) · [x86-64 装载器源码](https://gitlab.com/qemu-project/qemu/-/blob/v11.1.3/linux-user/x86_64/elfload.c)

检查点包含按内容哈希保存的压缩磁盘块，存档只携带各聊天及其历史分支引用的块。导入校验块大小和 SHA‑256。用预设的「导出存档」和「导入存档」在 Android 与 Windows 间继续使用工作目录。同一会话从历史消息分支时恢复该消息对应的磁盘检查点。

历史版本只保存会话原始记录和世界快照，恢复时从原始记录重建全文索引。当前会话索引普通笔记和可召回消息，指定归档前缀时直接查原文。版本在本地压缩，导出仍使用原有的归档布局。已删除的会话进入压缩回收目录，保留 30 天。自动清理会保留仍被活动对话或回收记录引用的版本，并回收超期的中断 VM 工作目录和 Android 导入、导出临时文件。

## 构建

1. 在 x86-64 Linux 或 WSL 中以 root 运行 `sh eng/container/build-image.sh <仓库路径>/artifacts/container-image`。需要 curl、cpio、kmod、squashfs-tools、C 编译器、make、binutils 和 patch。两端使用同一次生成的镜像、校验清单和 `ROOM_VM` 标记。
2. Android 从经 Termux 软件源签名验证的 aarch64 软件包中解包无界面 QEMU 和依赖，也可用 `eng/container/build-qemu.sh android` 构建精简运行时。在相同版本的 QEMU 源码上应用 Termux 补丁与 `eng/container/qemu-android-bionic.patch`，设置 Android NDK 编译器与 Termux 依赖路径。运行 `python3 eng/container/prepare-android.py <Termux前缀> artifacts/container-runtime/android`，用 `--executable` 选择自编译产物，需要 readelf 和 patchelf。用 `eng/build-android.ps1` 构建。分发前从干净的 Gradle 输出构建 APK，以免增量打包留下旧镜像的空洞。APK 中的 SquashFS 直接作为只读磁盘打开，内核和 initrd 放入可回收缓存。
3. Windows 从 QEMU 项目列出的 Windows 分发源安装或解包 QEMU。运行 `eng/container/prepare-windows.ps1 -Qemu <QEMU目录> -Destination artifacts/container-runtime/windows`，再运行 `eng/build-desktop.ps1`。构建把 QEMU 的 x86-64 目标、依赖和镜像复制到桌面产物。检查软件包时用新目录解压启动，确认无需系统 PATH 中的 QEMU。
4. 用 `eng/curl/build.sh` 构建宿主 curl。准备 curl 8.23.0、Mbed TLS 3.6.7、libssh2 1.11.1、nghttp2 1.71.0、zlib 1.4 和 Mozilla CA bundle。Android 还使用 OpenLDAP 2.6.16 和 Linux 主机版 NDK。构建输出放入 `artifacts/curl-runtime/android` 或 `artifacts/curl-runtime/windows`。协议和可选特性可通过 `curl --version` 查看。

相关实现见 [QEMU 启动参数](https://www.qemu.org/docs/master/system/invocation.html)、[WHPX 加速](https://www.qemu.org/docs/master/system/whpx.html)、[Termux 无界面 QEMU](https://github.com/termux/termux-packages/tree/master/packages/qemu-system-x86-64-headless)和 [ext4 日志](https://docs.kernel.org/filesystems/ext4/journal.html)。
