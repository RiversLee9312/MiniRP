# Actions、Assets 与平台 I/O

插件对外通过 Action URL 提供调用入口，通过 Asset URL 提供资源。Skill 给模型暴露的工具可以引用已声明的 Action URL。插件直接调用该 URL 时走普通 Action 授权。

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
| `actions.publishOutput({text, name?, mime?})` | 在 Skill 工具的 Action 中发布文本结果。最多 8192 个 Unicode 码点时返回 `{output}`，更长时返回 `{asset_url, size, instruction}`。默认文件名为 `tool-output.txt`，类型为 `text/plain`。 |
| `assets` 参数 | 形如 `{report:"minirp://assets/com.example.notes/report"}`，每次至多 16 个命名 URL。 |
| 平台 Action | 内置设置可打开 `minirp://actions/minirp.app/hooks/language`、`providers`、`models`、`plugins`，由客户端路由。 |

Actions 是异步投递。需要回复时在协议里约定第二个 Hook 或事件源，先发布数据 Asset，再把 URL 传给接收方。嵌套转发最多 8 层。

### 长工具输出

把网页正文、命令日志和生成的报告交给 `api.actions.publishOutput`。返回值可以与退出码、标题或来源 URL 等摘要字段合并。临时 Asset 保留完整 UTF-8 内容，最大 128 MiB，仅本次 Agent 会话可读，七天后由宿主回收。Agent 用 `view_asset` 查找内容，也可以在回复中提供下载链接。已经发给用户的下载链接保留发布时的文件版本。

```js
return {
  source: page.url,
  ...await api.actions.publishOutput({
    text: page.markdown,
    name: "webpage.md",
    mime: "text/markdown",
  }),
};
```

后台操作可以先返回已授权的输出 Asset URL 和一个 `db_key`。通过 `running`、`failed`、`success` 表达操作状态，完成写入输出后再更新状态。Agent 可以用 `db_watch` 订阅该键，并用 `db_read` 读取当前状态。插件需要给 Agent 暴露自己的进度时，可在 Skill 的 `stateEntries` 声明进度项，使其获得可观察的会话 DB 投影。

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

Agent 可把已获授权的 Asset URL 传给技能工具。对于这种调用，`assets.open` 和 `assets.read` 可打开本次参数中传入的资源，包括工作区附件和来自其他工具的资源。宿主在打开时复查原始授权。Agent 工具传递方式见 [技能文档](skills-settings.md)。

Agent 可在 Markdown 回答里链接到已获授权的 Asset，点击后打开系统保存窗口。OpenUI 的 `Link` 和 `@OpenUrl` 支持相同的下载方式，`Image` 可以加载 PNG、JPEG、GIF、WebP、AVIF 等栅格图片。读取范围限定在对应的回复或卡片。会话自建文件和已分享的文件版本随 Room 或存档导出。

base 插件的 `research` 技能为抓取页和搜索条目分配私有 Asset URL。页正文是 Markdown，条目是 JSON，两者都能通过 `view_asset` 和 `edit_asset` 操作。当前会话通过工具结果获得授权，分支从其快照解析对应的版本。暂存候选只在研究轮次可用，缓存后的资源随 Room 或预设存档导出。宿主按内容哈希保存正文，并在导入时校验。

## 预设聊天背景

在预设所属的签名插件中声明图片、GIF 或短视频 Asset。插件清单的 `presetId` 必须与预设 UUID 一致，插件作用域也须覆盖该预设。预设 Agent 使用 `preset-editing` 中的 `list_chat_backgrounds` 查找资源，再用 `configure_chat_appearance` 配置背景。常用格式包括 PNG、JPEG、WebP、GIF、MP4 和 WebM。视频按静音循环播放。

预设保存在 `appearance` 中。`background_url` 是已声明的 Asset URL，`background_blur` 默认开启，`theme_color` 可用 `#RRGGBB` 覆盖从背景提取的主题色。动图和视频使用预览帧取色，播放时主题色保持稳定。`background_url` 或 `theme_color` 传空字符串可清除对应设置。Android 12 及以上支持背景模糊。聊天页还会按浅色或深色模式调整背景明暗。背景资源也会随其所属插件一起导出。

## 查看和转换资源

- `api.assets.describe(url)` 返回已授权资源的 MIME 类型、来源、名称和已知大小。
- `api.text.encode(text, {encoding?})` 返回 `Uint8Array`。`api.text.decode(bytes, {encoding?})` 解码字节。支持 UTF-8、UTF-16LE、UTF-16BE，每次最多 64 KiB；非法字符序列报错。`api.text.read(url, {encoding?, offset?, length?})` 分页读取文本，优先使用附件处理器提取的文本，文件读取可按字节顺序标记选择编码。
- `api.image.metadata(url)` 查询尺寸及可用格式信息。`api.image.decode(url)` 返回 `AssetBody`，另含 `width`、`height`、`pixel_format:"rgba8"`。流是按行排列的 RGBA 字节。`api.image.encode(url, {format?, quality?})` 将来源图片编码为 PNG、JPEG 或 WebP。解码与重新编码最长边限制为 2048 像素。
- `api.media.videoMetadata(url)` 查询视频属性。`api.media.decodeFrame(url, {start_ms?})` 输出 PNG。`api.media.decodeAudio(url, {start_ms?, duration_ms?})` 从音频或视频音轨读取一段 PCM WAV。`api.media.encodeAudio` 也可将同一段音频导出为 PCM WAV。`api.media.encodeVideo` 以相同的时间参数输出 H.264/AAC MP4。每段默认 10 秒，最长 30 秒。
- `api.pdf.metadata(url)` 查询页数等文档信息。`api.pdf.renderPage(url, {page?})` 将单页渲染为 PNG，页号从 1 开始。`api.pdf.createPage(imageUrl)` 从一张图片生成单页 PDF。

转换结果是本次调用内可消费的 `AssetBody`。动态 Asset 处理器可以返回 `body`，技能工具再以 `returnType: "asset-url"` 返回对应 URL。Android 使用系统图像、音频和 PDF API，MP4 转码使用 Media3 Transformer。桌面使用 PATH 上的 FFmpeg、Poppler 和用于创建 PDF 的 `img2pdf`。转换继续受 Asset 访问、文件大小、时间和插件调用限制约束。

`image.preview`、`media.decodeFrame` 和 `pdf.renderPage` 接受可选 `viewport`，返回源尺寸和实际输出尺寸。Android 使用区域解码和 PDF 分区渲染，桌面使用 FFmpeg 裁切和 Poppler 渲染。 [Android 区域解码](https://developer.android.com/reference/android/graphics/BitmapRegionDecoder) · [Android PDF 分区渲染](https://developer.android.com/reference/android/graphics/pdf/PdfRenderer.Page) · [FFmpeg crop 滤镜](https://ffmpeg.org/ffmpeg-filters.html#crop)

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
