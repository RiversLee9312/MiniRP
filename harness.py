"""一个极简的 chat-agent harness。

主 Agent（Chat）通过 function calling 调用 search / fetch_page；
检索与抓取走 DeepSeek Search / Fetch API，再由“编辑 Agent”按照当前 preset
自主完成请求。EnvAgent 管理剧情与事实，PresetAgent 编辑或创建预设。
"""
from __future__ import annotations

import copy
import json
import os
import sys

import term
import deepseek_client as client
from settings import SettingsStore
from conversations import ConversationError, ConversationStore
from agent_context import AgentContext
from deepseek_client import (
    COMPACT_AT,
    CONTEXT_WINDOW,
    DeepSeekError,
    MAX_OUTPUT_TOKENS,
    api_key,
)
from presets import PresetError, PresetStore
from search_agent import SearchAgent, SearchAgentError
from env_agent import EnvAgent, EnvAgentError
from preset_agent import PresetAgent
from world import WorldState
from agent_runtime import execute_tool, get_trace, stream_chat

BASE_SYSTEM = """Current date: {date}
User location: Unknown

You are Chat, a helpful AI assistant. You answer questions accurately and concisely, and you reply in the same language the user uses.

You have access to two tools:
- search: queries the public web and returns a list of results (title, source, date, link, snippet).
- fetch_page: opens a specific http/https page and returns its main text.

Use them whenever the user asks about news, current events, recent developments, official statements, or any factual detail you are not confident about. Search with focused queries; if a result looks important, open it with fetch_page to verify details. Search again if the results are thin or contradictory.

When you use retrieved material:
- Base your answer on what the results actually say; do not invent facts, numbers, names, or quotes.
- Results may include a credibility field (high / medium / low) and may contradict each other on details that are not confirmed. Weigh sources accordingly: official statements and wire services outrank mainstream media, which outrank preprints, social posts and forums.
- Cite the source name and, when useful, the title and link.
- If sources disagree, say so and explain which is more credible and why.
- Distinguish confirmed information from speculation or unverified claims; explicitly flag rumors that officials have denied.

For ordinary questions that do not require current information, answer directly without searching. Be direct and useful; avoid unnecessary caveats or dramatic language."""

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "search",
            "description": (
                "检索公开互联网。输入自然语言查询（中文或英文），由搜索 Agent 自主决定结果数量，"
                "包含标题、来源、日期、链接和摘要。适用于查询新闻、事实核查、官方声明、"
                "科学分析、时间线、市场反应等。"
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": "检索关键词或一句话查询，例如：最新 官方 通报",
                    }
                },
                "required": ["query"],
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "fetch_page",
            "description": (
                "抓取并读取一个网页的正文。可以用于打开 search 返回的链接、"
                "或用户给出的 http/https 地址，核实细节。"
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "url": {"type": "string", "description": "要读取的 http/https 链接"},
                },
                "required": ["url"],
            },
        },
    },
]

