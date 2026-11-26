# 智能体容器

Android 和 Windows x64 的安装包包含同一份 x86-64 Alpine 镜像和各平台的无界面 QEMU。ChatAgent、PresetAgent 和 PluginAgent 可以激活 `container` 技能。PresetAgent 和所属的 PluginAgent 共用同一预设容器、`/workspace` 和 Asset URL。容器在第一次调用时启动，切换容器时按预设保存工作盘。插件项目可以使用各自的子目录。

- `shell(command, seconds?, memory_mib?, async?)` 在 `/workspace` 执行 Bash，返回退出码和合并输出。超过 8192 个 Unicode 码点的输出使用临时 `asset_url`，可用 `assets` 查看。`async: true` 立即返回输出的 `asset_url` 和 `db_key`，完成后可用同一 URL 读取输出。通过 `db_watch` 订阅下一轮模型调用前的更新，用 `db_read` 读取 `running`、`failed` 或 `success`。非零退出码、超时、输出达到 128 MiB 上限以及执行中断都会标记为 `failed`，可读取已捕获的输出。

`shell`、`curl`、`ffmpeg` 和 `ffprobe` 的 `seconds` 参数用于设置可选的执行期限，留空时运行到完成或取消。
- 镜像内含 Alpine 的 `apk`、BusyBox、Bash、curl、Node.js、npm、Python 3、pip、Git、jq、ripgrep（`rg`）、`sqlite3`、`pdftotext`、`unzip`、`zstd` 和 GNU diffutils。两端提供相同的用户态网络和工具参数。
- `export_asset(url, path)` 将当前会话或所属预设已授权的 Asset 写入客户机文件。
- `import_asset(url, path)` 将客户机普通文件写入已授权且声明为可写的 Asset。成功提交后复用现有 Asset 作用域和原子替换机制。
- 智能体可单独激活 `curl` Skill，使用 `curl(args, read_assets?, write_assets?, stdin_asset?, asset_url?, seconds?)`。`args` 是原始 curl 参数数组，省略命令名。把命令里的文件参数写成 Asset URL，并在 `read_assets` 或 `write_assets` 中声明。`-o` 等显式目的地只在 curl 成功后提交。标准输入可来自 `stdin_asset`，标准输出保存为 Asset。可用 `asset_url` 指定已有的可写文件。单个 Asset 上限为 128 MiB。宿主内置单独从 curl 源码构建的工具，启动 Skill 时无需等待 Room 虚拟机。

## 宿主媒体技能

`ffmpeg` 和 `ffprobe` 独立激活，参数与 `curl` 使用同一套 Asset 授权。`args` 使用原版命令参数，省略命令名。输入用 `read_assets` 声明，可写输出用 `write_assets` 声明。使用 `stdin_asset` 和 `pipe:0` 提供标准输入。使用 `pipe:1` 写标准输出，结果包含其 `asset_url`。命令成功后才提交声明的输出文件。

宿主提供 FFmpeg 运行时时，ChatAgent、EnvAgent、PresetAgent、SearchAgent、BrowserAgent、PluginAgent 和插件创建的 TemporaryAgent 均可激活这两个技能。TemporaryAgent 可通过标准输出在本会话内生成临时 Asset。

- 探测媒体时可以传入 `["-v","error","-show_streams","-show_format","-of","json","<输入 Asset URL>"]`。`ffprobe` 同时返回短文本结果和标准输出 Asset。
- 转码时可先创建可写 Asset，再传入 `["-nostdin","-y","-i","<输入 Asset URL>","-ar","24000","-f","wav","<输出 Asset URL>"]`。Asset URL 没有扩展名，使用 `-f` 指定输出格式。
- 编码 H.264 MP4 时可用 `-c:v libx264 -pix_fmt yuv420p -f mp4`。可用 `-preset` 和 `-crf` 调整速度和质量。两遍编码可用 `-passlogfile <日志 Asset URL> -x264-params mbtree=0`，在第一遍声明可写日志 Asset，第二遍将同一 URL 声明为输入。

宿主在独立进程运行 FFmpeg CLI。CLI 使用共享库的 AVIO 回调读写二进制流。已声明的文件从 Asset 流分块暂存，供 CLI 和 x264 随机读写。随包的媒体运行时包含 `libx264`，采用 GPL 2.0 或更高版本。可用的格式、编解码器和滤镜以 `-formats`、`-encoders`、`-decoders`、`-filters` 为准。使用 `curl` 下载远程媒体。设备采集、网络协议和直接读写宿主路径的滤镜及分段格式不在随包构建中。

系统盘是使用 XZ 压缩的只读 SquashFS，`/workspace` 是独立的 ext4 日志盘，也用作智能体的主目录。打开 Room 时异步引导虚拟机。后续调用复用来宾内核、进程和临时目录。后台进程的输出可重定向到 `/workspace`，供后续命令读取。离开 Room 后保存工作盘检查点并退出虚拟机。保存检查点期间短暂冻结 ext4，完成后恢复后台写入。

每个 Room 的来宾内存上限为 2 GiB。QEMU 使用按需分配的内存后端。来宾支持 cgroup v2 内存控制时，可用 `memory_mib` 为单条命令及其子进程设置更低的上限。镜像重建与 Alpine 包对应的 musl，将默认 pthread 栈设为 8 MiB。Android ARM64 使用 microVM 和 TCG。Windows x64 优先使用可用的 WHPX，并可使用 TCG。

