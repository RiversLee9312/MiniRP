"""离线验证剧情 Agent 的工具循环、世界状态与对话上下文。"""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from harness import ChatAgent
from presets import PresetStore, default_state
from search_agent import SearchAgent
from env_agent import EnvAgent, EnvAgentError
from world import WorldState


def call(name, args, ident="call1"):
    return {"id": ident, "type": "function", "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)}}


def assistant(*calls):
    return {"content": "", "reasoning_content": "先检查再编辑。", "tool_calls": list(calls)}


class WorldProgressTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.store = PresetStore(ROOT / "presets", Path(self.tmp.name) / "state.json")
        self.config = self.store.get_config("ufo-2026")
        self.state = default_state(self.config)
        self.state.update(sim_date="2030-01-01", phase="进展后", canon=["原有事实"],
                          recent=[{"query": "旧查询"}], pages={"old": {}}, stages=[{"summary": "旧剧情"}], search_index=5)
        self.store.save_state("ufo-2026", self.state)
        self.store.save_state("other", {"canon": ["其他世界"]})
        self.world = WorldState(self.config, self.state, lambda state: self.store.save_state("ufo-2026", state))
        self.env = EnvAgent(self.world)

    def test_reset_persists_only_active_world(self):
        self.env.reset_canon()
        doc = json.loads(self.store.state_path.read_text(encoding="utf-8"))
        self.assertEqual(self.world.state, default_state(self.config))
        self.assertEqual(doc["presets"]["ufo-2026"], default_state(self.config))
        self.assertEqual(doc["presets"]["other"], {"canon": ["其他世界"]})

    def test_edit_loop_retries_tools_and_saves_at_end(self):
        requests = []
        responses = iter([
            assistant(call("replace_fact", {"old": "不存在", "new": "修改后的事实"})),
            assistant(call("inspect_world", {})),
            assistant(call("replace_fact", {"old": "原有事实", "new": "修改后的事实"}),
                      call("set_phase", {"phase": "调整后的世界状态"}, "call2")),
            {"content": "我修改了这条事实，也同步调整了世界状态。"},
        ])
        def fake(messages, **kwargs):
            requests.append(copy.deepcopy(messages))
            self.assertEqual(self.world.state["canon"], ["原有事实"])
            return next(responses)
        with patch("env_agent.stream_chat", side_effect=fake):
            answer = self.env.edit_canon("修改事实", conversation="用户此前讨论了观测")
        self.assertIn("用户此前讨论了观测", requests[0][1]["content"])
        self.assertIn("错误", requests[1][-1]["content"])
        self.assertEqual(requests[1][2]["reasoning_content"], "先检查再编辑。")
        self.assertIn("修改了这条事实", answer)
        saved = json.loads(self.store.state_path.read_text(encoding="utf-8"))["presets"]["ufo-2026"]
        self.assertEqual(saved["canon"], ["修改后的事实"])
        self.assertEqual(saved["phase"], "调整后的世界状态")
        self.assertEqual(saved["sim_date"], "2030-01-01")
        self.assertEqual(saved["stages"], self.state["stages"])
        self.assertIn("修改后的事实", "\n".join(self.world.updates))

    def test_failed_turn_does_not_save_partial_changes(self):
        before = copy.deepcopy(self.world.state)
        responses = [assistant(call("add_fact", {"fact": "暂存事实"}))] + [assistant(call("inspect_world", {}))] * 15
        with patch("env_agent.stream_chat", side_effect=responses):
            with self.assertRaises(EnvAgentError):
                self.env.edit_canon("修改")
        self.assertEqual(self.world.state, before)
        self.assertEqual(self.store.get_state("ufo-2026"), before)

    def test_edit_cannot_advance_date_and_stage_has_bounded_date(self):
        state = copy.deepcopy(self.state)
        with self.assertRaises(ValueError):
            self.env._apply_world_tool(state, "edit", "advance_date", {"days": 1}, "2030-01-01")
        self.env._apply_world_tool(state, "stage", "advance_date", {"days": 3}, "2030-01-01")
        with self.assertRaises(ValueError):
            self.env._apply_world_tool(state, "stage", "advance_date", {"days": 1}, "2030-01-01")

    def test_stage_receives_conversation_and_saves_agent_reply(self):
        agent = ChatAgent(SearchAgent(self.world))
        agent.summary = "先前讨论摘要"
        agent.history = [{"role": "user", "content": "希望剧情关注天文台"},
                         {"role": "assistant", "content": "可以关注观测员", "reasoning_content": "秘密思考"},
                         {"role": "tool", "content": "工具内容"}]
        context = agent.stage_context()
        self.assertIn("先前讨论摘要", context)
        self.assertIn("希望剧情关注天文台", context)
        self.assertNotIn("秘密思考", context)
        self.assertNotIn("工具内容", context)
        requests = []
        responses = iter([assistant(call("add_fact", {"fact": "天文台获得新数据"})), {"content": "天文台获得了新数据。"}])
        def fake(messages, **kwargs):
            requests.append(copy.deepcopy(messages))
            return next(responses)
        with patch("env_agent.stream_chat", side_effect=fake):
            answer = self.env.next_stage(conversation=context)
        self.assertIn("希望剧情关注天文台", requests[0][1]["content"])
        self.assertEqual(answer, "天文台获得了新数据。")
        self.assertEqual(self.world.state["stages"][-1]["summary"], answer)
        self.assertIn("天文台获得新数据", "\n".join(self.world.updates))
        agent.reset()
        self.assertEqual(agent.stage_context(), "")


if __name__ == "__main__":
    unittest.main()
