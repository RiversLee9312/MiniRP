# Minichat 插件 API v2

[开发、签名与安装](plugin-development.md) · [项目结构](architecture.md) · [SDK 类型](../sdk/javascript/index.d.ts) · [清单 JSON Schema](../sdk/javascript/manifest.schema.json)

## 生命周期与执行

- 启动时宿主加载已安装包，验证签名，随后投递 `app.ready`。内置插件随应用发布，拥有保留身份。安装和更新保存原始包，然后投递 `plugin.enabled`。同一已安装包只接受原证书签名的更新。卸载停止分发并取消该插件正在进行的调用，保留按包名和证书指纹隔离的 DB。当前没有卸载 Hook。
- 每个已加载插件按需创建一个 V8 isolate。入口脚本每次 isolate 初始化执行一次，定义 `globalThis.handlers`。同一 isolate 内可以复用全局缓存和内存状态，升级、卸载、应用退出或 isolate 因异常重建时会丢失它们。
- 每个插件有独立且有界的事件队列。普通 Hook 和 Actions 按入队顺序运行。技能工具、附件处理和动态资源由各自的调用方等待，可以在同一 isolate 内和普通 Hook 交错执行。每次调用拥有自己的参数、环境和待完成 Promise。单次 V8 执行仍是单线程，两个调用可在 `await` 边界交错。
- V8 步骤和宿主 I/O 在后台运行。一个插件等待模型、文件选择或网络时，别的插件照常接收事件。普通 Hook 有 90 秒调用期限。动态资源在首次拉取时执行，生成期限为 5 分钟，取消时关闭流。单次 V8 步骤有 2 秒执行限制。一个插件超限可能导致其 isolate 重建。队列容量 128，宿主流式事件可能被丢弃，Actions 有明确的接受回执。
- JavaScript 基线是 ECMAScript 2022，宿主提供 WHATWG Streams。可以用原生 `Uint8Array`、`ArrayBuffer`、`DataView` 和 Promise。运行环境是内嵌 V8，可用能力以本文所列为准。HTTP、文件选择、模型和持久化通过各自的宿主 API 完成。

## 清单

`manifest.json` 位于 ZIP 根目录，UTF-8 编码，只读，由包签名覆盖。完整示例见 [示例清单](../examples/stream-assets/plugin/manifest.json)。

| 字段 | 类型和含义 |
| --- | --- |
| `id` | 宿主 UI、CLI 命令和技能工具前缀使用的短 ID。小写字母开头，允许小写字母、数字、连字符和下划线，至多 48 字符。 |
| `packageName` | URL 和签名身份使用的反域名，如 `com.example.notes`。各段以小写字母开头，允许小写字母、数字和下划线。总长至多 150 字符。`minichat.*` 保留。 |
| `name` | 安装界面显示的名称，1–120 字符。 |
| `apiVersion` | 宿主契约版本，当前为 `2`。 |
| `entry` | ZIP 内入口脚本路径，扩展名为 `.js`，UTF-8，最大 2 MiB。 |
| `permissions` | 可选。声明 `db`、`harness.read`、`harness.write`、`http`、`model`、`picker`、`settings`。 |
| `network` | 使用 HTTP 时列出精确 HTTP(S) origin，例如 `https://example.com`。路径、通配符和自动重定向不参与授权。 |
| `actions` | `handlers` 把 Hook ID 映射到 JS 导出名，`events` 声明本包的事件源 ID，`subscriptions` 将事件源 URL 映射到本包的 Hook URL。 |
| `hooks` | 宿主事件名到本包 Hook URL 的映射。 |
| `assets` | 资源声明数组，详见 Assets。 |
| `skills` | Agent 技能声明数组，详见 Skills。 |
| `attachments` | 附件类型声明数组。 |
| `settings` | 设置节点数组。 |

Hook 的实现以 `handlers[导出名](event, api)` 被调用，可返回 Promise。清单中只有 `actions.handlers` 填写 JS 导出名。宿主事件、技能 Hook、技能工具、附件类型和动态 Asset 里的回调均使用本包的 Hook URL。

`api.environment` 是调用时的只读快照，可能含有 `session`、`chat`、`preset`、`agent`。无上下文的字段可能为 `null` 或缺失，例如 `app.ready` 没有会话。上下文字段与 DB 作用域一一对应。Chat、Env、Search 分别属于当前对话，Preset 属于该预设，Fetch 和临时 Agent 按任务建立各自的 session。

用户可在插件管理中设置预设作用域。第三方插件默认在所有预设中运行，可仅启用所选预设，也可排除所选预设。仅启用所选预设时，插件也会跳过无预设上下文的事件。排除模式仍接收无预设上下文的事件。内置插件固定为全局。宿主保存此配置，签名包及其 `manifest.json` 保持只读。

