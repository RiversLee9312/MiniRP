# 技能与设置

## Skills

技能声明 `{name, description, agents, instructions?, tools?, hooks?}`。技能可以有多个工具，也可以只提供指令。每个工具都声明在所属技能的 `tools` 中。 `agents` 可包含 `chat`、`env`、`preset`、`search`、`fetch`、`temporary`。内置 base 插件还为 `plugin` 角色提供技能。工具声明为 `{name, description, parameters, handler, returnType?}`，`parameters` 是顶层 `type:"object"` 的 JSON Schema，`handler` 是本包 Hook URL。工具名在模型侧是 `plugin_<id>_<name>`，长度最多 64，同一插件的工具名全局唯一。

智能体先看到技能说明，通过 `activate_skill` 激活后，下一次模型请求才包含这个技能的工具。模型调用工具时，宿主调用其声明的 Action，并提供 `event.name = "skill.tool"` 以及 `skill`、`tool`、`arguments`。插件也可以通过 `actions.invoke` 调用同一个 Action URL。此时处理器收到 `actions.invoke` 事件，按普通 Action 授权处理。`skills[].hooks` 可绑定 `skill.activated`、`turn.started`、`turn.finished`、`tool.started`、`tool.finished`。回调值统一写本包 Actions Hook URL。

`returnType` 默认是 `"json"`。声明 `"asset-url"` 时，处理器直接返回一个 Asset URL 字符串或字符串数组，例如 `return [api.assets.url("report"), api.assets.url("cover")]`。宿主检查插件能否访问每个资源，向调用该工具的智能体会话 授权，并在每次读取时复查签名、可见性和预设作用域。

消息附件和工具返回的资源都以 Asset URL 交给智能体。消息附件的 URI 形如 `minirp://assets/minirp.attachments/<id>`。模型可以激活 base 的 `view_asset`，也可把 URL 原样填入其他技能工具的参数。接收工具通过 `api.assets.open(url)` 或 `api.assets.read(url)` 读取本次传入的资源。工作区附件和经智能体转交的资源授权都限于本次工具调用。工具可以声明 `returnType: "asset-url"` 并把已传入的 URL 返回给同一智能体。

宿主按会话能力公开工具。返回 Asset URL 的工具出现在有 Asset 读取权限的会话中。生成卡片的工具出现在可显示卡片的 Chat、Env 和 Preset 会话中，调用时会重新核对插件是否仍然安装并对当前预设可用。

- `plugin_base_view_url_metadata` 查询 URL 的包名、资源 ID、来源、MIME 类型和已知字节数。
- `plugin_base_grep` 搜索原始文本或附件处理器提取的文本。`pattern` 默认使用 ripgrep 正则语法，`literal: true` 匹配原文。结果给出行号、匹配列和上下文。`from_line` 从 1 开始，可用 `next_line` 继续搜索。
- `plugin_base_raw_grep` 搜索原始字节。每行最多 32 个字节，字节间用空格，每 8 个字节用 ` | ` 分组。正则匹配显示出的单行 HEX。`literal: true` 把 `50 4B 03 04` 一类输入当作连续字节，可跨行和流分块。`from_offset`、`next_offset` 和 `match_offset` 都以原始字节计，从 0 开始。
- `plugin_base_view_image` 查看图片，`plugin_base_view_video` 和 `plugin_base_view_audio` 查看完整音视频。完整视频通过 Gemini 的媒体输入发送。
- `plugin_base_view_video_frame` 用 `start_ms` 取视频帧。`plugin_base_view_video_audio_clip` 用 `start_ms` 和 `duration_ms` 取视频音轨，默认从开头取 10 秒，单次最多 30 秒。`plugin_base_view_pdf_page` 渲染指定的 `page`，页号从 1 开始。
- `plugin_base_view_pdf_metadata`、`plugin_base_view_image_metadata` 和 `plugin_base_view_video_metadata` 分别查看 PDF、图像和视频的文件属性。返回字段随平台和文件可用属性而变化。

两个搜索工具可用 `context` 请求最多前后各 5 行。`limit` 默认 20 条匹配行，结果还受输出长度限制。用 `pattern: "^"` 可以连续浏览文本或 HEX。

