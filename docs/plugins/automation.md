# 用插件连接智能体、事件和界面

当用户说「桥上的闸门可能要关了」，ChatAgent 可以继续扮演现场的角色。同时，它也可以通过技能报告风险，让 EnvAgent 决定世界里有没有发生变化，再把结果显示在状态卡上。对话是入口，规则、状态和界面各有各的职责。

## 一个完整流程

可以从 [航站状态示例](../../examples/room-status/plugin/) 开始。它在房间内维护一条值班状态，并演示如下流程。

1. ChatAgent 或 EnvAgent 激活 `room-status-status` 技能。技能给智能体提供读状态、显示卡片和报告变化三个工具。
2. 智能体调用 `plugin_room-status_report`，传入一段简短的新状态。JavaScript 处理器读取当前修订，校验输入，再调用 `api.state.set`。
3. 保存成功后，宿主安排 `state.changed`。处理器用事件里的上一版和当前值判断变化，通过 `harness.send` 请 EnvAgent 检查是否要推进世界。这个 API 返回排队回执。
4. EnvAgent 按世界规则决定实际更新，用 `notify_chat` 通知普通对话，或用群像技能的 `notify_chat`、`notify_character` 选择通知对象。示例采用 Room scope，同一 Room 的多条 Chat 可以读到同一份航站状态。
5. 智能体调用 `plugin_room-status_status-card`，Android 显示一张 OpenUI 卡。以后底层状态改动，打开的卡片会更新。重新打开旧聊天时，卡片也会从插件读取当前状态。

