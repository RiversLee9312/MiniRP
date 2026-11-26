# 打包、安装与调试

## 创建证书、打包和验签

下面的命令在项目根目录执行。为开发签名使用本地证书，正式发布可使用自己的 PKCS#12 证书。在需要让多个插件共享 `protected` Asset 时，用同一张证书签这些包。保持证书与私钥备份，更新已经安装的包时需要同一张证书。

1. 在当前终端设置 `MINIRP_SIGNING_PASSWORD`，签名工具从环境变量读取密码。把密钥存放在包目录以外。仓库的 `.gitignore` 会屏蔽 `.keys/` 和 `.pfx`。
2. 准备目录并生成私钥证书。以下为 PowerShell 示例。

       New-Item -ItemType Directory -Force .keys, artifacts/plugins
       dotnet run --project sdk/MiniRP.Plugin.Tool -- certificate .keys/dev.pfx

3. 打包并核对包名和证书的 SHA-256 指纹。

       dotnet run --project sdk/MiniRP.Plugin.Tool -- pack examples/stream-assets/plugin .keys/dev.pfx artifacts/plugins/stream-example.plugin
       dotnet run --project sdk/MiniRP.Plugin.Tool -- verify artifacts/plugins/stream-example.plugin

打包工具拒绝覆盖既有输出。每次打包使用新产物名或移动上一个产物。安装包为 ZIP，压缩后和解包后的大小上限均为 128 MiB，至多 256 个条目。签名为 detached CMS，存放在 `META-INF/minichat.p7s`。签名输入固定为 UTF-8 字节，首行为 `MCPLUGIN/2`，之后按条目名 ordinal 升序，每行是大写 SHA-256 十六进制摘要、TAB、准确条目名、LF，结尾有 LF。签名条目不参与摘要。证书用来证明包的签名身份，允许自签名。