作用域适用于事件、技能、附件、设置和通过 URL 调用的 Hook 与 Asset。插件使用 Harness API 显式指定预设时，宿主也检查目标预设。`harness.listPresets()` 按作用域分页。预设删除后，插件的作用域不会自动扩大。

## Actions

- Hook URL 为 `minichat://actions/<packageName>/hooks/<id>`。
- 事件源 URL 为 `minichat://actions/<packageName>/events/<id>`。只有声明它的包可以发布。
- 订阅者和被调用方各用自己的权限。`event.sender` 由宿主填写，值为发起方包名。`event.url` 是本次调用的 Hook 或事件源 URL，`event.name` 分别为 `actions.invoke` 或 `actions.publish`。
- 二进制资源通过 `event.assets` 的命名 URL 引用传递。接收方打开 URL 时按自身身份校验访问权限。普通 JSON Hook 入参适用宿主各自的事件结构。

| API | 参数与结果 |
| --- | --- |
| `actions.invoke({url, assets?})` | 触发自己或其他插件声明的 Hook。返回 `{accepted:true}`，表示已入队。目标不可用、URL 无效、资源访问不满足或队列满时拒绝。 |
| `actions.publish({url, assets?})` | 发布自己声明的事件。返回 `{accepted:true, subscribers}`，计数是成功加入队列的订阅者数。 |
| `assets` 参数 | 形如 `{report:"minichat://assets/com.example.notes/report"}`，每次至多 16 个命名 URL。 |
| 平台 Action | 内置设置可打开 `minichat://actions/minichat.app/hooks/language`、`providers`、`models`、`plugins`，由客户端路由。 |

Actions 是异步投递。需要回复时在协议里约定第二个 Hook 或事件源，先发布数据 Asset，再把 URL 传给接收方。嵌套转发最多 8 层。

## Assets

Asset URL 为 `minichat://assets/<packageName>/<id>`。清单中每项填写 `id`、`type`、`mimeType` 和可选的 `accessibility`。

- `type: "file"` 填写 ZIP 内 `path`。宿主按已验签的条目读取，不将 URL 当成文件路径。路径只用 ASCII 字母、数字、点、连字符、下划线和目录分隔符 `/`，每一段均有内容。
- `type: "dynamic"` 填写本包 Actions `handler` URL。处理器收到 `asset.requested`，字段为 `url`、`request`（读取参数）、`sender`（调用包名或宿主的 `null`），以及附件预览请求的 `attachment`。返回 `Uint8Array`、`ReadableStream<Uint8Array>` 或相应 Promise。
- `private` 为默认，仅资源所有者插件和宿主可访问。 `protected` 另允许使用同一 X.509 证书签名的包访问。 `public` 可被其他插件读取。相同包名、证书主题或相同公钥都不能代替同一证书指纹校验。

| API | 参数与结果 |
| --- | --- |
| `assets.url(id)` | 同步生成本包资源的 URL。ID 语法无效时抛错。 |
| `assets.open(url, options?)` | 返回 `{mimeType, length, body}`。长度未知为 `null`，`body` 为 WHATWG 可读字节流。支持默认 reader、BYOB reader、异步迭代和取消。调用方负责消费、释放或取消。 |
| `assets.read(url, options?)` | 返回 `{mimeType, bytes:Uint8Array}`。适合小资源，最多读 8 MiB。更大资源用 `open`。 |

宿主跨语言每次最多传 64 KiB 原始字节，保留 TypedArray 的视图范围。动态生成最多 128 MiB，`options` 序列化大小最多 8 KiB，每个调用最多打开 16 条流，Asset 嵌套最多 8 层。

## Harness

所有函数均返回 Promise。可省略的目标 ID 默认取 `environment`，缺少所需上下文时会拒绝。读接口用 `offset`（从 0 开始）和 `limit`（默认 30，范围 1–100）。消息结构为 `{role,agent,text}`，只返回用户、助手及系统消息，单条文本最多 8192 UTF-16 个字符。

