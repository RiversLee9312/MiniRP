"""基于 DeepSeek Search / Fetch API 的真实检索与抓取。

官方文档：
- POST https://api.deepseek.com/v1/tools/search
- POST https://api.deepseek.com/v1/tools/fetch
鉴权与 /chat/completions 相同，使用 DEEPSEEK_API_KEY。

同一账号 Search 100 QPS、Fetch 25 QPS；按成功请求计费（$0.75 / 1000 次）。
"""
from __future__ import annotations

import gzip
import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import deepseek_client as client
from deepseek_client import api_key

_OFFLINE = os.environ.get("MINICHAT_OFFLINE") == "1"
_SEARCH_FIXTURE = os.environ.get("MINICHAT_SEARCH_FIXTURE")  # 测试用：从 JSON 文件返回固定结果

_RETRY_STATUS = {408, 429, 500, 502, 503, 504}
_cache: dict[str, list[dict]] = {}


class WebAPIError(RuntimeError):
    def __init__(self, message: str, status: int | None = None):
        super().__init__(message)
        self.status = status


def _api_post(url: str, payload: dict, *, timeout: float = 30.0, retries: int = 3) -> dict:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    last_error: WebAPIError | None = None
    for attempt in range(retries):
        req = urllib.request.Request(
            url,
            data=data,
            headers={
                "Authorization": "Bearer " + api_key(),
                "Content-Type": "application/json",
                "Accept": "application/json",
                "Accept-Encoding": "gzip",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
                if (resp.headers.get("Content-Encoding") or "").lower() == "gzip":
                    raw = gzip.decompress(raw)
                return json.loads(raw.decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = ""
            try:
                body = e.read().decode("utf-8", "replace")
            except Exception:
                pass
            message = body[:500]
            try:
                obj = json.loads(body)
                message = str((obj.get("error") or {}).get("message") or message)
            except (json.JSONDecodeError, AttributeError):
                pass
            last_error = WebAPIError(f"HTTP {e.code}: {message}", status=e.code)
            if e.code in _RETRY_STATUS and attempt < retries - 1:
                time.sleep((2 ** attempt) + 0.2)
                continue
            raise last_error from e
        except urllib.error.URLError as e:
            last_error = WebAPIError(f"网络错误: {e.reason}")
            if attempt < retries - 1:
                time.sleep((2 ** attempt) + 0.2)
                continue
            raise last_error from e
        except TimeoutError as e:
            raise WebAPIError("请求超时") from e
    raise last_error or WebAPIError("请求失败")


def _credibility_from_authority(authority) -> str:
    try:
        a = int(authority)
    except (TypeError, ValueError):
        return "medium"
    if a >= 3:
        return "high"
    if a == 0:
        return "low"
    return "medium"


def _domain(url: str) -> str:
    try:
        return (urllib.parse.urlsplit(url).hostname or "").removeprefix("www.")
    except ValueError:
        return ""


def _short_date(value: str | None) -> str:
    if not value:
        return ""
    return str(value)[:10]


def web_search(query: str, limit: int = 6) -> list[dict]:
    """调用 DeepSeek Search API。失败或离线时返回空列表。"""
    query = (query or "").strip()
    if not query:
        return []
    limit = max(1, min(50, int(limit)))

    if _SEARCH_FIXTURE:
        try:
            data = json.loads(Path(_SEARCH_FIXTURE).read_text(encoding="utf-8"))
            return data[:limit]
        except (OSError, json.JSONDecodeError):
            return []

    if _OFFLINE:
        return []
    query = query[:128]  # 文档限制 1–128 个 Unicode 字符
    cache_key = f"{query}|{limit}"
    if cache_key in _cache:
        return _cache[cache_key]

    try:
        data = _api_post(client.BASE_URL + "/v1/tools/search", {"query": query, "count": limit})
    except WebAPIError:
        return []

    out: list[dict] = []
    seen_urls: set[str] = set()
    seen_titles: set[str] = set()
    for item in data.get("results") or []:
        if not isinstance(item, dict):
            continue
        title = str(item.get("title") or "").strip()
        url = str(item.get("url") or "").strip()
        if not title or not url:
            continue
        url_key = url.rstrip("/").lower()
        title_key = re.sub(r"\s+", " ", title).strip().lower()
        if url_key in seen_urls or title_key in seen_titles:
            continue
        seen_urls.add(url_key)
        seen_titles.add(title_key)
        site_name = str(item.get("site_name") or "").strip() or _domain(url)
        out.append(
            {
                "title": title,
                "url": url,
                "snippet": str(item.get("snippet") or "").strip(),
                "summary": str(item.get("summary") or "").strip(),
                "content": str(item.get("content") or "").strip(),
                "source": site_name,
                "date": _short_date(item.get("date")),
                "authority": item.get("authority"),
                "credibility": _credibility_from_authority(item.get("authority")),
                "rerank_score": item.get("rerank_score"),
            }
        )
    _cache[cache_key] = out
    return out


def format_search(results: list[dict]) -> str:
    """把真实搜索结果整理成给研究 Agent 的文本。"""
    if not results:
        return "（没有检索到结果，可能是查询太窄或搜索服务暂时不可用）"
    lines = []
    for i, r in enumerate(results, 1):
        lines.append(f"{i}. {r['title']}")
        lines.append(
            f"   来源：{r['source']}｜日期：{r['date'] or '未知'}"
            f"｜权威等级：{r.get('authority')}"
        )
        lines.append(f"   链接：{r['url']}")
        if r.get("snippet"):
            lines.append(f"   摘要：{r['snippet'][:220]}")
        if r.get("summary"):
            lines.append(f"   详细摘要：{r['summary'][:420]}")
    return "\n".join(lines)


def _is_safe_url(url: str) -> bool:
    try:
        parts = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    return parts.scheme in ("http", "https") and bool(parts.hostname)


def fetch_page_details(url: str, max_chars: int | None = None) -> tuple[str, str]:
    """调用 DeepSeek Fetch API，返回 (Markdown 正文, 标题)；失败时正文为错误说明。"""
    if _OFFLINE:
        return "抓取失败：离线测试模式", ""
    url = (url or "").strip()
    if not _is_safe_url(url):
        return "抓取失败：不支持的 URL。", ""

    try:
        data = _api_post(client.BASE_URL + "/v1/tools/fetch", {"url": url}, timeout=30, retries=1)
    except WebAPIError as e:
        return f"抓取失败：{e}", ""

    content = str(data.get("content") or "").strip()
    title = str(data.get("title") or "").strip()
    if not content:
        return "抓取失败：页面没有可提取的正文。", title
    return content[:max_chars] if max_chars is not None else content, title


def fetch_page(url: str, max_chars: int = 4000) -> str:
    return fetch_page_details(url, max_chars)[0]


if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1 and sys.argv[1].startswith("http"):
        print(fetch_page(sys.argv[1]))
    else:
        q = " ".join(sys.argv[1:]) or "unidentified flying objects"
        for r in web_search(q):
            print(json.dumps(r, ensure_ascii=False, indent=2))
