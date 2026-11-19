# 技能与设置

## Skills

技能声明 `{name, description, agents, instructions?, tools, hooks?}`。 `agents` 可包含 `chat`、`env`、`preset`、`search`、`fetch`、`temporary`。内置 base 插件还使用 `plugin` 角色为 PluginAgent 提供开发工具。每个工具为 `{name, description, parameters, handler, returnType?}`，其中 `parameters` 是顶层 `type:"object"` 的 JSON Schema，`handler` 是本包 Hook URL。工具名在模型侧是 `plugin_<id>_<name>`，长度最多 64，同一插件的工具名全局唯一。

Agent 先看到技能说明，通过 `activate_skill` 激活后才看到技能工具。技能工具调用时 `event.name` 为 `skill.tool`，另有 `skill`、`tool`、`arguments`。 `skills[].hooks` 可绑定 `skill.activated`、`turn.started`、`turn.finished`、`tool.started`、`tool.finished`。回调值统一写本包 Actions Hook URL。

`returnType` 默认是 `"json"`。声明 `"asset-url"` 时，处理器直接返回一个 Asset URL 字符串或字符串数组，例如 `return [api.assets.url("report"), api.assets.url("cover")]`。宿主检查插件能否访问每个资源，向调用该工具的 Agent session 授权，并在每次读取时复查签名、可见性和预设作用域。

Agent 激活 `assets` 后可调用 `read_asset({url, mode?, offset?, length?, page?, start_ms?, duration_ms?})`。每次可检查一个 URL 中的一页、一个帧或一个片段。

- `inspect` 默认返回 MIME 类型和已知长度。
- `text` 分页读取文本。默认 6,000 个字符，每页最多 16,000 个。用 `next_offset` 继续读。
- `image` 读取图片。 `pdf_page` 渲染 `page` 指定的单页，页号从 1 开始。 `video_frame` 采样 `start_ms` 对应的帧。 `audio_clip` 读取音频或视频音轨，默认从 0 开始取 10 秒，单次最多 30 秒。
- 媒体样本以附件提供给支持该样本格式的模型。Android 使用系统媒体组件。桌面需要 PATH 上的 Poppler `pdftoppm` 渲染 PDF，使用 FFmpeg 采样视频和音频。
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
