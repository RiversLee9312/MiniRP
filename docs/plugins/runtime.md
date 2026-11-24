# 运行时、清单与事件

## 生命周期与执行

内置的 `base` 插件随 MiniRP 发布，提供应用设置、预设创建引导和开源许可证页。

- 启动时宿主加载已安装包，验证签名，随后向启用的插件投递 `app.ready`。内置插件随应用发布，拥有保留身份。启用的插件在手动安装、更新或由 PluginAgent 部署后收到 `plugin.enabled`，重新启用时也会收到该事件。如果当前预设在允许的范围内，事件会使用它的上下文。限定预设的插件没有匹配上下文时，宿主会使用一个允许的预设。作用域为空时，启用事件可以运行，但无法访问工作区。停用、更新和卸载都会取消旧实例的调用、关闭它的 Asset 句柄并拒绝它仍在排队的 Harness 写入。已提交的数据按包名和证书指纹保留。停用状态由宿主持久化，签名包仍可导出。当前没有卸载 Hook。
- 每个已加载插件按需创建一个 V8 isolate。入口脚本每次 isolate 初始化执行一次，定义 `globalThis.handlers`。同一 isolate 内可以复用全局缓存和内存状态，升级、卸载、应用退出或 isolate 因异常重建时会丢失它们。
- 每个插件有独立且有界的事件队列。普通 Hook 和 Actions 按入队顺序运行。技能工具、附件处理和动态资源由各自的调用方等待，可以在同一 isolate 内和普通 Hook 交错执行。每次调用拥有自己的参数、环境和待完成 Promise。单次 V8 执行仍是单线程，两个调用可在 `await` 边界交错。
- V8 步骤和宿主 I/O 在后台运行。一个插件等待模型、文件选择或网络时，别的插件照常接收事件。动态资源在首次拉取时执行。打开的 Asset 句柄 5 分钟后过期。单次 V8 步骤和 JS 清理均有 2 秒执行限制。取消宿主 I/O 后，宿主会清理对应调用。同一个 isolate 上的 V8 执行如果被中断、超时或未能完成清理，宿主会关闭它并在下一次调用时重新加载入口脚本。独立 isolate 继续运行。队列容量为 128，宿主流式事件在队列满时可能被丢弃，Actions 有明确的接受回执。
- JavaScript 基线是 ECMAScript 2022，宿主提供 WHATWG Streams。可以用原生 `Uint8Array`、`ArrayBuffer`、`DataView` 和 Promise。运行环境是内嵌 V8，可用能力以本文所列为准。HTTP、文件选择、模型和持久化通过各自的宿主 API 完成。
## 清单

`manifest.json` 位于 ZIP 根目录，UTF-8 编码，只读，由包签名覆盖。完整示例见 [示例清单](../../examples/stream-assets/plugin/manifest.json)。

| 字段 | 类型和含义 |
| --- | --- |
| `id` | 宿主 UI、CLI 命令和技能工具前缀使用的短 ID。小写字母开头，允许小写字母、数字、连字符和下划线，至多 48 字符。 |
| `packageName` | URL 和签名身份使用的反域名，如 `com.example.notes`。各段以小写字母开头，允许小写字母、数字和下划线。总长至多 150 字符。`minirp.*` 与旧前缀 `minichat.*` 均保留。 |
| `name` | 安装界面显示的名称，1–120 字符。 |
| `apiVersion` | 宿主契约版本，当前为 `2`。 |
| `entry` | ZIP 内的 UTF-8 入口路径。`.js` 是单脚本，最大 2 MiB。`.mjs` 是 ES 模块入口，包内 `.js` 和 `.mjs` 文件合计至多 2 MiB。 |
| `permissions` | 可选。声明 `db`、`harness.read`、`harness.write`、`http`、`model`、`picker`、`settings`。 |
| `network` | 使用 HTTP 时列出精确 HTTP(S) origin，例如 `https://example.com`。路径、通配符和自动重定向不参与授权。 |
| `actions` | `handlers` 把 Hook ID 映射到 JS 导出名，`events` 声明本包的事件源 ID，`subscriptions` 将事件源 URL 映射到本包的 Hook URL。 |
| `hooks` | 宿主事件名到本包 Hook URL 的映射。 |
| `assets` | 资源声明数组，详见 Assets。 |
| `skills` | 智能体技能声明数组，详见 Skills。 |
| `stateEntries` | 插件维护的结构化状态声明，可按角色技能投影为会话 DB 的只读项。 |
| `cards` | 可由智能体引用的 OpenUI 卡片声明。 |
| `attachments` | 附件类型声明数组。 |
| `settings` | 设置节点数组。 |
| `i18n` | 可选。按语言覆盖清单中已声明的可翻译文本。 |

