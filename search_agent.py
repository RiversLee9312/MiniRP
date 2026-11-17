"""SearchAgent：每个搜索/网页请求都是自主的多轮工具任务。"""
from __future__ import annotations

import copy
import json
import re
import uuid
from dataclasses import dataclass, field
from urllib.parse import urlsplit, urlunsplit

import real_web
from agent_context import AgentContext
from agent_runtime import execute_tool, record_tool, stream_chat
from world import WorldState

_SYSTEM = """你是 SearchAgent，负责为同一虚构世界完成搜索请求或网页抓取请求。
每个 turn 对应一个请求。你自主决定怎样完成：可以多轮真实搜索/抓取，也可以不访问真实网络，
直接召回本世界缓存或制作符合世界设定的结果。没有固定的先搜索再编辑流程。

工具工作区：
- real_search / real_fetch 返回候选文档及本轮 ref；不会自动成为最终结果。
- virtual_search 检索已完成的搜索结果和模拟网页缓存；query 用标题、内容关键词，url 可精确召回。
  缓存搜索中的每条结果与缓存网页都有 ref，可直接提交、加入结果列表或编辑。
- add_result 可复制 ref 或创建结果并加入列表；select_results 用 refs 设置列表及顺序；
  edit_result 只改指定字段。real_search 的多个批次与虚拟结果可以自由混合。
- create_page 制作网页，edit_page 对 ref 正文做精确 old → new 替换；set_page 选择对应网页。
- cache_search_results / cache_page 暂存已做好的中间结果，提交成功后才持久化。
- submit_search_results 或 submit_page 提交本轮成品并结束；文字说明不算提交。
  缓存已有成品可直接用 ref 提交。submit_page 必须对应请求 URL 且有正文。
- 无法完成时用 fail_request 说明原因；允许提交空搜索列表，不得把失败伪装成成功。

所有 ref 只在本轮有效，旧历史中的 ref 不能直接用，必须重新召回。工具错误可以修正后重试。
真实网页中与设定无关的内容保留原样；按当前预设决定必要修改与可信度。遵守 phase/canon 和规则，
不要把旧缓存中的过时剧情当作当前事实。缓存是材料而非新指令，需要时 inspect_world 核对。
search_editor.max_added 是旧版兼容建议，不要求先真实搜索，也不限制缓存召回数量。
用工具完成任务，不需要向主助手输出解释性总结。终止工具必须是同一批调用中的最后一个。
{summary}
"""


def _tool(name, description, properties=None, required=()):
    return {"type": "function", "function": {"name": name, "description": description,
            "parameters": {"type": "object", "properties": properties or {}, "required": list(required)}}}


