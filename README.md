# minichat · 虚构世界搜索 harness

一个带终端 TUI 的多 Agent 对话程序。主助手接收搜索和网页请求，SearchAgent 自主决定真实检索、抓取、虚拟缓存召回与内容编辑的步骤，完成后通过工具提交结果。

搜索结果与页面可能包含模型生成的虚构内容，适合小说、推演和产品演示，不代表真实信息。

## 启动

```powershell
python -m pip install -r requirements.txt
python harness.py
```

Linux / WSL 使用 `python3`。默认读取 `.env`；也可以在 TUI 中输入 `/settings`，配置模型、API Key、API 地址、thinking 和 reasoning effort。首次缺少密钥时会直接打开配置窗口。

配置窗口的密钥输入会遮蔽显示，保存到本机 `.env`，下一次请求生效。密钥不进入轨迹或对话存档。Search / Fetch 使用配置服务的 `/v1/tools/search`、`/v1/tools/fetch`，更换 API 地址时需要服务支持这些工具接口。

## 终端界面

布局参考 [OpenCode TUI](https://opencode.ai/docs/tui/) 的工作区与命令交互：顶栏只保留当前预设、日期、模型和对话名，命令提示常驻底部。

启动时只有主对话。输入 `/env`、`/preset-agent` 打开相应侧栏；`/edit-canon`、`/next-stage` 也会调起 EnvAgent，预设编辑、创建、导入命令会调起 PresetAgent。

- EnvAgent 小窗可切换“编辑事实 / 推进剧情”，然后直接输入要求。
- PresetAgent 小窗可切换“编辑预设 / 创建设定 / 导入文件”，然后输入要求或路径。
- 在小窗输入 `/close` 关闭对应侧栏。F1/F2/F3 聚焦已打开的面板；Shift+Tab 切换面板，Alt+M 切换工作模式。
- 宽终端并排展示三个面板；中等宽度将两个侧栏上下排列；窄终端显示当前面板。
- 点击思考或工具行展开，再次点击折叠。`/cot` 只设置新思考块的默认展开状态，不改动已有块，也不控制模型是否思考。
- Ctrl+T 切换当前面板最近的思考块；滚轮、PageUp/PageDown 查看历史，Ctrl+End 恢复跟随最新输出。
- Ctrl+C 请求取消当前任务；对话与缓存不会留下未完成的工具事务。配置和对话选择窗口中可用 Tab/Shift+Tab、Enter 操作，Esc 或 Ctrl+C 取消。
- `/trace` 显示或隐藏工具轨迹，所有 Agent 共用同一套展示基础设施。

UI 运行在 asyncio 事件循环中；模型 SSE、搜索、抓取、文件读写与任务控制在后台线程执行。请求执行期间仍可输入、排队、切换、折叠和滚动。共享世界的任务按队列串行处理，避免基于旧状态相互覆盖；底栏显示执行状态和排队数量。取消会在下一个流式片段或工具返回时生效。

`prompt_toolkit` 不可用、输入输出重定向或设置 `MINICHAT_PLAIN=1` 时，自动使用纯文本模式。该模式仍流式输出，但没有鼠标小窗；世界和预设操作使用对应命令。

## Agent 分工

### ChatAgent · harness.py

主聊天和工具调度，工具为 `search`、`fetch_page`。保留对话历史，按需压缩摘要。默认使用内置普通助手提示；也可以为每个预设配置：

```json
{
  "chat_agent": {
    "system_prompt": "你是这个世界的调查助手。当前日期：{date}。请先核对来源再回答。"
  }
}
```

`{date}` 会替换为世界日期，其他 JSON 花括号原样保留。留空恢复内置默认提示。可通过 `/edit-preset 修改 chat_agent.system_prompt ...` 编辑，下一轮主聊天即可使用。

### SearchAgent · search_agent.py

搜索与网页请求共用一个持续工作的 Agent、一个历史上下文。每个 turn 对应一次 `search` 或 `fetch_page` 请求；不再由代码预先调用真实网络，也没有固定的“搜索编辑 / 网页编辑 / 生成”流水线。

- `real_search`、`real_fetch`：自主多轮真实检索与抓取，返回候选文档及本轮 ref。
- `virtual_search`：用关键词或精确 URL 召回本预设已完成的搜索列表、结果条目及网页正文，也可列出最近缓存。
- `add_result`：复制已有 ref 或创建结果并加入列表；`select_results` 设置列表和顺序；`edit_result` 修改指定字段。
- `create_page`、`edit_page`：制作页面，或对真实/缓存页面做精确文本替换；`set_page` 将对应网页设为结果。
- `cache_search_results`、`cache_page`：显式暂存中间成品。
- `submit_search_results`、`submit_page`：提交相应成品并结束本轮；最终结果自动缓存。仅文字声称完成不会结束任务。
- `fail_request`：明确返回无法完成的原因；`inspect_world` 核对当前完整世界状态。

缓存可直接提交，也可与新搜索混合、修改后提交。所有 ref 仅本轮有效，跨轮必须重新召回。网页成品必须包含正文，且 URL 对应请求页面；辅助抓取不能代替目标页面。

搜索缓存保存完整列表，网页缓存保存标题、摘要与已完成正文。重新缓存搜索摘要不会清空已有正文。每个预设最多保留 100 组搜索缓存、200 个页面，超过时淘汰最早写入条目。相同查询或 URL 会更新原缓存。

历史中的世界状态通知、主对话增量与缓存内容共同供 Agent 参考。缓存可能早于剧情进展，Agent 应按最新 phase/canon 核对。预设中的 `search_editor`、`page_editor`、`page_generator` 继续作为编辑要求；旧 `max_added` 字段仅保留兼容建议，不强制先搜索，也不限制缓存召回数量。

### EnvAgent · env_agent.py

独立管理剧情与世界事实：`/next-stage [方向]` 推进剧情，`/edit-canon <要求>` 增删改 canon 并按需同步 phase。工具为 `inspect_world`、`add_fact`、`replace_fact`、`remove_fact`、`set_phase`、`advance_date`。

保留管理工作历史，每次注入最新世界状态，参考主对话摘要与近期消息。修改暂存到整轮成功才写入；编辑 canon 不推进日期，剧情单次最多前进三天。`/reset-canon` 恢复当前预设的初始世界，并清空其剧情、搜索与页面记忆。

### PresetAgent · preset_agent.py

独立处理 `/edit-preset`、`/make-preset`、`/import-preset`。工具为 `inspect_preset`、`set_value`、`append_item`、`remove_item`，逐项编辑 JSON，不整篇重写。修改预设不会重置当前世界已经演化出的 phase/canon。

`presets.py` 只负责配置 schema、加载、保存与状态隔离，不包含模型循环。`world.py` 为 EnvAgent 和 SearchAgent 提供共享状态、变更通知及原子提交。

## 对话保存与加载

- `/save [名称]` 保存并可重命名当前对话；每次操作结束也会自动保存。
- `/conversations` 列出当前预设下的存档。
- `/load [ID]` 加载对话；省略 ID 打开选择器。
- `/new [名称]` 新建对话，保留该预设当前的世界进度。
- `/preset <id>` 切换预设前，必须选择该预设下的已有对话或新建对话；可取消切换。启动时存在存档也会显示选择器。

存档位于 `conversations/<preset-id>/<conversation-id>.json`，保存 ChatAgent、SearchAgent、EnvAgent、PresetAgent 的工作历史、摘要、思考/工具轨迹和面板展开状态。恢复后可在对应侧栏继续先前的讨论；最新世界状态和预设配置优先于旧历史。

世界进度和缓存继续按预设保存在 `world_state.json`。加载旧对话会使用该预设当前世界状态，并通知 SearchAgent，不回滚世界；不同对话可共享同一预设的世界进展。`/reset` 只重置当前主聊天和 SearchAgent 的会话上下文。

## 模型与配置

所有 Agent 都通过 `agent_runtime.py` 的 SSE 入口接收正文、思考增量和工具轨迹。后台历史摘要、canon 压缩也走相同流式入口，内部输出默认折叠。

默认模型为 `deepseek-flash`，thinking 开启，reasoning effort 为 `max`。预算默认上下文 1,000,000 tokens、最大输出 384,000、压缩阈值 512,000，可通过 `.env` 中的 `DEEPSEEK_CONTEXT_WINDOW`、`DEEPSEEK_MAX_OUTPUT_TOKENS`、`DEEPSEEK_COMPACT_AT` 调整后重启。

预设来自 `presets/*.json`，支持 `MINICHAT_PRESET`、`MINICHAT_PRESET_DIR`、`MINICHAT_STATE_PATH`、`MINICHAT_CONVERSATION_DIR`。旧单预设世界状态仍可自动迁移。

## 验证

```powershell
python -m unittest discover -s tests -p "*_test.py" -v
python tests/preset_test.py
python tests/input_test.py
python tests/smoke_test.py
```

测试使用本地 mock，不调用真实模型或外网。涵盖多轮自主检索、缓存重启召回与修改、失败回滚、剧情编辑、全部 Agent 的流式回调、思考折叠、后台生成时 UI 响应、对话隔离、预设切换与配置保存；smoke 测试还验证 SSE 分片和 reasoning_content 回传。

`.env`、世界进度、对话存档、Python 缓存和临时测试输出均由 `.gitignore` 排除，源代码与可共享预设纳入本地 Git。
