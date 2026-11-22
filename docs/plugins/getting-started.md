# 开始开发插件

## PluginAgent 第一次接手草稿

假设委派要求是「让角色能报告值班状态」。先选一个看得见的结果，例如输入「已交班」后，状态卡显示「已交班」。围绕这一次操作查文档、改源码和测试。遇到字段或行为不确定，先查一段原文，再用一个输入试出来。

1. **查契约。** 调用 `activate_skill({"name":"base-plugin-docs"})`。从下一次模型请求起可用 `plugin_base_plugin_docs`。例如用 `{"topic":"schema","query":"stateEntries","length":1000}` 查清单字段，用 `{"topic":"state","query":"state.set","length":1000}` 查状态更新。返回的 `next_offset` 可作为下次的 `offset`，继续读时保留 `topic`、省略 `query`。`found:false` 表示未找到这串文字，可换字段名或主题。
2. **打开分配给你的源码。** 激活 `project_assets`，调用 `project_asset_list({})`。新草稿已有 `manifest.json` 和 `index.js`，结果为每个包内路径给出一个 Asset URL。激活 `view_asset`，用 `plugin_base_grep` 传入原样的 `url`、`pattern:"^"` 和较小的 `limit` 读文件。激活 `edit_asset` 后，用 `plugin_base_text_edit` 对照读到的 `old_string` 替换为 `new_string`。保留宿主分配的 `id`、`packageName` 和 `presetId`。每次调试和发布都会读取关联 Asset 的最新内容。
3. **先跑一个 Action。** Action 是插件用 URL 标识的处理函数。下面是值班示例的两个清单字段。把它们合入草稿的 `manifest.json`，保留原来的身份和入口字段。将示例中的 `<packageName>` 换为本项目的包名。`agents` 指定 ChatAgent 可激活这个技能，`parameters` 描述它可以传给工具的参数。

   ```json
   {
     "actions": { "handlers": { "report": "report" } },
     "skills": [{
       "name": "duty",
       "description": "报告值班状态",
       "agents": ["chat"],
       "tools": [{
         "name": "report",
         "description": "提交一条值班状态",
         "parameters": {
           "type": "object",
           "properties": { "line": { "type": "string" } },
           "required": ["line"]
         },
         "handler": "minirp://actions/<packageName>/hooks/report"
       }]
     }]
   }
   ```

   在新草稿 `index.js` 已有的 `globalThis.handlers = {};` 后添加处理器。

   ```js
   globalThis.handlers.report = function (event) {
       const line = String(event.arguments?.line ?? "").trim();
       if (!line) throw new Error("请填写值班状态");
       return { line };
   };
   ```

   激活 `base-plugin-project`，调用 `plugin_base_project_debug`。将下面 URL 中的 `<packageName>` 替换成刚读到的包名，`report` 对应清单的映射键。

   ```json
   {
     "handler": "minirp://actions/<packageName>/hooks/report",
     "event": { "arguments": { "line": " 已交班 " } }
   }
   ```

   预期得到 `{"ok":true,"result":{"line":"已交班"}}`。把输入改成一个空格，应看到「请填写值班状态」。按实际返回值或错误修正，重跑同一输入。工具只传 `{}` 时只会加载脚本。要查实际聊天中的工具调用契约，用 `topic:"api", query:"skill.tool"`。