class ChatAgent(AgentContext):
    def __init__(self, search_agent: SearchAgent, *, history_budget: int | None = None, max_rounds: int = 6):
        self.search_agent = search_agent
        super().__init__(history_budget=history_budget)
        self.max_rounds = max_rounds

    # ---------- 上下文管理 ----------
    def _system_prompt(self) -> str:
        configured = self.search_agent.world.config.get("chat_agent", {}).get("system_prompt")
        base = (configured or BASE_SYSTEM).replace("{date}", self.search_agent.world.sim_date) + "\n\n" + self.memory_prompt
        if not self.summary:
            return base
        return base + "\n\nConversation summary so far:\n" + self.summary

    def _api_messages(self) -> list[dict]:
        return [{"role": "system", "content": self._system_prompt()}] + self.history

    def stage_context(self) -> str:
        """向剧情 Agent 提供摘要和近期对话，不包含工具输出或思维链。"""
        messages = [m for m in self.history if m.get("role") in ("user", "assistant") and m.get("content")]
        transcript = "\n".join(
            f"[{'用户' if m['role'] == 'user' else '助手'}] {m['content'].strip()}"
            for m in messages
        )
        if len(transcript) > 80000:
            transcript = "（较早的对话已省略）\n" + transcript[-80000:]
        parts = []
        if self.summary:
            parts.append("【较早对话摘要】\n" + self.summary)
        if transcript:
            parts.append("【近期对话】\n" + transcript)
        return "\n\n".join(parts)

    # ---------- 主循环 ----------
    def ask(
        self, user_text: str, trace: term.Trace | None = None
    ) -> str:
        previous = copy.deepcopy(self.history)
        try:
            return self._ask(user_text, trace)
        except BaseException:
            # 中断/异常不能留下缺少 tool result 的 assistant 消息。
            self.history = previous
            raise

    def _ask(self, user_text: str, trace: term.Trace | None) -> str:
        self.history.append({"role": "user", "content": user_text})
        trace = trace or term.Trace()

        for _ in range(self.max_rounds):
            msg = stream_chat(self._api_messages(), agent="ChatAgent", tools=self.tools(TOOLS), temperature=0.7)

            assistant: dict = {"role": "assistant", "content": msg.get("content") or ""}
            if msg.get("tool_calls"):
                assistant["tool_calls"] = msg["tool_calls"]
            # 官方文档：带 tools 的请求必须把历史每一轮的 reasoning_content 原样传回
            if msg.get("reasoning_content"):
                assistant["reasoning_content"] = msg["reasoning_content"]
            self.history.append(assistant)

            tool_calls = msg.get("tool_calls") or []
            if not tool_calls:
                answer = (msg.get("content") or "").strip() or "（模型没有返回内容）"
                self._compact_if_needed()
                return answer

            for call in tool_calls:
                result = execute_tool("ChatAgent", call.get("function", {}).get("name", ""),
                                      call.get("function", {}).get("arguments", "{}"), lambda: self._run_tool(call, trace))
                self.history.append(
                    {"role": "tool", "tool_call_id": call.get("id") or "", "content": self.tool_result(result)}
                )

        self._compact_if_needed()
        return "（工具调用轮数达到上限，先停在这里。你可以换个问法再试。）"

    def _run_tool(self, call: dict, trace: term.Trace) -> str:
        fn = call.get("function") or {}
        name = fn.get("name") or ""
        raw_args = fn.get("arguments") or "{}"
        try:
            args = json.loads(raw_args) if raw_args.strip() else {}
            if not isinstance(args, dict):
                args = {}
        except json.JSONDecodeError:
            args = {}

        if self.is_db_tool(name):
            return json.dumps(self.call_db(name, args), ensure_ascii=False)

        if name == "search":
            query = str(args.get("query") or "").strip()
            if not query:
                return "错误：search 需要一个非空的 query 参数。"
            try:
                return self.search_agent.search(query, on_event=trace.handle, conversation=self.stage_context())
            except (SearchAgentError, DeepSeekError) as e:
                trace.status.stop()
                term.error(f"检索失败：{e}")
                return f"检索失败：{e}"

        if name == "fetch_page":
            url = str(args.get("url") or "").strip()
            if not url:
                return "错误：fetch_page 需要一个非空的 url 参数。"
            try:
                return self.search_agent.open_url(url, on_event=trace.handle, conversation=self.stage_context())
            except (SearchAgentError, DeepSeekError) as e:
                trace.status.stop()
                term.error(f"网页抓取失败：{e}")
                return f"网页抓取失败：{e}"

        trace.status.stop()
        return f"错误：未知工具 {name}"


def _preset_event(ev: dict) -> None:
    if ev.get("kind") == "preset_done":
        term.info(f"预设已保存：{ev.get('name')}（{ev.get('id')}）")


