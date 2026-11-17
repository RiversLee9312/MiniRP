"""极简 DeepSeek 客户端：只用标准库，兼容 OpenAI 的 /chat/completions 协议。

支持：普通对话、function/tool calling、JSON 输出、429/5xx 自动重试。
API Key 从环境变量 DEEPSEEK_API_KEY 读取，也支持项目目录下的 .env。
"""
from __future__ import annotations

import json
import os
import random
import time
import urllib.error
import urllib.request
from pathlib import Path

_RETRY_STATUS = {429, 500, 502, 503, 504}
_KEY: str | None = None


def _load_dotenv() -> None:
    path = Path(__file__).with_name(".env")
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        name, value = line.split("=", 1)
        name = name.strip()
        value = value.strip().strip('"').strip("'")
        # 不覆盖已存在的真实环境变量
        os.environ.setdefault(name, value)


_load_dotenv()

BASE_URL = os.environ.get("DEEPSEEK_BASE_URL", "https://api.deepseek.com").rstrip("/")
MODEL = os.environ.get("DEEPSEEK_MODEL", "deepseek-flash")  # 官方文档当前可用模型
THINKING = os.environ.get("DEEPSEEK_THINKING", "enabled")  # enabled / disabled
REASONING_EFFORT = os.environ.get("DEEPSEEK_REASONING_EFFORT", "max")  # low/high/max


def apply_settings(values: dict) -> None:
    """设置窗口保存成功后调用；后续请求使用新配置。"""
    global BASE_URL, MODEL, THINKING, REASONING_EFFORT, _KEY
    os.environ.update(values)
    BASE_URL = os.environ.get("DEEPSEEK_BASE_URL", "https://api.deepseek.com").rstrip("/")
    MODEL = os.environ.get("DEEPSEEK_MODEL", "deepseek-flash")
    THINKING = os.environ.get("DEEPSEEK_THINKING", "enabled")
    REASONING_EFFORT = os.environ.get("DEEPSEEK_REASONING_EFFORT", "max")
    _KEY = None


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, default))
    except (TypeError, ValueError):
        return default


