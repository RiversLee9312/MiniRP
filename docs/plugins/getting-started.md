# 开始开发插件

## 环境

签名和打包工具需要 [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)，版本见仓库的 [global.json](../../global.json)。纯 JavaScript 插件只需要编辑器和该 SDK。使用 TypeScript 或第三方模块时，再安装 [Node.js LTS](https://nodejs.org/) 和你选用的打包器。Android 端运行插件需要 Android 10 或以上，桌面交互版在同一 V8 API 契约下运行。

开启编辑器的 JavaScript 类型检查，或把 [JavaScript SDK](../../sdk/javascript/) 作为工作区依赖。SDK 提供 TypeScript 声明、URL helper 与清单 Schema。发布包内只放清单、打包后的入口和声明资源。TypeScript、npm 配置和依赖目录放在包外。

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

在预设对话里描述要开发的插件，以及是否要部署。PresetAgent 通过内置 base 插件委派任务、给一个或多个 PluginAgent 发送后续指令、等待结果和存档。PluginAgent 与项目一一对应。其源文件、签名身份、聊天和记忆随预设持久化，存档后可恢复。

`plugin_wait` 返回当时的状态。如果 PluginAgent 之后才完成或失败，结果或错误会作为系统消息交给所属预设的 PresetAgent。回合仍在进行时，通知会通过 steer 打断正在生成的模型请求，并在同一回合继续。回合已结束时，当前预设会自动开启后续回合。切换到其他预设期间，通知会保留到原预设再次打开。已经由 `plugin_wait` 带回并处理的结果会去重。

PluginAgent 可以列出、读写或修改自己项目的文件，调用已声明的 Actions 处理器以测试事件，以及签名、部署或卸载自己的插件。预览使用单独的 V8 环境和调试数据。首次部署的作用域仅包含当前预设，更新会沿用配置过的作用域。调试时可以使用本地设置、数据库、资源和项目内 Actions。

同一预设中的 PresetAgent 和 PluginAgent 可以双向传递 Asset URL，并在同一个容器的 `/workspace` 中交换文件。委派任务或回报结果时可以直接附上相应的 URL 或路径。PluginAgent 可用宿主 `curl` 技能将下载保存为 Asset，也可通过 `research` 技能调用 `real_search` 和 `real_fetch`，使用应用配置的搜索、抓取服务和本地浏览器回退。

PresetAgent 和 PluginAgent 都能激活 `base-plugin-docs`，按页查询 `development`、`api`、`schema`、`preset` 和 `automation`，也可传 `query` 查找具体字段。 `preset` 解释对话分工与智能体搜索。 `automation` 提供从技能触发事件、更新世界、持久化状态到渲染卡片的完整流程。
