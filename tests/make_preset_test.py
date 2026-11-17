"""验证从提示词创建预设及 CLI 接入。"""
import io
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import harness
import presets
from preset_agent import PresetAgent


def call(name, args, ident):
    return {"id": ident, "type": "function", "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)}}


def fake_preset_agent(requests):
    count = 0
    def fake(messages, **kwargs):
        nonlocal count
        requests.append(json.loads(json.dumps(messages, ensure_ascii=False)))
        count += 1
        if count == 1:
            return {"content": "", "reasoning_content": "先构建设定。", "tool_calls": [
                call("set_value", {"path": "name", "value": "Rain City"}, "1"),
                call("set_value", {"path": "phase", "value": "雨城今天停雨。"}, "2"),
                call("set_value", {"path": "world.summary", "value": "一座常年下雨的城市。"}, "3"),
            ]}
        if count == 2:
            return {"content": "", "reasoning_content": "补充事实。", "tool_calls": [
                call("append_item", {"path": "canon", "value": fact}, str(i))
                for i, fact in enumerate(["雨城常年下雨。", "今天早上第一次停雨。", "居民正在寻找原因。"], 4)]}
        return {"content": "设定已创建。"}
    return fake


class MakePresetTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        directory = Path(self.tmp.name) / "presets"
        directory.mkdir()
        shutil.copy(ROOT / "presets/ufo-2026.json", directory)
        self.store = presets.PresetStore(directory, Path(self.tmp.name) / "state.json")
        self.agent = PresetAgent(self.store)

    def test_creates_new_unique_preset_with_agent_loop(self):
        existing = self.store.preset_dir / "rain-city.json"
        existing.write_text('{"original": true}', encoding="utf-8")
        requests = []
        with patch("preset_agent.stream_chat", side_effect=fake_preset_agent(requests)):
            cfg = self.agent.make("创建一个雨城", conversation="用户喜欢悬疑故事")
        self.assertEqual(cfg["id"], "rain-city-2")
        self.assertEqual(json.loads(existing.read_text(encoding="utf-8")), {"original": True})
        self.assertIn("用户喜欢悬疑故事", requests[0][1]["content"])
        self.assertEqual(requests[1][2]["reasoning_content"], "先构建设定。")
        self.assertEqual(len(self.store.get_config(cfg["id"])["canon"]), 3)

    def test_incomplete_and_runaway_agent_do_not_save(self):
        before = set(self.store.preset_dir.iterdir())
        with patch("preset_agent.stream_chat", return_value={"content": "完成"}):
            with self.assertRaises(presets.PresetError):
                self.agent.make("创建一个世界")
        with patch("preset_agent.stream_chat", return_value={"content": "", "tool_calls": [call("inspect_preset", {}, "x")]}):
            with self.assertRaises(presets.PresetError):
                self.agent.make("创建一个世界")
        self.assertEqual(set(self.store.preset_dir.iterdir()), before)

    def test_cli_creates_and_activates(self):
        requests = []
        output = io.StringIO()
        with patch("harness.PresetStore", return_value=self.store), patch("harness.api_key", return_value="test"), \
             patch("preset_agent.stream_chat", side_effect=fake_preset_agent(requests)), \
             patch("sys.stdin", io.StringIO("/make-preset 建立雨城\n/new\n/preset\n/exit\n")), patch("sys.stdout", output):
            self.assertEqual(harness.main(), 0)
        self.assertEqual(self.store.active_id, "rain-city")
        self.assertIn("已创建并切换", output.getvalue())
        self.assertEqual(self.store.get_state("rain-city")["canon"], self.store.get_config("rain-city")["canon"])


if __name__ == "__main__":
    unittest.main()
