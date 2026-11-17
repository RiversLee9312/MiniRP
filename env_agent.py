"""EnvAgent：独立处理剧情推进、canon 编辑与世界重置。"""
from __future__ import annotations

import copy
import json
from datetime import date, timedelta

from agent_runtime import execute_tool, stream_chat
from agent_context import AgentContext
from world import WorldState

_WORLD_AGENT_SYSTEM = """你是 EnvAgent，负责剧情与世界事实管理，像编辑项目的助手一样工作。
先理解用户要求、世界状态和当前对话，再根据需要多轮调用工具检查、增删改事实及状态。
工具修改先暂存，完成后统一保存；工具报错时修正并重试。只改必要内容，保持事实之间一致。
世界设定及规则是约束；对话中的猜测、建议不自动成为已确立事实。
完成后直接用自然语言向用户说明实际做了什么及有意义的剧情变化；不要输出 JSON、模板化标题列表或虚构热搜。
若无法完成，解释原因，不要声称完成没有成功的修改。

【工作模式】{mode}
【世界设定与当前状态】
{world}
【最近检索记录】
{recent}
【剧情推进要求】
{guidance}
"""

_WORLD_AGENT_TOOLS = [
    {"type": "function", "function": {"name": "inspect_world", "description": "查看暂存的日期、世界状态和完整事实列表。", "parameters": {"type": "object", "properties": {}}}},
    {"type": "function", "function": {"name": "add_fact", "description": "添加一条确立的世界事实。", "parameters": {"type": "object", "properties": {"fact": {"type": "string"}}, "required": ["fact"]}}},
    {"type": "function", "function": {"name": "replace_fact", "description": "精确替换一条事实，old 必须与已有事实完全一致。", "parameters": {"type": "object", "properties": {"old": {"type": "string"}, "new": {"type": "string"}}, "required": ["old", "new"]}}},
    {"type": "function", "function": {"name": "remove_fact", "description": "精确删除一条事实，fact 必须与已有事实完全一致。", "parameters": {"type": "object", "properties": {"fact": {"type": "string"}}, "required": ["fact"]}}},
    {"type": "function", "function": {"name": "set_phase", "description": "修改当前世界状态，使其与事实一致。", "parameters": {"type": "object", "properties": {"phase": {"type": "string"}}, "required": ["phase"]}}},
    {"type": "function", "function": {"name": "advance_date", "description": "仅推进剧情时使用，将日期前进 0–3 天。", "parameters": {"type": "object", "properties": {"days": {"type": "integer", "minimum": 0, "maximum": 3}}, "required": ["days"]}}},
]


class EnvAgentError(RuntimeError):
    pass


