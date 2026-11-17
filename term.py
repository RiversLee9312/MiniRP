"""终端 UI：颜色、CJK 宽度换行、流式打印、状态行与工具轨迹。"""
from __future__ import annotations

import os
import re
import shutil
import sys
import unicodedata

try:
    import msvcrt  # Windows 下的行编辑
except ImportError:  # pragma: no cover
    msvcrt = None

try:
    import readline as _readline  # Unix / WSL 下可用，Windows 原生 Python 没有
except ImportError:  # pragma: no cover
    _readline = None

_EDITOR_OPT_IN = os.environ.get("MINICHAT_EDITOR") == "1"


def _console_input_ok() -> bool:
    """只有真正的 Windows 控制台才能安全使用 msvcrt.getwch()。

    Git Bash / mintty / 某些 pty 里 stdin 是管道，msvcrt 会去读另一个控制台，
    表现为“键盘没反应”。这里用 GetFileType 判断。
    """
    if msvcrt is None or not sys.stdin.isatty():
        return False
    try:
        import ctypes

        handle = msvcrt.get_osfhandle(sys.stdin.fileno())
        return ctypes.windll.kernel32.GetFileType(handle) == 2  # FILE_TYPE_CHAR
    except Exception:
        return False


def setup_readline(commands: list[tuple[str, str]]) -> bool:
    """在支持 readline 的平台上启用 Tab 补全；Windows 原生 Python 通常不可用。"""
    if _readline is None:
        return False
    names = [cmd for cmd, _ in commands]

    def completer(text: str, state: int) -> str | None:
        matches = [n for n in names if n.startswith(text)]
        return matches[state] if state < len(matches) else None

    _readline.set_completer(completer)
    _readline.set_completer_delims(" \t\n")
    _readline.parse_and_bind("tab: complete")
    return True


def make_prompt_session(
    commands: list[tuple[str, str]],
    *,
    input=None,
    output=None,
):
    """用 prompt_toolkit 创建输入会话：输入 / 自动弹菜单、Tab 补全、↑↓ 历史。

    prompt_toolkit 是可选的；不可用时返回 None，由调用方退回 input()。
    """
    try:
        from prompt_toolkit import PromptSession
        from prompt_toolkit.completion import Completer, Completion
        from prompt_toolkit.history import InMemoryHistory
        from prompt_toolkit.key_binding import KeyBindings
        from prompt_toolkit.styles import Style
    except ImportError:
        return None

    class SlashCompleter(Completer):
        def get_completions(self, document, complete_event):
            text = document.text_before_cursor
            if not text.startswith("/") or " " in text:
                return
            for cmd, desc in commands:
                if cmd.startswith(text):
                    yield Completion(
                        cmd,
                        start_position=-len(text),
                        display=cmd,
                        display_meta=desc,
                    )

    bindings = KeyBindings()

    @bindings.add("tab")
    def _(event):
        buf = event.current_buffer
        if buf.complete_state:
            buf.complete_next()
        else:
            buf.start_completion(select_first=True)

    style = Style.from_dict(
        {
            "prompt": "bold ansicyan",
            "completion-menu.completion": "bg:#1f1f1f #c8c8c8",
            "completion-menu.completion.current": "bg:#00afff #ffffff",
            "completion-menu.meta.completion": "bg:#1f1f1f #888888",
            "completion-menu.meta.completion.current": "bg:#00afff #ffffff",
        }
    )
    return PromptSession(
        completer=SlashCompleter(),
        complete_while_typing=True,
        history=InMemoryHistory(),
        style=style,
        key_bindings=bindings,
        input=input,
        output=output,
    )

_ANSI = re.compile(r"\x1b\[[0-9;]*m")
_TTY = sys.stdout.isatty()
_COLOR = _TTY and not os.environ.get("NO_COLOR")

RESET = "\x1b[0m" if _COLOR else ""
BOLD = "\x1b[1m" if _COLOR else ""
DIM = "\x1b[2m" if _COLOR else ""
BLUE = "\x1b[34m" if _COLOR else ""
CYAN = "\x1b[36m" if _COLOR else ""
MAGENTA = "\x1b[35m" if _COLOR else ""
GREEN = "\x1b[32m" if _COLOR else ""
YELLOW = "\x1b[33m" if _COLOR else ""
RED = "\x1b[31m" if _COLOR else ""
GREY = "\x1b[90m" if _COLOR else ""

_SPIN = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"


def term_width(default: int = 100) -> int:
    return shutil.get_terminal_size((default, 24)).columns


def char_width(ch: str) -> int:
    if unicodedata.combining(ch):
        return 0
    if unicodedata.east_asian_width(ch) in ("W", "F"):
        return 2
    return 1