| API | 权限 | 参数与结果 |
| --- | --- | --- |
| `harness.listPresets` | `harness.read` | `{offset?,limit?}` → `[{id,name}]`。 |
| `harness.listChats` | `harness.read` | `{preset?,offset?,limit?}` → 当前预设下的 `[{id,name}]`。 |
| `harness.getPreset` | `harness.read` | `{preset?}` → 预设配置 JSON。 |
| `harness.readChat` | `harness.read` | `{preset?,chat?,offset?,limit?}` → `{id,preset,name,messages}`。 |
| `harness.readSession` | `harness.read` | `{preset?,chat?,agent?,id?,offset?,limit?}` → 指定 Agent 的消息数组。Fetch 和临时 Agent 可提供任务 ID，临时会话只向所有者开放。 |
| `harness.createChat` | `harness.write` | `{preset?}` → 将新对话加入指定预设并选中。 |
| `harness.createPreset` | `harness.write` | `{name?}` → 新建预设，省略时用默认名称。 |
| `harness.send` | `harness.write` | `{chat?,agent?,text?,role?,stage?,attachments?}` → 发送并请求模型回复。Agent 可选 `chat`、`env`、`preset`。Env 的 `stage:true` 推进剧情。附件必须由当前调用获得授权。 |
| `harness.steer` | `harness.write` | `{chat?,agent?,text,system?}` → `{accepted:true}`，在生成中插话并打断当前模型流。已执行的工具可以继续完成。 |
| `harness.insertSystemMessage` | `harness.write` | `{chat?,agent?,text}` → 正在生成时插入系统消息，返回 `{accepted:true}`。空闲时写入历史并返回排队回执。 |
| `harness.renameChat` | `harness.write` | `{chat?,name}` → 手动更新标题。 |
| `harness.generateTitle` | `harness.write` | `{chat?}` → 用标题模型根据 ChatAgent 会话重生成标题。 |
| `harness.setSearchMode` | `harness.write` | `{chat?,enabled}` → 更新当前预设的代理搜索开关。 |
| `harness.setPrompt` | `harness.write` | `{chat?,agent?,text}` → 保存该 Agent 的系统提示，最多 16000 字符。另支持 `search`、`fetch`、`title`。 |
| `harness.branchChat` | `harness.write` | `{chat?,block}` → 从带检查点的用户消息 ID 分支，并重新执行该条消息。 |
| `harness.createTemporarySession` | `harness.write` | `{}` → `{id}`。使用当前 Chat 模型创建内存会话，不进入会话列表。宿主总数上限 16。 |
| `harness.runTemporary` | `harness.write` | `{id,text,system?}` → `{text}`。在插件自己的临时会话中运行，保留同一临时会话的前文。 |
| `harness.closeTemporary` | `harness.write` | `{id}` → `{closed:true}`。释放会话，后续不能再使用该 ID。 |
| `harness.generate` | `model` | `{text,system?}` → `{text}`。使用 Chat 模型做独立生成，最多 384 输出 token，无会话和工具。 |

写操作通常返回 `{queued:true,request}`。该结果表示进入工作区队列，完成情况从对应宿主事件或之后的读取确认。普通消息文本上限 64,000 字符。发送系统角色消息会发起生成，空闲时插入系统消息只写入历史。

## DB

持久化使用 SQLite。插件只能打开自己的 `session`、`chat` 或 `preset` 分区，默认 `session`。所有方法需 `db` 权限。键是 1–500 字符的字符串。写入和替换文本上限 256 Ki UTF-16 字符。通过 `revision` 防止覆盖其他调用已经提交的修改。

| API | 参数与结果 |
| --- | --- |
| `db.get` | `{scope?,key,start?,length?}` → 未找到时为 `{missing:true}`，否则为 `{key,revision,content,total_chars,start,next_offset}`。`length` 默认 12000，最大 64000；`next_offset:null` 表示读完。 |
| `db.put` | `{scope?,key,content,expected_revision?}` → `{key,revision,total_chars}`。已有键需要匹配当前修订号。创建时可传 `0`。 |
| `db.patch` | `{scope?,key,old_string,new_string,replace_all?,expected_revision?}` → `{key,revision,total_chars}`。默认要求 `old_string` 唯一，找不到或竞争时拒绝。 |
| `db.delete` | `{scope?,key,expected_revision}` → `{deleted:true,key}`。 |
| `db.list` | `{scope?,prefix?,after?,limit?,notes_only?}` → `{entries,next_after}`。`entries` 包含 `key,revision,chars,snippet`；将上一页 `next_after` 作为下一页 `after`。 |
| `db.search` | `{scope?,query,match?,prefix?,offset?,limit?,newest_first?,notes_only?}` → `{matches,next_offset,algorithm}`。`match` 选 `all`、`keys` 或 `content`，采用 SQLite FTS5 trigram 或字面搜索。 |

`db.list`、`db.search` 的 `limit` 默认 30，范围 1–200。`notes_only` 可排除以下划线开头的记录。

## HTTP、Picker 和附件