Hook 的实现以 `handlers[导出名](event, api)` 被调用，可返回 Promise。清单中只有 `actions.handlers` 填写 JS 导出名。宿主事件、技能 Hook、技能工具、附件类型和动态 Asset 里的回调均使用本包的 Hook URL。

`actions.handlers` 的键是 URL 里的 Hook ID，例如 `show-count`。值是 `globalThis.handlers` 里的 JS 名，例如 `showCount`。JS 名最多 64 字符，可用 ASCII 字母、数字、`_` 和 `$`。需要宿主 API 的处理器在第二个参数接收 `api`，再按需传给其他模块。

模块在各自的 isolate 中按包内路径加载。同一文件在一个 isolate 中只求值一次，入口负责将回调登记到 `globalThis.handlers`。使用静态 `import` 和 `export`，用 `./` 或 `../` 指向同一签名包内的 `.js` 或 `.mjs` 文件，保留扩展名和大小写。包外依赖可在本地打包进输出。模块顶层用于同步初始化，异步宿主 API 放在回调里调用。见[多文件 JavaScript](getting-started.md#多文件-javascript)。

`api.environment` 是调用时的只读快照，可能含有 `locale`、`session`、`room`、`chat`、`preset`、`agent` 和群像角色 `participant`。无上下文的字段可能为 `null` 或缺失，例如 `app.ready` 没有会话。普通会话的 Chat 和 Room 使用同一个 UUID。群像的 Env 会话属于 Room，每个角色的 Chat 和 Search 会话也保存在 Room 内，事件的 `chat` 则指当前发生交互的那条 Chat。Preset 会话按预设共享，Fetch 和临时智能体按任务建立会话。作用域详见 [数据库与状态](state.md)。

用户可在插件管理中设置预设作用域。第三方插件默认在所有预设中运行，可仅启用所选预设，也可排除所选预设。仅启用所选预设时，插件也会跳过无预设上下文的事件。排除模式仍接收无预设上下文的事件。内置插件固定为全局。宿主保存此配置，签名包及其 `manifest.json` 保持只读。

作用域适用于事件、技能、附件、设置和通过 URL 调用的 Hook 与 Asset。插件使用 Harness API 显式指定预设时，宿主也检查目标预设。`harness.listPresets()` 按作用域分页。预设删除后，插件的作用域不会自动扩大。

## 本地化

在清单中保留源文案，将翻译放在 `i18n.locales` 里。每项使用区分大小写的 RFC 6901 JSON Pointer，数组下标从 0 开始。指针的路径片段用 `~1` 表示斜杠，用 `~0` 表示波浪号。以下片段来自 [房间值班状态示例](../../examples/room-status/plugin/manifest.json)。

```json
{
  "i18n": {
    "defaultLocale": "en",
    "locales": {
      "zh-Hans": {
        "/name": "房间值班状态",
        "/settings/0/title": "房间值班状态",
        "/settings/0/items/0/markdown": "记录每个房间最近一次核实的值班状态。",
        "/cards/0/title": "值班状态"
      }
    }
  }
}
```

宿主只接受指向已有字符串字段的指针。`i18n.locales` 至少有一份非空字典。每条译文须包含非空白字符，并满足目标字段的长度限制。支持以下字段。

- 顶层 `name` 和 `description`。
- 附件类型的 `name` 和 `description`，卡片的 `title` 与 `labels` 中的各项文案，状态条目的 `description`。
- 任意层级设置节点的 `title`、`description`、内联 `markdown`，以及选项的 `title`。
- 技能的 `title`、`description`、`instructions`，工具的 `title`、`description`，以及工具参数 JSON Schema 各级子 schema 的 `title`、`description`、`markdownDescription`。例如 `/skills/0/tools/0/title` 翻译第一个工具的显示名称。

标识符、权限、URL、选项值、模式约束、程序和独立 Asset 内容沿用清单原值。在 [OpenUI 卡片](state.md#卡片本地化)中引用 `$title` 和 `$labels`，或将运行时翻译写入卡片数据。

语言标签用 BCP 47（最多 128 个字符），比对忽略大小写。每个字段依次尝试请求的完整标签及其父标签、MiniRP 界面语言别名、`defaultLocale` 及其父标签、清单源文案。例如 `zh-TW`、`zh-HK` 和 `zh-MO` 可回退到 `zh-Hant`，`zh-CN` 和 `zh` 可回退到 `zh-Hans`，`pt-PT` 和 `pt` 可回退到 `pt-BR`。中文标签已指定简繁文字时，以指定文字为准。带扩展的 `en-US-u-ca-gregory` 依次尝试 `en-US-u-ca`、`en-US`、`en`。空缺或无效的请求语言从默认语言开始。若节点仍使用旧的 `titles` 映射，宿主先采用新字典中命中的设置标题，再按原方式查 `titles` 和源标题。切换界面语言后宿主按新语言重新解析这些文本，签名清单保持原样。

### 在处理器中翻译文本

每次回调从 `api.environment.locale` 读取语言。设置和 Action 使用宿主界面语言，智能体调用优先使用本次回合的语言，继而使用最近保存的会话或界面语言，最后使用英语。启动事件和旧宿主的语言可能为空或缺失。可在 isolate 内复用字典，翻译在每次需要文本时执行。

JavaScript SDK 的 `defineI18n` 用于运行时生成的通知、错误和卡片文本。将 SDK 随应用代码打包后调用。`getLocale(api, fallback?)` 读取当前回调的语言，缺失或无效时使用所给语言，省略时用英语。在生成清单时可用 `localePointer("cards", 0, "labels", "status")` 构造翻译路径并处理字符转义。

```js
import { defineI18n, getLocale } from "@minirp/plugin-sdk";

const t = defineI18n({
    defaultLocale: "en",
    locales: {
        en: { progress: "{1} of {2} rooms checked" },
        "zh-Hans": { progress: "共 {2} 个房间，已检查 {1} 个" },
    },
});

globalThis.handlers = {
    async report(event, api) {
        return { message: t(getLocale(api), "progress", 2, 5) };
    },
};
```

运行时按同样的语言顺序逐键匹配。缺失或空白译文继续回退，最终使用可选 `source` 字典的原文或键本身。空键或全空白键返回空字符串。编号占位符从 `{1}` 开始，允许重排和重复。缺失、`null` 或 `undefined` 参数保留对应占位符。数值和布尔值按字符串插入。插入的文本保持原样，在将结果放进 HTML、Markdown 或 OpenUI 代码时，应用相应格式的转义。

## 宿主事件

宿主事件均含 `name`。聊天、工具等事件的详细 payload 需要 `harness.read`。没有该权限时仍能得到名称及环境。应用保留的权限和事件用在内置扩展中。

| 名称 | 触发时机和主要载荷 |
| --- | --- |
| `app.ready` | 插件加载完成。无聊天上下文。 |
| `plugin.enabled` | 启用的包成功安装、更新，或用户重新启用本包。 |
| `plugin.command` | 用户在插件页点运行或输入 `/plugin-run <id>`。 |
| `chat.loaded` | 打开普通聊天或群像 Room 中的一条 Chat。群像提供 `room`、`chat` 和 `preset`。 |
| `room.loaded` | 群像 Room 已准备完成并可进入各场景。提供 `room`、`preset`、`chats` 和 `config`。 |
| `chat.changed` | 打开首页或切换预设，`preset`、`config`。 |
| `chat.title` | 标题生成或更新，`id`、`name`。 |
| `room.title` | 群像 Room 的标题生成或更新，`id`、`preset`、`name`。 |
| `library.changed` | 会话或预设目录变化，可能有 `preset`。 |
| `preset.changed` | 当前预设配置变更，`config`。 |
| `preset.created` | 预设建档后。事件环境带新预设 ID。拥有 `harness.read` 时还可读取 `preset` 和 `title`。 |
| `message.user` | 用户消息已写入，`agent`、`block`、`text`，可附检查点或附件。 |
| `message.system` | 系统消息已写入，`agent`、`block`、`text`。 |
| `message.completed` | 智能体完成回复，`agent`、`text`。 |
| `turn.started` | 开始一次模型请求，`agent`、`block`。 |
| `turn.delta` | 流式正文增量，`block`、`text`。 |
| `turn.finished` | 模型请求结束，`agent`、`block`、`interrupted`。 |
| `turn.cancelled` | 工作区操作取消，可含 `request`。 |
| `tool.started` | 工具执行开始，`agent`、`block`、`name`（事件名覆盖原字段）、`arguments`。 |
| `tool.finished` | 工具结束，`agent`、`block`、`text`。 |
| `context.compacted` | 智能体历史完成压缩。 |
| `world.changed` | Room 世界状态已保存，`calendar` 为 `{mode,date}`。由 Harness 日期写入触发时，`request` 与排队结果对应。 |
| `settings.changed` | 本包设置保存，`key`、`value`。 |

附件处理、技能激活和动态资源通过各自的声明投递，名称分别为 `attachment.process`、`skill.activated`、`asset.requested`。