def visible_width(text: str) -> int:
    return sum(char_width(c) for c in _ANSI.sub("", text))


def _pad(text: str, width: int) -> str:
    return text + " " * max(0, width - visible_width(text))


def banner(
    model: str,
    thinking: str,
    effort: str,
    sim_date: str,
    name: str = "Chat agent",
    subtitle: str = "",
) -> str:
    rows = [
        name,
        f"{model} · thinking={thinking} · effort={effort}",
        f"{sim_date}" + (f" · {subtitle}" if subtitle else ""),
    ]
    inner = max(44, max(visible_width("  " + r) for r in rows))
    title = "─ minichat "
    dashes = max(0, inner + 2 - visible_width(title))
    top = f"{DIM}╭{title}" + "─" * dashes + f"╮{RESET}"
    body = "\n".join(
        f"{DIM}│{RESET} {_pad('  ' + r, inner)} {DIM}│{RESET}" for r in rows
    )
    bottom = f"{DIM}╰" + "─" * (inner + 2) + f"╯{RESET}"
    return f"{top}\n{body}\n{bottom}"


def user_prompt() -> str:
    return f"\n{BOLD}{CYAN}你 ❯ {RESET}"


def info(text: str) -> None:
    print(f"{GREY}{text}{RESET}")


def error(text: str) -> None:
    print(f"\n{RED}{text}{RESET}")


class StreamPrinter:
    """带左边栏的流式打印，按显示宽度自动换行（中文按 2 列计）。"""

    def __init__(
        self,
        label: str = "Chat",
        max_width: int | None = None,
        prefix_color: str = BLUE,
        text_color: str = "",
    ):
        self.label = label
        self.width = max(40, min(max_width or term_width() - 4, 100))
        self.prefix_len = len(label) + 3
        self.avail = max(20, self.width - self.prefix_len)
        self.prefix_color = prefix_color
        self.text_color = text_color
        self.col = 0
        self.started = False

    def _newline(self) -> None:
        sys.stdout.write(RESET + "\n" if self.text_color else "\n")
        self.col = 0
        self.started = False

    def _write_prefix(self) -> None:
        if self.started or self.col > 0:
            return
        sys.stdout.write(f"{self.prefix_color}{self.label}{RESET}{DIM} │ {RESET}")
        self.started = True

    def feed(self, text: str) -> None:
        for ch in text:
            if ch == "\r":
                continue
            if ch == "\n":
                self._newline()
                continue
            w = char_width(ch)
            if self.col + w > self.avail and self.col > 0:
                self._newline()
            if not self.started:
                self._write_prefix()
            if self.text_color:
                sys.stdout.write(f"{self.text_color}{ch}{RESET}")
            else:
                sys.stdout.write(ch)
            self.col += w
        sys.stdout.flush()

    def close(self) -> None:
        if self.started:
            sys.stdout.write(RESET + "\n" if self.text_color else "\n")
            sys.stdout.flush()
            self.started = False
            self.col = 0


class StatusLine:
    """单行原地更新的状态提示；非 TTY 时不输出。"""

    def __init__(self, enabled: bool | None = None):
        self.enabled = _TTY if enabled is None else enabled
        self.active = False
        self._frame = 0

    def update(self, text: str) -> None:
        if not self.enabled:
            return
        self._frame = (self._frame + 1) % len(_SPIN)
        sys.stdout.write(f"\r\x1b[2K{DIM}  {_SPIN[self._frame]} {text}{RESET}")
        sys.stdout.flush()
        self.active = True

    def stop(self) -> None:
        if self.enabled and self.active:
            sys.stdout.write("\r\x1b[2K")
            sys.stdout.flush()
        self.active = False


class Trace:
    """保留事件回调接口；所有 Agent 的工具和思考由 agent_runtime 统一呈现。"""
    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        self.status = StatusLine(enabled=False)

    def off(self) -> None:
        self.enabled = False

    def handle(self, ev: dict) -> None:
        pass


def _short_url(url: str) -> str:
    url = (url or "").replace("https://", "").replace("http://", "")
    return url if len(url) <= 72 else url[:69] + "…"


def _short_err(err: str) -> str:
    err = (err or "").splitlines()[0]
    return err if len(err) <= 60 else err[:57] + "…"


def wrap_display(text: str, width: int) -> list[str]:
    lines: list[str] = []
    cur, col = "", 0
    for ch in text:
        if ch == "\n":
            lines.append(cur)
            cur, col = "", 0
            continue
        w = char_width(ch)
        if col + w > width and cur:
            lines.append(cur)
            cur, col = "", 0
        cur += ch
        col += w
    if cur:
        lines.append(cur)
    return lines


