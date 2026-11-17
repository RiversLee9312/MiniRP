"""离线冒烟测试：mock DeepSeek SSE，验证

- 主流式响应与 tool-call 循环
- 搜索编辑 Agent：select_results / edit_result
- 编辑后的结果显示、可信度标签
- 聊天 Agent 的 fetch_page（虚构链接生成页面）
- /next-stage 剧情推进
- thinking 模式下 reasoning_content 必须回传
"""
import json
import os
import subprocess
import sys
import threading
import tempfile
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CALLS = {"main": 0, "editor": 0, "page": 0, "stage": 0, "canon": 0, "stage_context": False, "canon_context": False, "page_context": False}

FAKE_URL = "https://www.reuters.com/world/ufo-objects-orbit-2026-11-16/"


def _sse(self, deltas):
    self.send_response(200)
    self.send_header("Content-Type", "text/event-stream; charset=utf-8")
    self.send_header("Cache-Control", "no-cache")
    self.end_headers()
    for delta in deltas:
        event = {"choices": [{"delta": delta}]}
        self.wfile.write(("data: " + json.dumps(event, ensure_ascii=False) + "\n\n").encode("utf-8"))
        self.wfile.flush()
    self.wfile.write(b"data: [DONE]\n\n")
    self.wfile.flush()


def _chunks(text, n=40):
    return [text[i : i + n] for i in range(0, len(text), n)]