def main(*, ui=None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    settings = SettingsStore()

    def edit_settings():
        if ui is None:
            term.info("配置窗口需要交互终端；可直接编辑项目 .env。")
            return False
        values, error = settings.current(), ""
        while True:
            selected = ui.configure_settings(values, error)
            if selected is None:
                return False
            try:
                settings.save(selected)
                import real_web
                real_web._cache.clear()
                term.info("模型与 API 配置已保存，下一次请求生效。")
                return True
            except (ValueError, OSError) as failure:
                values, error = selected, str(failure)

    try:
        api_key()
    except DeepSeekError as error:
        if ui is None:
            term.error(str(error))
            return 1
        if not edit_settings():
            return 0

    try:
        store = PresetStore()
        active_id = store.active_id
        config = store.get_config(active_id)
    except (PresetError, OSError) as e:
        term.error(f"加载设定失败：{e}")
        return 1

    preset_agent = PresetAgent(store)

    def make_agents(cfg: dict, pid: str):
        world = WorldState(cfg, store.get_state(pid),
                           on_save=lambda state, pid=pid: store.save_state(pid, state))
        return world, SearchAgent(world), EnvAgent(world)

    world, search_agent, env_agent = make_agents(config, active_id)
    agent = ChatAgent(search_agent)
    conversations = ConversationStore(store.state_path.parent / "conversations")
    conversation = conversations.new(active_id)

    def bind_context_dbs():
        for role, context in (("chat", agent), ("search", search_agent.agent_context),
                              ("env", env_agent.agent_context), ("preset", preset_agent.agent_context)):
            context.bind_db(conversations.db_path(active_id, conversation["id"], role))

    def save_conversation(name=""):
        if name:
            conversation["name"] = name
        conversation["chat"] = {"history": copy.deepcopy(agent.history), "summary": agent.summary}
        conversation["search"] = {"history": copy.deepcopy(search_agent.agent_context.history),
                                  "summary": search_agent.agent_context.summary,
                                  "conversation_snapshot": search_agent._conversation_snapshot}
        for key, context in (("env", env_agent.agent_context), ("preset", preset_agent.agent_context)):
            conversation[key] = {"history": copy.deepcopy(context.history), "summary": context.summary}
        conversation["trace"] = get_trace().export()
        if ui is not None:
            conversation["ui"] = ui.export_state()
        conversations.save(conversation)

    def restore_conversation(saved):
        nonlocal conversation, agent, search_agent
        conversation = saved
        agent = ChatAgent(search_agent)
        bind_context_dbs()
        for context, key in ((agent, "chat"), (search_agent.agent_context, "search"),
                             (env_agent.agent_context, "env"), (preset_agent.agent_context, "preset")):
            context.history = copy.deepcopy(saved.get(key, {}).get("history", []))
            context.summary = saved.get(key, {}).get("summary", "")
        search_agent._conversation_snapshot = saved.get("search", {}).get("conversation_snapshot", "")
        search_agent._update_cursor = len(world.updates)
        world.updates.append("【加载对话后的当前世界状态，优先于存档历史】\n" + world.world_block())
        get_trace().restore(saved.get("trace", []))
        if ui is not None:
            ui.restore_state(saved.get("ui", {}))
            ui.set_context(config, client.MODEL, world.sim_date, conversation["name"])

    def choose_conversation(pid):
        items = conversations.list(pid)
        if ui is not None:
            selection = ui.choose_conversation(pid, items)
        else:
            term.info(f"选择预设 {pid} 下的对话：")
            for item in items:
                print(f"  {item['id']}  {item['name']}  {item['updated_at']}")
            try:
                selection = input("输入对话 ID 加载，/new 新建，/cancel 取消 ❯ ").strip()
            except (EOFError, KeyboardInterrupt):
                return None
        if selection in (None, "/cancel"):
            return None
        if selection in ("/new", "__new__"):
            return conversations.new(pid)
        return conversations.load(pid, selection)

    trace = term.Trace(enabled=True)
    reader = term.LineReader(
        [
            ("/settings", "配置模型与 API Key"),
            ("/env", "打开世界管理侧栏"),
            ("/preset-agent", "打开预设编辑侧栏"),
            ("/save", "保存对话，可指定名称"),
            ("/load", "加载当前预设下的对话"),
            ("/new", "新建对话"),
            ("/conversations", "列出当前预设下的对话"),
            ("/preset", "查看/切换设定"),
            ("/edit-preset", "编辑当前设定"),
            ("/make-preset", "根据要求创建设定"),
            ("/import-preset", "从 Markdown 导入设定"),
            ("/next-stage", "推进一小步剧情"),
            ("/edit-canon", "按要求编辑当前世界事实"),
            ("/reset-canon", "重置当前世界进度"),
            ("/cot", "设置思考块默认展开状态"),
            ("/trace", "开关工具轨迹"),
            ("/canon", "查看已确立事实"),
            ("/reset", "清空对话上下文"),
            ("/help", "帮助"),
            ("/exit", "退出"),
        ]
    )
    if ui is not None:
        ui.set_commands(reader.commands)
    # 交互输入优先用 prompt_toolkit（跨终端可靠，带自动联想）；
    # 非 TTY 或在 Git Bash 等 pty 环境下也不会阻塞。
    session = None
    if ui is None and sys.stdin.isatty() and sys.stdout.isatty() and not os.environ.get("MINICHAT_PLAIN"):
        session = term.make_prompt_session(reader.commands)

    def show_banner(cfg: dict) -> None:
        if ui is not None:
            ui.set_context(cfg, client.MODEL, world.sim_date, conversation["name"])
            return
        print(
            term.banner(
                client.MODEL, client.THINKING, client.REASONING_EFFORT, world.sim_date,
                cfg.get("name", "Chat agent"),
                cfg.get("subtitle", ""),
            )
        )

    def activate(pid: str) -> bool:
        nonlocal world, search_agent, env_agent, agent, active_id, config
        cfg = store.get_config(pid)
        saved = choose_conversation(pid)
        if saved is None:
            return False
        save_conversation()
        store.set_active(pid)
        active_id = pid
        config = cfg
        world, search_agent, env_agent = make_agents(cfg, pid)
        restore_conversation(saved)
        show_banner(cfg)
        term.info(f"当前设定：{cfg['name']}（{pid}）｜对话：{conversation['name']}")
        return True

    if conversations.list(active_id):
        saved = choose_conversation(active_id)
        if saved is None:
            return 0
        restore_conversation(saved)
    bind_context_dbs()
    show_banner(config)
    if ui is None:
        term.info(
            f"context {CONTEXT_WINDOW:,} · max output {MAX_OUTPUT_TOKENS:,} · "
            f"压缩阈值 {COMPACT_AT:,} tokens"
        )
        print(
            f"{term.GREY}  SearchAgent 自主检索、抓取、召回缓存并提交结果。"
            f"{term.RESET}"
        )
        print(
            f"{term.GREY}  /help 查看命令 · 输入 / 列出命令 · /preset 切换设定 · "
            f"/next-stage 推进剧情{term.RESET}"
        )
        if ui is not None:
            term.info("点击思考/工具行展开或折叠；PageUp/PageDown 滚动，Ctrl+End 跟随最新输出。")
        elif session is not None:
            term.info("prompt_toolkit 输入已启用：输入 / 自动联想，Tab 补全，↑↓ 历史。")
        elif term.setup_readline(reader.commands):
            term.info("Tab 补全与 ↑↓ 历史已启用。")
        elif reader.native:
            term.info("行内联想已启用（Tab 补全 / ↑↓ 历史）。")
        else:
            term.info("当前终端使用标准输入；输入 / 可列出命令。")

    while True:
        try:
            if ui is not None:
                line = ui.read().strip()
            elif session is not None:
                line = session.prompt("你 ❯ ").strip()
            else:
                line = reader.read(term.user_prompt()).strip()
        except (EOFError, KeyboardInterrupt):
            print("\n再见。")
            break
        except (OSError, RuntimeError) as e:
            term.error(f"输入界面异常，改用标准输入：{e}")
            session = None
            continue
        try:
            if line == "/settings":
                edit_settings()
                continue
            if line in ("/env", "/preset-agent"):
                if ui is not None:
                    ui.open_sidebar("env" if line == "/env" else "preset")
                else:
                    term.info("世界管理：/edit-canon、/next-stage；预设管理：/edit-preset、/make-preset、/import-preset。")
                continue
            if line == "/save" or line.startswith("/save "):
                save_conversation(line[5:].strip())
                term.info(f"已保存对话：{conversation['name']}（{conversation['id']}）")
                continue
            if line == "/conversations":
                for item in conversations.list(active_id):
                    print(f"{item['id']}  {item['name']}  {item['updated_at']}")
                continue
            if line == "/new" or line.startswith("/new "):
                save_conversation()
                search_agent.reset_agent_context()
                restore_conversation(conversations.new(active_id, line[4:].strip()))
                term.info(f"已新建对话：{conversation['name']}")
                continue
            if line == "/load" or line.startswith("/load "):
                try:
                    saved = conversations.load(active_id, line[5:].strip()) if line[5:].strip() else choose_conversation(active_id)
                    if saved is not None:
                        save_conversation()
                        search_agent.reset_agent_context()
                        restore_conversation(saved)
                        term.info(f"已加载对话：{conversation['name']}")
                except (ConversationError, OSError) as error:
                    term.error(str(error))
                continue
            if not line:
                continue
            if line in ("/exit", "/quit", "exit", "quit"):
                print("再见。")
                break
            if line == "/":
                term.info("可用命令：")
                for cmd, desc in reader.commands:
                    print(f"  {cmd:<16} {desc}")
                continue
            if line == "/help":
                term.info(
                    "直接输入问题即可。/env、/preset-agent 打开侧栏，侧栏 /close 关闭。\n"
                    "/save [名称] 保存｜/load [ID] 加载｜/new [名称] 新建｜/conversations 列出对话。\n"
                    "命令：/preset [id] 查看/切换设定｜/edit-preset <要求> 编辑当前设定｜"
                    "/make-preset <要求> 创建设定｜"
                    "/import-preset <path.md> 导入设定｜/next-stage [方向] 推进剧情｜\n"
                    "/edit-canon <要求> 编辑事实｜/reset-canon 重置世界进度｜\n"
                    "/settings 模型与密钥｜/cot 设置默认展开状态｜/trace 工具轨迹｜/reset 清空对话｜/canon 已确立事实｜/exit 退出\n"
                    "输入 / 列出命令。Tab 补全由 prompt_toolkit 提供；不可用时退回标准输入"
                    "（MINICHAT_PLAIN=1 可强制标准输入）。"
                )
                continue

            if line == "/preset" or line.startswith("/preset "):
                arg = line[len("/preset") :].strip()
                if not arg:
                    term.info("可用设定：")
                    for item in store.list_presets():
                        marker = "*" if item["id"] == active_id else " "
                        print(f"  {marker} {item['id']:<18} {item['name']}  {item['subtitle']}")
                    term.info("用法：/preset <id> 切换｜/import-preset <path.md> 导入新设定")
                    continue
                if not store.exists(arg):
                    term.error(f"找不到设定：{arg}")
                    continue
                try:
                    activate(arg)
                except (ConversationError, PresetError, OSError) as e:
                    term.error(f"切换失败：{e}")
                continue

            if line.startswith("/edit-preset"):
                prompt = line[len("/edit-preset") :].strip()
                if not prompt:
                    term.info("用法：/edit-preset <修改要求>，例如：把 search 编辑范围改成只改天气相关的内容")
                    continue

                def preset_event_handler(ev: dict) -> None:
                    _preset_event(ev)

                try:
                    updated = preset_agent.edit(
                        prompt, on_event=preset_event_handler, preset_id=active_id
                    )
                except (ConversationError, PresetError, DeepSeekError, OSError) as e:
                    term.error(f"设定编辑失败：{e}")
                    continue
                # 共享世界对象，两个 Agent 会立即看到更新后的配置。
                world.update_config(updated)
                config = updated
                show_banner(updated)
                term.info("设定配置已更新；当前世界状态未重置（phase/canon 仍以 state 为准）。")
                continue

            if line == "/make-preset" or line.startswith("/make-preset "):
                prompt = line[len("/make-preset") :].strip()
                if not prompt:
                    term.info("用法：/make-preset <世界设定要求>")
                    continue
                try:
                    cfg = preset_agent.make(prompt, on_event=_preset_event, conversation=agent.stage_context())
                    if not activate(cfg["id"]):
                        continue
                except (ConversationError, PresetError, DeepSeekError, OSError) as e:
                    term.error(f"创建设定失败：{e}")
                    continue
                term.info(f"已创建并切换：{cfg['name']}（{cfg['id']}）")
                continue

            if line.startswith("/import-preset"):
                path = line[len("/import-preset") :].strip()
                if not path:
                    term.info("用法：/import-preset <path.md>")
                    continue

                def preset_event_handler(ev: dict) -> None:
                    _preset_event(ev)

                try:
                    cfg = preset_agent.import_markdown(path, on_event=preset_event_handler)
                except (ConversationError, PresetError, DeepSeekError, OSError) as e:
                    term.error(f"导入失败：{e}")
                    continue
                if not activate(cfg["id"]):
                    continue
                term.info(f"已导入并切换：{cfg['name']}（{cfg['id']}）")
                continue

            if line == "/next-stage" or line.startswith("/next-stage "):
                prompt = line[len("/next-stage") :].strip()
                try:
                    answer = env_agent.next_stage(prompt, on_event=trace.handle, conversation=agent.stage_context())
                except (EnvAgentError, DeepSeekError, OSError) as e:
                    trace.status.stop()
                    term.error(f"剧情推进失败：{e}")
                    continue
                agent.history.extend([{"role": "user", "content": "/next-stage " + prompt},
                                      {"role": "assistant", "content": answer}])
                if ui is not None:
                    ui.set_context(config, client.MODEL, world.sim_date, conversation["name"])
                continue
            if line == "/edit-canon" or line.startswith("/edit-canon "):
                prompt = line[len("/edit-canon") :].strip()
                if not prompt:
                    term.info("用法：/edit-canon <修改要求>")
                    continue
                try:
                    answer = env_agent.edit_canon(prompt, on_event=trace.handle, conversation=agent.stage_context())
                except (EnvAgentError, DeepSeekError, OSError) as e:
                    trace.status.stop()
                    term.error(f"事实编辑失败：{e}")
                    continue
                agent.history.extend([{"role": "user", "content": "/edit-canon " + prompt},
                                      {"role": "assistant", "content": answer}])
                if ui is not None:
                    ui.set_context(config, client.MODEL, world.sim_date, conversation["name"])
                continue
            if line == "/reset-canon":
                try:
                    env_agent.reset_canon()
                except OSError as e:
                    term.error(f"世界进度重置失败：{e}")
                    continue
                term.info("当前世界已恢复预设初始状态；剧情、检索和页面记忆已清空，对话保留。")
                continue
            if line == "/reset":
                agent.reset()
                search_agent.reset_agent_context()
                term.info("聊天和 SearchAgent 的消息历史已清空；session DB、世界状态与检索记忆保留。")
                continue
            if line == "/canon":
                term.info(
                    f"设定：{config['name']}（{active_id}）｜当前虚构日期：{world.sim_date}｜"
                    f"已确立事实 {len(world.canon)} 条｜剧情推进 {len(world.state.get('stages', []))} 次"
                )
                term.info(f"世界状态：{world.phase}")
                for i, fact in enumerate(world.canon, 1):
                    print(f"{i}. {fact}")
                continue
            if line == "/cot":
                get_trace().default_expanded = not get_trace().default_expanded
                term.info(f"新思考块默认：{'展开' if get_trace().default_expanded else '折叠'}（已有块保持原状态）")
                continue
            if line == "/trace":
                trace.enabled = not trace.enabled
                get_trace().tools_visible = trace.enabled
                if not trace.enabled:
                    trace.off()
                term.info(f"工具轨迹：{'开启' if trace.enabled else '关闭'}")
                continue

            if conversation["name"] == "新对话":
                conversation["name"] = line[:30]
            try:
                agent.ask(line, trace=trace)
            except DeepSeekError as e:
                trace.status.stop()
                term.error(f"API 错误：{e}")
                continue
            except KeyboardInterrupt:
                trace.status.stop()
                term.error("已中断")
                continue
            print()
        finally:
            try:
                save_conversation()
                if ui is not None:
                    ui.set_context(config, client.MODEL, world.sim_date, conversation["name"])
            except OSError as error:
                term.error(f"保存对话失败：{error}")

    return 0


if __name__ == "__main__":
    from terminal_ui import run_cli
    raise SystemExit(run_cli(main))
