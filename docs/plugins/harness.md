# Harness API

## Harness

所有函数均返回 Promise。可省略的目标 ID 默认取 `environment`，缺少所需上下文时会拒绝。读接口用 `offset`（从 0 开始）和 `limit`（默认 30，范围 1–100）。消息结构为 `{role,agent,text}`，只返回用户、助手及系统消息，单条文本最多 8192 UTF-16 个字符。

Preset、Room 和 Chat 都使用标准小写 UUID，例如 `7844467a-14ae-48eb-8ef7-9c34d21b484d`。普通对话的 Room 和 Chat 共享一个 UUID；群像中的每条 Chat 都有自己的 UUID，Room 由独立 UUID 标识。插件应从 Environment 或列表 API 取得实际 ID。SDK 的 `workspaceId(value)` 可对已保存或外部传入的值做格式校验。插件接口会拒绝旧短 ID，人类可读的名称适合展示和由用户选择。

| API | 权限 | 参数与结果 |
| --- | --- | --- |
| `harness.listPresets` | `harness.read` | `{offset?,limit?}` → `[{id,name}]`。 |
| `harness.listChats` | `harness.read` | `{preset?,room?,offset?,limit?}` → 普通预设的聊天列表，或群像 Room 内的 `[{id,name,members}]`。 `members` 为角色 ID。 |
| `harness.getPreset` | `harness.read` | `{preset?}` → 预设配置 JSON。 |
| `harness.readChat` | `harness.read` | `{preset?,room?,chat?,offset?,limit?}` → `{id,room,preset,name,messages}`。群像消息可含 `participant` 与 `speaker`。 |
| `harness.listRooms` | `harness.read` | `{preset?,offset?,limit?}` → 预设下的 Room 列表 `[{id,name}]`。普通预设的 Room 与 Chat 使用同一个 ID。 |
| `harness.readRoom` | `harness.read` | `{preset?,room?,offset?,limit?}` → `{id,preset,name,chats}`，其中 `chats` 是该 Room 的 Chat 列表。 |
| `harness.getWorldDate` | `harness.read` | `{preset?,room?}` → `{mode,date}`。`mode` 为 `real` 或 `plugin`，`date` 为 `YYYY-MM-DD`。 |
| `harness.setWorldDate` | `harness.write` | `{preset?,room?,date}` → `{queued:true,request}`。目标为当前打开的 Room，预设使用 `plugin` 日期模式。 |
| `harness.readSession` | `harness.read` | `{preset?,room?,chat?,agent?,id?,participant?,offset?,limit?}` → 指定智能体当前上下文中的可见消息。群像的 Chat/Search 会话以 Room 和角色 ID `participant` 定位。Fetch 和临时智能体可提供任务 ID，临时会话只向所有者开放。 |
| `harness.getSessionInfo()` | `harness.read` | 当前回调绑定到模型会话时，返回 `{model,provider,contextWindow,usedTokens,remainingTokens}`。没有已计算上下文的会话时返回 `null`。 |
| `harness.createChat` | `harness.write` | `{preset?}` → 在普通预设中新建聊天并选中。 |
| `harness.createRoom` | `harness.write` | `{preset?}` → 在群像预设中新建 Room，用预设定义初始化角色和 Chat。EnvAgent 可以创建、调整和归档 Room 内的群聊。 |
| `harness.createPreset` | `harness.write` | `{name?}` → 新建预设草稿，省略时用默认名称。打开后由 PresetAgent 完成设置。 |
| `harness.send` | `harness.write` | `{chat?,room?,agent?,text?,role?,stage?,attachments?}` → 发送并请求模型回复。智能体可选 `chat`、`env`、`preset`。EnvAgent 根据请求和预设规则判断是否推进。旧插件的 `stage:true` 仍可用来提示 EnvAgent 考虑推进。群像聊天发送给 `chat` 时提供房间，事件有房间时可沿用。附件必须由当前调用获得授权。 |
| `harness.steer` | `harness.write` | `{room?,chat?,agent?,text,system?}` → `{accepted:true}`，在目标 Chat 生成中插话并打断当前模型流。已执行的工具可以继续完成。 |
| `harness.insertSystemMessage` | `harness.write` | `{chat?,room?,participant?,agent?,text}` → 普通智能体生成时可插入系统消息，返回 `{accepted:true}`。空闲时入队写入历史。群像模式给 `chat` 发消息时用 `participant` 选择单个角色，或用 `room` 通知房间。 |
| `harness.renameChat` | `harness.write` | `{room?,chat?,name}` → 手动更新普通 Chat 或群像 Room 中指定 Chat 的标题。 |
| `harness.renameRoom` | `harness.write` | `{room?,name}` → 手动更新 Room 的标题。 |
| `harness.generateTitle` | `harness.write` | `{chat?}` → 用标题模型根据 ChatAgent 会话重生成标题。 |
| `harness.setSearchMode` | `harness.write` | `{chat?,enabled}` → 更新当前预设的智能体搜索开关。 |
| `harness.setPrompt` | `harness.write` | `{chat?,agent?,text}` → 保存该智能体的系统提示，最多 16000 字符。另支持 `search`、`fetch`、`title`。 |
| `harness.branchChat` | `harness.write` | `{chat?,block}` → 从带检查点的用户消息 ID 分支，并重新执行该条消息。 |
| `harness.createTemporarySession` | `harness.write` | `{}` → `{id}`。使用当前 Chat 模型创建内存会话，不进入会话列表。宿主总数上限 16。 |
| `harness.runTemporary` | `harness.write` | `{id,text,system?}` → `{text}`。在插件自己的临时会话中运行，保留同一临时会话的前文。 |
| `harness.closeTemporary` | `harness.write` | `{id}` → `{closed:true}`。释放会话，后续不能再使用该 ID。 |
| `harness.generate` | `model` | `{text,system?}` → `{text}`。使用 Chat 模型做独立生成，最多 384 输出 token，无会话和工具。 |

