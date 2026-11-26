# 技能与设置

## Skills

技能声明 `{name, title?, description, agents, instructions?, tools?, hooks?}`。技能可以有多个工具，也可以只提供指令。每个工具都声明在所属技能的 `tools` 中。 `agents` 可包含 `chat`、`env`、`preset`、`search`、`fetch`、`temporary`。内置 base 插件还为 `plugin` 角色提供技能。工具声明为 `{name, title?, description, parameters, handler, returnType?, presentation?}`，`parameters` 是顶层 `type:"object"` 的 JSON Schema，`handler` 是本包 Hook URL。工具名在模型侧是 `plugin_<id>_<name>`，长度最多 64，同一插件的工具名全局唯一。

可给技能和工具提供面向用户的 `title`。在 `i18n.locales` 中使用 `/skills/0/title` 或 `/skills/0/tools/0/title` 翻译标题。模型调用、插件 Action 和归档继续使用稳定的 `name`。

智能体先看到技能说明，通过 `activate_skill` 激活后，下一次模型请求才包含这个技能的工具。模型调用工具时，宿主调用其声明的 Action，并提供 `event.name = "skill.tool"` 以及 `skill`、`tool`、`arguments`。插件也可以通过 `actions.invoke` 调用同一个 Action URL。此时处理器收到 `actions.invoke` 事件，按普通 Action 授权处理。`skills[].hooks` 可绑定 `skill.activated`、`turn.started`、`turn.finished`、`tool.started`、`tool.finished`。回调值统一写本包 Actions Hook URL。

`returnType` 默认是 `"json"`。声明 `"asset-url"` 时，处理器直接返回一个 Asset URL 字符串或字符串数组，例如 `return [api.assets.url("report"), api.assets.url("cover")]`。宿主检查插件能否访问每个资源，向调用该工具的智能体会话 授权，并在每次读取时复查签名、可见性和预设作用域。

消息附件和工具返回的资源都以 Asset URL 交给智能体。消息附件的 URI 形如 `minirp://assets/minirp.attachments/<id>`。模型可以激活 `assets`，也可把 URL 原样填入其他技能工具的参数。接收工具通过 `api.assets.open(url)` 或 `api.assets.read(url)` 读取本次传入的资源。工作区附件和经智能体转交的资源授权都限于本次工具调用。工具可以声明 `returnType: "asset-url"` 并把已传入的 URL 返回给同一智能体。

宿主按会话能力公开工具。返回 Asset URL 的工具出现在有 Asset 读取权限的会话中。生成卡片的工具出现在可显示卡片的 Chat、Env 和 Preset 会话中，调用时会重新核对插件是否仍然安装并对当前预设可用。

- `view_url_metadata` 查询 URL 的包名、资源 ID、来源、MIME 类型、已知字节数和可监听状态。
- `watch_asset({url})` 订阅当前会话有权读取且声明 `listenable: true` 的 Asset。工具返回声明的事件源。用 `unwatch_asset({url})` 取消订阅。
- `grep` 搜索原始文本或附件处理器提取的文本。`pattern` 默认使用 ripgrep 正则语法，`literal: true` 匹配原文。结果给出行号、匹配列和上下文。`from_line` 从 1 开始，可用 `next_line` 继续搜索。
- `raw_grep` 搜索原始字节。每行最多 32 个字节，字节间用空格，每 8 个字节用 ` | ` 分组。正则匹配显示出的单行 HEX。`literal: true` 把 `50 4B 03 04` 一类输入当作连续字节，可跨行和流分块。`from_offset`、`next_offset` 和 `match_offset` 都以原始字节计，从 0 开始。
- `view_image` 查看图片，`view_video` 和 `view_audio` 查看完整音视频。完整视频通过 Gemini 的媒体输入发送。
- `view_video_frame` 用 `start_ms` 取视频帧。`view_video_audio_clip` 用 `start_ms` 和 `duration_ms` 取视频音轨，默认从开头取 10 秒，单次最多 30 秒。`view_pdf_page` 渲染指定的 `page`，页号从 1 开始。
- `view_pdf_metadata`、`view_image_metadata` 和 `view_video_metadata` 分别查看 PDF、图像和视频的文件属性。返回字段随平台和文件可用属性而变化。

两个搜索工具可用 `context` 请求最多前后各 5 行。`limit` 默认 20 条匹配行，结果还受输出长度限制。用 `pattern: "^"` 可以连续浏览文本或 HEX。

