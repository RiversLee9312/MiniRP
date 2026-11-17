"""离线验证自主工具循环、跨轮缓存、网页精确编辑及事务边界。"""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from agent_context import AgentContext
from deepseek_client import DeepSeekError
from presets import PresetStore, default_state
from search_agent import SearchAgent, SearchAgentError
from world import WorldState

URL = "https://example.com/news"
ITEM = {"title": "轨道观测", "url": URL, "source": "观测站", "snippet": "六个物体仍在轨道上", "credibility": "high"}


def call(name, args=None, ident="c1"):
    return {"id": ident, "type": "function", "function": {"name": name, "arguments": json.dumps(args or {}, ensure_ascii=False)}}


def response(*calls):
    return {"content": "", "reasoning_content": "先检查，再完成请求。", "tool_calls": list(calls)}


def tool_results(messages):
    return [json.loads(m["content"]) for m in messages if m["role"] == "tool"]


class SearchAgentTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = PresetStore(ROOT / "presets", Path(self.tmp.name) / "world.json")
        self.cfg = self.store.get_config("ufo-2026")
        self.world = WorldState(self.cfg, default_state(self.cfg), lambda state: self.store.save_state(self.cfg["id"], state))
        self.agent = SearchAgent(self.world)
        self.web = patch("search_agent.real_web.web_search", return_value=[ITEM]).start()
        self.fetch = patch("search_agent.real_web.fetch_page_details", return_value=("原句。无关的公司财报不变。", "轨道观测")).start()
        self.addCleanup(patch.stopall)

    def generated_search(self, query="轨道观测"):
        with patch("search_agent.stream_chat", side_effect=[response(call("add_result", ITEM)), response(call("submit_search_results"))]):
            return self.agent.search(query)

    def test_no_automatic_network_and_explicit_submission(self):
        out = self.generated_search()
        self.web.assert_not_called()
        self.fetch.assert_not_called()
        self.assertIn("轨道观测", out)
        self.assertEqual(self.world.state["search_index"], 1)
        saved = self.store.get_state(self.cfg["id"])
        self.assertTrue(saved["search_cache"])
        self.assertEqual(saved["pages"][URL]["title"], ITEM["title"])

    def test_agent_decides_result_count_without_legacy_visible_cap(self):
        self.cfg["search_editor"]["max_visible"] = 1
        calls = [call("add_result", {**ITEM, "url": f"https://example.com/{i}", "title": f"结果 {i}"}, str(i)) for i in range(18)]
        with patch("search_agent.stream_chat", side_effect=[response(*calls), response(call("submit_search_results"))]):
            out = self.agent.search("需要完整的结果集合")
        self.assertIn("共 18 条", out)
        self.assertIn("结果 17", out)

    def test_multiple_searches_fetches_and_editing(self):
        captured = []
        def fake(messages, **kwargs):
            captured.append(copy.deepcopy(messages))
            step = len(captured)
            if step == 1:
                return response(call("real_search", {"query": "第一次核对"}))
            if step == 2:
                return response(call("real_fetch", {"url": URL}))
            if step == 3:
                return response(call("real_search", {"query": "另一种角度"}))
            first_ref = tool_results(messages)[0]["results"][0]["ref"]
            if step == 4:
                return response(call("select_results", {"refs": [first_ref]}),
                                call("edit_result", {"ref": first_ref, "title": "修改后的轨道观测"}, "c2"),
                                call("add_result", {**ITEM, "url": "https://example.org/other", "title": "补充观测"}, "c3"))
            return response(call("submit_search_results"))
        with patch("search_agent.stream_chat", side_effect=fake):
            out = self.agent.search("联合观测")
        self.assertEqual(self.web.call_count, 2)
        self.fetch.assert_called_once()
        self.assertIn("修改后的轨道观测", out)
        self.assertIn("补充观测", out)
        self.assertIn("先检查，再完成请求。", json.dumps(captured[1], ensure_ascii=False))

    def test_cached_search_and_page_recall_after_restart(self):
        self.generated_search()
        with patch("search_agent.stream_chat", side_effect=[response(call("create_page", {"title": "缓存网页", "content": "原句。无关段落不变。"})), response(call("submit_page"))]):
            self.agent.open_url(URL)
        # 同一搜索结果再次缓存不能清空已经完成的网页正文。
        self.generated_search()
        world = WorldState(self.cfg, self.store.get_state(self.cfg["id"]))
        agent = SearchAgent(world)
        steps = []
        def fake(messages, **kwargs):
            steps.append(1)
            if len(steps) == 1:
                return response(call("virtual_search", {"url": URL}))
            hits = tool_results(messages)[-1]["hits"] if len(steps) == 2 else None
            if len(steps) == 2:
                self.assertTrue(any(h["kind"] == "search" for h in hits))
                page = next(h["page"] for h in hits if h["kind"] == "page")
                return response(call("set_page", {"ref": page["ref"]}),
                                call("edit_page", {"ref": page["ref"], "old_string": "原句", "new_string": "修订句"}, "c2"))
            return response(call("submit_page"))
        with patch("search_agent.stream_chat", side_effect=fake):
            out = agent.open_url(URL)
        self.assertIn("修订句。无关段落不变。", out)
        self.assertEqual(world.state["pages"][URL]["content"], "修订句。无关段落不变。")
        self.web.assert_not_called()
        self.fetch.assert_not_called()

    def test_cached_results_can_be_submitted_and_appended(self):
        self.generated_search()
        count = 0
        def fake(messages, **kwargs):
            nonlocal count
            count += 1
            if count == 1:
                return response(call("virtual_search", {"query": "轨道"}))
            if count == 2:
                hit = next(h for h in tool_results(messages)[-1]["hits"] if h["kind"] == "search")
                return response(call("add_result", {"ref": hit["results"][0]["ref"]}))
            return response(call("submit_search_results"))
        with patch("search_agent.stream_chat", side_effect=fake):
            out = self.agent.search("复查缓存")
        self.assertIn("轨道观测", out)
        self.web.assert_not_called()

    def test_real_page_passthrough_and_precise_edit_retry(self):
        count = 0
        page_ref = ""
        def fake(messages, **kwargs):
            nonlocal count, page_ref
            count += 1
            if count == 1:
                return response(call("real_fetch", {"url": URL}))
            if count == 2:
                page_ref = tool_results(messages)[-1]["ref"]
                return response(call("edit_page", {"ref": page_ref, "old_string": "不存在", "new_string": "修改"}))
            if count == 3:
                self.assertIn("error", tool_results(messages)[-1])
                return response(call("edit_page", {"ref": page_ref, "old_string": "原句", "new_string": "修订句"}))
            return response(call("submit_page", {"ref": page_ref}))
        with patch("search_agent.stream_chat", side_effect=fake):
            out = self.agent.open_url(URL)
        self.assertIn("修订句。无关的公司财报不变。", out)
        self.agent.reset_agent_context()
        count = 0
        def noop(messages, **kwargs):
            nonlocal count
            count += 1
            if count == 1:
                return response(call("real_fetch", {"url": URL}))
            return response(call("submit_page", {"ref": tool_results(messages)[-1]["ref"]}))
        with patch("search_agent.stream_chat", side_effect=noop):
            self.assertIn("原句。无关的公司财报不变。", self.agent.open_url(URL))

    def test_failed_or_unsubmitted_turn_rolls_back(self):
        before = copy.deepcopy(self.world.state)
        self.agent.max_rounds = 3
        with patch("search_agent.stream_chat", side_effect=[response(call("add_result", ITEM)),
                 response(call("cache_search_results")), {"content": "口头说完成不算提交"}]):
            with self.assertRaises(SearchAgentError):
                self.agent.search("失败")
        self.assertEqual(self.world.state, before)
        self.assertEqual(self.agent.agent_context.history, [])
        with patch("search_agent.stream_chat", side_effect=DeepSeekError("失败")):
            with self.assertRaises(DeepSeekError):
                self.agent.search("网络失败")
        self.assertEqual(self.world.state, before)

    def test_wrong_page_and_stale_refs_return_errors(self):
        self.generated_search()
        old_ref = next(m["tool_calls"][0]["function"]["arguments"] for m in self.agent.agent_context.history if m.get("tool_calls"))
        count = 0
        def fake(messages, **kwargs):
            nonlocal count
            count += 1
            if count == 1:
                return response(call("submit_page", {"ref": "previous-turn:1"}))
            if count == 2:
                self.assertIn("error", tool_results(messages)[-1])
                return response(call("real_fetch", {"url": "https://example.org/wrong"}))
            if count == 3:
                return response(call("submit_page", {"ref": tool_results(messages)[-1]["ref"]}))
            self.assertIn("error", tool_results(messages)[-1])
            return response(call("fail_request", {"message": "没有对应页面"}))
        with patch("search_agent.stream_chat", side_effect=fake):
            out = self.agent.open_url(URL)
        self.assertIn("网页抓取失败", out)

    def test_shared_history_incremental_world_updates_and_reset(self):
        self.generated_search("第一轮")
        state = copy.deepcopy(self.world.state)
        state["canon"].append("第一次新事实")
        self.world.commit(state)
        state["canon"].append("第二次新事实")
        self.world.commit(state)
        requests = []
        def fake(messages, **kwargs):
            requests.append(copy.deepcopy(messages))
            return response(call("submit_search_results", {"refs": []}))
        with patch("search_agent.stream_chat", side_effect=fake):
            self.agent.search("第二轮", conversation="[用户] 新对话")
            self.agent.search("第三轮", conversation="[用户] 新对话")
        self.assertIn("第一次新事实", requests[0][-1]["content"])
        self.assertIn("第二次新事实", requests[0][-1]["content"])
        self.assertIn("第一轮", json.dumps(requests[0], ensure_ascii=False))
        self.assertNotIn("世界状态更新通知", requests[1][-1]["content"])
        self.agent.reset_agent_context()
        self.assertEqual(self.agent.agent_context.history, [])
        self.assertTrue(self.world.state["search_cache"])

    def test_history_compaction_preserves_on_failure(self):
        ctx = AgentContext(history_budget=1)
        ctx.history = [{"role": role, "content": str(i)} for i in range(8) for role in ("user", "assistant")]
        before = copy.deepcopy(ctx.history)
        with patch("agent_context.stream_chat", side_effect=DeepSeekError("离线")):
            ctx._compact_if_needed()
        self.assertEqual(ctx.history, before)
        with patch("agent_context.stream_chat", return_value={"content": "先前工作摘要"}):
            ctx._compact_if_needed()
        self.assertEqual(ctx.summary, "先前工作摘要")
        self.assertLess(len(ctx.history), len(before))


if __name__ == "__main__":
    unittest.main()