| API | 权限 | 参数与结果 |
| --- | --- | --- |
| `http.request` | `http` | `{url,method?,headers?,body?,timeout_ms?}` → `{status,headers,body}`。方法允许 GET、HEAD、POST、PUT、PATCH、DELETE，默认 GET。请求体上限 256 Ki 字符，UTF-8 响应体上限 1 MiB，超时 1–30 秒，默认 15 秒。3xx 响应如常返回。 |
| `picker.media` | `picker` | `{type?}`，类型 `image`、`video` 或 `both`。Android 使用系统 Photo Picker。 |
| `picker.file` | `picker` | `{mimeTypes?}`，支持具体 MIME 或通配类型。Android 使用系统文档选择器，桌面交互模式使用文件选择器。 |
| `attachments.open` | 当前调用的文件授权 | `{id}` → 和 `assets.open` 相同的流结构。用户选择或附件处理事件授权的文件才能打开。附件流的 MIME 元数据以 picker 或附件事件给出的类型为准。 |

Picker 返回 `{files:[{id,name,mime}]}`。用户取消时 `files` 为空。CLI stdio 模式没有交互选择器。文件 ID 是宿主引用，只能在拿到相应授权的调用里打开。

附件类型声明 `{id,name,mimeTypes,handler,thumbnail?}`。处理器从 `event.attachment` 得到文件，返回 `{text}` 给模型，最多 64000 字符。 `thumbnail` 是私有图像 Asset ID，MIME 必须为 PNG、JPEG 或 WebP。宿主给动态资源绑定附件引用，在 UI 需要时按 Asset URL 拉取。原文件随对话保存，预览图像独立显示。

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

节点使用全包唯一的 `key`，除 Markdown 外均填 `title`，可选 `titles` 提供 BCP 47 语言映射。每组最多 50 项，嵌套最多 5 层。宿主内部设置绑定和首页渲染权限仅开放给内置插件。

## Skills

技能声明 `{name, description, agents, instructions?, tools, hooks?}`。 `agents` 可包含 `chat`、`env`、`preset`、`search`、`fetch`、`temporary`。每个工具为 `{name, description, parameters, handler}`，其中 `parameters` 是顶层 `type:"object"` 的 JSON Schema，`handler` 是本包 Hook URL。工具名在模型侧是 `plugin_<id>_<name>`，长度最多 64，同一插件的工具名全局唯一。

Agent 先看到技能说明，通过 `activate_skill` 激活后才看到技能工具。技能工具调用时 `event.name` 为 `skill.tool`，另有 `skill`、`tool`、`arguments`。返回值为 JSON，作为工具结果交给模型。 `skills[].hooks` 可绑定 `skill.activated`、`turn.started`、`turn.finished`、`tool.started`、`tool.finished`。回调值统一写本包 Actions Hook URL。

## 宿主事件

宿主事件均含 `name`。聊天、工具等事件的详细 payload 需要 `harness.read`。没有该权限时仍能得到名称及环境。应用保留的权限和事件用在内置扩展中。

| 名称 | 触发时机和主要载荷 |
| --- | --- |
| `app.ready` | 插件加载完成。无聊天上下文。 |
| `plugin.enabled` | 本包成功安装或更新。 |
| `plugin.command` | 用户在插件页点运行或输入 `/plugin-run <id>`。 |
| `chat.loaded` | 打开对话，`chat`、`preset`、`config`。 |
| `chat.changed` | 打开首页或切换预设，`preset`、`config`。 |
| `chat.title` | 标题生成或更新，`id`、`name`。 |
| `library.changed` | 会话或预设目录变化，可能有 `preset`。 |
| `preset.changed` | 当前预设配置变更，`config`。 |
| `message.user` | 用户消息已写入，`agent`、`block`、`text`，可附检查点或附件。 |
| `message.system` | 系统消息已写入，`agent`、`block`、`text`。 |
| `message.completed` | Agent 完成回复，`agent`、`text`。 |
| `turn.started` | 开始一次模型请求，`agent`、`block`。 |
| `turn.delta` | 流式正文增量，`block`、`text`。 |
| `turn.finished` | 模型请求结束，`agent`、`block`、`interrupted`。 |
| `turn.cancelled` | 工作区操作取消，可含 `request`。 |
| `tool.started` | 工具执行开始，`agent`、`block`、`name`（事件名覆盖原字段）、`arguments`。 |
| `tool.finished` | 工具结束，`agent`、`block`、`text`。 |
| `context.compacted` | Agent 历史完成压缩。 |
| `world.changed` | 该回合修改了世界状态。 |
| `settings.changed` | 本包设置保存，`key`、`value`。 |
| `home.requested` | 首页请求推荐，`request`、`locale`。内置插件可通过 `ui.setHomeSuggestions({request,items})` 提交最多 5 条。 |

附件处理、技能激活和动态资源通过各自的声明投递，名称分别为 `attachment.process`、`skill.activated`、`asset.requested`。