4. **接入实际功能。** 此例接下来要保存状态并显示卡片。用 `topic:"automation", query:"声明状态和卡片"` 查声明和渲染示例。需要卡片时，用 `plugin_base_project_card_catalog` 查所需组件，再用 Android 的 `plugin_base_project_card_preview` 传卡片 `id`、测试用的 `handler` 和 `event`。检查渲染文字是否包含刚写入的值。有交互时，用返回的控件名称重放一步，核对操作参数和结果。详见[在草稿中调试卡片](automation.md#在草稿中调试卡片)。
5. **部署后复测。** 委派要求部署时，在 `plugin_base_project_publish` 中传入刚才通过的 `handler`、`event`，以及 `expected_result_json: "{\"line\":\"已交班\"}"`。宿主用同一输入检查签名草稿和已安装版本。查看返回的 `verification`、启用状态和作用域，再从真实入口验收。向 PresetAgent 交付时写明测试角色或界面、输入和预期结果。详见[验证已部署的插件](packaging.md#验证已部署的插件)。

### 按任务读内置文档

- `development` 含本指南和打包、安装与调试。`api` 可按方法名、事件名查权限、参数和返回值。`schema` 是随宿主发布的清单 JSON Schema。
- `assets` 可用 `query:"## Assets"` 直接查资源声明、权限和读取方式。
- `state` 讲状态的作用域、修订和卡片。`automation` 演示用技能、事件、状态和卡片连接多个智能体，也包含 Android 卡片预览步骤。
- `preset` 介绍预设内的对话分工、搜索和工作流程。只需了解群像房间或智能体搜索时，分别查 `rooms` 或 `search`。

## 多文件 JavaScript

在 `manifest.json` 中设置 `"entry": "index.mjs"`，然后用相对路径导入包内文件。以下示例在 `actions.handlers` 中声明 `"report": "report"`，入口将同名函数登记到 `globalThis.handlers`。

`index.mjs`

```javascript
import { formatStatus } from "./lib/status.js";

globalThis.handlers = {
    report(event) {
        return formatStatus(event.arguments?.line);
    },
};
```

`lib/status.js`

```javascript
export function formatStatus(line) {
    return { line: String(line ?? "").trim() };
}
```

PluginAgent 用 `create_asset` 创建这两个源码文件，选择 `text/javascript`，再用 `project_asset_add` 分别关联到 `index.mjs` 和 `lib/status.js`。修改清单入口后，用 `plugin_base_project_debug` 传入 `handler: "minirp://actions/<packageName>/hooks/report"` 和 `event: {"arguments":{"line":" 已交班 "}}`，检查返回的 `line` 是 `已交班`。迁移旧草稿时，确认新入口可用后再解除旧入口的文件关联。发布后用同一输入检查 `verification`。

导入路径从写下 `import` 的文件算起。用 `./` 或 `../`、准确的大小写和 `.js` 或 `.mjs` 扩展名。每个模块都要打进同一个签名包。使用旧版 MiniRP 时，继续发布 `.js` 单文件入口。

## 在本地开发插件

签名和打包工具需要 [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)，版本见仓库的 [global.json](../../global.json)。纯 JavaScript 插件只需要编辑器和该 SDK。使用 TypeScript 或第三方模块时，再安装 [Node.js LTS](https://nodejs.org/) 和你选用的打包器。Android 端运行插件需要 Android 10 或以上，桌面交互版在同一 V8 API 契约下运行。

开启编辑器的 JavaScript 类型检查，或把 [JavaScript SDK](../../sdk/javascript/) 作为工作区依赖。SDK 提供 TypeScript 声明、URL helper 与清单 Schema。发布包内放清单、入口、包内模块和声明资源。TypeScript、npm 配置和依赖目录放在包外。

## 从示例开始

[Stream assets 示例](../../examples/stream-assets/plugin/) 已包含可直接打包的入口、动态资源、事件源、订阅者和资源驱动的设置页。插件入口将函数登记到全局对象。宿主根据 Actions URL 找到对应声明，再调用其中绑定的导出名。

    globalThis.handlers = {
        async run(event, api) {
            const resource = await api.assets.open(api.assets.url("sample"));
            let size = 0;
            for await (const chunk of resource.body) size += chunk.byteLength;
            return { size };
        }
    };

使用 SDK 做静态类型检查时，可以在源码顶部声明以下 JSDoc。将示例中的相对路径改成你项目实际引用的 SDK 路径。

    /** @typedef {import("../../../sdk/javascript/index.js").MiniRPApi} MiniRPApi */
    /** @type {import("../../../sdk/javascript/index.js").Handler} */
    async function run(event, api) {
        return { chat: api.environment.chat ?? null };
    }
    globalThis.handlers = { run };

如果需要打包 TypeScript 或 npm 依赖，选择 ECMAScript 2022、IIFE 输出，并将你的入口赋给 `globalThis.handlers`。例如在已安装 esbuild 的插件工作目录执行下列命令。

    npx esbuild src/index.ts --bundle --format=iife --target=es2022 --outfile=plugin/index.js

宿主只加载清单指定的 JavaScript 入口。依赖宿主模型、网络、文件或持久化时，使用 [插件 API](README.md) 中对应的入口。

## 在预设中委派插件开发

在预设对话里描述想从哪个入口使用插件、应看到什么，以及是否要部署。PresetAgent 用 `base-plugin-development` 委派任务、跟进现有项目、等待结果和存档。每个项目由自己的 PluginAgent 阅读开发文档、编辑源码、调试和部署。源文件、签名身份、聊天和记忆随预设持久化，存档后可恢复。PresetAgent 自己要查预设分工、搜索或群像房间时，可以激活 `base-preset-docs` 并调用 `plugin_base_preset_docs`。

`plugin_wait` 返回当时的状态。如果 PluginAgent 之后才完成或失败，结果或错误会作为系统消息交给所属预设的 PresetAgent。回合仍在进行时，通知会通过 steer 打断正在生成的模型请求，并在同一回合继续。回合已结束时，当前预设会自动开启后续回合。切换到其他预设期间，通知会保留到原预设再次打开。已经由 `plugin_wait` 带回并处理的结果会去重。

要给项目添加文件，激活 `asset_storage` 并用 `create_asset` 以 `text` 写入新内容，或用 `source_url` 复制已有资源。再用 `project_asset_add` 将返回的 `url` 关联到包内相对路径 `path`。临时资源可这样复制为持久文件。要解除单个关联，用 `project_asset_remove` 同时传入 `url` 和 `path`。原 Asset 会保留。

同一预设中的 PresetAgent 和 PluginAgent 可以传递 Asset URL，共用容器 `/workspace`。PresetAgent 可只读查看收到的草稿 Asset，按实际使用结果提出修改。由原 PluginAgent 更新项目关联的源文件并复测。PluginAgent 可用宿主 `curl` 技能将下载保存为 Asset，也可通过 `research` 技能调用 `real_search` 和 `real_fetch`，使用应用配置的搜索、抓取服务和本地浏览器回退。

首次部署的作用域仅包含当前预设，更新会沿用已配置的作用域。委派要求卸载时，`plugin_base_project_remove` 移除安装的插件，项目草稿仍可编辑。
