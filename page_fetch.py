"""网页后端路由：API、本地浏览器、API 失败后本地回退。"""
from __future__ import annotations

import os

import real_web
from agent_runtime import AgentCancelled


def fetch_document(url, *, backend="auto", context=None):
    selected = os.environ.get("MINICHAT_FETCH_BACKEND", "api_then_local") if backend == "auto" else backend
    if selected not in ("api", "local", "api_then_local"):
        raise ValueError("backend 必须是 auto/api/local/api_then_local")
    attempts = []
    if selected in ("api", "api_then_local"):
        content, title = real_web.fetch_page_details(url, None)
        if content.strip() and not content.startswith("抓取失败"):
            return {"url": url, "requested_url": url, "title": title or url, "content": content, "backend": "api",
                    "complete": None, "truncated": None,
                    "note": "API 未提供完整性/截断标记；若怀疑缺失，可显式 backend=local 重新抓取。", "attempts": [{"backend": "api", "ok": True}]}
        attempts.append({"backend": "api", "ok": False, "error": content or "空正文"})
        if selected == "api" or os.environ.get("MINICHAT_OFFLINE") == "1":
            return {"error": content or "空正文", "url": url, "attempts": attempts}
    try:
        from fetch_agent import FetchAgent
        result = FetchAgent(context).fetch(url)
        result["attempts"] = attempts + [{"backend": "local", "ok": True}]
        return result
    except AgentCancelled:
        raise
    except Exception as error:
        # MCP 的任务组可能包装异常；保留原始错误和取消语义。
        while isinstance(error, BaseExceptionGroup) and error.exceptions:
            error = error.exceptions[0]
        if isinstance(error, AgentCancelled):
            raise error
        attempts.append({"backend": "local", "ok": False, "error": str(error)})
        return {"error": "本地网页抓取失败：" + str(error), "url": url, "attempts": attempts}