Linux user-mode 的 `-R` 参数预留来宾虚拟地址区间。x86-64 的高地址 vsyscall 页超出 `-R 4G` 的范围，Android ARM64 上的 PRoot 原型会因此在启动阶段失败。 [QEMU user-mode 参数](https://www.qemu.org/docs/master/user/main.html) · [x86-64 装载器源码](https://gitlab.com/qemu-project/qemu/-/blob/v11.1.3/linux-user/x86_64/elfload.c)

检查点包含按内容哈希保存的压缩磁盘块，完整存档携带预设会话、各聊天及其历史分支引用的块。导入校验块大小和 SHA‑256。用预设的「导出存档」和「导入存档」在 Android 与 Windows 间继续使用工作目录。同一会话从历史消息分支时恢复该消息对应的磁盘检查点。

历史版本只保存会话原始记录和世界快照，恢复时从原始记录重建全文索引。当前会话索引普通笔记和可召回消息，指定归档前缀时直接查原文。版本在本地压缩，导出仍使用原有的归档布局。已删除的会话进入压缩回收目录，保留 30 天。自动清理会保留仍被活动对话或回收记录引用的版本，并回收超期的中断 VM 工作目录和 Android 导入、导出临时文件。

## 构建

1. 在 x86-64 Linux 或 WSL 中以 root 运行 `sh eng/container/build-image.sh <仓库路径>/artifacts/container-image`。需要 curl、cpio、kmod、squashfs-tools、C 编译器、make、binutils 和 patch。两端使用同一次生成的镜像、校验清单和 `ROOM_VM` 标记。
2. Android 从经 Termux 软件源签名验证的 aarch64 软件包中解包无界面 QEMU 和依赖，也可用 `eng/container/build-qemu.sh android` 构建精简运行时。在相同版本的 QEMU 源码上应用 Termux 补丁与 `eng/container/qemu-android-bionic.patch`，设置 Android NDK 编译器与 Termux 依赖路径。运行 `python3 eng/container/prepare-android.py <Termux前缀> artifacts/container-runtime/android`，用 `--executable` 选择自编译产物，需要 readelf 和 patchelf。用 `eng/build-android.ps1` 构建。分发前从干净的 Gradle 输出构建 APK，以免增量打包留下旧镜像的空洞。APK 中的 SquashFS 直接作为只读磁盘打开，内核和 initrd 放入可回收缓存。
3. Windows 从 QEMU 项目列出的 Windows 分发源安装或解包 QEMU。运行 `eng/container/prepare-windows.ps1 -Qemu <QEMU目录> -Destination artifacts/container-runtime/windows`，再运行 `eng/build-desktop.ps1`。构建把 QEMU 的 x86-64 目标、依赖和镜像复制到桌面产物。检查软件包时用新目录解压启动，确认无需系统 PATH 中的 QEMU。
4. 用 `eng/curl/build.sh` 构建宿主 curl。准备 curl 8.23.0、Mbed TLS 3.6.7、libssh2 1.11.1、nghttp2 1.71.0、zlib 1.4 和 Mozilla CA bundle。Android 还使用 OpenLDAP 2.6.16 和 Linux 主机版 NDK。Linux 目标使用系统 OpenLDAP 开发库，可选 `eng/curl/build.sh linux`。构建输出放入对应的 `artifacts/curl-runtime/android`、`artifacts/curl-runtime/windows` 或 `artifacts/curl-runtime/linux`。协议和可选特性可通过对应产物的 `--version` 查看。
5. Linux 宿主可使用发行版的 `qemu-system-x86_64`，或通过 `MINIRP_QEMU_RUNTIME` 随包放入兼容的 QEMU。用 `eng/build-desktop.sh` 和相同的 Alpine 镜像构建带 TUI 与 WebUI 的 Linux 产物。
6. 验证并解包 [FFmpeg 8.1.3 源码](https://ffmpeg.org/download.html#releases)，准备 [x264 源码](https://www.videolan.org/developers/x264.html)的修订 `31e19f92f00c7003fa115047ce50978bc98c3a0d`。运行 `sh eng/ffmpeg/build.sh <android|windows|linux> <FFmpeg 源码目录> <构建目录> <运行时目录> <x264 源码目录>`。Linux 构建使用 zlib 开发包。Windows 使用 MinGW-w64，可通过 `MINIRP_MINGW_LICENSE` 指定工具链的运行时许可文本。Android 用 Linux 主机版 NDK，路径通过 `ANDROID_NDK_ROOT` 指定。三端默认从 `artifacts/ffmpeg-x264-runtime/<平台>` 打包，可通过 Windows 和 Android 的 `-FfmpegRuntime` 参数或 Linux 的 `MINIRP_FFMPEG_RUNTIME` 指定位置。运行时目录包含 GPL 许可文本与对应源码归档。

相关实现见 [QEMU 启动参数](https://www.qemu.org/docs/master/system/invocation.html)、[WHPX 加速](https://www.qemu.org/docs/master/system/whpx.html)、[Termux 无界面 QEMU](https://github.com/termux/termux-packages/tree/master/packages/qemu-system-x86-64-headless)和 [ext4 日志](https://docs.kernel.org/filesystems/ext4/journal.html)。