# DeepSeek-V4.1-Flash-1017（模型名 deepseek-flash）：1M 上下文、最大输出 384K。
# 所有 Agent 共用这套预算；默认在上下文估算达到 512K token 时触发压缩。
CONTEXT_WINDOW = _env_int("DEEPSEEK_CONTEXT_WINDOW", 1_000_000)
MAX_OUTPUT_TOKENS = min(
    _env_int("DEEPSEEK_MAX_OUTPUT_TOKENS", 384_000),
    384_000,
    max(1_024, CONTEXT_WINDOW // 2),
)
COMPACT_AT = min(
    _env_int("DEEPSEEK_COMPACT_AT", 512_000),
    max(1_024, CONTEXT_WINDOW - MAX_OUTPUT_TOKENS),
)


def estimate_tokens(text: str) -> int:
    """粗略 token 估算：CJK 按 1 字≈1 token，其他按 4 字符≈1 token。"""
    if not text:
        return 0
    cjk = 0
    other = 0
    for ch in text:
        o = ord(ch)
        if (
            0x3400 <= o <= 0x4DBF
            or 0x4E00 <= o <= 0x9FFF
            or 0x3040 <= o <= 0x30FF
            or 0xAC00 <= o <= 0xD7AF
            or 0xF900 <= o <= 0xFAFF
        ):
            cjk += 1
        else:
            other += 1
    return cjk + max(1, other // 4)


def estimate_messages_tokens(messages: list[dict]) -> int:
    total = 0
    for m in messages:
        total += estimate_tokens(m.get("content") or "") + 8
        total += estimate_tokens(m.get("reasoning_content") or "")
        if m.get("tool_calls"):
            total += estimate_tokens(json.dumps(m["tool_calls"], ensure_ascii=False))
    return total


class DeepSeekError(RuntimeError):
    def __init__(self, message: str, status: int | None = None, body: str = ""):
        super().__init__(message)
        self.status = status
        self.body = body


def api_key() -> str:
    global _KEY
    if _KEY:
        return _KEY
    key = os.environ.get("DEEPSEEK_API_KEY") or os.environ.get("DEEPSEEK_KEY")
    if not key:
        raise DeepSeekError(
            "未找到 API Key。请设置环境变量 DEEPSEEK_API_KEY，"
            "或在项目目录的 .env 中写入一行：DEEPSEEK_API_KEY=sk-..."
        )
    _KEY = key.strip().strip('"').strip("'")
    return _KEY


def _post(payload: dict, timeout: float) -> dict:
    url = BASE_URL + "/chat/completions"
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={
            "Content-Type": "application/json",
            "Authorization": "Bearer " + api_key(),
            "Accept": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        body = ""
        try:
            body = e.read().decode("utf-8", "replace")
        except Exception:
            pass
        raise DeepSeekError(f"HTTP {e.code}: {body[:600]}", status=e.code, body=body) from e
    except urllib.error.URLError as e:
        raise DeepSeekError(f"网络错误: {e.reason}", status=None) from e
    except TimeoutError as e:
        raise DeepSeekError("请求超时", status=None) from e


def chat(
    messages: list[dict],
    *,
    tools: list[dict] | None = None,
    tool_choice: str | None = None,
    temperature: float = 0.7,
    max_tokens: int = MAX_OUTPUT_TOKENS,
    response_format: dict | None = None,
    thinking: str | None = None,
    reasoning_effort: str | None = None,
    model: str | None = None,
    timeout: float = 120.0,
    retries: int = 4,
) -> dict:
    """调用 chat/completions，返回 choices[0].message。"""
    payload = _payload(
        messages,
        tools=tools,
        tool_choice=tool_choice,
        temperature=temperature,
        max_tokens=max_tokens,
        response_format=response_format,
        thinking=thinking,
        reasoning_effort=reasoning_effort,
        model=model,
    )
    last: DeepSeekError | None = None
    for attempt in range(retries):
        try:
            data = _post(payload, timeout)
            choices = data.get("choices") or []
            if not choices:
                raise DeepSeekError(f"响应中没有 choices: {json.dumps(data)[:400]}")
            msg = choices[0].get("message")
            if not isinstance(msg, dict):
                raise DeepSeekError("响应格式异常")
            return msg
        except DeepSeekError as e:
            last = e
            retryable = e.status is None or e.status in _RETRY_STATUS
            if attempt >= retries - 1 or not retryable:
                raise
            time.sleep((2 ** attempt) + random.random())
    raise last or DeepSeekError("未知错误")


def _payload(
    messages: list[dict],
    *,
    tools: list[dict] | None,
    tool_choice: str | None,
    temperature: float,
    max_tokens: int,
    response_format: dict | None,
    thinking: str | None,
    reasoning_effort: str | None,
    model: str | None,
    stream: bool = False,
) -> dict:
    payload: dict = {
        "model": model or MODEL,
        "messages": messages,
        "temperature": temperature,
        "max_tokens": max(1, min(max_tokens, MAX_OUTPUT_TOKENS)),
        "stream": stream,
    }
    if tools:
        payload["tools"] = tools
        payload["tool_choice"] = tool_choice or "auto"
    if response_format:
        payload["response_format"] = response_format
    # 官方文档：thinking 通过 {"type": "enabled" | "disabled"} 控制；
    # 开启时用 reasoning_effort（low/high/max）控制思考挡位。
    mode = thinking if thinking is not None else THINKING
    if mode:
        payload["thinking"] = {"type": mode}
        if mode == "enabled":
            payload["reasoning_effort"] = reasoning_effort or REASONING_EFFORT
    return payload


def stream_chat(
    messages: list[dict],
    *,
    on_content=None,
    on_reasoning=None,
    tools: list[dict] | None = None,
    tool_choice: str | None = None,
    temperature: float = 0.7,
    max_tokens: int = MAX_OUTPUT_TOKENS,
    response_format: dict | None = None,
    thinking: str | None = None,
    reasoning_effort: str | None = None,
    model: str | None = None,
    timeout: float = 300.0,
    retries: int = 3,
) -> dict:
    """SSE 流式调用。通过回调实时吐出增量，返回组装好的 assistant message。

    组装后的 message 可直接 append 回 messages（含 reasoning_content / tool_calls）。
    """
    payload = _payload(
        messages,
        tools=tools,
        tool_choice=tool_choice,
        temperature=temperature,
        max_tokens=max_tokens,
        response_format=response_format,
        thinking=thinking,
        reasoning_effort=reasoning_effort,
        model=model,
        stream=True,
    )
    url = BASE_URL + "/chat/completions"
    key = api_key()

    for attempt in range(retries):
        content_parts: list[str] = []
        reasoning_parts: list[str] = []
        tool_slots: dict[int, dict] = {}
        emitted = False

        try:
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            req = urllib.request.Request(
                url,
                data=data,
                headers={
                    "Content-Type": "application/json",
                    "Authorization": "Bearer " + key,
                    "Accept": "text/event-stream",
                },
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                for raw_line in resp:
                    line = raw_line.decode("utf-8", "replace").strip()
                    if not line or not line.startswith("data:"):
                        continue
                    chunk = line[5:].strip()
                    if chunk == "[DONE]":
                        break
                    try:
                        obj = json.loads(chunk)
                    except json.JSONDecodeError:
                        continue
                    choices = obj.get("choices") or []
                    if not choices:
                        continue
                    delta = choices[0].get("delta") or {}

                    text = delta.get("content")
                    if text:
                        emitted = True
                        content_parts.append(text)
                        if on_content:
                            on_content(text)
                    rtext = delta.get("reasoning_content")
                    if rtext:
                        emitted = True
                        reasoning_parts.append(rtext)
                        if on_reasoning:
                            on_reasoning(rtext)

                    for tc in delta.get("tool_calls") or []:
                        idx = tc.get("index", 0)
                        slot = tool_slots.setdefault(
                            idx,
                            {"id": "", "type": "function", "function": {"name": "", "arguments": ""}},
                        )
                        if tc.get("id"):
                            slot["id"] = tc["id"]
                            emitted = True
                        fn = tc.get("function") or {}
                        name = fn.get("name") or ""
                        args = fn.get("arguments") or ""
                        if name:
                            slot["function"]["name"] += name
                            emitted = True
                        if args:
                            slot["function"]["arguments"] += args
                            emitted = True

            msg: dict = {"role": "assistant", "content": "".join(content_parts)}
            if reasoning_parts:
                msg["reasoning_content"] = "".join(reasoning_parts)
            if tool_slots:
                msg["tool_calls"] = [tool_slots[i] for i in sorted(tool_slots)]
            return msg

        except DeepSeekError:
            if not emitted and attempt < retries - 1:
                time.sleep((2 ** attempt) + random.random())
                continue
            raise
        except urllib.error.HTTPError as e:
            body = ""
            try:
                body = e.read().decode("utf-8", "replace")
            except Exception:
                pass
            err = DeepSeekError(f"HTTP {e.code}: {body[:600]}", status=e.code, body=body)
            if not emitted and e.code in _RETRY_STATUS and attempt < retries - 1:
                time.sleep((2 ** attempt) + random.random())
                continue
            raise err from e
        except urllib.error.URLError as e:
            err = DeepSeekError(f"网络错误: {e.reason}", status=None)
            if not emitted and attempt < retries - 1:
                time.sleep((2 ** attempt) + random.random())
                continue
            raise err from e
        except TimeoutError as e:
            raise DeepSeekError("请求超时", status=None) from e
    raise DeepSeekError("流式请求失败")
