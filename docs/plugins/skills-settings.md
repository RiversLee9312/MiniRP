# 技能与设置

## Skills

技能声明 `{name, description, agents, instructions?, tools?, hooks?}`。技能可以有多个工具，也可以只提供指令。每个工具都声明在所属技能的 `tools` 中。 `agents` 可包含 `chat`、`env`、`preset`、`search`、`fetch`、`temporary`。内置 base 插件还为 `plugin` 角色提供技能。工具声明为 `{name, description, parameters, handler, returnType?}`，`parameters` 是顶层 `type:"object"` 的 JSON Schema，`handler` 是本包 Hook URL。工具名在模型侧是 `plugin_<id>_<name>`，长度最多 64，同一插件的工具名全局唯一。

Agent 先看到技能说明，通过 `activate_skill` 激活后才看到技能工具。技能工具调用时 `event.name` 为 `skill.tool`，另有 `skill`、`tool`、`arguments`。 `skills[].hooks` 可绑定 `skill.activated`、`turn.started`、`turn.finished`、`tool.started`、`tool.finished`。回调值统一写本包 Actions Hook URL。

`returnType` 默认是 `"json"`。声明 `"asset-url"` 时，处理器直接返回一个 Asset URL 字符串或字符串数组，例如 `return [api.assets.url("report"), api.assets.url("cover")]`。宿主检查插件能否访问每个资源，向调用该工具的 Agent session 授权，并在每次读取时复查签名、可见性和预设作用域。

消息附件和工具返回的资源都以 Asset URL 交给 Agent。消息附件的 URI 形如 `minirp://assets/minirp.attachments/<id>`。模型可以激活 base 的 `view_asset`，也可把 URL 原样填入其他技能工具的参数。接收工具通过 `api.assets.open(url)` 或 `api.assets.read(url)` 读取本次传入的资源。工作区附件和经 Agent 转交的资源授权都限于本次工具调用。工具可以声明 `returnType: "asset-url"` 并把已传入的 URL 返回给同一 Agent。

宿主按会话能力公开工具。返回 Asset URL 的工具出现在有 Asset 读取权限的会话中。生成卡片的工具出现在可显示卡片的 Chat、Env 和 Preset 会话中，调用时会重新核对插件是否仍然安装并对当前预设可用。

- `plugin_base_view_url_metadata` 查询 URL 的包名、资源 ID、来源、MIME 类型和已知字节数。
- `plugin_base_view_text` 分页读取文本或附件处理器提取的文本。默认 6,000 个字符，每页最多 16,000 个。用 `next_offset` 继续读。
- `plugin_base_view_image`、`plugin_base_view_video` 和 `plugin_base_view_audio` 查看完整媒体。完整视频通过 Gemini 的媒体输入发送。
- `plugin_base_view_video_frame` 用 `start_ms` 取视频帧。`plugin_base_view_video_audio_clip` 用 `start_ms` 和 `duration_ms` 取视频音轨，默认从开头取 10 秒，单次最多 30 秒。`plugin_base_view_pdf_page` 渲染指定的 `page`，页号从 1 开始。
- `plugin_base_view_pdf_metadata`、`plugin_base_view_image_metadata` 和 `plugin_base_view_video_metadata` 分别查看 PDF、图像和视频的文件属性。返回字段随平台和文件可用属性而变化。

每个查看工具都接收 `url`。兼容的模型会收到媒体内容，返回的 `sample_url` 可继续用作工具参数。宿主使用模型提供的输入模态声明和协议限制。未公布输入模态的模型沿用协议支持的类型。Android 用系统组件采样。桌面通过 PATH 上的 Poppler `pdftoppm`、`pdfinfo` 和 FFmpeg 的 `ffmpeg`、`ffprobe` 采样和读取元数据。
## 设置

`settings.get({key})` 需 `settings` 权限，读取本包声明的可存值节点，返回字符串、布尔值或默认的 `null`。用户写入由宿主处理，变更触发本包的 `settings.changed`。

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
