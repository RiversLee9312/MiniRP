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
| `harness.readSession` | `harness.read` | `{preset?,room?,chat?,agent?,id?,participant?,offset?,limit?}` → 指定 Agent 的消息数组。群像的 Chat/Search 会话以 Room 和角色 ID `participant` 定位。Fetch 和临时 Agent 可提供任务 ID，临时会话只向所有者开放。 |
| `harness.createChat` | `harness.write` | `{preset?}` → 在普通预设中新建聊天并选中。 |
| `harness.createRoom` | `harness.write` | `{preset?}` → 在群像预设中新建 Room，用预设定义初始化角色和 Chat。Room 内新增 Chat 或改变成员由 EnvAgent 处理。 |
| `harness.createPreset` | `harness.write` | `{name?}` → 新建预设，省略时用默认名称。 |
| `harness.send` | `harness.write` | `{chat?,room?,agent?,text?,role?,stage?,attachments?}` → 发送并请求模型回复。Agent 可选 `chat`、`env`、`preset`。Env 的 `stage:true` 请求推进剧情。群像聊天发送给 `chat` 时提供房间，事件有房间时可沿用。附件必须由当前调用获得授权。 |
| `harness.steer` | `harness.write` | `{room?,chat?,agent?,text,system?}` → `{accepted:true}`，在目标 Chat 生成中插话并打断当前模型流。已执行的工具可以继续完成。 |
| `harness.insertSystemMessage` | `harness.write` | `{chat?,room?,participant?,agent?,text}` → 普通 Agent 生成时可插入系统消息，返回 `{accepted:true}`。空闲时入队写入历史。群像模式给 `chat` 发消息时用 `participant` 选择单个角色，或用 `room` 通知房间。 |
| `harness.renameChat` | `harness.write` | `{room?,chat?,name}` → 手动更新普通 Chat 或群像 Room 中指定 Chat 的标题。 |
| `harness.renameRoom` | `harness.write` | `{room?,name}` → 手动更新 Room 的标题。 |
| `harness.generateTitle` | `harness.write` | `{chat?}` → 用标题模型根据 ChatAgent 会话重生成标题。 |
| `harness.setSearchMode` | `harness.write` | `{chat?,enabled}` → 更新当前预设的代理搜索开关。 |
| `harness.setPrompt` | `harness.write` | `{chat?,agent?,text}` → 保存该 Agent 的系统提示，最多 16000 字符。另支持 `search`、`fetch`、`title`。 |
| `harness.branchChat` | `harness.write` | `{chat?,block}` → 从带检查点的用户消息 ID 分支，并重新执行该条消息。 |
| `harness.createTemporarySession` | `harness.write` | `{}` → `{id}`。使用当前 Chat 模型创建内存会话，不进入会话列表。宿主总数上限 16。 |
| `harness.runTemporary` | `harness.write` | `{id,text,system?}` → `{text}`。在插件自己的临时会话中运行，保留同一临时会话的前文。 |
| `harness.closeTemporary` | `harness.write` | `{id}` → `{closed:true}`。释放会话，后续不能再使用该 ID。 |
| `harness.generate` | `model` | `{text,system?}` → `{text}`。使用 Chat 模型做独立生成，最多 384 输出 token，无会话和工具。 |

写操作通常返回 `{queued:true,request}`。该结果表示进入工作区队列，完成情况从对应宿主事件或之后的读取确认。普通消息文本上限 64,000 字符。发送系统角色消息会发起生成，空闲时插入系统消息只写入历史。
