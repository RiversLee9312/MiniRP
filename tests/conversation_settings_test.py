import copy
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import deepseek_client as client
import harness
from conversations import ConversationError, ConversationStore
from presets import PresetStore, default_state, normalize_config
from preset_agent import PresetAgent
from search_agent import SearchAgent
from settings import SettingsStore
from world import WorldState


class ConversationSettingsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        directory = self.root / "presets"
        directory.mkdir()
        shutil.copy(ROOT / "presets/ufo-2026.json", directory)
        self.store = PresetStore(directory, self.root / "state.json")

    def test_save_load_roundtrip_and_preset_isolation(self):
        store = ConversationStore(self.root / "conversations")
        saved = store.new("ufo-2026", "轨道调查")
        saved.update(chat={"history": [{"role": "user", "content": "继续查证"}], "summary": "先前摘要"},
                     search={"history": [{"role": "assistant", "reasoning_content": "工具前思考", "content": ""}], "summary": "检索摘要"},
                     env={"history": [{"role": "user", "content": "保留此前的剧情方向"}], "summary": "剧情管理摘要"},
                     preset={"history": [{"role": "user", "content": "调整此前的风格"}], "summary": "预设编辑摘要"})
        store.save(saved)
        loaded = store.load("ufo-2026", saved["id"])
        self.assertEqual(loaded["chat"], saved["chat"])
        self.assertEqual(loaded["search"], saved["search"])
        self.assertEqual(loaded["env"], saved["env"])
        self.assertEqual(loaded["preset"], saved["preset"])
        self.assertEqual(store.list("other"), [])
        with self.assertRaises(ConversationError):
            store.load("other", saved["id"])
        with self.assertRaises(ConversationError):
            store.load("../escape", saved["id"])

    def test_cli_save_new_load_and_switch_requires_selection(self):
        second = self.store.get_config("ufo-2026")
        second.update(id="other", name="另一个世界")
        self.store.save_config(second)
        output = io.StringIO()
        commands = "你好\n/save 调查记录\n/new 新话题\n/load\n"  # 加载选择后补充保存的 id。
        conversations = ConversationStore(self.root / "conversations")
        saved = conversations.new("ufo-2026", "已有对话")
        saved["chat"] = {"history": [{"role": "user", "content": "过去的问题"}], "summary": ""}
        conversations.save(saved)
        # 启动先选择新建，然后从当前预设加载已有对话，再切换到新预设选择新建。
        commands = "/new\n" + commands + saved["id"] + "\n/preset other\n/new\n/exit\n"
        with patch("harness.PresetStore", return_value=self.store), patch("harness.api_key", return_value="test"), \
             patch("harness.stream_chat", return_value={"content": "回答"}), \
             patch("sys.stdin", io.StringIO(commands)), patch("sys.stdout", output):
            self.assertEqual(harness.main(), 0)
        self.assertEqual(self.store.active_id, "other")
        self.assertTrue(conversations.list("other"))
        self.assertIn("已加载对话：已有对话", output.getvalue())
        self.assertIn("选择预设 other 下的对话", output.getvalue())
        named = next(c for c in conversations.list("ufo-2026") if c["name"] == "调查记录")
        self.assertEqual(conversations.load("ufo-2026", named["id"])["chat"]["history"][0]["content"], "你好")

    def test_preset_system_prompt_can_be_edited_and_preserves_json_braces(self):
        cfg = self.store.get_config("ufo-2026")
        prompt = '自定义助手，日期 {date}；JSON 示例 {"key": "value"}'
        calls = [{"content": "", "tool_calls": [{"id": "c", "type": "function", "function": {"name": "set_value", "arguments": json.dumps({"path": "chat_agent.system_prompt", "value": prompt}, ensure_ascii=False)}}]}, {"content": "已更新"}]
        with patch("preset_agent.stream_chat", side_effect=calls):
            cfg = PresetAgent(self.store).edit("修改主聊天 system prompt")
        world = WorldState(cfg, default_state(cfg))
        system = harness.ChatAgent(SearchAgent(world))._system_prompt()
        self.assertIn(world.sim_date, system)
        self.assertIn('{"key": "value"}', system)
        self.assertEqual(self.store.get_config("ufo-2026")["chat_agent"]["system_prompt"], prompt)

    def test_settings_save_preserves_unrelated_values_and_updates_cached_key(self):
        path = self.root / ".env"
        path.write_text("# user note\nMINICHAT_OFFLINE=1\nDEEPSEEK_API_KEY=old-key\n", encoding="utf-8")
        settings = SettingsStore(path)
        original = {key: getattr(client, key) for key in ("BASE_URL", "MODEL", "THINKING", "REASONING_EFFORT", "_KEY")}
        self.addCleanup(lambda: [setattr(client, key, value) for key, value in original.items()])
        with patch.dict(os.environ, {}, clear=False):
            client._KEY = "old-key"
            values = {"DEEPSEEK_MODEL": "test-model", "DEEPSEEK_API_KEY": "test-new-key", "DEEPSEEK_BASE_URL": "http://localhost:9999",
                      "DEEPSEEK_THINKING": "disabled", "DEEPSEEK_REASONING_EFFORT": "high"}
            settings.save(values)
            self.assertEqual(client.api_key(), "test-new-key")
            self.assertEqual(client.MODEL, "test-model")
            self.assertEqual(client.BASE_URL, "http://localhost:9999")
            text = path.read_text(encoding="utf-8")
            self.assertIn("# user note", text)
            self.assertIn("MINICHAT_OFFLINE=1", text)
            self.assertNotIn("old-key", text)
            with self.assertRaises(ValueError):
                settings.save({**values, "DEEPSEEK_API_KEY": "secret\nINJECT=1"})
            self.assertEqual(path.read_text(encoding="utf-8"), text)


if __name__ == "__main__":
    unittest.main()
