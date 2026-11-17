"""输入层测试：用 prompt_toolkit 的管道输入模拟真实按键。

覆盖：Tab 补全、多个候选时的循环、↑ 历史。
不依赖真实终端，也不需要 API Key。
"""
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from prompt_toolkit.input import create_pipe_input
from prompt_toolkit.output import DummyOutput

import term

COMMANDS = [
    ("/help", "帮助"),
    ("/next-stage", "推进剧情"),
    ("/cot", "CoT"),
    ("/trace", "轨迹"),
    ("/canon", "事实"),
    ("/reset", "清空"),
    ("/exit", "退出"),
]


def prompt_with(session, inp, keys, gap=0.4):
    time.sleep(0.35)  # 等上一轮 prompt_toolkit 的渲染/历史写入完成
    for i, key in enumerate(keys):
        threading.Timer(0.3 + i * gap, lambda k=key: inp.send_text(k)).start()
    return session.prompt("> ")


def main() -> int:
    results = {}
    with create_pipe_input() as inp:
        session = term.make_prompt_session(COMMANDS, input=inp, output=DummyOutput())
        if session is None:
            print("prompt_toolkit unavailable; skipping")
            return 0
        results["tab_unique"] = prompt_with(session, inp, ["/he", "\t", "\r"])
        results["menu_first"] = prompt_with(session, inp, ["/", "\t", "\r"])
        results["menu_cycle"] = prompt_with(session, inp, ["/", "\t", "\t", "\r"])
        results["history_up"] = prompt_with(session, inp, ["\x1b[A", "\r"])

    print(results)
    ok = (
        results["tab_unique"] == "/help"
        and results["menu_first"] == "/help"
        and results["menu_cycle"] == "/next-stage"
        and results["history_up"] == "/next-stage"
    )
    print("INPUT TEST:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
