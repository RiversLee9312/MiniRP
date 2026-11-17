"""OpenCode 风格的终端界面：三通道、可折叠轨迹、后台任务与常驻命令栏。"""
from __future__ import annotations

import asyncio
import contextlib
import json
import os
import queue
import re
import sys
import traceback

from agent_runtime import AgentTrace, get_trace, set_trace

_ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
_LANES = ("main", "env", "preset")
_MODES = {"env": [("编辑事实", "/edit-canon"), ("推进剧情", "/next-stage")],
          "preset": [("编辑预设", "/edit-preset"), ("创建设定", "/make-preset"), ("导入文件", "/import-preset")]}
COMMANDS = [("/preset", "切换设定"), ("/cot", "默认展开思考"), ("/trace", "工具轨迹"),
            ("/env", "世界侧栏"), ("/preset-agent", "预设侧栏"), ("/save", "保存对话"),
            ("/load", "加载对话"), ("/new", "新建对话"), ("/settings", "模型与密钥"), ("/help", "全部命令"), ("/exit", "退出")]


class ConsoleRenderer:
    """管道/纯文本模式仍使用相同的流式块模型。"""
    def __init__(self):
        self.printers = {}

    def __call__(self, event, block, text):
        import term
        if block.kind == "tool":
            if event == "finish" and get_trace().tools_visible:
                args = block.text.split("\n结果：\n", 1)[0].replace("参数：\n", "").replace("\n", " ")
                print(f"  {'!' if block.error else '·'} {block.agent} › {block.title} {args[:140]}")
            return
        if event == "delta" and block.expanded and block.kind != "log":
            if block.id not in self.printers:
                label = block.agent + (" · 思考" if block.kind == "thought" else "")
                self.printers[block.id] = term.StreamPrinter(label)
            self.printers[block.id].feed(text)
        if event == "finish":
            printer = self.printers.pop(block.id, None)
            if printer:
                printer.close()
            elif block.kind == "thought" and block.text and not block.expanded:
                print(f"  ▸ [{block.id}] {block.agent} · 思考 {len(block.text)} 字（折叠）")


class _Writer:
    encoding = "utf-8"

    def __init__(self, trace):
        self.trace = trace

    def write(self, text):
        clean = _ANSI.sub("", text).replace("\r", "")
        if clean:
            self.trace.log(clean)
        return len(text)

    def flush(self):
        pass

    def isatty(self):
        return False

    def reconfigure(self, **kwargs):
        pass