写操作通常返回 `{queued:true,request}`。该结果表示进入工作区队列，完成情况从对应宿主事件或之后的读取确认。普通消息文本上限 64,000 字符。发送系统角色消息会发起生成，空闲时插入系统消息只写入历史。

### 推进群像房间

在群像 Room 的剧情事件里，用 `harness.send` 给 EnvAgent 发送推进指令。EnvAgent 可以检查群聊及成员，创建群聊、替换成员或归档已退出剧情的群聊。插件应描述已经发生的剧情和希望调整的群聊。用 `harness.listChats` 核对变更结果。归档的群聊保留在 Room 存档中，活动列表与普通聊天读取使用当前有效的群聊。

```js
async function closeStation(event, api) {
  return api.harness.send({
    room: event.room,
    agent: "env",
    text: "The observatory has closed. Archive the Observatory chat. Open a Workshop chat for Ada and Bryn.",
  });
}
```

剧情需要让某个角色暂停或重新参与时，由插件通过同一个 `harness.send` 指导 EnvAgent。现有 `ensemble-management` Skill 提供 `disable_chat_agent({ participant })` 和 `enable_chat_agent({ participant, message? })`。工具异步返回 `accepted`、`requested` 和当前 `lifecycle`。禁用要求角色处于 `enabled`，启用要求角色处于 `disabled`。重复请求、过渡期间的请求和其他非法状态都会立即返回工具错误。用 `inspect_chats` 查看角色的 `lifecycle`（`enabled`、`stopping`、`disabled` 或 `starting`）。状态随 Room 保存。剧情条件、触发时机和用户交互由插件实现。

- 请求禁用时，宿主停止接收新的回复任务，并将正在处理的消息及待回复消息汇总到一条系统通知。角色有正在运行的轮次时，宿主通过 steer 投递。角色决定处理或放下这些消息，通过临时 `chat-agent-lifecycle` Skill 的 `confirm_chat_agent_stopped` 确认后才正式禁用。私聊和公开聊天继续遵循各自的可见性边界。
- 请求启用时，角色使用同一个 Skill 的 `confirm_chat_agent_enabled` 确认。确认后收尾该轮并移除临时 Skill。宿主把已启用的系统通知写入角色会话，附加 EnvAgent 可选的 `message`，并从空回复队列接收新消息。群聊成员关系、私聊和历史会保留。

`getSessionInfo()` 不接收目标参数。它返回触发回调的会话快照，例如工具调用或模型轮次的 Hook。`model` 是模型 ID，`provider` 是宿主配置的提供商 ID。`contextWindow` 是整个模型窗口，`usedTokens` 沿用上下文压缩估算，并在提供商返回输入用量时校准。`remainingTokens` 是窗口减去当前用量，下限为 0。这个数字尚未扣除为模型输出预留的空间。

日期按 Room 保存。为剧情事件指定下一日期时，先读当前日期，再提交更新。可以从 `world.changed` 的 `calendar` 和 `request` 确认已生效的变更。

```js
async function finishScene({ nextDate }, api) {
  const { mode, date } = await api.harness.getWorldDate();
  if (mode === "plugin" && date !== nextDate) {
    return api.harness.setWorldDate({ date: nextDate });
  }
  return { date };
}
```