class EnvAgent:
    def __init__(self, world: WorldState):
        self.world = world
        self.agent_context = AgentContext()

    def next_stage(self, prompt: str = "", on_event=None, *, conversation: str = "", on_reasoning=None) -> str:
        return self._turn("stage", prompt, on_event, conversation, on_reasoning)

    def reset_canon(self) -> None:
        self.world.reset()

    def edit_canon(self, prompt: str, on_event=None, *, conversation: str = "", on_reasoning=None) -> str:
        if not (prompt or "").strip():
            raise EnvAgentError("请输入 canon 修改要求")
        return self._turn("edit", prompt, on_event, conversation, on_reasoning)

    def _turn(self, mode: str, prompt: str, on_event, conversation: str, on_reasoning) -> str:
        emit = on_event or (lambda _event: None)
        prompt = (prompt or "").strip()
        mode_text = (
            "推进剧情：默认只推进一小步，依据用户方向与对话，兼容已确立事实；"
            "默认日期最多前进一天，用户明确要求时最多三天。"
            if mode == "stage" else
            "编辑 canon：按用户要求增删改事实，必要时同步修改 phase；不要推进剧情或日期。"
        )
        system = (_WORLD_AGENT_SYSTEM.replace("{mode}", mode_text)
                  .replace("{world}", self.world.world_block())
                  .replace("{recent}", self.world.recent_text())
                  .replace("{guidance}", self.world.config["stage"]["guidance"]))
        self.agent_context._compact_if_needed()
        if self.agent_context.summary:
            system += "\n【先前管理工作摘要】\n" + self.agent_context.summary
        system += "\n" + self.agent_context.memory_prompt
        messages = [{"role": "system", "content": system}] + copy.deepcopy(self.agent_context.history) + [
            {"role": "user", "content": "【当前对话参考】\n" + (conversation or "（暂无对话）")
             + "\n\n【用户要求】\n" + (prompt or "请自行推进一小步。")},
        ]
        state = copy.deepcopy(self.world.state)
        emit({"kind": "world_agent_start", "mode": mode})
        for _ in range(16):
            msg = stream_chat(messages, agent="EnvAgent", tools=self.agent_context.tools(_WORLD_AGENT_TOOLS), on_reasoning=on_reasoning)
            calls = msg.get("tool_calls") or []
            assistant = {"role": "assistant", "content": msg.get("content") or ""}
            if calls:
                assistant["tool_calls"] = calls
            if msg.get("reasoning_content"):
                assistant["reasoning_content"] = msg["reasoning_content"]
            messages.append(assistant)
            if not calls:
                answer = (msg.get("content") or "").strip()
                if not answer:
                    raise EnvAgentError("剧情 Agent 没有返回答复，修改未保存")
                if state != self.world.state:
                    if mode == "stage":
                        record = {"date": state["sim_date"], "prompt": prompt, "summary": answer,
                                  "phase": state["phase"]}
                        state["stages"] = (state["stages"] + [record])[-30:]
                    self.world.commit(state)
                emit({"kind": "world_agent_done"})
                self.agent_context.history = messages[1:]
                self.agent_context._compact_if_needed()
                return answer
            for call in calls:
                fn = call.get("function") or {}
                name = fn.get("name") or ""
                try:
                    args = json.loads(fn.get("arguments") or "{}")
                    if not isinstance(args, dict):
                        raise ValueError("参数必须是对象")
                    result = execute_tool("EnvAgent", name, args, lambda: self.agent_context.call_db(name, args) if self.agent_context.is_db_tool(name) else self._apply_world_tool(state, mode, name, args, self.world.sim_date))
                except (ValueError, TypeError) as e:
                    result = f"错误：{e}"
                emit({"kind": "world_agent_tool", "name": name, "result": result})
                messages.append({"role": "tool", "tool_call_id": call.get("id") or "", "content": self.agent_context.tool_result(result)})
        raise EnvAgentError("剧情 Agent 达到工具调用轮数上限，修改未保存")

    @staticmethod
    def _apply_world_tool(state: dict, mode: str, name: str, args: dict, initial_date: str) -> str:
        def required(field: str) -> str:
            value = args.get(field)
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f"{field} 必须是非空字符串")
            return value.strip()

        if name == "inspect_world":
            return json.dumps({k: state[k] for k in ("sim_date", "phase", "canon")}, ensure_ascii=False)
        if name == "add_fact":
            fact = required("fact")
            if fact in state["canon"]:
                raise ValueError("事实已存在")
            state["canon"].append(fact)
            return "已暂存新增事实：" + fact
        if name in ("replace_fact", "remove_fact"):
            old = required("old" if name == "replace_fact" else "fact")
            if state["canon"].count(old) != 1:
                raise ValueError("原事实不存在或不唯一，请先查看当前事实并精确复制")
            index = state["canon"].index(old)
            if name == "replace_fact":
                state["canon"][index] = required("new")
                return "已暂存替换事实。"
            state["canon"].pop(index)
            return "已暂存删除事实。"
        if name == "set_phase":
            state["phase"] = required("phase")
            return "已暂存世界状态。"
        if name == "advance_date":
            days = args.get("days")
            if mode != "stage":
                raise ValueError("编辑 canon 时不能推进日期")
            if type(days) is not int or not 0 <= days <= 3:
                raise ValueError("days 必须是 0 到 3 的整数")
            new_date = _advance_date(state["sim_date"], days)
            if (date.fromisoformat(new_date) - date.fromisoformat(initial_date)).days > 3:
                raise ValueError("单次剧情推进最多前进三天")
            state["sim_date"] = new_date
            return "已暂存日期：" + state["sim_date"]
        raise ValueError("未知工具：" + name)


def _advance_date(iso: str, days: int) -> str:
    return (date.fromisoformat(iso) + timedelta(days=days)).isoformat()