def _tool(name, call_id, args, index=0):
    yield {
        "tool_calls": [
            {
                "index": index,
                "id": call_id,
                "type": "function",
                "function": {"name": name, "arguments": ""},
            }
        ]
    }
    yield {
        "tool_calls": [
            {"index": index, "function": {"arguments": json.dumps(args, ensure_ascii=False)}}
        ]
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert self.headers.get("Authorization", "").startswith("Bearer ")
        assert body.get("stream") is True
        messages = body["messages"]
        tool_messages = [m for m in messages if m.get("role") == "tool"]
        last_user = max((i for i, m in enumerate(messages) if m.get("role") == "user"), default=0)
        current_tools = [m for m in messages[last_user + 1:] if m.get("role") == "tool"]
        tools = [t["function"]["name"] for t in body.get("tools") or []]
        is_json = (body.get("response_format") or {}).get("type") == "json_object"
        system_text = "".join(m.get("content") or "" for m in messages if m.get("role") == "system")
        user_text = "".join(m.get("content") or "" for m in messages if m.get("role") == "user")

        # 官方要求：带 tools 的请求必须回传历史 reasoning_content
        if tools:
            for m in messages:
                if m.get("role") == "assistant" and m.get("tool_calls"):
                    assert m.get("reasoning_content"), "missing reasoning_content"

        if "real_search" in tools:
            task = next((m["content"] for m in reversed(messages) if m.get("role") == "user" and "【本轮请求】" in m.get("content", "")), "")
            if "【本轮请求】抓取网页" in task:
                if not current_tools:
                    CALLS["page"] += 1
                    history_text = json.dumps(messages, ensure_ascii=False)
                    CALLS["page_context"] = "UFO 最新 官方" in history_text and "UNOOSA 发布第1号技术通报" in history_text
                    args = {"title": "Six objects remain in orbit, no reply to radio calls",
                            "content": "路透社 2026-11-16 电：六个不明物体仍在轨道上。NORAD 表示未收到任何无线电回应，物体也没有接近地面。",
                            "new_facts": ["NORAD 表示仍未收到任何无线电回应。"]}
                    deltas = [{"reasoning_content": "直接制作对应网页。"}] + list(_tool("create_page", "call_page", args))
                else:
                    deltas = [{"reasoning_content": "提交成品。"}] + list(_tool("submit_page", "call_submit_page", {}))
                return _sse(self, deltas)
            CALLS["editor"] += 1
            if not current_tools:
                deltas = [{"reasoning_content": "自主决定真实搜索。"}] + list(_tool("real_search", "call_real", {"query": "UFO 最新 官方"}))
            elif len(current_tools) == 1:
                candidates = json.loads(current_tools[-1]["content"])["results"]
                refs = [r["ref"] for r in candidates[:2]]
                deltas = [{"reasoning_content": "保留可靠信息并编辑。"}]
                deltas += list(_tool("select_results", "call_sel", {"refs": refs}, index=0))
                deltas += list(_tool("edit_result", "call_edit", {"ref": refs[0], "title": "路透：六物体仍未回应无线电呼叫", "credibility": "high"}, index=1))
                deltas += list(_tool("add_result", "call_add", {
                    "title": "UNOOSA 发布第1号技术通报：呼吁勿主动发射信号",
                    "url": "https://www.unoosa.org/oosa/en/informationforpress/pressreleases/2026/ijog-bulletin-01.html",
                    "source": "UNOOSA", "date": "2026-11-16", "snippet": "联合观测组协调观测数据。", "credibility": "high"}, index=2))
            else:
                deltas = [{"reasoning_content": "提交搜索成品。"}] + list(_tool("submit_search_results", "call_submit", {}))
            return _sse(self, deltas)

        if "add_fact" in tools:
            is_edit = "编辑 canon：" in system_text
            if not current_tools:
                if is_edit:
                    CALLS["canon"] += 1
                    CALLS["canon_context"] = "帮我查一下最近的不明飞行物新闻" in user_text and "联合观测报告已经发布" in user_text
                    deltas = [{"reasoning_content": "检查后修改事实。"}]
                    deltas += list(_tool("add_fact", "call_fact", {"fact": "测试修改后的事实"}))
                    deltas += list(_tool("set_phase", "call_phase", {"phase": "测试修改后的状态"}, index=1))
                else:
                    CALLS["stage"] += 1
                    CALLS["stage_context"] = "帮我查一下最近的不明飞行物新闻" in user_text and "据路透社报道" in user_text
                    deltas = [{"reasoning_content": "先推进状态。"}]
                    deltas += list(_tool("add_fact", "call_fact", {"fact": "IJOG 发布首份联合观测报告。"}))
                    deltas += list(_tool("advance_date", "call_date", {"days": 1}, index=1))
                return _sse(self, deltas)
            answer = "我已添加测试修改后的事实，并同步调整世界状态。" if is_edit else "联合观测报告已经发布，世界向前推进了一天。"
            return _sse(self, [{"content": c} for c in _chunks(answer)])

        if is_json:
            return _sse(self, [{"content": "{}"}])

        # 聊天主 Agent
        CALLS["main"] += 1
        if not any((m.get("content") or "").startswith("搜索结果") for m in tool_messages):
            deltas = [{"reasoning_content": "先检索。"}] + list(
                _tool("search", "call_s1", {"query": "UFO 最新 官方"})
            )
        elif not any(
            (m.get("content") or "").startswith(("[网页内容]", "[网页抓取失败]")) for m in tool_messages
        ):
            deltas = [{"reasoning_content": "打开关键链接核实。"}] + list(
                _tool("fetch_page", "call_p1", {"url": FAKE_URL})
            )
        else:
            deltas = [{"reasoning_content": "信息够了。"}] + [
                {"content": c}
                for c in _chunks(
                    "据路透社报道，六个不明物体仍在轨道上，没有任何通信回应；"
                    "社交平台上“接触成功”的说法已被官方否认。"
                )
            ]
        _sse(self, deltas)


def main() -> int:
    tmp = tempfile.TemporaryDirectory()
    state_file = Path(tmp.name) / "state.json"
    server = HTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    env = dict(os.environ)
    env.update(
        {
            "DEEPSEEK_API_KEY": "sk-test",
            "DEEPSEEK_BASE_URL": f"http://127.0.0.1:{port}",
            "DEEPSEEK_MODEL": "deepseek-flash",
            "MINICHAT_STATE_PATH": str(state_file),
            "MINICHAT_OFFLINE": "1",
            "MINICHAT_SEARCH_FIXTURE": str(ROOT / "tests" / "search_fixture.json"),
            "NO_COLOR": "1",
        }
    )
    proc = subprocess.run(
        [sys.executable, "harness.py"],
        cwd=ROOT,
        env=env,
        input="帮我查一下最近的不明飞行物新闻\n/next-stage 让联合国发布首份联合观测报告\n/edit-canon 修改测试事实\n/canon\n/reset-canon\n/canon\n/exit\n",
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=90,
    )
    server.shutdown()

    print(proc.stdout)
    if proc.stderr:
        print("STDERR:", proc.stderr)
    ok = (
        proc.returncode == 0
        and CALLS["main"] >= 3
        and CALLS["editor"] >= 2
        and CALLS["page"] == 1
        and CALLS["page_context"]
        and CALLS["stage"] == 1
        and CALLS["canon"] == 1
        and CALLS["stage_context"]
        and CALLS["canon_context"]
        and "select_results" in proc.stdout
        and "edit_result" in proc.stdout
        and "add_result" in proc.stdout
        and "UNOOSA 发布第1号技术通报" in proc.stdout
        and "路透：六物体仍未回应无线电呼叫" in proc.stdout
        and "联合观测报告已经发布" in proc.stdout
        and "我已添加测试修改后的事实" in proc.stdout
        and "2026-11-17" in proc.stdout
        and "测试修改后的事实" in proc.stdout
        and "当前世界已恢复预设初始状态" in proc.stdout
    )
    saved = json.loads(state_file.read_text(encoding="utf-8"))["presets"]["ufo-2026"]
    ok = ok and saved["sim_date"] == "2026-11-16" and not saved["stages"] and not saved["pages"]
    tmp.cleanup()
    print("SMOKE TEST:", "PASS" if ok else "FAIL", CALLS)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
