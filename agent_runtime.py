"""所有 Agent 共用的 SSE、思考块和工具轨迹；渲染与模型请求分离。"""
from __future__ import annotations

import copy
import json
import threading
from dataclasses import asdict, dataclass

from deepseek_client import DeepSeekError, stream_chat as _stream_chat


@dataclass
class TraceBlock:
    id: int
    agent: str
    kind: str
    text: str = ""
    expanded: bool = True
    done: bool = False
    error: bool = False
    title: str = ""
    channel: str = "main"


class AgentCancelled(DeepSeekError):
    pass


class AgentTrace:
    def __init__(self, *, default_expanded: bool = False, sink=None):
        self.default_expanded = default_expanded
        self.tools_visible = True
        self.blocks: list[TraceBlock] = []
        self.sink = sink
        self.cancel_requested = False
        self.channel = "main"
        self._lock = threading.RLock()

    def _notify(self, event, block, text=""):
        if self.sink:
            self.sink(event, copy.copy(block), text)

    def start(self, agent, kind, *, expanded=None, title="", channel=None):
        with self._lock:
            if expanded is None:
                expanded = self.default_expanded if kind == "thought" else True
            block = TraceBlock(len(self.blocks) + 1, agent, kind, expanded=expanded, title=title)
            block.channel = channel or {"EnvAgent": "env", "PresetAgent": "preset", "ChatAgent": "main", "SearchAgent": "main"}.get(agent, self.channel)
            self.blocks.append(block)
            self._notify("start", block)
            return block.id

    def append(self, ident, text):
        if not text:
            return
        with self._lock:
            block = self.blocks[ident - 1]
            block.text += text
            self._notify("delta", block, text)

    def finish(self, ident, *, error=False):
        with self._lock:
            block = self.blocks[ident - 1]
            if block.done:
                if error and not block.error:
                    block.error = True
                    self._notify("error", block)
                return
            block.done, block.error = True, error
            self._notify("finish", block)

    def toggle(self, ident):
        with self._lock:
            block = self.blocks[ident - 1]
            block.expanded = not block.expanded
            self._notify("toggle", block)
            return block.expanded

    def snapshot(self):
        with self._lock:
            return [copy.copy(block) for block in self.blocks]

    def export(self):
        return [asdict(block) for block in self.snapshot()]

    def restore(self, values):
        with self._lock:
            blocks = []
            for value in values:
                if isinstance(value, dict) and value.get("kind") in ("thought", "content", "tool", "log"):
                    data = {key: value[key] for key in TraceBlock.__dataclass_fields__ if key in value}
                    data.update(id=len(blocks) + 1, done=True)
                    blocks.append(TraceBlock(**data))
            self.blocks = blocks
        if self.sink:
            self.sink("restore", TraceBlock(0, "", "log"), "")

    def log(self, text, *, channel=None):
        with self._lock:
            channel = channel or self.channel
            if self.blocks and self.blocks[-1].kind == "log" and self.blocks[-1].channel == channel:
                ident = self.blocks[-1].id
            else:
                ident = self.start("", "log", channel=channel)
            self.append(ident, text)

    def check_cancelled(self):
        if self.cancel_requested:
            raise AgentCancelled("请求已取消，未提交的修改不会保存。")


_trace = AgentTrace()


def get_trace() -> AgentTrace:
    return _trace


def set_trace(trace: AgentTrace) -> AgentTrace:
    global _trace
    old, _trace = _trace, trace
    return old


def stream_chat(messages, *, agent: str, content_visible=True, on_content=None, on_reasoning=None, **kwargs):
    """所有角色都经过此入口；/cot 只影响新思考块的默认展开状态。"""
    trace = get_trace()
    trace.check_cancelled()
    thought = trace.start(agent, "thought")
    output = None
    reasoning_seen = content_seen = False

    def reasoning(text):
        nonlocal reasoning_seen
        trace.check_cancelled()
        reasoning_seen = True
        trace.append(thought, text)
        if on_reasoning:
            on_reasoning(text)

    def content(text):
        nonlocal output, content_seen
        trace.check_cancelled()
        content_seen = True
        if output is None:
            trace.finish(thought)
            output = trace.start(agent, "content", expanded=content_visible)
        trace.append(output, text)
        if on_content:
            on_content(text)

    failed = False
    try:
        message = _stream_chat(messages, on_content=content, on_reasoning=reasoning, **kwargs)
        # mock 或非标准客户端可能只在最终消息里给出文本。
        if not reasoning_seen and message.get("reasoning_content"):
            reasoning(message["reasoning_content"])
        if not content_seen and message.get("content"):
            content(message["content"])
        return message
    except BaseException:
        failed = True
        raise
    finally:
        trace.finish(thought, error=failed)
        if output is not None:
            trace.finish(output, error=failed)


def execute_tool(agent, name, args, function):
    trace = get_trace()
    trace.check_cancelled()
    ident = trace.start(agent, "tool", expanded=False, title=name)
    trace.append(ident, "参数：\n" + json.dumps(args, ensure_ascii=False, indent=2) + "\n结果：\n")
    try:
        result = function()
        trace.check_cancelled()
    except BaseException as error:
        trace.append(ident, str(error) or type(error).__name__)
        trace.finish(ident, error=True)
        raise
    trace.append(ident, result if isinstance(result, str) else json.dumps(result, ensure_ascii=False, indent=2))
    failed = (isinstance(result, dict) and "error" in result) or (isinstance(result, str) and result.startswith("错误"))
    trace.finish(ident, error=failed)
    return result


def record_tool(agent, name, args, result):
    return execute_tool(agent, name, args, lambda: result)