图片、视频帧、PDF 页面和 OpenUI 卡片截图的最长边最多 4096 像素。查看工具接受可选的 `viewport: {x, y, width, height}`，坐标从源图左上角起算。图片、视频帧和卡片用源像素，PDF 用页面点。先裁切或直接渲染该区域，再限制输出尺寸。`source_width` 和 `source_height` 给出裁切前的坐标范围。PDF 整页保留 1400 像素的预览尺寸，选区可渲染到 4096 像素；卡片的默认截图沿用较小的预览尺寸。

`assets` 可修改当前会话获得授权的可写 Asset。第三方插件在签名清单中声明 `writable: true`，编辑写入当前预设的资源副本。编辑研究 Asset 会更新当前分支的版本。

所有可用 `assets` 的会话都能订阅资源，订阅按会话保存，包括临时会话。变更沿用 [Asset 变更事件](actions-assets.md#监听-asset-变更)，并在该会话下一次模型请求前附上通知。同一个 Asset 在两次请求之间多次变化时，通知使用最新一次变更。宿主投递时重新检查授权和预设作用域。空闲会话在下一次请求时接收通知。

`search` 提供 `search` 和 `open_url`，用于开启智能体搜索的 ChatAgent，以及 EnvAgent、PresetAgent 和 PluginAgent。`search-full` 额外提供 `real_search`、`real_fetch`、`virtual_search`、`cache_page` 和 `cache_search_results`，用于关闭智能体搜索的 ChatAgent、SearchAgent 和 BrowserAgent。同一轮只会分配其中一个技能。SearchAgent 用 `search-workflow` 提交给调用方，直接搜索的 ChatAgent 可缓存结果后继续作答。

BrowserAgent 用 `browser` 导航、查看可访问性树、操作页面和采集 Markdown。抓取任务使用 `browser-fetch-workflow` 的 `finish_fetch`，结果页任务使用 `browser-search-workflow` 的 `finish_search`。两个工作流都提供 `fail_request`。

持久会话激活 `assets` 后也可管理会话自有文件。用 `create_asset` 指定文件名和可选 MIME 类型，从 UTF-8 文本、已授权的 Asset URL 或空内容创建可写文件。用 `list_assets`、`rename_asset`、`delete_asset` 管理会话文件，用 `text_edit`、`bin_patch` 或 `container.import_asset` 改写内容。回答可以用 `[下载报告](minirp://assets/minirp.agent/<uuid>)` 分享创建工具返回的实际 URL。回复里的链接保留发布时的文件名和版本。

`text_edit` 按 `old_string` 精确替换，遇到重复内容时需要更多上下文或显式 `replace_all`。`bin_patch` 在 `offset` 处核对 `old_hex` 后写入 `new_hex`，使用 `raw_grep` 的空格和 `|` 分组格式。两类可写 Asset 的相应版本都可随存档恢复。

每个查看工具都接收 `url`。兼容的模型会收到媒体内容，返回的 `sample_url` 可继续用作工具参数。宿主使用模型提供的输入模态声明和协议限制。未公布输入模态的模型沿用协议支持的类型。Android 用系统组件采样。桌面通过 PATH 上的 Poppler `pdftoppm`、`pdfinfo` 和 FFmpeg 的 `ffmpeg`、`ffprobe` 采样和读取元数据。

如果当前模型缺少图像或音频输入能力，并在设置中选了对应的描述模型，相应的查看工具会返回 `source_url` 和文字 `description`。

## 工具轨迹展示

可给 `skills[].tools[]` 增加 `presentation`，在 Android 和 WebUI 的工具轨迹中显示只读 OpenUI 卡片。`arguments` 和 `result` 分别指定参数和结果卡片，可以只填一项。它们的值是同一份签名清单顶层 `cards` 中的 `id`，可以与用于普通回复的卡片分别声明。用于轨迹展示的卡片省略 `state` 字段。

例如，`report` 工具声明 `"presentation": {"arguments":"report-input", "result":"report-output"}`。在同一清单中声明对应卡片。

```json
{
  "cards": [
    {"id":"report-input", "title":"上报的状态", "program":"root = Col([Title($title, \"md\"), Text($data.line)])"},
    {"id":"report-output", "title":"已保存的状态", "program":"root = Col([Title($title, \"md\"), Text($data.value.line)])"}
  ]
}
```

- `$data` 是工具轨迹中的本次参数或结果。JSON 解析后按对象、数组、数字、布尔值、字符串或 `null` 传入。返回普通文本的工具收到字符串。示例的参数包含 `line`，结果包含 `value.line`。
- `$tool` 是宿主提供的完整工具名，例如 `plugin_room-status_report`。`$phase` 是 `arguments` 或 `result`。
- `$title` 是按当前语言解析后的卡片标题，可在程序中直接显示。`$locale` 是本次展示请求的语言标签，未提供时为空字符串。
- 在卡片声明中提供 `labels` 后，通过 `$labels.<key>` 读取翻译后的字段标题和提示。普通回复卡片使用同一份[卡片本地化契约](state.md#卡片本地化)。
- 轨迹组件包括 `Badge`、`Card`、`Chart`、`Col`、`Divider`、`Row`、`Table`、`Text`、`Title` 和 `ToolFacts`。只读页面禁用动作交互，内容安全策略阻止网络、图片和 Asset 加载以及 Worker。可以使用纯表达式计算显示值。把参数和结果作为卡片数据传入，保持签名卡片的 OpenUI 程序固定。
- 模型收到原有的工具定义和返回值。工具轨迹保留参数和结果的原始文本供复制。未声明展示、卡片不可用、渲染失败、工具返回错误或数据超出展示限制时，宿主显示原有文本或 JSON。

测试时从真实聊天调用工具。展开轨迹并分别检查参数、结果和原始数据复制。再触发一次错误，或构造一次无法渲染的卡片，确认原始文本仍可读、可复制。用窄窗口或手机复查较长的字段。完整清单见 [航站状态示例](../../examples/room-status/plugin/manifest.json)。

`ToolFacts(rows, status, note, more, less)` 用于只读轨迹。提供最多 18 个 `{label, value, mono?}` 形式的字段。标题和取值使用纯文本字符串，`mono` 可将取值设为等宽字体。文本可选择，长字段可单独展开。`status` 和 `note` 为空时隐藏，`more` 和 `less` 是展开与收起的文案。可以使用卡片的 `$labels` 为这些文字提供译文。

## 设置

`settings.get({key})` 需 `settings` 权限，读取本包声明的可存值节点，返回字符串、布尔值或默认的 `null`。用户写入由宿主处理，变更触发本包的 `settings.changed`。

### Secret Storage

在签名清单的 `permissions` 中声明 `secrets`。用普通设置保存密钥 ID，运行时通过 `api.secretStorage` 读取密钥文本。ID 使用 1–128 个小写 ASCII 字母、数字或连字符。

- `getSecret(id)` 返回字符串，缺少该 ID 时返回 `null`。
- `setSecret(id, secret)` 写入或替换字符串，最多 16 KiB UTF-8。
- `listSecrets()` 按 ID 排序返回本插件保存的密钥名称。
- `deleteSecret(id)` 删除指定密钥，重复删除可以安全执行。

调用是异步的。密钥按工作区、包名和签名身份隔离，并遵循插件预设作用域。草稿预览无法访问已安装插件的密钥。密钥保存在本机，从预设与会话档案导出时会排除。密钥文本只在需要身份验证时读取。工具结果、Action 结果和普通日志应只包含完成操作所需的非敏感信息。

```js
const id = await api.settings.get({ key: "credential" });
const token = typeof id === "string" && id ? await api.secretStorage.getSecret(id) : null;
```

Base 的搜索和网页抓取设置使用同一存储，以服务名作为密钥 ID。Windows 使用当前用户的 DPAPI，Android 使用 Android Keystore，Linux 使用用户私有配置目录中的密钥加密。

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

节点使用全包唯一的 `key`，除 Markdown 外均填 `title`。通过 [清单本地化](runtime.md#本地化) 翻译标题、描述、内联 Markdown 和选项标题。现有 `titles` 语言映射继续可用。每组最多 50 项，嵌套最多 5 层。宿主内部设置绑定仅开放给内置插件。

Android 可用可选字段 `icon` 显示设置图标，例如 `"icon": "minirp://assets/example.minichat.roomstatus/settings-icon"`。URL 指向本包 `assets` 声明的 PNG、JPEG 或 WebP 资源，允许使用私有 Asset。静态文件与动态 Asset 都通过宿主的 Asset 流和预设作用域校验，Android 最多读取 256 KiB。
