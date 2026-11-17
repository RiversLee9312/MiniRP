"""共享流式轨迹、动态侧栏与 UI 响应性，不调用真实模型。"""
import asyncio
import json
import sys
import time
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from agent_context import AgentContext
from agent_runtime import AgentTrace, get_trace, set_trace, stream_chat
from env_agent import EnvAgent
from harness import ChatAgent
from preset_agent import PresetAgent
from presets import default_state, normalize_config
from search_agent import SearchAgent
from terminal_ui import TerminalUI
from world import WorldState
from prompt_toolkit.data_structures import Point, Size
from prompt_toolkit.input import create_pipe_input
from prompt_toolkit.mouse_events import MouseButton, MouseEvent, MouseEventType
from prompt_toolkit.output import DummyOutput


class WideOutput(DummyOutput):
    def get_size(self):
        return Size(rows=45, columns=160)


class RuntimeTest(unittest.TestCase):
    def setUp(self):
        self.trace = AgentTrace()
        old = set_trace(self.trace)
        self.addCleanup(set_trace, old)

    def test_all_six_roles_receive_both_stream_callbacks(self):
        world = WorldState(normalize_config({"id": "test", "canon": []}), default_state(normalize_config({"id": "test"})))
        def fake(messages, **kwargs):
            self.assertTrue(callable(kwargs["on_reasoning"]))
            self.assertTrue(callable(kwargs["on_content"]))
            kwargs["on_reasoning"]("逐字")
            kwargs["on_reasoning"]("思考")
            tools = [t["function"]["name"] for t in kwargs.get("tools", [])]
            self.assertTrue({"db_list", "db_read", "db_write", "db_patch", "db_search", "db_delete"}.issubset(tools))
            if "real_search" in tools:
                return {"content": "", "reasoning_content": "逐字思考", "tool_calls": [
                    {"id": "c", "type": "function", "function": {"name": "submit_search_results", "arguments": '{"refs": []}'}}]}
            text = '{"facts":["压缩后的事实"]}' if kwargs.get("response_format") else "处理完成"
            kwargs["on_content"](text[:2])
            kwargs["on_content"](text[2:])
            return {"content": text, "reasoning_content": "逐字思考"}
        with patch("agent_runtime._stream_chat", side_effect=fake):
            search = SearchAgent(world)
            ChatAgent(search).ask("你好")
            search.search("检索")
            EnvAgent(world).next_stage()
            PresetAgent.edit_config(world.config, "保留当前预设")
            AgentContext()._summarize([])
            state = default_state(world.config)
            state["canon"] = [f"事实 {i}" for i in range(121)]
            world.compact(state)
        thoughts = [b for b in self.trace.snapshot() if b.kind == "thought"]
        self.assertEqual({b.agent for b in thoughts}, {"ChatAgent", "SearchAgent", "EnvAgent", "PresetAgent", "HistorySummary", "CanonSummary"})
        self.assertTrue(all(b.text == "逐字思考" and b.done for b in thoughts))

    def test_default_expansion_and_individual_toggle_are_independent(self):
        first = self.trace.start("ChatAgent", "thought")
        self.trace.append(first, "思考")
        self.assertFalse(self.trace.snapshot()[0].expanded)
        self.assertTrue(self.trace.toggle(first))
        self.trace.default_expanded = True
        second = self.trace.start("EnvAgent", "thought")
        self.assertTrue(self.trace.snapshot()[1].expanded)
        self.assertFalse(self.trace.toggle(first))
        self.assertTrue(self.trace.snapshot()[1].expanded)
        exported = self.trace.export()
        restored = AgentTrace()
        restored.restore(exported)
        self.assertEqual(restored.snapshot()[0].text, "思考")
        self.assertFalse(restored.snapshot()[0].expanded)

    def test_ui_stays_responsive_during_blocking_model_stream(self):
        async def scenario():
            with create_pipe_input() as inp:
                ui = TerminalUI(AgentTrace(), input=inp, output=WideOutput())
                beats, screen_text = [], []
                def fake(messages, **kwargs):
                    for _ in range(10):
                        time.sleep(0.05)
                        kwargs["on_reasoning"]("生成中的思考。")
                    kwargs["on_content"]("完成")
                    return {"content": "完成", "reasoning_content": "生成中的思考。" * 10}
                def main(*, ui):
                    self.assertEqual(ui.read(), "/edit-canon 修改事实")
                    stream_chat([], agent="EnvAgent")
                    self.assertEqual(ui.read(), "/exit")
                    return 0
                async def actor():
                    await asyncio.sleep(0.1)
                    self.assertEqual(ui.visible_lanes, {"main"})
                    inp.send_text("/env\r")
                    await asyncio.sleep(0.12)
                    self.assertIn("env", ui.visible_lanes)
                    inp.send_text("修改事实\r")
                    await asyncio.sleep(0.15)
                    thought = next(b for b in ui.trace.snapshot() if b.kind == "thought")
                    self.assertFalse(thought.done)
                    mouse = MouseEvent(Point(1, 1), MouseEventType.MOUSE_UP, MouseButton.LEFT, frozenset())
                    handler = ui.mouse_handler("env", thought.id)
                    handler(mouse)
                    self.assertTrue(ui.trace.snapshot()[thought.id - 1].expanded)
                    handler(mouse)
                    self.assertFalse(ui.trace.snapshot()[thought.id - 1].expanded)
                    ui.enqueue("main", "/preset-agent")
                    await asyncio.sleep(0.05)
                    self.assertEqual(ui.visible_lanes, {"main", "env", "preset"})
                    inp.send_text("/close\r")
                    await asyncio.sleep(0.05)
                    self.assertNotIn("preset", ui.visible_lanes)
                    screen = ui.app.renderer._last_screen
                    if screen:
                        screen_text.append("\n".join("".join(screen.data_buffer[y][x].char for x in range(160)) for y in range(45)))
                    for _ in range(50):
                        if any(b.kind == "content" and b.done for b in ui.trace.snapshot()):
                            break
                        await asyncio.sleep(0.02)
                    ui.enqueue("env", "/close")
                    self.assertEqual(ui.visible_lanes, {"main"})
                    ui.enqueue("main", "/exit")
                async def heartbeat():
                    while not ui.exit_requested:
                        beats.append(time.perf_counter())
                        await asyncio.sleep(0.01)
                with patch("agent_runtime._stream_chat", side_effect=fake):
                    actor_task = asyncio.create_task(actor())
                    beat_task = asyncio.create_task(heartbeat())
                    result = await asyncio.wait_for(ui.run_async(main), 8)
                    await actor_task
                    await beat_task
                self.assertEqual(result, 0)
                self.assertGreater(len(beats), 25)
                self.assertLess(max(b - a for a, b in zip(beats, beats[1:])), 0.2)
                self.assertIn("/ 命令", "\n".join(screen_text))
                self.assertIn("EnvAgent", "\n".join(screen_text))
        asyncio.run(scenario())

    def test_modal_selection_and_masked_settings_do_not_enter_trace(self):
        async def scenario():
            with create_pipe_input() as inp:
                ui = TerminalUI(AgentTrace(), input=inp, output=WideOutput())
                secret = "private-test-api-key"
                selected = []
                def main(*, ui):
                    selected.append(ui.choose_conversation("test-preset", []))
                    selected.append(ui.configure_settings({"DEEPSEEK_MODEL": "example-model", "DEEPSEEK_API_KEY": secret,
                                                          "DEEPSEEK_BASE_URL": "https://api.example.com"}))
                    return 0
                async def actor():
                    for _ in range(100):
                        if ui._dialog_cancel:
                            break
                        await asyncio.sleep(0.01)
                    await asyncio.sleep(0.08)
                    inp.send_text("\t\r")
                    for _ in range(100):
                        if selected and ui._dialog_cancel:
                            break
                        await asyncio.sleep(0.01)
                    await asyncio.sleep(0.08)
                    screen = ui.app.renderer._last_screen
                    text = "\n".join("".join(screen.data_buffer[y][x].char for x in range(160)) for y in range(45))
                    self.assertIn("API Key", text)
                    self.assertNotIn(secret, text)
                    inp.send_text("\x03")  # Ctrl+C 取消窗口，不影响应用事件循环。
                actor_task = asyncio.create_task(actor())
                self.assertEqual(await asyncio.wait_for(ui.run_async(main), 6), 0)
                await actor_task
                self.assertEqual(selected, ["__new__", None])
                self.assertNotIn(secret, json.dumps(ui.trace.export()))
        asyncio.run(scenario())

    def test_settings_dialog_save_can_be_reached_with_keyboard(self):
        async def scenario():
            with create_pipe_input() as inp:
                ui = TerminalUI(AgentTrace(), input=inp, output=WideOutput())
                values = {"DEEPSEEK_MODEL": "configured-model", "DEEPSEEK_API_KEY": "test-password-value",
                          "DEEPSEEK_BASE_URL": "https://api.example.com", "DEEPSEEK_THINKING": "disabled",
                          "DEEPSEEK_REASONING_EFFORT": "high", "MINICHAT_FETCH_BACKEND": "api_then_local"}
                selected = []
                def main(*, ui):
                    selected.append(ui.configure_settings(values))
                    return 0
                async def actor():
                    for _ in range(100):
                        if ui._dialog_cancel:
                            break
                        await asyncio.sleep(0.01)
                    await asyncio.sleep(0.08)
                    inp.send_text("\t\t\t\t\t\t\r")
                actor_task = asyncio.create_task(actor())
                self.assertEqual(await asyncio.wait_for(ui.run_async(main), 6), 0)
                await actor_task
                self.assertEqual(selected, [values])
                self.assertNotIn(values["DEEPSEEK_API_KEY"], json.dumps(ui.trace.export()))
        asyncio.run(scenario())

    def test_input_grows_caps_and_scrolls_with_cursor_in_all_panes(self):
        async def scenario():
            with create_pipe_input() as inp:
                ui = TerminalUI(AgentTrace(), input=inp, output=WideOutput())
                measurements = []
                def main(*, ui):
                    self.assertEqual(ui.read(), "/exit")
                    return 0
                async def actor():
                    await asyncio.sleep(0.1)
                    for lane in ("main", "env", "preset"):
                        if lane != "main":
                            ui.open_sidebar(lane)
                            await asyncio.sleep(0.05)
                        ui.focus(lane)
                        area = ui.inputs[lane]
                        area.text = ""
                        await asyncio.sleep(0.06)
                        initial = area.window.render_info.window_height
                        area.text = "中文输入自动增长" * 40
                        area.buffer.cursor_position = len(area.text)
                        await asyncio.sleep(0.08)
                        grown = area.window.render_info.window_height
                        self.assertGreater(grown, initial)
                        area.text = "很长的中文输入内容" * 900 + "光标尾部"
                        area.buffer.cursor_position = len(area.text)
                        await asyncio.sleep(0.08)
                        info = area.window.render_info
                        self.assertEqual(info.window_height, 8)
                        self.assertGreater(area.window.vertical_scroll + area.window.vertical_scroll_2, 0)
                        self.assertLess(info.cursor_position.y, info.window_height)
                        area.buffer.cursor_position = 0
                        await asyncio.sleep(0.06)
                        self.assertEqual(area.window.vertical_scroll, 0)
                        self.assertEqual(area.window.vertical_scroll_2, 0)
                        measurements.append((lane, initial, grown, info.window_height))
                    ui.enqueue("main", "/exit")
                actor_task = asyncio.create_task(actor())
                self.assertEqual(await asyncio.wait_for(ui.run_async(main), 8), 0)
                await actor_task
                self.assertEqual(len(measurements), 3)
        asyncio.run(scenario())


if __name__ == "__main__":
    unittest.main()
