# MiniRP

.NET 10 / C# 对话 Harness。桌面使用 Terminal.Gui，Android 使用 Kotlin + Jetpack Compose Material 3 Expressive；两端通过 UTF-8 C ABI 调用同一个 NativeAOT 核心。

安装包见 [GitHub Releases](https://github.com/RiversLee9312/MiniRP/releases)。从旧版升级时，Android 可直接覆盖安装。桌面解压后运行 `minirp.exe <旧工作区路径>`，继续使用已有对话、预设和模型配置。旧的 `MINICHAT_*` 配置键仍可读取，新配置建议使用 `MINIRP_*`。

## Agent

- **ChatAgent**：主对话，调用 agentic 搜索和网页工具；system prompt 可按预设配置，支持 `{date}`。
- **EnvAgent**：编辑事实、世界状态和剧情日期。修改暂存，完成后提交。
- **PresetAgent**：用精确工具编辑预设，包括 ChatAgent 的 system prompt。
- **SearchAgent**：每轮完成一次搜索或网页请求，自主决定是否搜索、搜索多少轮、抓取哪些页面，以及召回、编辑和缓存哪些材料。最终结果数量不设上限。
- **FetchAgent**：临时浏览器 Agent，操作网页、采集全文、在 DB 中精修 Markdown，再提交正文和单独的摘要。

Agent 共用 `AgentSession` 的流式事件、工具循环、取消、上下文压缩和记忆工具。UI 展开/折叠只影响展示，不修改模型请求中的思考设置。

## 运行桌面版

需要 .NET SDK 10.0.400、Visual Studio C++ 桌面生成工具。浏览器抓取另外需要 Node.js 和 Chrome；只用 API 或缓存时不启动浏览器。

```powershell
npm ci
pwsh -File eng/build-desktop.ps1
.\artifacts\desktop\win-x64\minirp.exe .
```

首次运行选择预设，再选择其下已有对话或新建对话。`.env` 可参照 `.env.example`，也可在界面输入 `/config` 编辑。API Key 字段会遮盖显示。

常用命令：

- `/env`、`/preset-agent`：打开相应侧栏；侧栏中输入 `/close` 关闭，再次打开保留记录。
- `/canon 修改要求`、`/stage 推进要求`：在 EnvAgent 中编辑或推进剧情。
- `/presets`、`/chats`、`/new`：切换预设、加载对话或新建对话。切换预设后选择该预设下的对话。
- `/save 对话名`：保存并可选重命名；对话也会自动保存。
- `/models`：从配置的提供商读取模型列表。
- `/cot on`、`/cot off`：设置新思考块的默认展开状态；已有块仍可单独点击展开或折叠。
- `/memory 关键词`：检索当前 Agent 的记忆；省略关键词列出索引。
- `/stop`、`/quit`：取消当前任务或退出。

Enter 发送，Alt+Enter 换行。输入框随换行和软换行增高，最多八行，之后在框内滚动。命令提示固定在底部。网络、浏览器和 Agent 工作不在 UI 线程执行。

## Android

当前提供 ARM64 构建，已在连接的Android ARM64 真机 上验证。需要 Android Studio/JDK 21、SDK 37、NDK 28.2.13676358 和 Node.js 24。Gradle 根据锁文件构建随 APK 分发的 OpenUI 资源。

```powershell
pwsh -File eng/build-android.ps1 -Install -Device '<ADB device serial>'
```

输出 `artifacts/MiniRP.apk`。也可用 Android Studio 打开 `apps/android`；先运行 `eng/build-native.ps1 -Runtime linux-bionic-arm64` 生成本地库。

Android 的世界、预设和记忆面板打开即全屏。对话页支持流式 Markdown、可折叠的工具轨迹和附件；搜索轨迹缩进展示。长按支持分支的用户消息可从发送前的世界与 Agent 状态创建新对话。旧消息没有历史快照，不能分支。标题只根据主聊天生成，点击标题可编辑或重新生成。

预设可以给 ChatAgent 配置一条隐藏的开场系统消息。有开场的预设在首页使用「新建对话」按钮，创建后由 ChatAgent 生成欢迎语。详见 [为新对话设置开场](docs/presets/workflows.md#为新对话设置开场)。

ChatAgent 可按需激活 `interactive-ui` 技能，在 Markdown 中插入流式交互界面。支持图表、表格、表单、本地计算、筛选、图片和地图。可以试试「做一个可以调整人数和天数的旅行预算计算器」。界面随对话保存，单次运行期间的输入会在滚动和展开时保留。详见 [Android 生成式 UI](docs/android/generated-ui.md)。

在「提供商」设置名称、协议、地址和 API 密钥；在「模型」自动获取可用模型、保存常用模型、为 Agent 和标题指定模型。对话输入框下也可切换该页相关的 Agent 模型。提供商配置存入 DataStore，密钥用 Android Keystore AES-GCM 加密。附件可选图片、音频、PDF 和视频，能否发送取决于当前协议及模型。对话抽屉使用 Paging 3 按预设分页，默认打开聊天列表，收起预设列表。

UI 文案使用 Android 字符串资源，支持英文及另外 29 种语言；语言列表由 AGP 自动生成，使用系统按应用语言设置，低版本通过 AppCompat 适配。支持 RTL，方向图标会镜像。图标使用 Google Fonts Material Symbols 官方子集；应用图标由 GPT-image 生成，前景安全区和来源记录保存在 `assets/branding`。

.NET 的 Android/bionic NativeAOT 目前仍是实验支持。核心不依赖该平台缺失的 TLS 栈：网络和浏览器由 Kotlin/OkHttp/WebView 或桌面平台适配层执行，Agent 编排、协议转换和存储仍在 C# 中。Windows 和Android ARM64 真机 已实际构建测试；Linux/macOS 核心可在对应系统使用标准 NativeAOT 工具链构建，尚未在本项目逐个平台实测。

## 提供商

原生协议包含 OpenAI Responses、OpenAI Chat Completions、Anthropic Messages、Gemini 和 Ollama。OpenAI 兼容配置覆盖 DeepSeek、OpenRouter、Groq、Mistral、xAI、Together、Fireworks、SiliconFlow、Alibaba Cloud、Moonshot、Z.ai、Cerebras、SambaNova、Perplexity、NVIDIA、GitHub Models、LM Studio，以及自定义地址。

兼容程度取决于服务实际支持的模型、工具调用和思考参数。协议回归使用本地夹具验证，没有逐个使用真实服务密钥联调。平台专有的云账号登录和签名认证不在当前密钥配置范围内。

主模型使用其他服务时，桌面可单独填写 `MINIRP_SEARCH_API_KEY`；Android 从已配置的 DeepSeek 提供商读取搜索密钥。

## 网页工作流

预设的 `agentic_search` 默认开启。关闭后由 ChatAgent 直接调用相同的真实与虚拟搜索工具，不调用 SearchAgent；EnvAgent 可以为当前世界补充 ChatAgent 的搜索指引。

`real_fetch(url, backend)` 保持同一工具接口：

- `api`：调用 DeepSeek Fetch API。
- `local`：显式使用临时 FetchAgent；可用于重抓疑似截断的页面。
- `api_then_local`：API 出错、超时或返回空正文时使用本地浏览器。
- `auto`：采用配置的默认策略。

API 没有可靠的完整性字段时记录为 `unknown`。本地采集不加正文字符上限：原文完整归档，编辑稿存入 `page/markdown`，长内容通过 DB 分页读取。摘要保存在独立字段。无法访问的嵌入框架会被标记，避免宣称已完整采集。

调研后采用语义工具和稳定元素引用：`browser_navigate`、`browser_snapshot`、`browser_click`、`browser_type`、`browser_press_key`、`browser_scroll`、`browser_wait`，再用 `capture_page` / `finish_fetch` 提交。桌面复用官方 Playwright MCP 和 C# MCP SDK；Android 用 WebView 实现相同平台接口。页面 JavaScript 仅由固定采集代码执行，模型没有宿主代码执行工具。

相关设计来源：[Playwright MCP](https://playwright.dev/mcp/introduction)、[快照和引用](https://playwright.dev/mcp/snapshots)、[agent-browser](https://github.com/vercel-labs/agent-browser)、[browser-use](https://docs.browser-use.com/open-source/tools)、[Microsoft UFO MCP](https://microsoft.github.io/UFO/mcp/overview/)、[OpenAI computer-use 驱动](https://developers.openai.com/api/docs/guides/tools-computer-use-sdk-drivers)。采用现有浏览器工具链，未另外维护 CEF 嵌入和分发层。

## 上下文与存储

ChatAgent、EnvAgent、SearchAgent 的会话和 SQLite DB 分别保存在 `conversations/<preset>/<chat>/agents`，彼此独立。PresetAgent 的会话和 DB 保存在 `preset_sessions/<preset>`，供该预设的所有聊天共享。临时 FetchAgent 的 DB 保存在该对话的 `fetch` 目录。Android 记忆面板按 Agent 显示、搜索和编辑普通笔记。

工具包括 `db_list`、`db_read`、`db_write`、`db_patch`、`db_delete`、`db_search`。索引是字符串；写入/删除校验 revision，修改使用精确文本替换。搜索采用 FTS5 trigram；短查询使用文本扫描，不使用向量和 RAG。分页以 UTF-16 偏移计数，并保留完整 Unicode 字符。

所有 Agent 都会被提示主动记录值得保留的信息，并及时更新过期笔记。`history_search` 和 `history_recall` 用于在自己的会话中搜索和分页读取原始消息，无需依赖模型的上下文。压缩滚动更新任务、事实、引用和 DB key 的摘要，在预算内保留当前请求、最近消息和成组的工具调用及结果。应用摘要时将被覆盖的历史和上一个检查点归档。世界进度和搜索缓存按聊天隔离，分支时恢复对应版本。

异步压缩默认开启。估算使用量到上下文窗口的 70% 时后台生成检查点，到 80% 时校验并应用，接回这期间新增的消息。Android 在「设置 → 模型」的已保存模型旁可编辑两个阈值和异步开关。窗口优先取模型 API 返回的大小，缺少时使用提供商配置，调度会为模型输出预留空间。

旧 Python 格式的对话会自动导入，并把原档备份到对话目录的 `migration/python-v1.json`。旧 `world_state.json` 保留；导入时迁移该预设的世界进度和缓存。Python 运行时已从主线移除，最后可运行的 Python 检查点为 Git 提交 `24a52c2`。

### 导出与导入

- 在 Android 聊天列表长按 Room 导出单个 Room，群像 Room 包含其中所有聊天。
- 在预设列表长按预设导出预设或存档。预设文件包含配置和归属插件。存档另外包含该预设的 Room、会话、附件、分支快照和作用域内的插件数据。两个文件都可以从预设列表导入。
- 桌面可在选定预设或 Room 后运行 `/export-room <文件>`、`/export-preset <文件>`、`/export-save <文件>`。恢复使用 `/import-preset <文件>` 或 `/import-save <文件>`。

导入保留原 UUID 和插件签名。标识相同且内容冲突时停止导入，保留本地数据。分享存档可能会分享私人聊天和附件。应用的提供商凭据、插件签名私钥和未发布草稿不随包导出。

## 插件

Android 和桌面使用同一种签名 `.plugin` 包，每个插件复用自己的 V8 isolate。清单声明 Actions、Assets、Skill、附件和设置。Actions 通过 URL 投递事件，Assets 以 WHATWG 字节流读取，跨包访问按资源可见性和签名证书校验。内置 `base` 插件声明应用设置、预设创建引导和开源许可证页。

[开发、签名与安装](docs/plugins/getting-started.md) · [完整 API 参考](docs/plugins/README.md) · [插件 SDK](sdk/javascript/) · [示例插件](examples/stream-assets/plugin/) · [MIT 许可证](LICENSE)

## 代码与验证

[项目结构](docs/project/README.md)列出 HarnessCore、桌面、Android、测试、SDK 和构建入口。

NativeAOT 核心通过 `LibraryImport` 生成 P/Invoke，JSON 使用 source generator，SSE 使用 .NET 自带解析器。C ABI 用 `mc_create`、`mc_dispatch`、`mc_poll`、`mc_release`、`mc_destroy` 管理会话和事件，用 `mc_binary_take` 和 `mc_binary_write` 传输原始字节。

```powershell
dotnet run --project tests/MiniRP.Core.Tests
$env:JAVA_HOME = '<JDK 21 installation directory>'
.\apps\android\gradlew.bat -p apps/android :app:connectedDebugAndroidTest
```

提供商交互参考了 [Agora](https://github.com/newo-ether/Agora) 的配置和模型发现流程，协议边界参考了 [opencode](https://github.com/anomalyco/opencode)。没有复制 Agora 的 GPL 实现。平台依据：[NativeAOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)、[Android/bionic 状态](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/android-bionic.md)、[Android 应用语言](https://developer.android.com/guide/topics/resources/app-languages)、[Compose Material 3](https://developer.android.com/develop/ui/compose/designsystems/material3)、[自适应图标](https://developer.android.com/develop/ui/compose/system/icon_design_adaptive)、[Apple UI 写作](https://developer.apple.com/design/human-interface-guidelines/writing)。