展开 `report` 的工具轨迹可以看见本次上报内容和实际保存值。它们由两个只读卡片显示，原始参数和结果仍可复制。卡片接收该次工具调用的数据，见[工具轨迹展示](skills-settings.md#工具轨迹展示)。

把示例源码打包、签名，再在预设中启用。想由智能体做，可以请 PresetAgent 委派 PluginAgent 阅读本篇、建立项目、调试并按你的要求部署。手动打包入口见 [开发指南](packaging.md)。

示例通过 `manifest.json` 的 `i18n` 提供简体中文的设置、卡片标题和技能文本，运行时根据回调的 `api.environment.locale` 生成状态输入错误。见[清单和运行时本地化](runtime.md#本地化)。

## 声明状态和卡片

`manifest.json` 顶层的 `stateEntries` 逐项声明 `key`、`scope`、`description`、`initial` 和可选的 `onChange`。 `initial` 是 JSON 对象，读取初始值时修订为 0。描述告诉智能体这项状态是什么、何时需要参考。把状态变更的验证写在业务处理器里。

`cards` 逐项声明 `id`、`title`、`program` 和可选的 `state`、`labels`。`state` 填状态条目的 `key` 字符串。OpenUI Lang 程序用 `$line` 引用该条目值中的 `line` 字段。示例通过 [卡片本地化](state.md#卡片本地化)提供 `$title` 和 `$labels.current`。

```json
{
  "stateEntries": [
    {"key":"station","scope":"room","description":"最新值班状态","initial":{"line":"尚无变化。"}}
  ],
  "cards": [
    {"id":"station","title":"值班状态","state":"station","program":"root = Col([Title(\"值班状态\", \"md\"), Text($line)])\n$line = \"尚无变化。\""}
  ]
}
```

使用 renderer 已支持的组件。表单和按钮可用 OpenUI 的继续对话操作把用户选择交回智能体，智能体再调用插件业务工具。表单的临时状态由 renderer 管理，长期状态由插件确认和持久化。

在 `skills[].stateEntries` 与 `skills[].cards` 里列出该技能可以触达的声明。宿主分别生成 `plugin_<id>_<skill>-state({key})` 和 `plugin_<id>_<skill>-card({id})`。读状态工具还会返回 `db_key`。将业务操作单独声明在 `skills[].tools` 并绑定自己的 Actions URL。

## 在草稿中调试卡片

PluginAgent 激活 `base-plugin-project` 后，可以在 Android 上独立完成下列循环。

1. 用 `plugin_base_project_card_catalog` 查询组件签名。目录由 Android 渲染器所用的组件库生成，支持按 `next_offset` 翻页。
2. 把卡片写进草稿的 `manifest.json`，再用 `plugin_base_project_card_preview({id})` 预览声明的卡片。预览会签名并加载草稿，返回 OpenUI 诊断、渲染文本、尺寸、横向溢出和控件列表。可以指定 `width` 和 `dark` 检查不同布局和深浅色。
3. 添加 `handler` 和 `event`，可以先执行草稿自己的 Action 并用测试数据填充绑定状态。普通会话使用相同的虚拟 Chat 和 Room ID，`ensemble: true` 使用不同的 ID。每次预览从独立的临时存储开始。
4. 从预览结果取控件标签，用 `steps` 重放最多六个步骤。省略 `value` 时点击控件，支持直接赋值的原生输入也可传入 `value`。逐项核对交互结果、渲染内容和状态。提交表单或打开链接时，预览记录操作和参数，以便检查实际载荷。
5. 支持图像输入的模型可以传 `screenshot: true`。宿主截取交互结束后的卡片，把 PNG 作为下一次模型请求的图片附件。长卡片截取首屏，结果包含 `cropped`。
6. 用 `project_assets` 取得源码的 Asset URL，通过 `edit_asset` 修正源码并重跑同一组预览。委派要求部署时，把能独立运行的 Action 测试随 `plugin_base_project_publish` 一起提交。在目标预设的聊天中用对应 Skill 显示卡片，再观察一次真实交互。测试角色和入口见[验证已部署的插件](packaging.md#验证已部署的插件)。

预览复用聊天中的 Android WebView 和 OpenUI 组件。它在离线环境中返回结构化结果，远程图片和地图底图依赖正式使用时的网络加载。Windows 终端会返回 `available: false`。

## 读取、更新、订阅

- `api.state.get({key})` 返回 `{key,scope,value,revision}`。宿主根据事件环境和声明寻找持久化位置。
- `api.state.set({key,value,expected_revision})` 替换 JSON 对象并返回新的修订。先读最新状态。遇到修订冲突时，重新读并判断业务操作是否仍成立。相同值重复写入会沿用原修订。
- 可选的 `onChange` 使用本包 Hook URL。事件含 `name:"state.changed"`、`key`、`scope`、`revision`、`value` 和 `previous`。事件发生在持久化成功后。对相同动作保持幂等；外部副作用用处理过的修订或业务 ID 去重。
- 智能体使用 `db_read` 读取 `db_key` 对应的只读投影。 `db_watch({key,enabled:true})` 订阅，`enabled:false` 取消。变更在智能体下一次请求模型前以系统消息提示，可再读最新值。连续变化可合并为最新修订。还可以监视自己创建的普通 DB 条目。
- 智能体用插件业务工具提交写入请求。插件验证条件后通过自己的存储保存。会话 DB 投影参与读取和订阅，插件状态是权威数据。

## 选对作用域

| 作用域 | 适合存什么 | 例子 |
| --- | --- | --- |
| `session` | 一个智能体的私有工作状态。 | 某个角色的调查线索。 |
| `room` | 群像 Room 中所有 Chat 共享的数据。普通会话中与 Chat 相同。 | 同一房间的闸门状态。 |
| `chat` | 一条聊天独享的数据。 | 只属于驾驶台 Chat 的调查记录。 |
| `preset` | 同一预设下共享的配置或资料。 | 可选地图清单。 |

群像中的 `api.environment.room` 是外层 Room 的 ID，`chat` 是其内当前 Chat 的 ID，`participant` 是当前角色。打开房间列表时可以有 `room` 而没有 `chat`。角色通过 `chat_send` 向自己加入的 Chat 发言；EnvAgent 通过 `set_chat_members` 管理成员。

同一个人物在 Room 中使用同一人物会话，当前 Chat 随交互变化。用 Room、Chat 和人物的 ID 识别事件和作用域。共享的结构化事实由插件按修订写入；向角色同步事实时，先完成状态写入，再向有权获知的 Chat 发出消息。Chat 内局部数据放在 Chat scope。允许角色读取的字段通过所属 Skill 投影，可用 `db_watch` 在下一次模型请求前收到修订变更。预设设计流程见[群像预设的角色与状态设计](../presets/modes.md#群像预设的角色与状态设计)。

## 做成可维护的联动

给事件取业务含义明确的名字，例如「交接已确认」「嫌疑线索已核实」。让触发它的工具验证输入和权限。把只有 EnvAgent 知道的规则放在环境层。通知角色时写它需要知道的后果和来源。使用 `api.actions.publish` 和签名 Asset 交换跨插件事件及内容。将 UI 按钮映射到已有的业务操作，让选择、展示和智能体调用复用同一份规则。

测试至少覆盖一次正常更新、重复输入、并发写入、无房间上下文、同一 Room 的两条 Chat 同名状态键、撤销插件作用域，以及已有聊天重新打开。验证实际状态和可见结果，避免把「已排队」当成「已执行」。涉及群像时，用同一 Room 下的两条 Chat 和至少一个只加入其中一条的角色检验边界。

群像角色及其可选的 SearchAgent 并发准备。在 Room 开放期间，角色持续处理已加入场景中的消息，用户切换或退出场景视图时仍可触发相应工具。准备和开场需要的共享状态由清单声明，或由首次使用该状态的工具幂等初始化。`room.loaded` 在开场完成后异步投递，适合此时才应启动的操作。通过修订号处理共享状态的并发冲突，各角色的开始和结束事件可以按不同顺序抵达。
