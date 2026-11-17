import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from agent_context import AgentContext
from deepseek_client import DeepSeekError
from session_db import SessionDB


class SessionDBTest(unittest.TestCase):
    def test_exact_edits_revisions_and_literal_chinese_search(self):
        db = SessionDB()
        self.addCleanup(db.close)
        result = db.write("notes/轨道观测", "中国天文台确认六个物体仍在轨道。\n待办：核查正式通报。")
        self.assertEqual(result["revision"], 1)
        with self.assertRaises(ValueError):
            db.write("notes/轨道观测", "不能静默覆盖")
        db.patch("notes/轨道观测", "核查正式通报", "正式通报已核查", expected_revision=1)
        with self.assertRaises(ValueError):
            db.patch("notes/轨道观测", "通报", "消息", expected_revision=1)
        self.assertEqual(db.search("中国")["algorithm"], "literal_scan")
        self.assertEqual(db.search("天文台")["algorithm"], "fts5_trigram")
        self.assertEqual(db.search("轨道", scope="keys")["matches"][0]["key"], "notes/轨道观测")
        db.write("notes/operators", 'literal " OR * not a query operator')
        self.assertEqual(len(db.search('" OR *')["matches"]), 1)
        db.delete("notes/轨道观测", expected_revision=2)
        self.assertEqual(db.search("天文台")["matches"], [])

    def test_full_documents_can_be_read_in_pages_and_survive_reopen(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "context.sqlite"
            body = "完整网页正文。" * 40000 + "终点标记"
            db = SessionDB(path)
            db.write("sources/long-page", body)
            chunks, start = [], 0
            while True:
                part = db.read("sources/long-page", start=start, length=64000)
                chunks.append(part["content"])
                if part["next_offset"] is None:
                    break
                start = part["next_offset"]
            self.assertEqual("".join(chunks), body)
            db.close()
            reopened = SessionDB(path)
            self.assertEqual(reopened.search("终点标记")["matches"][0]["key"], "sources/long-page")
            reopened.close()

    def test_compaction_archives_full_tools_and_keeps_user_notes(self):
        context = AgentContext(history_budget=1)
        self.addCleanup(context.db.close)
        context.db.write("notes/next-step", "继续追踪观测站")
        giant = "不应丢失的工具细节" * 3000
        context.history = [message for i in range(8) for message in (
            {"role": "user", "content": str(i)}, {"role": "assistant", "content": giant})]
        original = json.dumps(context.history, ensure_ascii=False)
        with patch("agent_context.stream_chat", return_value={"content": "已压缩。需要时读取 notes/next-step 与 _archive/history。"}):
            context._compact_if_needed()
        self.assertLess(len(json.dumps(context.history, ensure_ascii=False)), len(original))
        archived = context.db.list(prefix="_archive/history/")["entries"]
        self.assertEqual(len(archived), 1)
        self.assertIn(giant, context.db.read(archived[0]["key"], length=64000)["content"])
        self.assertEqual(context.db.read("notes/next-step")["content"], "继续追踪观测站")
        context.reset()
        self.assertEqual(context.db.read("notes/next-step")["content"], "继续追踪观测站")

    def test_session_binding_isolated_and_bad_tool_is_recoverable(self):
        with tempfile.TemporaryDirectory() as directory:
            context = AgentContext()
            context.bind_db(Path(directory) / "one.sqlite")
            context.call_db("db_write", {"key": "notes/task", "content": "只属于会话一"})
            context.bind_db(Path(directory) / "two.sqlite")
            self.assertEqual(context.db.list()["entries"], [])
            self.assertIn("error", context.call_db("db_patch", {"key": "missing", "old_string": "x", "new_string": "y"}))
            context.bind_db(Path(directory) / "one.sqlite")
            self.assertEqual(context.db.read("notes/task")["content"], "只属于会话一")
            context.db.close()


if __name__ == "__main__":
    unittest.main()