图片、视频帧、PDF 页面和 OpenUI 卡片截图的最长边最多 4096 像素。查看工具接受可选的 `viewport: {x, y, width, height}`，坐标从源图左上角起算。图片、视频帧和卡片用源像素，PDF 用页面点。先裁切或直接渲染该区域，再限制输出尺寸。`source_width` 和 `source_height` 给出裁切前的坐标范围。PDF 整页保留 1400 像素的预览尺寸，选区可渲染到 4096 像素；卡片的默认截图沿用较小的预览尺寸。

base 的 `edit_asset` 技能可修改当前会话获得授权的可写 Asset。第三方插件在签名清单中声明 `writable: true`，编辑写入当前预设的资源副本。base 的 `research` 技能在 ChatAgent 直连搜索和 SearchAgent 的研究轮次中提供 `real_search`、`real_fetch`、`virtual_search`、`cache_page` 和 `cache_search_results`。编辑研究 Asset 会更新当前分支的版本。

持久会话可激活宿主的 `asset_storage` 技能。用 `create_asset` 指定文件名和可选 MIME 类型，从 UTF-8 文本、已授权的 Asset URL 或空内容创建可写文件。用 `list_assets`、`rename_asset`、`delete_asset` 管理会话文件，用 `edit_asset` 或 `container.import_asset` 改写内容。回答可以用 `[下载报告](minirp://assets/minirp.agent/<uuid>)` 分享创建工具返回的实际 URL。回复里的链接保留发布时的文件名和版本。

`text_edit` 按 `old_string` 精确替换，遇到重复内容时需要更多上下文或显式 `replace_all`。`bin_patch` 在 `offset` 处核对 `old_hex` 后写入 `new_hex`，使用 `raw_grep` 的空格和 `|` 分组格式。两类可写 Asset 的相应版本都可随存档恢复。

每个查看工具都接收 `url`。兼容的模型会收到媒体内容，返回的 `sample_url` 可继续用作工具参数。宿主使用模型提供的输入模态声明和协议限制。未公布输入模态的模型沿用协议支持的类型。Android 用系统组件采样。桌面通过 PATH 上的 Poppler `pdftoppm`、`pdfinfo` 和 FFmpeg 的 `ffmpeg`、`ffprobe` 采样和读取元数据。
## 设置

`settings.get({key})` 需 `settings` 权限，读取本包声明的可存值节点，返回字符串、布尔值或默认的 `null`。用户写入由宿主处理，变更触发本包的 `settings.changed`。

Android 和桌面使用 HarnessCore 内置的同一份 `minirp.base` 设置声明。分组、页序、抓取选项和开源许可由该声明提供。模型、提供商及平台专属设置通过声明中的链接打开相应宿主编辑器。

| 节点 `type` | 字段与行为 |
| --- | --- |
| `group` | 用 `items` 声明同一标题下的子节点。 |
| `page` | 用 `items` 声明可独立打开的子页面。 |
| `boolean` | `default` 为布尔值，显示开关。 |
| `choice` | `options:[{value,title}]`，1–20 项。`default` 可指定默认选项值。 |
| `text` | `default` 为字符串，`multiline:true` 启用多行，最大 16000 字符。 |
| `link` | `url` 为 Actions Hook URL，点击投递对应 Action。 |
| `markdown` | `content` 指向 MIME 为 `text/markdown` 的 Asset ID，也可用 `markdown` 填写内联文本。Android 页面加载上限 256 KiB。 |

节点使用全包唯一的 `key`，除 Markdown 外均填 `title`，可选 `titles` 提供 BCP 47 语言映射。每组最多 50 项，嵌套最多 5 层。宿主内部设置绑定仅开放给内置插件。

Android 可用可选字段 `icon` 显示设置图标，例如 `"icon": "minirp://assets/example.minichat.roomstatus/settings-icon"`。URL 指向本包 `assets` 声明的 PNG、JPEG 或 WebP 资源，允许使用私有 Asset。静态文件与动态 Asset 都通过宿主的 Asset 流和预设作用域校验，Android 最多读取 256 KiB。