def stage_panel(record: dict) -> None:
    """打印一次剧情推进的结果。"""
    width = max(40, min(term_width() - 8, 90))
    date = record.get("date", "")
    print(f"\n{BOLD}{CYAN}  ── 剧情推进 · {date} ──{RESET}")
    summary = str(record.get("summary") or "").strip()
    for line in wrap_display(summary, width):
        print(f"  {line}")
    headlines = record.get("headlines") or []
    if headlines:
        print(f"\n  {DIM}头条{RESET}")
        for h in headlines:
            print(f"  {DIM}·{RESET} {h}")
    facts = record.get("facts") or []
    if facts:
        print(f"\n  {DIM}新增事实{RESET}")
        for f in facts:
            for i, line in enumerate(wrap_display(str(f), width - 2)):
                prefix = f"  {GREEN}·{RESET} " if i == 0 else "    "
                print(prefix + line)
    print()


class LineReader:
    """Windows 下的简单行编辑器：Tab 补全 / 命令联想 / 上下键历史。

    非 TTY 或没有 msvcrt 时自动退回 input()。
    """

    def __init__(self, commands: list[tuple[str, str]]):
        self.commands = list(commands)
        self.history: list[str] = []
        self.hist_idx = 0
        self.tab_idx = -1
        self._last_complete = ""
        # 默认用最稳妥的 input()；只有显式开启且确实是 Windows 控制台时才用自绘输入行。
        self.native = _EDITOR_OPT_IN and _console_input_ok()

    # ---------- 对外 ----------
    def read(self, prompt: str) -> str:
        if not self.native:
            return input(prompt)
        self.hist_idx = len(self.history)
        self.tab_idx = -1
        buf = ""
        self._render(prompt, buf)
        while True:
            ch = msvcrt.getwch()
            if ch == "\x03":
                self._leave()
                raise KeyboardInterrupt
            if ch in ("\r", "\n"):
                self._leave()
                if buf.strip():
                    self.history.append(buf)
                return buf
            if ch == "\x08":
                if buf:
                    buf = buf[:-1]
                self.hist_idx = len(self.history)
                self._reset_tab()
                self._render(prompt, buf)
            elif ch == "\t":
                completed = self._complete(buf)
                if completed != buf:
                    buf = completed
                    self._render(prompt, buf)
            elif ch in ("\x00", "\xe0"):
                code = msvcrt.getwch()
                if code == "H" and self.history:
                    self.hist_idx = max(0, self.hist_idx - 1)
                    buf = self.history[self.hist_idx]
                    self._render(prompt, buf)
                elif code == "P":
                    if self.history:
                        self.hist_idx = min(len(self.history), self.hist_idx + 1)
                        buf = self.history[self.hist_idx] if self.hist_idx < len(self.history) else ""
                        self._render(prompt, buf)
            elif ch >= " ":
                buf += ch
                self.hist_idx = len(self.history)
                self._reset_tab()
                self._render(prompt, buf)

    # ---------- 内部 ----------
    def _reset_tab(self) -> None:
        self.tab_idx = -1
        self._last_complete = ""

    def _matches(self, buf: str) -> list[str]:
        if not buf.startswith("/") or " " in buf:
            return []
        return [cmd for cmd, _ in self.commands if cmd.startswith(buf)]

    def _suggestion(self, buf: str) -> str:
        matches = self._matches(buf)
        if not matches:
            return ""
        text = "  ".join(matches)
        avail = max(20, term_width() - 4)
        while visible_width(text) > avail and "  " in text:
            text = text.rsplit("  ", 1)[0]
        return text

    def _render(self, prompt: str, buf: str) -> None:
        hint = self._suggestion(buf)
        out = "\r\x1b[2K" + prompt + buf
        out += "\x1b7"          # 保存光标
        out += "\x1b[B\r\x1b[2K"  # 下一行清空，用来放联想
        if hint:
            out += f"{DIM}{hint}{RESET}"
        out += "\x1b8"          # 恢复光标
        sys.stdout.write(out)
        sys.stdout.flush()

    def _complete(self, buf: str) -> str:
        matches = self._matches(buf)
        if not matches:
            return buf
        if len(matches) == 1:
            return matches[0] + " "
        common = os.path.commonprefix(matches)
        if len(common) > len(buf):
            return common
        if buf != self._last_complete:
            self.tab_idx = -1
            self._last_complete = buf
        self.tab_idx = (self.tab_idx + 1) % len(matches)
        return matches[self.tab_idx]

    @staticmethod
    def _leave() -> None:
        # 光标从输入行移到下一行（联想行），清空后停在行首
        sys.stdout.write("\x1b[B\r\x1b[2K\r")
        sys.stdout.flush()