使用 `.mjs` 入口时，将导入的文件放到源码目录的对应位置，草稿项目则关联到对应包内路径。每个模块都参与签名。打包会检查清单和文件限制，运行草稿调试或已安装插件时才会检查导入与执行。调用用到导入代码的 Action，确认实际返回值。见[多文件 JavaScript](getting-started.md#多文件-javascript)。

## 安装和验证

- Android 在「设置 → 插件」选择已签名的 `.plugin`。检查安装确认中的包名、证书指纹、权限和网络 origin。安装后可运行示例，并可从设置打开示例声明的 Markdown 页。
- Android 在「设置 → 插件」点按插件打开详情。第三方插件可以启用、停用、调整预设作用域、导出或卸载。「导出」通过系统文件选择器保存原始签名 `.plugin` 包。桌面可用 `/plugin-export stream-example stream-example.plugin`。内置插件保持启用，没有独立安装包。
- 桌面输入 `/plugin-install path/to/stream-example.plugin`，用 `/plugins` 查看，用 `/plugin-run stream-example` 运行，用 `/plugin-remove stream-example` 卸载。交互文件选择需要图形终端会话。
- 测试示例时先打开一场聊天。它会生成 1,000,003 字节并按块消费，将大小写到插件 chat DB 的 `stream-example/last-size`。可在另一个调试 Hook 里用 `api.db.get({scope:"chat",key:"stream-example/last-size"})` 核对。
- 从旧版包迁移时，加入 `packageName`，设 `apiVersion:2`，把回调集中登记到 `actions.handlers`，把所有事件和回调声明改成 Hook URL，重新签名。安装 API 接受已签名的 v2 包。已在旧版安装的包有兼容读取路径，旧的未签名 DB 与新签名身份分开存储。

## 归属预设

预设自己的插件在签名前把 `presetId` 写入 `manifest.json`，值为预设的小写 UUID。通过预设委派创建的插件由宿主填写该字段。预设包和存档包均携带归属该预设的原始签名包，导入时重新验签，新装插件的作用域默认为该预设。归属决定插件随哪个预设导出，运行作用域继续由宿主管理。旧版已发布草稿可由本地部署记录核对归属和签名。

## 调试与发布

PluginAgent 激活 `plugin-dev` 后，用 `plugin_debug({})` 签名、校验并加载草稿。要检查某个行为，传入草稿声明的 Action URL 作为 `handler`，并在 `event` 中放入该处理器读取的字段。省略事件名时默认为 `actions.invoke`。返回的 `result` 是这次直接调用处理器的结果。`skill.tool` 事件由宿主在真实智能体调用工具时创建，届时也会检查角色、Skill 激活状态和资源授权。可运行的参数和预期值见[第一次接手草稿](getting-started.md#pluginagent-第一次接手草稿)。

普通草稿调试提供 `preset` 和本次调用的 `session`。它可以访问隔离的设置、数据库和本项目的资源、Actions。涉及 `chat` 或 `room` 状态的卡片，可在一次 `plugin_card_preview` 调用里用 `handler` 写入初始数据再渲染。需要真实网络、文件选择器、模型、Harness 或另一个插件配合的部分，留到安装后从对应入口验证。

数据库作用域不可用时，回执会列出请求的作用域和当前事件提供的作用域。需要 `chat` 或 `room` 的 Action 应从具备该上下文的入口验证。测试卡片时可通过卡片预览初始化隔离的聊天数据。

### 验证已部署的插件

1. 委派要求部署时，调用 `plugin_publish`。有可独立执行的测试时，传入在草稿中验证过的 `handler`、`event` 和 `expected_result_json`。宿主用隔离数据测试签名草稿，通过后才安装。省略 `handler` 时，宿主验证脚本能加载。更新保留已有设置，尤其要确认当前预设可以使用这个插件。

   第一次用 `plugin_debug` 省略预期结果，核对实际返回是否满足业务要求。如果回执为 `{"ok":true,"result":{"count":2}}`，发布时传 `"expected_result_json":"{\"count\":2}"`。宿主解析这段 JSON，再精确比较字段、类型和数组顺序。已有调用也可以用 `expected_result` 直接传 JSON 值，一次只传其中一个字段。
2. 选一个用户实际会用的入口和一组测试数据。例如技能声明给 ChatAgent 时，在目标预设的测试聊天里让角色报告「已交班」再显示状态卡，看工具轨迹及卡片文字。声明给 PresetAgent 的技能，可由 PresetAgent 激活后调用一次。带 `plugin.command` 的插件可从 Android 插件详情点运行，或在桌面输入 `/plugin-run <插件短 ID>`。设置、附件或宿主事件则分别打开设置、选择测试附件、触发相应事件。
3. 检查用户关心的结果。要保存状态就再读一遍，要通知另一个智能体就观察接收方的消息或实际状态变化。记录触发入口、输入、期望值和实测值。`queued` 或 `accepted` 回执只说明请求已排队。

发布返回插件的 `id`、`packageName`、`enabled`、`scope` 和 `verification`。安装后宿主会用新隔离数据复跑同一项检查，在 `verification` 中报告实际测试的 `generation` 和结果。`verification.ok:false` 表示插件已安装，但安装后的检查失败。用 `plugin_verify` 提交同样的参数，可对当前安装版本复测。它读取已安装的代码，也遵守草稿调试的隔离限制。

结果不匹配时，回执会标明失败的阶段，并列出预期和实际 JSON 的类型及内容摘要。核对字段、类型和数组顺序。如果显示预期为字符串、实际为对象或数组，检查是否把序列化文本传给了 `expected_result`。要查看超长结果的完整内容，用同一组参数调用 `plugin_debug` 或 `plugin_verify`，省略预期结果字段。

PluginAgent 的会话提供内置插件技能。把部署结果和真实入口的测试步骤交给 PresetAgent。PresetAgent 可以直接验证自己能用的入口。若入口属于其他角色或需要点按客户端，就说明应在哪个对话或界面操作、预期会看到什么。报告中把等待实测的行为标为待验证。若实测出现偏差，将实际返回值或界面现象交回原 PluginAgent，使它能在同一项目修复并复测。

### 手动排查

先用打包工具抓清单和签名错误，再分别验证安装、Hook、资源读取和取消。给大资源优先使用 `assets.open`，逐块处理并在退出时取消 reader。通过 `api.db` 保存跨调用或进程的状态，通过模块变量保存可重建的缓存。测试两个插件共享资源时，要分别覆盖同证书、不同证书和私有 Asset。

插件失败由宿主上报 `plugin_error`，错误包含插件短 ID 和失败消息。回调中应显式处理来自宿主 API 的 Promise 拒绝。同一个插件的 Hook 可以与本插件的资源生成交错执行，更新共享状态时要考虑 `await` 间的顺序。升级和复现版本问题时记录 SDK API 版本、包名、证书指纹和相关 Hook URL。