class TerminalUI:
    def __init__(self, trace=None, *, input=None, output=None):
        from prompt_toolkit.application import Application
        from prompt_toolkit.completion import Completer, Completion
        from prompt_toolkit.data_structures import Point
        from prompt_toolkit.history import InMemoryHistory
        from prompt_toolkit.filters import Condition
        from prompt_toolkit.key_binding import KeyBindings
        from prompt_toolkit.layout import DynamicContainer, Float, FloatContainer, HSplit, Layout, VSplit, Window
        from prompt_toolkit.layout.controls import FormattedTextControl
        from prompt_toolkit.layout.dimension import Dimension
        from prompt_toolkit.layout.margins import ScrollbarMargin
        from prompt_toolkit.layout.menus import CompletionsMenu
        from prompt_toolkit.styles import Style
        from prompt_toolkit.widgets import Frame, TextArea

        self.trace = trace or AgentTrace()
        self.trace.sink = lambda *_: self.invalidate()
        self.pending = queue.Queue()
        self.busy_lane = None
        self.mode = {"env": 0, "preset": 0}
        self.commands = list(COMMANDS)
        self.context = "minichat"
        self.active_lane = "main"
        self.visible_lanes = {"main"}
        self.follow = {lane: True for lane in _LANES}
        self.line_counts = {lane: 1 for lane in _LANES}
        self.controls, self.windows, self.inputs, self.panes = {}, {}, {}, {}
        self.exit_requested = False
        self._loop = None
        self._dialog_cancel = None
        self._ui_tasks = set()
        self._Point = Point

        ui = self
        class SlashCompleter(Completer):
            def get_completions(self, document, complete_event):
                text = document.text_before_cursor
                if text.startswith("/") and " " not in text:
                    commands = ui.commands + ([("/close", "关闭侧栏")] if ui.focused_lane() != "main" else [])
                    for command, description in commands:
                        if command.startswith(text):
                            yield Completion(command, start_position=-len(text), display_meta=description)

        for lane in _LANES:
            control = FormattedTextControl(lambda lane=lane: self.fragments(lane), focusable=True,
                                           show_cursor=False, get_cursor_position=lambda lane=lane: self._cursor(lane))
            self.controls[lane] = control
            window = Window(control, wrap_lines=True, right_margins=[ScrollbarMargin()], always_hide_cursor=True)
            self.windows[lane] = window
            area = TextArea(height=3, multiline=False, prompt="❯ ", focus_on_click=True,
                            accept_handler=lambda buffer, lane=lane: self.accept(lane, buffer),
                            history=InMemoryHistory(), completer=SlashCompleter(), complete_while_typing=True)
            self.inputs[lane] = area
            rows = []
            if lane != "main":
                rows.append(Window(FormattedTextControl(lambda lane=lane: self.mode_fragments(lane)), height=1))
            rows += [window, Window(height=1, char="─", style="class:separator"), area]
            title = {"main": "ChatAgent / SearchAgent  [F1]", "env": "EnvAgent  [F2]", "preset": "PresetAgent  [F3]"}[lane]
            self.panes[lane] = Frame(HSplit(rows), title=title)

        wide = VSplit([HSplit([self.panes["main"]], width=Dimension(weight=4)),
                       HSplit([self.panes["env"]], width=Dimension(weight=2)),
                       HSplit([self.panes["preset"]], width=Dimension(weight=2))], padding=1)
        medium = VSplit([HSplit([self.panes["main"]], width=Dimension(weight=3)),
                         HSplit([self.panes["env"], self.panes["preset"]], width=Dimension(weight=2))], padding=1)

        def body():
            width = self.app.output.get_size().columns if hasattr(self, "app") else 160
            visible = [lane for lane in _LANES if lane in self.visible_lanes]
            if len(visible) == 1 or width < 85:
                return self.panes[self.active_lane if self.active_lane in self.visible_lanes else "main"]
            if len(visible) == 2:
                return VSplit([HSplit([self.panes["main"]], width=Dimension(weight=3)),
                               HSplit([self.panes[visible[1]]], width=Dimension(weight=2))], padding=1)
            return wide if width >= 130 else medium

        keys = KeyBindings()
        for index, lane in enumerate(_LANES, 1):
            keys.add(f"f{index}", filter=Condition(lambda: self._dialog_cancel is None))(lambda event, lane=lane: self.focus(lane))

        @keys.add("s-tab")
        def _(event):
            if self._dialog_cancel:
                event.app.layout.focus_previous()
                return
            lanes = [lane for lane in _LANES if lane in self.visible_lanes]
            self.focus(lanes[(lanes.index(self.focused_lane()) + 1) % len(lanes)])

        @keys.add("tab")
        def _(event):
            if self._dialog_cancel:
                event.app.layout.focus_next()
                return
            buffer = event.current_buffer
            if buffer.complete_state:
                buffer.complete_next()
            else:
                buffer.start_completion(select_first=True)

        @keys.add("escape", "m")
        def _(event):
            lane = self.focused_lane()
            if lane in _MODES:
                self.mode[lane] = (self.mode[lane] + 1) % len(_MODES[lane])

        @keys.add("pageup")
        def _(event):
            self.scroll(self.focused_lane(), -12)

        @keys.add("pagedown")
        def _(event):
            self.scroll(self.focused_lane(), 12)

        @keys.add("c-end")
        def _(event):
            self.follow[self.focused_lane()] = True

        @keys.add("c-t")
        def _(event):
            lane = self.focused_lane()
            for block in reversed(self.trace.snapshot()):
                if block.channel == lane and block.kind == "thought" and block.text:
                    self.trace.toggle(block.id)
                    break

        @keys.add("c-c")
        def _(event):
            if self._dialog_cancel:
                self._dialog_cancel()
            elif self.busy_lane:
                self.trace.cancel_requested = True
                self.trace.log("\n正在取消当前请求…\n", channel=self.busy_lane)
            else:
                event.current_buffer.reset()

        @keys.add("c-d")
        def _(event):
            if self._dialog_cancel:
                self._dialog_cancel()
            self.enqueue("main", "/exit")

        @keys.add("escape", filter=Condition(lambda: self._dialog_cancel is not None))
        def _(event):
            self._dialog_cancel()

        container = HSplit([
            Window(FormattedTextControl(lambda: [("class:header", "  " + self.context)]), height=1),
            DynamicContainer(body),
            Window(FormattedTextControl(self.footer), height=2, wrap_lines=True),
        ])
        self.root = FloatContainer(container, floats=[Float(xcursor=True, ycursor=True, content=CompletionsMenu(max_height=8))])
        self.app = Application(layout=Layout(self.root, focused_element=self.inputs["main"]), key_bindings=keys,
                               full_screen=True, mouse_support=True, input=input, output=output,
                               min_redraw_interval=0.04,
                               style=Style.from_dict({"header": "bold ansicyan", "frame.border": "ansibrightblack",
                                   "frame.label": "bold", "thought": "ansibrightblack", "tool": "ansicyan",
                                   "error": "ansired", "separator": "ansibrightblack", "footer": "ansibrightblack",
                                   "selected": "reverse", "user": "bold ansicyan"}))

    def invalidate(self):
        if hasattr(self, "app"):
            self.app.invalidate()  # prompt_toolkit 的线程安全失效通知；不在工作线程渲染。

    def set_commands(self, commands):
        self.commands = list(commands)
        self.invalidate()

    def set_context(self, config, model, sim_date, conversation=""):
        self.context = f"minichat  ·  {config['name']}  ·  {sim_date}  ·  {model}" + (f"  ·  {conversation}" if conversation else "")
        self.invalidate()

    def focused_lane(self):
        for lane, area in self.inputs.items():
            if self.app.layout.has_focus(area):
                return lane
        return self.active_lane

    def focus(self, lane):
        if lane not in self.visible_lanes:
            return
        self.active_lane = lane
        self.app.layout.focus(self.inputs[lane])
        self.invalidate()

    def open_sidebar(self, lane):
        # 所有 Layout 操作只在 UI 事件循环执行。
        def apply():
            self.visible_lanes.add(lane)
            self.focus(lane)
        if self._loop:
            self._loop.call_soon_threadsafe(apply)
        else:
            apply()

    def close_sidebar(self, lane):
        if lane == "main":
            return
        self.visible_lanes.discard(lane)
        self.focus("main")

    def export_state(self):
        return {"visible_lanes": sorted(self.visible_lanes), "mode": dict(self.mode)}

    def restore_state(self, state):
        def apply():
            self.visible_lanes = {"main"} | (set(state.get("visible_lanes", [])) & {"env", "preset"})
            for lane in _MODES:
                value = state.get("mode", {}).get(lane, 0)
                self.mode[lane] = value if type(value) is int and 0 <= value < len(_MODES[lane]) else 0
            self.focus("main")
        if self._loop:
            self._loop.call_soon_threadsafe(apply)
        else:
            apply()

    def choose_conversation(self, preset_id, items):
        """后台调用，在 UI 线程显示选择器；等待期间 UI 继续处理事件。"""
        from prompt_toolkit.layout import Float
        from prompt_toolkit.widgets import Button, Dialog, Label, RadioList
        result = queue.Queue()

        def show():
            choices = RadioList([("__new__", "新建对话")] + [(item["id"], f"{item['name']}  ·  {item['updated_at']}") for item in items])
            dialog_float = None

            def finish(value):
                self._dialog_cancel = None
                self.root.floats.remove(dialog_float)
                self.focus("main")
                result.put(value)

            from prompt_toolkit.layout import HSplit
            dialog = Dialog(title=f"选择对话 · {preset_id}",
                            body=HSplit([Label("加载这个预设下的已有对话，或新建对话。"), choices]),
                            buttons=[Button("确认", handler=lambda: finish(choices.current_value)),
                                     Button("取消", handler=lambda: finish(None))], modal=True, width=76)
            dialog_float = Float(content=dialog)
            self._dialog_cancel = lambda: finish(None)
            self.root.floats.append(dialog_float)
            self.app.layout.focus(choices)
            self.invalidate()

        self._loop.call_soon_threadsafe(show)
        return result.get()

    def configure_settings(self, values, error=""):
        from prompt_toolkit.layout import Float, HSplit
        from prompt_toolkit.widgets import Button, Dialog, Label, RadioList, TextArea
        result = queue.Queue()

        def show():
            fields = {key: TextArea(text=values.get(key, ""), height=1, multiline=False,
                                   password=key == "DEEPSEEK_API_KEY") for key in (
                                       "DEEPSEEK_MODEL", "DEEPSEEK_API_KEY", "DEEPSEEK_BASE_URL")}
            thinking = RadioList([("enabled", "开启思考"), ("disabled", "关闭思考")], default=values.get("DEEPSEEK_THINKING", "enabled"))
            effort = RadioList([("low", "low"), ("high", "high"), ("max", "max")], default=values.get("DEEPSEEK_REASONING_EFFORT", "max"))
            dialog_float = None

            def finish(save):
                self._dialog_cancel = None
                selected = {key: area.text for key, area in fields.items()} if save else None
                if selected is not None:
                    selected.update(DEEPSEEK_THINKING=thinking.current_value, DEEPSEEK_REASONING_EFFORT=effort.current_value)
                # 密钥控件不进入轨迹、日志或对话存档。
                for area in fields.values():
                    area.text = ""
                self.root.floats.remove(dialog_float)
                self.focus("main")
                result.put(selected)

            rows = [Label(error, style="class:error")] if error else []
            for key, title in (("DEEPSEEK_MODEL", "模型"), ("DEEPSEEK_API_KEY", "API Key"), ("DEEPSEEK_BASE_URL", "API 地址")):
                rows.extend([Label(title), fields[key]])
            rows.extend([Label("思考模式"), thinking, Label("思考强度"), effort])
            dialog = Dialog(title="模型与 API 配置 · .env", body=HSplit(rows),
                            buttons=[Button("保存", handler=lambda: finish(True)), Button("取消", handler=lambda: finish(False))],
                            modal=True, width=76)
            dialog_float = Float(content=dialog)
            self._dialog_cancel = lambda: finish(False)
            self.root.floats.append(dialog_float)
            self.app.layout.focus(fields["DEEPSEEK_MODEL"])
            self.invalidate()

        self._loop.call_soon_threadsafe(show)
        return result.get()

    def _cursor(self, lane):
        y = self.line_counts[lane] - 1 if self.follow[lane] else self.windows[lane].vertical_scroll
        return self._Point(0, max(0, y))

    def scroll(self, lane, amount):
        self.follow[lane] = False
        window = self.windows[lane]
        window.vertical_scroll = min(max(0, window.vertical_scroll + amount), max(0, self.line_counts[lane] - 1))
        self.invalidate()

    def mouse_handler(self, lane, ident=None):
        from prompt_toolkit.mouse_events import MouseButton, MouseEventType
        def handler(event):
            if event.event_type == MouseEventType.SCROLL_UP:
                self.scroll(lane, -3)
            elif event.event_type == MouseEventType.SCROLL_DOWN:
                self.scroll(lane, 3)
            elif ident is not None and event.event_type == MouseEventType.MOUSE_UP and event.button == MouseButton.LEFT:
                self.follow[lane] = False
                self.trace.toggle(ident)
            return None
        return handler

    def fragments(self, lane):
        fragments = []
        for block in self.trace.snapshot():
            if block.channel != lane or (block.kind == "tool" and not self.trace.tools_visible):
                continue
            if block.kind == "log":
                fragments.append(("", block.text, self.mouse_handler(lane)))
                continue
            if block.done and not block.text:
                continue
            style = "class:error" if block.error else "class:thought" if block.kind == "thought" else "class:tool" if block.kind == "tool" else ""
            if block.kind in ("thought", "tool") or not block.expanded:
                kind = "思考" if block.kind == "thought" else block.title if block.kind == "tool" else "输出"
                status = " · 失败" if block.error else " · 进行中" if not block.done else ""
                label = f" {'▾' if block.expanded else '▸'} {block.agent} · {kind}{status}  {len(block.text)} 字\n"
                fragments.append((style, label, self.mouse_handler(lane, block.id)))
                if block.expanded:
                    fragments.append((style, "   " + block.text.replace("\n", "\n   ") + "\n", self.mouse_handler(lane)))
            else:
                fragments.append(("class:tool", f"\n {block.agent}\n", self.mouse_handler(lane)))
                fragments.append(("", " " + block.text + "\n", self.mouse_handler(lane)))
        if not fragments:
            hint = {"main": "输入问题，SearchAgent 会按需完成检索与网页请求。", "env": "直接输入世界事实修改要求，或切换到推进剧情。",
                    "preset": "直接编辑预设，或切换到创建、导入。"}[lane]
            fragments = [("class:thought", "\n " + hint + "\n", self.mouse_handler(lane))]
        self.line_counts[lane] = max(1, sum(fragment[1].count("\n") for fragment in fragments) + 1)
        return fragments

    def mode_fragments(self, lane):
        from prompt_toolkit.mouse_events import MouseButton, MouseEventType
        fragments = []
        for index, (title, _) in enumerate(_MODES[lane]):
            def click(event, index=index):
                if event.event_type == MouseEventType.MOUSE_UP and event.button == MouseButton.LEFT:
                    self.mode[lane] = index
                    self.invalidate()
            fragments.append(("class:selected" if self.mode[lane] == index else "class:thought", f" {title} ", click))
        return fragments

    def footer(self):
        lane = self.focused_lane()
        text = self.inputs[lane].text
        source = self.commands if text.startswith("/") else COMMANDS
        matches = [f"{command} {desc}" for command, desc in source if not text.startswith("/") or command.startswith(text.split(" ")[0])]
        if lane != "main":
            matches.insert(0, "/close 关闭侧栏")
        commands = "/ 命令  ·  " + ("  ·  ".join(matches[:6]) or "/help 全部命令")
        status = f"执行 {self.busy_lane}" if self.busy_lane else "就绪"
        return [("class:footer", f" {commands}\n F1 主聊天 · F2 世界 · F3 预设 · Shift+Tab 切换 · Alt+M 模式 · 点击展开/折叠 · Ctrl+C 取消  │ {status} · 排队 {self.pending.qsize()}")]

    def accept(self, lane, buffer):
        text = buffer.text.strip()
        if text:
            self.enqueue(lane, text)
        return False

    def enqueue(self, lane, text):
        if text == "/close" and lane != "main":
            self.close_sidebar(lane)
            return
        if text in ("/env", "/preset-agent"):
            self.open_sidebar("env" if text == "/env" else "preset")
            return
        command = text.split(" ", 1)[0]
        if command in ("/edit-canon", "/next-stage"):
            self.open_sidebar("env")
        elif command in ("/edit-preset", "/make-preset", "/import-preset"):
            self.open_sidebar("preset")
        self.trace.log(f"\n你 ❯ {text}\n", channel=lane)
        if text == "/cot":
            self.trace.default_expanded = not self.trace.default_expanded
            self.trace.log(f"新思考块默认{'展开' if self.trace.default_expanded else '折叠'}，已有块保持原状态。\n", channel=lane)
        elif text == "/trace":
            self.trace.tools_visible = not self.trace.tools_visible
        else:
            if lane in _MODES and not text.startswith("/"):
                text = _MODES[lane][self.mode[lane]][1] + " " + text
            if text in ("/exit", "/quit"):
                self.exit_requested = True
                if self.busy_lane:
                    self.trace.cancel_requested = True
                while not self.pending.empty():
                    try:
                        self.pending.get_nowait()
                    except queue.Empty:
                        break
            self.pending.put((lane, text))
        self.invalidate()

    def read(self):
        # 仅在后台控制线程阻塞；主 asyncio/UI 线程始终处理鼠标、键盘与重绘。
        self.busy_lane = None
        self.invalidate()
        lane, text = self.pending.get()
        self.trace.cancel_requested = False
        self.trace.channel = lane
        self.busy_lane = lane
        self.invalidate()
        return text

    async def run_async(self, main):
        self._loop = asyncio.get_running_loop()
        writer = _Writer(self.trace)

        def worker():
            old = set_trace(self.trace)
            try:
                with contextlib.redirect_stdout(writer), contextlib.redirect_stderr(writer):
                    return main(ui=self)
            except BaseException:
                self.trace.log(traceback.format_exc())
                return 1
            finally:
                set_trace(old)

        async def work():
            result = await asyncio.to_thread(worker)
            self.app.exit(result=result)

        def start():
            task = asyncio.create_task(work())
            self._ui_tasks.add(task)
            task.add_done_callback(self._ui_tasks.discard)

        try:
            return await self.app.run_async(pre_run=start)
        finally:
            if self._dialog_cancel:
                self._dialog_cancel()
            self.trace.cancel_requested = True
            self.pending.put(("main", "/exit"))
            if self._ui_tasks:
                await asyncio.gather(*self._ui_tasks, return_exceptions=True)


def run_cli(main):
    if sys.stdin.isatty() and sys.stdout.isatty() and not os.environ.get("MINICHAT_PLAIN"):
        try:
            ui = TerminalUI()
        except ImportError:
            ui = None
        if ui is not None:
            result = asyncio.run(ui.run_async(main))
            if result:
                for block in ui.trace.snapshot():
                    if block.kind == "log" and "Traceback" in block.text:
                        print(block.text, file=sys.stderr)
            return result
    trace = AgentTrace(sink=ConsoleRenderer())
    old = set_trace(trace)
    try:
        return main()
    finally:
        set_trace(old)
