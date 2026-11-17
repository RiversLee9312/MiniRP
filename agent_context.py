"""聊天和 SearchAgent 共用的对话历史与摘要压缩。"""
from deepseek_client import COMPACT_AT, DeepSeekError, estimate_messages_tokens
from agent_runtime import stream_chat

_SUMMARY_SYSTEM = """你负责压缩 Agent 历史，保留用户要求、重要事实、时间线、来源和已完成的操作。
保留虚构世界中有用的检索、页面细节和最新世界状态变化；新通知与旧记录冲突时保留新通知。
不要编造。丢弃重复内容和无关工具细节。
不超过 500 字，用要点列表。"""


def render_transcript(messages: list[dict]) -> str:
    lines = []
    for m in messages:
        role = m.get("role")
        content = (m.get("content") or "").strip()
        if role == "user":
            lines.append(f"[用户] {content}")
        elif role == "assistant":
            calls = m.get("tool_calls") or []
            names = ", ".join(f"{(c.get('function') or {}).get('name', '?')}({(c.get('function') or {}).get('arguments', '')})" for c in calls)
            lines.append(f"[助手] {content}" + (f"（调用工具：{names}）" if names else ""))
        elif role == "tool":
            lines.append(f"[工具结果] {content[:800]}")
    return "\n".join(lines)


class AgentContext:
    def __init__(self, *, history_budget: int | None = None):
        self.history: list[dict] = []
        self.summary = ""
        self.history_budget = history_budget if history_budget is not None else COMPACT_AT

    def reset(self) -> None:
        self.history.clear()
        self.summary = ""

    def _history_tokens(self) -> int:
        return estimate_messages_tokens(self.history)

    def _compact_if_needed(self) -> None:
        if self._history_tokens() < self.history_budget or len(self.history) <= 12:
            return
        split = len(self.history) - 8
        while split > 1 and self.history[split].get("role") != "user":
            split -= 1
        if split <= 1:
            return
        old = self.history[:split]
        try:
            summary = self._summarize(old)
        except DeepSeekError:
            return
        if not summary.strip():
            return
        self.summary = summary
        self.history = self.history[split:]

    def _summarize(self, messages: list[dict]) -> str:
        parts = []
        if self.summary:
            parts.append("【已有摘要】\n" + self.summary)
        parts.append("【新的对话记录】\n" + render_transcript(messages))
        msg = stream_chat([{"role": "system", "content": _SUMMARY_SYSTEM},
                    {"role": "user", "content": "\n\n".join(parts)}], agent="HistorySummary", content_visible=False)
        return (msg.get("content") or "").strip()
