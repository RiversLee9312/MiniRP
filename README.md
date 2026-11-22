# MiniRP

**原生 Agentic 角色扮演。用一句话交代你想玩什么。**

在 MiniRP 里，你可以把设定文档或角色卡发进对话，也可以直接描述脑海里的世界。智能体会读取资料、搭建预设、操作工具并保存结果。遇到需要你决定的玩法，它会在对话里问你。

[下载 Android、Windows 和 Linux 版](https://github.com/RiversLee9312/MiniRP/releases) · [预设玩法](docs/presets/workflows.md) · [插件开发](docs/plugins/getting-started.md)

## 从一句话开始

- **把设定变成可玩的预设。**「把这张角色卡和我的设定文档导进去，开场让我在码头收到一封匿名信。」PresetAgent 读取附件，整理角色、世界规则和故事开场。
- **让剧情有资料可依。**「查一下灯塔守夜人以前是怎么值班的，用查到的细节来写今晚的巡查。」ChatAgent 可以让 SearchAgent 查找来源，必要时由 FetchAgent 打开网页读取正文。
- **把剧情变化记进世界。**「港口停电了，把这条事实记下来，推进到下一班岗。」在世界面板发给 EnvAgent，它会更新当前世界的状态。
- **当场做出能用的界面。**「做一张补给计算卡，改人数和天数时马上算出要带多少食物。」Android 对话里可以生成可直接操作的 OpenUI 卡片。
- **给预设加一项长期玩法。**「给这个预设做一个线索板，把找到的线索保存下来。」PresetAgent 可以委派 PluginAgent 开发和调试，并按你的要求为该预设部署。

展开工具轨迹就能看到智能体查了哪些来源、用了哪些工具。你可以随时取消当前任务。想尝试另一条剧情时，可以从已有对话分支，带上当时的世界和智能体状态一起继续。

## 安装

Android ARM64 下载 `MiniRP.apk`，可直接覆盖升级。Windows x64 解压 `MiniRP-Windows-x64.zip` 后运行 `minirp.exe`。Linux x64 解压 `MiniRP-Linux-x64.tar.gz` 后运行 `./minirp`。桌面端可传入旧工作区路径，继续使用已有对话、预设和模型配置。旧的 `MINICHAT_*` 配置键仍可读取，新配置建议使用 `MINIRP_*`。

## 智能体如何分工

- **ChatAgent**：主对话，调用 agentic 搜索和网页工具；system prompt 可按预设配置，支持 `{date}`。
- **EnvAgent**：根据对话、已确认的事实和预设规则判断何时推进剧情。它可以编辑事实、世界状态和剧情日期，完成后提交修改。
- **PresetAgent**：用精确工具编辑预设，也能读取设定文档和角色卡，整理角色、世界规则和开场。Android 在预设对话中用文件选择器发送，桌面打开预设面板后用 `/attach`。未完成的预设打开时会继续进入 PresetAgent，直到它调用工具完成设置。
- **SearchAgent**：每轮完成一次搜索或网页请求，自主决定是否搜索、搜索多少轮、抓取哪些页面，以及召回、编辑和缓存哪些材料。最终结果数量不设上限。
- **FetchAgent**：临时浏览器智能体，操作网页、采集全文、在 DB 中精修 Markdown，再提交正文和单独的摘要。
- **PluginAgent**：接手 PresetAgent 委派的插件开发，编写、调试、签名和部署属于该预设的插件。

智能体共用 `AgentSession` 的流式事件、工具循环、取消、上下文压缩和记忆工具。UI 展开/折叠只影响展示，不修改模型请求中的思考设置。

ChatAgent、PresetAgent 和 PluginAgent 可激活 `container` 技能运行 x86-64 Alpine 命令，并在 Asset 和客户机文件间传递数据。同一预设下的 PresetAgent 和 PluginAgent 共用 Asset URL 和容器工作目录。Android 和 Windows x64 随包提供运行时和镜像，工作目录随分支和存档迁移。构建前准备同一份 [智能体容器镜像和两端运行时](docs/project/agent-container.md)。

## 运行桌面版

同一个 `minirp` 程序提供终端 UI 和 WebUI。直接运行进入终端，在输入框用 `/web` 切换到浏览器。运行 `minirp --web` 可直接进入 WebUI，加 `--no-open` 只打印访问链接。WebUI 使用每次启动生成的本机回环端口和访问链接，Ctrl+C 停止宿主。TUI 与 WebUI 共用指定工作区。

构建需要 .NET SDK 10.0.400 和 Node.js 24。Windows 另需 Visual Studio 的 C++ 桌面生成工具。Linux 或 WSL 需要 clang、zlib 开发包和标准 C/C++ 链接工具。本地网页抓取在 Windows 使用 Chrome，在 Linux 使用 Playwright Chromium。使用 API 或缓存时无须启动抓取浏览器。

桌面版的图像和音视频转换使用 PATH 中的 `ffmpeg`、`ffprobe`，PDF 元数据和页面渲染使用 `pdfinfo`、`pdftoppm`，从图片生成 PDF 使用 `img2pdf`。文本读取和 Asset URL 传递可以独立使用。

```powershell
pwsh -File eng/build-desktop.ps1
.\artifacts\desktop\win-x64\minirp.exe .
.\artifacts\desktop\win-x64\minirp.exe --web .
```

在 Linux 或 WSL 中构建和运行：

```sh
sh eng/build-desktop.sh
./artifacts/desktop/linux-x64/minirp .
./artifacts/desktop/linux-x64/minirp --web .
```

Windows 和 Linux 的构建脚本都可用 `MINIRP_WEB_PREBUILT` 指定已生成的静态资源目录。该目录需包含 WebUI 的 `index.html` 和桌面 OpenUI 的 `openui/index.html`。

Linux ARM64 的输出目录是 `linux-arm64`。构建会收录已准备的 Alpine Room 镜像和 Linux 独立 curl。用 `MINIRP_CONTAINER_IMAGE`、`MINIRP_QEMU_RUNTIME` 和 `MINIRP_CURL_RUNTIME` 指定其他位置。Linux 可用系统的 `qemu-system-x86_64`，镜像与 curl 的准备步骤见[智能体容器](docs/project/agent-container.md)。

Linux 的本地网页抓取默认使用 Playwright Chromium。从构建目录运行 `npx playwright install chromium` 安装对应版本，必要时先按 Playwright 提示装齐系统依赖。使用普通 Linux 用户运行抓取浏览器，以便启用 Chromium 沙盒。在 WSL 中可用 Windows 浏览器打开 WebUI，Linux 抓取仍由宿主侧浏览器完成。

首次运行时创建或导入一个预设。WebUI 在设置中管理模型和提供商，保存到工作区的 `.minirp-desktop.json`。终端的 `/config` 编辑环境提供商，保存在 `.env`。两个入口都会读取这些配置，旧的 `.env` 可参照 `.env.example`。API Key 字段会遮盖显示。

常用命令：

- `/env`、`/preset-agent`：打开相应侧栏；侧栏中输入 `/close` 关闭，再次打开保留记录。
- `/canon 修改要求`：向 EnvAgent 发送要求。也可以在环境面板直接输入消息，由 EnvAgent 按当前剧情和预设规则决定如何处理。
- `/presets`、`/chats`、`/new`：切换预设、加载对话或新建对话。切换预设后选择该预设下的对话。
- `/save 对话名`：保存并可选重命名；对话也会自动保存。
- `/models`：从配置的提供商读取模型列表。
- `/web`：在当前回复结束后切换到随程序打包的 WebUI。
- `/cot on`、`/cot off`：设置新思考块的默认展开状态；已有块仍可单独点击展开或折叠。
- `/memory 关键词`：检索当前智能体的记忆；省略关键词列出索引。
- `/stop`、`/quit`：取消当前任务或退出。

Enter 发送，Alt+Enter 换行。输入框随换行和软换行增高，最多八行，之后在框内滚动。命令提示固定在底部。网络、浏览器和智能体工作不在 UI 线程执行。

用 `npm ci` 安装仓库的 Playwright 依赖后，可运行 `node tests/desktop-web-smoke.mjs <minirp 路径>`，在隔离工作区验证打包后的 WebUI。测试使用本地模拟模型，默认使用 Playwright Chromium，也可通过 `MINIRP_TEST_BROWSER=chrome` 选择已安装的 Chrome。

## Android

当前提供 Android ARM64 构建。需要 Android Studio 或 JDK 21、SDK 37、NDK 28.2.13676358 和 Node.js 24。通过 `JAVA_HOME` 或 PATH 选择 JDK，通过 `ANDROID_HOME` 指定 Android SDK。Gradle 根据锁文件构建随 APK 分发的 OpenUI 资源。

```powershell
pwsh -File eng/build-android.ps1 -Install
```

连接多个设备时，先用 `adb devices` 查看序列号，再用 `-Device` 选择目标。加上 `-Optimized` 可生成使用调试签名、启用发布版代码和资源裁剪的 APK，便于覆盖安装验证。

输出 `artifacts/MiniRP.apk`。也可用 Android Studio 打开 `apps/android`；先运行 `eng/build-native.ps1 -Runtime linux-bionic-arm64` 生成本地库。

Android 的世界、预设和记忆面板打开即全屏。对话页支持流式 Markdown、可折叠的工具轨迹和附件；搜索轨迹缩进展示。长按支持分支的用户消息可从发送前的世界与智能体状态创建新对话。旧消息没有历史快照，不能分支。标题只根据主聊天生成，点击标题可编辑或重新生成。

预设可以给 ChatAgent 配置一条隐藏的开场系统消息。有开场的预设在首页使用「新建对话」按钮，创建后由 ChatAgent 生成欢迎语。详见 [为新对话设置开场](docs/presets/workflows.md#为新对话设置开场)。

ChatAgent 可按需激活 `interactive-ui` 技能，在 Markdown 中插入流式交互界面。支持图表、表格、表单、本地计算、筛选、图片和地图。可以试试「做一个可以调整人数和天数的旅行预算计算器」。界面随对话保存，单次运行期间的输入会在滚动和展开时保留。详见 [Android 生成式 UI](docs/android/generated-ui.md)。

首次打开 Android 版时，引导会带你添加提供商、获取模型列表并选好默认模型。点「我不懂」可复制模型服务或搜索与网页抓取的求助提示词，请助手查找当地可用的服务、核对订阅和 API 的费用，再按官方文档指导接入。

在「提供商」设置名称、协议、地址和 API 密钥；在「模型」自动获取可用模型、保存常用模型、为智能体和标题指定模型。对话输入框下也可切换该页相关的智能体模型。提供商配置存入 DataStore，密钥用 Android Keystore AES-GCM 加密。附件可选图片、音频、PDF 和视频，能否发送取决于当前协议及模型。对话抽屉使用 Paging 3 按预设分页，默认打开聊天列表，收起预设列表。

UI 文案使用 Android 字符串资源，支持英文及另外 29 种语言；语言列表由 AGP 自动生成，使用系统按应用语言设置，低版本通过 AppCompat 适配。支持 RTL，方向图标会镜像。图标使用 Google Fonts Material Symbols 官方子集；应用图标由 GPT-image 生成，前景安全区和来源记录保存在 `assets/branding`。

.NET 的 Android/bionic NativeAOT 目前仍是实验支持。Android 与桌面的网络和浏览器请求由各平台适配层执行，智能体编排、协议转换和存储由 C# 核心处理。项目已验证 Windows x64、WSL Linux x64 和 Android ARM64 的产物。Linux 和 macOS 核心使用各自的 NativeAOT 工具链。

## 提供商

原生协议包含 OpenAI Responses、OpenAI Chat Completions、Anthropic Messages、Gemini 和 Ollama。OpenAI 兼容配置覆盖 DeepSeek、OpenRouter、Groq、Mistral、xAI、Together、Fireworks、SiliconFlow、Alibaba Cloud、Moonshot、Z.ai、Cerebras、SambaNova、Perplexity、NVIDIA、GitHub Models、LM Studio，以及自定义地址。

Android 的提供商设置和桌面的 `/config` 都可以选择 [Coding Plan、Agent Plan 和模型订阅模板](docs/project/provider-plans.md)。

兼容程度取决于服务实际支持的模型、工具调用和思考参数。协议回归使用本地夹具验证，没有逐个使用真实服务密钥联调。平台专有的云账号登录和签名认证不在当前密钥配置范围内。

网页搜索支持 DeepSeek、Tavily、Exa 和 Brave Search。网页抓取支持 DeepSeek、Tavily 和 Exa。Android 和 WebUI 可在「设置 → 搜索和抓取 API → 供应商」分别选择两项服务并填写密钥。同一服务的密钥可用于搜索和抓取。DeepSeek 默认沿用已保存模型提供商的密钥。

终端可通过 `/config` 填写服务和密钥，也可用 `MINIRP_SEARCH_PROVIDER` 与 `MINIRP_FETCH_PROVIDER` 选择服务。密钥分别使用 `MINIRP_SEARCH_API_KEY`（DeepSeek）、`MINIRP_TAVILY_API_KEY`、`MINIRP_EXA_API_KEY` 或 `MINIRP_BRAVE_SEARCH_API_KEY`。

搜索和网页抓取密钥可选。`real_search` 在所选服务未配置、失败或没有结果时尝试 [Mwmbl 的免密钥搜索 API](https://developer.mwmbl.org/)。Mwmbl 也没有可用结果时，FetchAgent 用现有的本地浏览器顺序尝试搜索网站。中国大陆尝试百度和 Bing，其他地区尝试 Google、DuckDuckGo 和 Bing。所有来源沿用相同的搜索结果格式。默认网页抓取策略会在 API 不可用时使用本地浏览器 Fetch。

## 网页工作流

预设的 `agentic_search` 默认开启。关闭后由 ChatAgent 直接调用相同的真实与虚拟搜索工具，不调用 SearchAgent；EnvAgent 可以为当前世界补充 ChatAgent 的搜索指引。

`real_fetch(url, backend)` 保持同一工具接口：

- `api`：只调用所选网页抓取 API。
- `local`：显式使用临时 FetchAgent；可用于重抓疑似截断的页面。
- `api_then_local`：API 出错、超时或返回空正文时使用本地浏览器。
- `auto`：采用配置的默认策略。

API 没有可靠的完整性字段时记录为 `unknown`。本地采集不加正文字符上限：原文完整归档，编辑稿存入 `page/markdown`，长内容通过 DB 分页读取。摘要保存在独立字段。无法访问的嵌入框架会被标记，避免宣称已完整采集。

内置 base 插件的 `research` 技能提供真实搜索、抓取和缓存。候选项附有当前轮次的 `ref` 以及 `asset_url`。网页 Asset 提供 Markdown，搜索条目 Asset 提供 JSON。用 `view_asset` 查询全文，用 `edit_asset` 编辑，再按 `ref` 缓存或提交。缓存随对话分支和存档恢复。ChatAgent 的 `open_url` 同时返回 Markdown 和 Asset URL。

智能体可用 `asset_storage` 创建和管理会话文件，并在回答中提供 Markdown 下载链接。Android 的 OpenUI 卡片也支持 Asset 图片和下载链接。

调研后采用语义工具和稳定元素引用：`browser_navigate`、`browser_snapshot`、`browser_click`、`browser_type`、`browser_press_key`、`browser_scroll`、`browser_wait`，再用 `capture_page` / `finish_fetch` 提交。桌面复用官方 Playwright MCP 和 C# MCP SDK；Android 用 WebView 实现相同平台接口。页面 JavaScript 仅由固定采集代码执行，模型没有宿主代码执行工具。

相关设计来源：[Playwright MCP](https://playwright.dev/mcp/introduction)、[快照和引用](https://playwright.dev/mcp/snapshots)、[agent-browser](https://github.com/vercel-labs/agent-browser)、[browser-use](https://docs.browser-use.com/open-source/tools)、[Microsoft UFO MCP](https://microsoft.github.io/UFO/mcp/overview/)、[OpenAI computer-use 驱动](https://developers.openai.com/api/docs/guides/tools-computer-use-sdk-drivers)。采用现有浏览器工具链，未另外维护 CEF 嵌入和分发层。

## 上下文与存储

ChatAgent、EnvAgent、SearchAgent 的会话和 SQLite DB 分别保存在 `conversations/<preset>/<chat>/agents`，彼此独立。PresetAgent 的会话和 DB 保存在 `preset_sessions/<preset>`，供该预设的所有聊天共享。临时 FetchAgent 的 DB 保存在该对话的 `fetch` 目录。Android 记忆面板按智能体显示、搜索和编辑普通笔记。

工具包括 `db_list`、`db_read`、`db_write`、`db_patch`、`db_delete`、`db_search`。索引是字符串；写入/删除校验 revision，修改使用精确文本替换。搜索采用 FTS5 trigram；短查询使用文本扫描，不使用向量和 RAG。分页以 UTF-16 偏移计数，并保留完整 Unicode 字符。

所有智能体都会被提示主动记录值得保留的信息，并及时更新过期笔记。`history_search` 和 `history_recall` 用于在自己的会话中搜索和分页读取原始消息，无需依赖模型的上下文。压缩滚动更新任务、事实、引用和 DB key 的摘要，在预算内保留当前请求、最近消息和成组的工具调用及结果。应用摘要时将被覆盖的历史和上一个检查点归档。世界进度和搜索缓存按聊天隔离，分支时恢复对应版本。

各智能体通过同一会话路径构建请求、估算输入和应用压缩。输入估算基于协议实际会发送的指令、消息、附件和工具定义。提供商返回输入 token 数时，后续请求沿用仍然匹配的用量基准，只估算新增内容。压缩、历史改写和模型切换会使旧用量基准失效。PresetAgent 用 `inspect_preset` 读取当前配置，预设快照留在预设存储中。

异步压缩默认开启。用量到上下文窗口的 70% 时后台生成检查点，到 80% 时校验并应用，接回这期间新增的消息。应用检查点后会按保留的内容重新估算。Android 在「设置 → 模型」的已保存模型旁可编辑两个阈值和异步开关。窗口优先取模型 API 返回的大小，缺少时使用提供商配置，调度会为模型输出预留空间。

旧 Python 格式的对话会自动导入，并把原档备份到对话目录的 `migration/python-v1.json`。旧 `world_state.json` 保留；导入时迁移该预设的世界进度和缓存。Python 运行时已从主线移除，最后可运行的 Python 检查点为 Git 提交 `24a52c2`。

### 导出与导入

- Android 可从普通聊天和群像聊天的「更多选项」导出 JSONL 转录。世界、预设和插件子智能体转录页的下载按钮也可导出。插件子智能体导出当前选中的运行。该转录中的回复或工具仍在生成时，等它完成或停止后再导出。桌面在当前对话或面板的输入框运行 `/export-transcript 文件名.jsonl`。
- JSONL 使用 UTF-8。首行记录转录类型和来源，之后每行记录一条消息、思考过程、工具调用与回执、卡片或子智能体状态。附件只附带文件名与媒体类型。导出的内容可能包含私人对话，分享前请检查。
- 在 Android 聊天列表长按 Room 导出单个 Room，群像 Room 包含其中所有聊天。
- 在 Android 预设列表长按预设可导出预设或存档。预设文件包含配置和归属插件。存档还包含该预设的 Room、会话、附件、分支快照和作用域内的插件数据。
- 在 Android 或桌面 WebUI 打开预设选择器，从底部的「导入…」选择文件类型或查看说明。
- 桌面可在选定预设或 Room 后运行 `/export-room <文件>`、`/export-preset <文件>`、`/export-save <文件>`。恢复使用 `/import-preset <文件>` 或 `/import-save <文件>`。

导入保留原 UUID 和插件签名。标识相同且内容冲突时停止导入，保留本地数据。分享存档可能会分享私人聊天和附件。应用的提供商凭据、插件签名私钥和未发布草稿不随包导出。

## 插件

Android 和桌面使用同一种签名 `.plugin` 包，每个插件复用自己的 V8 isolate。清单声明 Actions、Assets、Skill、附件和设置。Actions 通过 URL 投递事件，Assets 以 WHATWG 字节流读取，跨包访问按资源可见性和签名证书校验。内置 `base` 插件提供应用设置、预设创建引导、开源许可证页和 `view_asset` 技能。智能体通过 Asset URL 接收附件和工具资源，按需调用 `view_asset` 查看内容。

[开发、签名与安装](docs/plugins/getting-started.md) · [完整 API 参考](docs/plugins/README.md) · [插件 SDK](sdk/javascript/) · [示例插件](examples/stream-assets/plugin/) · [MIT 许可证](LICENSE)

## 代码与验证

MiniRP 的核心使用 .NET 10 和 C#。桌面使用 Terminal.Gui，并通过 ASP.NET Core 提供随包的 React WebUI。WebUI 与 Android 共用翻译资源和 OpenUI 卡片渲染器。Android 使用 Kotlin 和 Jetpack Compose Material 3 Expressive。宿主通过 UTF-8 C ABI 调用同一个 NativeAOT 核心。

[项目结构](docs/project/README.md)列出 HarnessCore、桌面、Android、测试、SDK 和构建入口。

NativeAOT 核心通过 `LibraryImport` 生成 P/Invoke，JSON 使用 source generator，SSE 使用 .NET 自带解析器。C ABI 用 `mc_create`、`mc_dispatch`、`mc_poll`、`mc_release`、`mc_destroy` 管理会话和事件，用 `mc_binary_take` 和 `mc_binary_write` 传输原始字节。

```powershell
dotnet run --project tests/MiniRP.Core.Tests
.\apps\android\gradlew.bat -p apps/android :app:connectedDebugAndroidTest
```
