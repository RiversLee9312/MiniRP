"""真实隔离无头 Chromium 的离线测试；无需 DS Key 或外网。"""
import json
import re
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from fetch_agent import FetchAgent


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        text = "完整网页内容验证。" * 18000 + "END-OF-FULL-PAGE"
        body = f'''<html><title>完整性测试</title><body><h1>测试页面</h1>
        <button onclick="document.getElementById('article').hidden=false">展开全文</button>
        <article id="article" hidden><p>{text}</p><a href="/reference">原始链接</a></article></body></html>'''.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.end_headers()
        self.wfile.write(body)


class LocalBrowserTest(unittest.TestCase):
    def test_real_navigation_refs_click_and_full_markdown(self):
        if not (Path(__file__).resolve().parent.parent / "node_modules/@playwright/mcp/cli.js").exists():
            self.skipTest("运行 npm install 以执行真实浏览器测试")
        server = HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        url = f"http://127.0.0.1:{server.server_port}/article"
        count = 0

        def fake(messages, **kwargs):
            nonlocal count
            count += 1
            if count == 1:
                name, args = "browser_navigate", {"url": url}
            elif count == 2:
                observation = messages[-1]["content"]
                match = re.search(r'button "展开全文".*?\[ref=([^\]]+)\]', observation)
                self.assertIsNotNone(match, observation[:1500])
                name, args = "browser_click", {"target": match.group(1)}
            elif count == 3:
                name, args = "capture_page", {}
            else:
                capture = json.loads(messages[-1]["content"])
                self.assertGreater(capture["total_chars"], 120000)
                name, args = "finish_fetch", {"page_key": "page/markdown", "complete": True, "summary": "展开全文后已完整读取。"}
            return {"content": "", "reasoning_content": "观察、操作、再次观察，再提交全文。", "tool_calls": [
                {"id": f"c{count}", "type": "function", "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)}}]}

        with patch("fetch_agent.stream_chat", side_effect=fake):
            result = FetchAgent().fetch(url)
        self.assertTrue(result["complete"])
        self.assertFalse(result["truncated"])
        self.assertGreater(len(result["content"]), 120000)
        self.assertIn("END-OF-FULL-PAGE", result["content"])
        self.assertIn(f"http://127.0.0.1:{server.server_port}/reference", result["content"])
        self.assertEqual(result["summary"], "展开全文后已完整读取。")


if __name__ == "__main__":
    unittest.main()