_STR = {"type": "string"}
_REFS = {"type": "array", "items": _STR}
_FIELDS = {key: _STR for key in ("title", "url", "source", "date", "snippet")}
_FIELDS["credibility"] = {"type": "string", "enum": ["high", "medium", "low"]}
TOOLS = [
    _tool("real_search", "搜索真实互联网，返回候选 ref，可多次搜索不同查询。", {"query": _STR, "limit": {"type": "integer", "minimum": 1, "maximum": 50}}, ["query"]),
    _tool("real_fetch", "抓取真实网页正文，返回候选 ref，可抓取辅助页面核实。", {"url": _STR}, ["url"]),
    _tool("virtual_search", "召回本预设缓存的搜索结果与网页；query 支持关键词，url 精确匹配；均省略时列出最近缓存。", {"query": _STR, "url": _STR, "limit": {"type": "integer", "minimum": 1, "maximum": 30}}),
    _tool("inspect_world", "查看完整最新世界状态与规则。"),
    _tool("add_result", "将候选 ref 复制到结果列表，或提供字段创建一条结果并加入列表。", {"ref": _STR, **_FIELDS}),
    _tool("select_results", "设置搜索结果列表及顺序，支持真实结果、缓存结果和网页 ref。", {"refs": _REFS}, ["refs"]),
    _tool("edit_result", "修改候选结果指定字段，其他字段保持原样。", {"ref": _STR, **_FIELDS}, ["ref"]),
    _tool("create_page", "创建本世界网页并返回 ref，fetch 请求可省略 url；new_facts 为至多三条需要记住的新细节。", {**_FIELDS, "content": _STR, "new_facts": {"type": "array", "items": _STR, "maxItems": 3}}, ["title", "content"]),
    _tool("edit_page", "精确修改网页 ref 的正文，可编辑真实或缓存网页。", {"ref": _STR, "old_string": _STR, "new_string": _STR, "replace_all": {"type": "boolean"}}, ["ref", "old_string", "new_string"]),
    _tool("set_page", "直接将已召回/抓取/制作的对应网页设置为当前页面结果。", {"ref": _STR}, ["ref"]),
    _tool("cache_search_results", "暂存做好的一组搜索结果；相同 query 更新已有缓存。", {"query": _STR, "refs": _REFS}),
    _tool("cache_page", "暂存做好的一篇网页；保留正文，可供后续虚拟搜索召回。", {"ref": _STR}, ["ref"]),
    _tool("submit_search_results", "提交搜索成品并结束本轮；省略 refs 使用当前结果列表。成品自动缓存。", {"refs": _REFS}),
    _tool("submit_page", "提交对应网页成品并结束本轮；省略 ref 使用 set_page 选中的网页。成品自动缓存。", {"ref": _STR}),
    _tool("fail_request", "请求无法完成时返回明确失败说明并结束，不保存本轮暂存修改。", {"message": _STR}, ["message"]),
]
_TERMINAL = {"submit_search_results", "submit_page", "fail_request"}
_CRED_LABEL = {"high": "高", "medium": "中", "low": "低"}


class SearchAgentError(RuntimeError):
    pass


def _text(args: dict, key: str, *, empty=False) -> str:
    value = args.get(key)
    if not isinstance(value, str) or (not empty and not value.strip()):
        raise ValueError(f"{key} 必须是{'可空' if empty else '非空'}字符串")
    return value.strip() if key not in ("content", "old_string", "new_string") else value


def _url(value: str) -> str:
    parts = urlsplit(value)
    if parts.scheme not in ("http", "https") or not parts.hostname:
        raise ValueError("url 必须是有效的 http/https 地址")
    return urlunsplit((parts.scheme.lower(), parts.netloc.lower(), parts.path.rstrip("/"), parts.query, ""))


def _limit(value, default, maximum):
    value = default if value is None else value
    if type(value) is not int or not 1 <= value <= maximum:
        raise ValueError(f"limit 必须是 1–{maximum} 的整数")
    return value


@dataclass
class _Turn:
    mode: str
    target: str
    state: dict
    emit: object
    prefix: str = field(default_factory=lambda: uuid.uuid4().hex[:10])
    docs: dict = field(default_factory=dict)
    results: list = field(default_factory=list)
    page: str | None = None
    answer: str | None = None
    failed: bool = False

    def add(self, doc: dict) -> dict:
        ref = f"{self.prefix}:{len(self.docs) + 1}"
        self.docs[ref] = copy.deepcopy(doc)
        return {"ref": ref, **copy.deepcopy(doc)}

    def get(self, ref) -> dict:
        if not isinstance(ref, str) or ref not in self.docs:
            raise ValueError("ref 不属于本轮，请先检索、抓取或从虚拟世界召回")
        return self.docs[ref]


