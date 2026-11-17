# minichat · 虚构世界搜索 harness

一个带终端 TUI 的多 Agent 对话程序。主助手接收搜索和网页请求，SearchAgent 自主决定真实检索、抓取、虚拟缓存召回与内容编辑的步骤，完成后通过工具提交结果。

搜索结果与页面可能包含模型生成的虚构内容，适合小说、推演和产品演示，不代表真实信息。

## 启动

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
npm ci
.venv\Scripts\python.exe harness.py
```

Linux / WSL 使用 `python3` 创建环境，再用 `.venv/bin/python`。本地浏览器需要 Node.js 18+ 和 Chrome；可通过 `MINICHAT_BROWSER=msedge` 使用 Edge。仅 API 模式不需要浏览器组件。默认读取 `.env`；也可以在 TUI 中输入 `/settings`，配置模型、API Key、API 地址、thinking、reasoning effort，以及网页抓取后端。首次缺少密钥时会直接打开配置窗口。

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
- 输入框随软换行和多行输入自动增高，最多 8 行，之后在框内滚动并跟随光标。Enter 发送，Alt+Enter 或 Ctrl+J 换行。
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

历史中的世界状态通知、主对话增量与缓存内容共同供 Agent 参考。缓存可能早于剧情进展，Agent 应按最新 phase/canon 核对。预设中的 `search_editor`、`page_editor`、`page_generator` 继续作为编辑要求；旧 `max_added`、`max_visible` 字段仅保留兼容建议，不限制最终结果数量；条目数量完全由 SearchAgent 按任务决定。真实搜索 API 的单次请求上限仍属于服务端约束，Agent 可以多次检索组合结果。

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

每个对话还包含同名目录，四个 Agent 的 DB 分别位于 `conversations/<preset>/<id>/agents/<role>.sqlite`；临时 FetchAgent 的 DB 位于该目录的 `fetch/` 下。迁移存档时一起复制 JSON 与同名目录。

世界进度和缓存继续按预设保存在 `world_state.json`。加载旧对话会使用该预设当前世界状态，并通知 SearchAgent，不回滚世界；不同对话可共享同一预设的世界进展。`/reset` 只重置当前主聊天和 SearchAgent 的消息历史，保留 session DB；`/new` 才创建互相隔离的新会话 DB。

## 统一 session 上下文与 DB

ChatAgent、SearchAgent、EnvAgent、PresetAgent 和临时 FetchAgent 都复用 `AgentContext`，拥有自己的 SQLite DB。数据库主键是 Agent 自己命名的稳定字符串，例如 `notes/next-step`、`sources/官方通报`；不使用 embedding 或向量检索。

所有 Agent 获得同一套工具：

- `db_list(prefix, after, limit)`：按字符串键列目录，支持字符串游标。
- `db_read(key, start, length)`：按字符范围读取正文，返回总长度、revision、next_offset。
- `db_write(key, content, expected_revision)`：创建条目；覆盖已有条目需要 revision。
- `db_patch(key, old_string, new_string, replace_all, expected_revision)`：唯一片段精确替换，报错后可重读重试。
- `db_delete(key, expected_revision)`：按版本删除。
- `db_search(query, scope, prefix, limit, offset)`：搜索索引字符串、正文或两者，返回短匹配片段与条目键。

检索使用 SQLite FTS5 trigram 的字面子串搜索；1–2 字符查询退回字面扫描，兼容中文短词。数据库没有 FTS5 时也可直接扫描。没有 RAG 模型调用。此行为依据 [SQLite FTS5 文档](https://www.sqlite.org/fts5.html)，并在当前 Python 的 SQLite 3.49.1 上实测。

所有会话使用同一套压缩机制：计入历史、摘要、DB 索引提示和工具 schema 的预算；超长工具结果先完整归档到 DB，再返回预览和读取键。压缩前保存完整原始消息，摘要保留关键字符串键，压缩失败则保留原历史。单轮很长时也会压缩，不再依赖“消息数量超过 12”这个条件。历史摘要与 canon 压缩任务使用同一套流式和 DB 工具，沿用发起者的 session DB。

DB 工具的成功写入立即持久化；它记录 Agent 的工作记忆，包括尚未成功的尝试。世界事实和预设配置仍保持各自的事务边界，不会因为写笔记就自动成为 canon。

## 网页后端与临时 FetchAgent

`/settings` 或 `.env` 的 `MINICHAT_FETCH_BACKEND` 提供三个模式：

- `api`：只用 DeepSeek Fetch API。
- `local`：只用本地真实浏览器和临时 FetchAgent。
- `api_then_local`：默认模式。先 API；明确失败、超时或空正文后回退本地，保留两次尝试的来源与错误。

SearchAgent 仍使用 `real_fetch`。可显式传入 `backend="local"` 绕过默认路由，重抓怀疑不完整的页面。DeepSeek [Fetch API 当前公开 schema](https://api-docs.deepseek.com/api/fetch/)只提供 title/content，未提供可靠的截断标记，因此 API 的 complete/truncated 为 unknown，不能用正文长度猜测。项目原来的额外 12000 字符截断已移除。

本地模式直接复用 Microsoft 的 `@playwright/mcp`，不重新实现浏览器驱动或元素引用。浏览器以无头、隔离 profile 和 sandbox 模式启动；MCP 进程通过异步 stdio 通信，不占用 TUI 事件循环，也不使用用户的登录 profile。当前锁定 MCP 0.0.85，依赖版本由 package-lock.json 固定。

临时 FetchAgent 能使用官方 schema 的导航、快照、查找、点击、悬停、填表、按键、下拉选择、条件等待、标签页和页面内 evaluate 工具。每次操作后的快照会提供当前状态；如果 MCP 将快照落到文件，适配层把内容读回 Agent 的 DB。没有把只含文件名的观察结果交给模型。宿主进程任意代码执行、工作区文件访问和网页自行注册的工具不暴露给模型。

额外只有两个任务工具：`capture_page` 与 `finish_fetch`。前者使用框架内固定的 Playwright 代码获取当前页面及 iframe 的真实 DOM，保存原始 HTML，再用 [markdownify](https://github.com/matthewwithanm/python-markdownify) 转换完整 Markdown，修正相对链接。后者提交指定 DB 条目作为成品正文，summary 是单独的可选总结；不能用总结代替全文。

本地正文不受 DS API 的输出长度限制，也不设固定正文字符截断。长文完整存储在 DB 中，工具回复只分页预览，可通过 `db_read`/`db_search` 读取全文。没有成功加载的 iframe、无限滚动尚未加载的内容、登录墙等仍需明确报告 complete=false；“不截断”不等于能获得站点尚未提供的数据。

## 网页工具设计调研与选择

公开实现有几类值得复用的设计：

- [Playwright MCP](https://playwright.dev/mcp/introduction)：快照 → 元素引用 → 操作 → 新快照。无需视觉模型；[引用会在页面变化后失效](https://playwright.dev/mcp/snapshots)，失效后应重新观察。适合本项目使用普通文本工具调用的临时抓取 Agent，因此直接采用。
- [agent-browser](https://github.com/vercel-labs/agent-browser/blob/main/docs/content/docs/quick-start.mdx)：相同的 snapshot/ref 思路以 CLI 暴露，可给 coding agent 用 shell 驱动。适合已有终端执行环境的编码 Agent；本项目已有原生 function calling，选 MCP 减少额外命令解析层。
- [OpenAI computer/browser drivers](https://developers.openai.com/api/docs/guides/tools-computer-use-sdk-drivers) 与 [Anthropic computer use](https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use-tool)：语义浏览器接口与截图、鼠标、键盘的 GUI 路线分开。截图适合作为没有可访问 DOM 的补充；首版以 AX 快照和语义操作为主，不要求模型猜像素。
- [Microsoft UFO 的混合工具层](https://microsoft.github.io/UFO/mcp/overview/)：办公 Agent 将状态采集和动作执行分开，结合 UI 自动化与应用 API。对本项目的启发是分开“完整页面采集”和“Agent 对页面的判断与整理”，不能让摘要覆盖原文。
- [Browser Use 当前工具设计](https://docs.browser-use.com/open-source/tools)也支持给 Agent 添加自定义工具，并将浏览器操作放入独立执行环境；没有把其旧版本的工具列表当作当前接口照搬。

CEF 可以通过 [off-screen rendering 与独立消息循环](https://chromiumembedded.github.io/cef/general_usage)实现，但会新增原生运行时、消息泵和分发维护。旧 cefpython 的兼容矩阵与本项目 Python 3.13 不匹配。既然需求是可靠的 Agent 网页工具而不是嵌入原生 UI，本项目直接采用已实测的 Playwright/Chromium 路线。

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

模型相关测试使用本地 mock，不调用真实模型或外网。local_browser_test 还启动隔离的真实无头 Chrome，与本机 HTTP fixture 交互，验证展开全文、元素引用、链接和 16 万字符长文末尾完整性。涵盖多轮自主检索、缓存重启召回与修改、失败回滚、剧情编辑、全部 Agent 的流式回调、思考折叠、后台生成时 UI 响应、对话隔离、预设切换与配置保存；smoke 测试还验证 SSE 分片和 reasoning_content 回传。

`.env`、世界进度、对话存档、Python 缓存和临时测试输出均由 `.gitignore` 排除，源代码与可共享预设纳入本地 Git。
