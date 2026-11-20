# Actions、Assets 与平台 I/O

## Actions

- Hook URL 为 `minirp://actions/<packageName>/hooks/<id>`。
- 事件源 URL 为 `minirp://actions/<packageName>/events/<id>`。只有声明它的包可以发布。

MiniRP 生成 `minirp://` URL。旧签名包中的 `minichat://` 地址仍可用，Actions 可以跨这两种写法匹配同一个事件源。第三方包的 `packageName` 和证书仍是其身份。
- 订阅者和被调用方各用自己的权限。`event.sender` 由宿主填写，值为发起方包名。`event.url` 是本次调用的 Hook 或事件源 URL，`event.name` 分别为 `actions.invoke` 或 `actions.publish`。
- 二进制资源通过 `event.assets` 的命名 URL 引用传递。接收方打开 URL 时按自身身份校验访问权限。普通 JSON Hook 入参适用宿主各自的事件结构。

| API | 参数与结果 |
| --- | --- |
| `actions.invoke({url, assets?})` | 触发自己或其他插件声明的 Hook。返回 `{accepted:true}`，表示已入队。目标不可用、URL 无效、资源访问不满足或队列满时拒绝。 |
| `actions.publish({url, assets?})` | 发布自己声明的事件。返回 `{accepted:true, subscribers}`，计数是成功加入队列的订阅者数。 |
| `assets` 参数 | 形如 `{report:"minirp://assets/com.example.notes/report"}`，每次至多 16 个命名 URL。 |
| 平台 Action | 内置设置可打开 `minirp://actions/minirp.app/hooks/language`、`providers`、`models`、`plugins`，由客户端路由。 |

Actions 是异步投递。需要回复时在协议里约定第二个 Hook 或事件源，先发布数据 Asset，再把 URL 传给接收方。嵌套转发最多 8 层。
## Assets

Asset URL 为 `minirp://assets/<packageName>/<id>`。清单中每项填写 `id`、`type`、`mimeType` 和可选的 `accessibility`。

- `type: "file"` 填写 ZIP 内 `path`。宿主按已验签的条目读取，不将 URL 当成文件路径。路径只用 ASCII 字母、数字、点、连字符、下划线和目录分隔符 `/`，每一段均有内容。
- `type: "dynamic"` 填写本包 Actions `handler` URL。处理器收到 `asset.requested`，字段为 `url`、`request`（读取参数）、`sender`（调用包名或宿主的 `null`），以及附件预览请求的 `attachment`。返回 `Uint8Array`、`ReadableStream<Uint8Array>` 或相应 Promise。
- `private` 为默认，仅资源所有者插件和宿主可访问。 `protected` 另允许使用同一 X.509 证书签名的包访问。 `public` 可被其他插件读取。相同包名、证书主题或相同公钥都不能代替同一证书指纹校验。

| API | 参数与结果 |
| --- | --- |
| `assets.url(id)` | 同步生成本包资源的 URL。ID 语法无效时抛错。 |
| `assets.open(url, options?)` | 返回 `{mimeType, length, body}`。长度未知为 `null`，`body` 为 WHATWG 可读字节流。支持默认 reader、BYOB reader、异步迭代和取消。调用方负责消费、释放或取消。 |
| `assets.read(url, options?)` | 返回 `{mimeType, bytes:Uint8Array}`。适合小资源，最多读 8 MiB。更大资源用 `open`。 |

宿主跨语言每次最多传 64 KiB 原始字节，保留 TypedArray 的视图范围。动态生成最多 128 MiB，`options` 序列化大小最多 8 KiB，每个调用最多打开 16 条流，Asset 嵌套最多 8 层。句柄打开 5 分钟后会自动关闭并释放名额，包括一直没有读取的句柄。资源所属插件更新或卸载时，宿主会撤销由应用打开的旧句柄。
## HTTP、Picker 和附件

| API | 权限 | 参数与结果 |
| --- | --- | --- |
| `http.request` | `http` | `{url,method?,headers?,body?,timeout_ms?}` → `{status,headers,body}`。方法允许 GET、HEAD、POST、PUT、PATCH、DELETE，默认 GET。请求体上限 256 Ki 字符，UTF-8 响应体上限 1 MiB，超时 1–30 秒，默认 15 秒。3xx 响应如常返回。 |
| `picker.media` | `picker` | `{type?}`，类型 `image`、`video` 或 `both`。Android 使用系统 Photo Picker。 |
| `picker.file` | `picker` | `{mimeTypes?}`，支持具体 MIME 或通配类型。Android 使用系统文档选择器，桌面交互模式使用文件选择器。 |
| `attachments.open` | 当前调用的文件授权 | `{id}` → 和 `assets.open` 相同的流结构。用户选择或附件处理事件授权的文件才能打开。附件流的 MIME 元数据以 picker 或附件事件给出的类型为准。 |

Picker 返回 `{files:[{id,name,mime}]}`。用户取消时 `files` 为空。CLI stdio 模式没有交互选择器。文件 ID 是宿主引用，只能在拿到相应授权的调用里打开。

附件类型声明 `{id,name,mimeTypes,handler,thumbnail?}`。处理器从 `event.attachment` 得到文件，返回 `{text}` 给模型，最多 64000 字符。转换附件在模型请求中以文本表示，原文件随对话保存。 `thumbnail` 是私有图像 Asset ID，MIME 必须为 PNG、JPEG 或 WebP。宿主给动态资源绑定附件引用，在 UI 需要时按 Asset URL 拉取并独立显示。