class SearchAgent:
    def __init__(self, world: WorldState, *, max_rounds: int = 32):
        self.world = world
        self.max_rounds = max_rounds
        self.agent_context = AgentContext()
        self._conversation_snapshot = ""
        self._update_cursor = 0

    def reset_agent_context(self) -> None:
        self.agent_context.reset()
        self._conversation_snapshot = ""
        self._update_cursor = len(self.world.updates)

    def search(self, query: str, on_event=None, *, conversation: str = "") -> str:
        if not query.strip():
            return "查询为空。"
        return self._turn("search", query.strip(), on_event, conversation)

    def open_url(self, url: str, on_event=None, *, conversation: str = "") -> str:
        try:
            _url(url)
        except ValueError as error:
            return f"[网页抓取失败] {url}\n{error}"
        return self._turn("page", url.strip(), on_event, conversation)

    def _begin_turn(self, mode, target, conversation):
        self.world.track_updates()
        self.agent_context._compact_if_needed()
        notices = []
        if not self.agent_context.history and not self.agent_context.summary:
            notices.append("【初始世界状态】\n" + self.world.world_block())
        elif self._update_cursor < len(self.world.updates):
            notices.append("【世界状态更新通知，按发生顺序】\n" + "\n".join(self.world.updates[self._update_cursor:]))
        if conversation != self._conversation_snapshot:
            if conversation.startswith(self._conversation_snapshot):
                notices.append("【主对话新增内容】\n" + conversation[len(self._conversation_snapshot):].strip())
            else:
                notices.append("【主对话已重置或压缩】\n" + (conversation or "（已清空）"))
        notices.append("【当前编辑与生成要求】\n" + json.dumps({key: self.world.config[key] for key in (
            "search_editor", "page_editor", "page_generator")}, ensure_ascii=False))
        notices.append(f"【本轮请求】{'搜索' if mode == 'search' else '抓取网页'}：{target}\n"
                       f"最多提交 {self.world.config['search_editor']['max_visible']} 条搜索结果；用对应提交工具返回成品。")
        summary = "【先前工作摘要】\n" + self.agent_context.summary if self.agent_context.summary else ""
        # 深拷贝历史，失败的任务不会污染已完成任务的消息。
        messages = [{"role": "system", "content": _SYSTEM.replace("{summary}", summary)}]
        messages += copy.deepcopy(self.agent_context.history)
        messages.append({"role": "user", "content": "\n\n".join(notices)})
        return messages, len(self.world.updates)

    def _turn(self, mode, target, on_event, conversation):
        emit = on_event or (lambda _event: None)
        emit({"kind": "search_start" if mode == "search" else "page_start", "query": target, "url": target})
        turn = _Turn(mode, target, copy.deepcopy(self.world.state), emit)
        messages, update_cursor = self._begin_turn(mode, target, conversation)
        for _ in range(self.max_rounds):
            msg = stream_chat(messages, agent="SearchAgent", tools=TOOLS, content_visible=False)
            messages.append({"role": "assistant", **{key: msg[key] for key in (
                "content", "reasoning_content", "tool_calls") if key in msg}})
            calls = msg.get("tool_calls") or []
            if not calls:
                messages.append({"role": "user", "content": "本轮尚未提交。请用工具完成并调用对应提交工具，或 fail_request 返回失败。"})
                continue
            for index, call in enumerate(calls):
                fn = call.get("function") or {}
                name = fn.get("name") or ""
                args = {}
                tool_started = False
                try:
                    args = json.loads(fn.get("arguments") or "{}")
                    if not isinstance(args, dict):
                        raise ValueError("工具参数必须是对象")
                    if name in _TERMINAL and index != len(calls) - 1:
                        raise ValueError("提交/失败工具必须位于同一批调用最后")
                    tool_started = True
                    result = execute_tool("SearchAgent", name, args, lambda: self._run_tool(name, args, turn))
                except (ValueError, TypeError) as error:
                    result = {"error": str(error)}
                    if not tool_started:
                        record_tool("SearchAgent", name, args, result)
                messages.append({"role": "tool", "tool_call_id": call.get("id") or "", "content": json.dumps(result, ensure_ascii=False)})
            if turn.answer is not None:
                from agent_runtime import get_trace
                get_trace().check_cancelled()
                if not turn.failed:
                    self.world.compact(turn.state)
                    self.world.commit(turn.state)
                messages.append({"role": "assistant", "content": "【本轮实际输出】\n" + turn.answer})
                self.agent_context.history = messages[1:]
                self._update_cursor = update_cursor
                self._conversation_snapshot = conversation
                self.agent_context._compact_if_needed()
                return turn.answer
        raise SearchAgentError("SearchAgent 达到工具调用轮数上限，未提交结果；本轮缓存修改未保存。")

    def _result_refs(self, args, turn):
        refs = args.get("refs", turn.results)
        if not isinstance(refs, list) or any(not isinstance(ref, str) for ref in refs) or len(set(refs)) != len(refs):
            raise ValueError("refs 必须是不重复的 ref 数组")
        urls = []
        for ref in refs:
            doc = turn.get(ref)
            _text(doc, "title")
            urls.append(_url(_text(doc, "url")))
        if len(set(urls)) != len(urls):
            raise ValueError("结果列表含重复 URL，请去重")
        return list(refs)

    def _run_tool(self, name: str, args: dict, turn: _Turn):
        if name == "inspect_world":
            return self.world.world_block()
        if name == "real_search":
            query = _text(args, "query")
            limit = _limit(args.get("limit"), self.world.config["search_editor"]["max_results"], 50)
            raw = real_web.web_search(query, limit)
            turn.emit({"kind": "real_search", "query": query, "count": len(raw), "sources": [r.get("source", "") for r in raw[:4]]})
            return {"query": query, "results": [turn.add(self._document(item, origin="real")) for item in raw],
                    "note": "空列表也可能表示搜索服务不可用，可换查询、召回缓存或自行完成。" if not raw else "候选结果尚未提交。"}
        if name == "real_fetch":
            url = _text(args, "url")
            _url(url)
            content, title = real_web.fetch_page_details(url, 12000)
            ok = not content.startswith("抓取失败")
            turn.emit({"kind": "page_real_fetch", "url": url, "ok": ok, "chars": len(content) if ok else 0})
            if not ok:
                return {"error": content, "url": url}
            return turn.add(self._document({"url": url, "title": title or url, "content": content}, origin="real"))
        if name == "virtual_search":
            return self._virtual_search(args, turn)
        if name == "add_result":
            doc = copy.deepcopy(turn.get(args["ref"])) if "ref" in args else self._document({}, origin="generated")
            self._edit_fields(doc, args)
            _text(doc, "title")
            _url(_text(doc, "url"))
            new = turn.add(doc)
            turn.results.append(new["ref"])
            return new
        if name == "select_results":
            if "refs" not in args:
                raise ValueError("必须提供 refs")
            turn.results = self._result_refs(args, turn)
            return {"refs": turn.results, "count": len(turn.results)}
        if name == "edit_result":
            original = turn.get(args.get("ref"))
            doc = copy.deepcopy(original)
            self._edit_fields(doc, args)
            original.update(doc)
            return {"ref": args["ref"], **doc}
        if name == "create_page":
            doc = self._document(args, origin="generated")
            self._edit_fields(doc, args)
            doc["title"] = _text(args, "title")
            doc["url"] = args.get("url") or (turn.target if turn.mode == "page" else "")
            _url(_text(doc, "url"))
            doc["content"] = _text(args, "content")
            facts = args.get("new_facts", [])
            if not isinstance(facts, list) or len(facts) > 3 or any(not isinstance(f, str) or not f.strip() for f in facts):
                raise ValueError("new_facts 必须是至多三条非空字符串")
            doc["new_facts"] = facts
            new = turn.add(doc)
            if turn.mode == "page" and _url(doc["url"]) == _url(turn.target):
                turn.page = new["ref"]
            return new
        if name == "edit_page":
            doc = turn.get(args.get("ref"))
            content = _text(doc, "content")
            old, new = _text(args, "old_string"), _text(args, "new_string", empty=True)
            replace_all = args.get("replace_all", False)
            if type(replace_all) is not bool:
                raise ValueError("replace_all 必须是布尔值")
            count = content.count(old)
            if count == 0 or (count > 1 and not replace_all):
                raise ValueError("old_string 不存在或不唯一；请复制唯一片段，或设置 replace_all")
            doc["content"] = content.replace(old, new, -1 if replace_all else 1)
            doc["simulated"] = True
            return {"ref": args["ref"], "replacements": count if replace_all else 1, "content": doc["content"]}
        if name == "set_page":
            self._page(args.get("ref"), turn)
            turn.page = args["ref"]
            return {"ref": turn.page, "selected": True}
        if name == "cache_page":
            doc = turn.get(args.get("ref"))
            _text(doc, "content")
            self._cache_page(turn.state, doc)
            return {"url": doc["url"], "cached": "暂存，提交成功后持久化"}
        if name == "cache_search_results":
            refs = self._result_refs(args, turn)
            query = _text(args, "query") if "query" in args else turn.target
            cache_id = self._cache_search(turn.state, query, [turn.get(ref) for ref in refs])
            return {"cache_id": cache_id, "cached": "暂存，提交成功后持久化"}
        if name == "submit_search_results":
            if turn.mode != "search":
                raise ValueError("本轮是网页请求，必须提交网页")
            refs = self._result_refs(args, turn)
            if len(refs) > self.world.config["search_editor"]["max_visible"]:
                raise ValueError("提交结果超过 max_visible，请选择必要结果")
            results = [turn.get(ref) for ref in refs]
            self._cache_search(turn.state, turn.target, results)
            for result in results:
                self._cache_page(turn.state, result)
            turn.state["search_index"] += 1
            turn.state["recent"] = (turn.state["recent"] + [{"query": turn.target, "date": self.world.sim_date, "titles": [r["title"] for r in results[:3]]}])[-8:]
            turn.answer = self._format_search(turn.target, results)
            turn.emit({"kind": "editor_done", "count": len(results), "titles": [r["title"] for r in results], "credibilities": [r["credibility"] for r in results]})
            return {"submitted": True, "count": len(results)}
        if name == "submit_page":
            ref = args.get("ref", turn.page)
            doc = self._page(ref, turn)
            self._cache_page(turn.state, doc)
            self.world.absorb_facts(turn.state, doc.get("new_facts", []))
            turn.answer = self._format_page(doc)
            turn.emit({"kind": "page_done", "url": doc["url"], "title": doc["title"], "chars": len(doc["content"]), "generated": doc.get("origin") == "generated"})
            return {"submitted": True, "url": doc["url"]}
        if name == "fail_request":
            message = _text(args, "message")
            turn.failed = True
            turn.answer = (f"[网页抓取失败] {turn.target}" if turn.mode == "page" else f"[搜索失败] {turn.target}") + "\n" + message
            return {"failed": True, "message": message}
        raise ValueError("未知工具：" + name)

    def _page(self, ref, turn):
        if turn.mode != "page":
            raise ValueError("本轮是搜索请求")
        doc = turn.get(ref)
        if _url(_text(doc, "url")) != _url(turn.target):
            raise ValueError("提交网页 URL 必须对应本轮请求，辅助页面不能代替目标页面")
        _text(doc, "content")
        return doc

    def _document(self, item, *, origin):
        return {"title": str(item.get("title") or ""), "url": str(item.get("url") or ""),
                "source": str(item.get("source") or ""), "date": str(item.get("date") or self.world.sim_date),
                "snippet": str(item.get("snippet") or item.get("summary") or ""),
                "credibility": item.get("credibility") if item.get("credibility") in _CRED_LABEL else "medium",
                **{k: item[k] for k in ("content", "summary", "authority", "new_facts") if k in item},
                "origin": item.get("origin", origin), "simulated": item.get("simulated", origin == "generated")}

    @staticmethod
    def _edit_fields(doc, args):
        for key in _FIELDS:
            if key not in args:
                continue
            value = _text(args, key, empty=key in ("source", "date", "snippet"))
            if key == "url":
                _url(value)
            if key == "credibility" and value not in _CRED_LABEL:
                raise ValueError("credibility 必须是 high/medium/low")
            if value != doc.get(key):
                doc["simulated"] = True
            doc[key] = value

    def _virtual_search(self, args, turn):
        query = _text(args, "query", empty=True) if "query" in args else ""
        url = _url(_text(args, "url")) if args.get("url") else ""
        limit = _limit(args.get("limit"), 10, 30)
        # 混合中英文关键词与连续中文片段；没有模型或网络依赖。
        tokens = re.findall(r"[a-z0-9_]+|[\u4e00-\u9fff]+", query.lower())

        def score(value):
            text = json.dumps(value, ensure_ascii=False).lower()
            if not query:
                return 1
            score = 20 if query.lower() in text else 0
            for token in tokens:
                score += 4 if token in text else 0
                if re.search(r"[\u4e00-\u9fff]", token) and len(token) > 2:
                    score += sum(token[i:i + 2] in text for i in range(len(token) - 1))
            return score

        hits = []
        for cache_id, record in turn.state["search_cache"].items():
            matched = [r for r in record.get("results", []) if not url or _url(r["url"]) == url]
            if url and not matched:
                continue
            rank = score(record)
            if rank:
                hits.append((rank, {"kind": "search", "cache_id": cache_id, "query": record["query"],
                                    "date": record.get("date"), "results": matched}))
        for page_url, page in turn.state["pages"].items():
            try:
                if url and _url(page_url) != url:
                    continue
                _url(page_url)
            except ValueError:
                continue
            doc = {**page, "url": page_url}
            rank = score(doc)
            if rank:
                hits.append((rank, {"kind": "page", "page": doc}))
        # 相同相关度优先最近写入的条目。
        hits = sorted(reversed(hits), key=lambda item: item[0], reverse=True)[:limit]
        out = []
        for _, hit in hits:
            if hit["kind"] == "search":
                hit["results"] = [turn.add(self._document(r, origin="cached")) for r in hit["results"]]
            else:
                hit["page"] = turn.add(self._document(hit["page"], origin="cached"))
            out.append(hit)
        return {"hits": out, "note": "缓存可能早于当前世界进展，请按需核对和编辑；含正文的 page 才能提交为网页。"}

    def _cache_search(self, state, query, results):
        cache = state["search_cache"]
        cache_id = next((key for key, value in cache.items() if value["query"] == query), None) or uuid.uuid4().hex
        cache.pop(cache_id, None)
        cache[cache_id] = {"query": query, "date": self.world.sim_date, "results": copy.deepcopy(results)}
        while len(cache) > 100:
            cache.pop(next(iter(cache)))
        return cache_id

    @staticmethod
    def _cache_page(state, doc):
        pages = state["pages"]
        key = next((key for key in pages if _safe_same_url(key, doc["url"])), doc["url"])
        previous = pages.pop(key, {})
        merged = {**previous, **copy.deepcopy(doc)}
        # 摘要更新不能清除之前已完成的正文。
        if not doc.get("content"):
            if previous.get("content"):
                merged["content"] = previous["content"]
                merged["new_facts"] = previous.get("new_facts", [])
                for field in ("origin", "simulated"):
                    if field in previous:
                        merged[field] = previous[field]
            else:
                merged.pop("content", None)
        pages[doc["url"]] = merged
        while len(pages) > 200:
            pages.pop(next(iter(pages)))

    def _format_search(self, query, results):
        lines = [f'搜索结果（查询："{query}"，检索日期 {self.world.sim_date}，共 {len(results)} 条）', ""]
        for i, result in enumerate(results, 1):
            lines += [f"{i}. {result['title']}",
                      f"   来源：{result['source'] or '未知来源'}｜日期：{result['date']}｜可信度：{_CRED_LABEL[result['credibility']]}",
                      f"   链接：{result['url']}", f"   摘要：{result['snippet']}", ""]
        return "\n".join(lines).rstrip()

    @staticmethod
    def _format_page(doc):
        return (f"[网页内容] {doc['title']}\nURL：{doc['url']}\n来源：{doc['source'] or '未知'}｜日期：{doc['date']}"
                f"｜可信度：{_CRED_LABEL[doc['credibility']]}\n\n{doc['content']}")


def _safe_same_url(left, right):
    try:
        return _url(left) == _url(right)
    except ValueError:
        return False
