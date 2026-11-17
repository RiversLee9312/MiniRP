"""Preset 测试：内置配置、旧状态迁移、设定编辑 Agent 与 Markdown 导入。"""
import json
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import presets
import preset_agent
from preset_agent import PresetAgent

UFO = Path(__file__).parent.parent / "presets" / "ufo-2026.json"


def tool_call(call_id, name, args):
    return {
        "id": call_id,
        "type": "function",
        "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)},
    }


def make_stream(tool_calls):
    state = {"i": 0}

    def fake(messages, **kwargs):
        state["i"] += 1
        if state["i"] == 1:
            return {
                "role": "assistant",
                "content": "",
                "reasoning_content": "按工具最小修改。",
                "tool_calls": tool_calls,
            }
        return {"role": "assistant", "content": "完成"}

    return fake


def test_normalize() -> bool:
    cfg = presets.normalize_config(json.loads(UFO.read_text(encoding="utf-8")))
    ok = (
        cfg["id"] == "ufo-2026"
        and cfg["canon"]
        and cfg["search_editor"]["max_visible"] == 6
        and cfg["search_editor"]["max_added"] == 2
        and cfg["stage"]["guidance"]
    )
    print("normalize:", "OK" if ok else "FAIL")
    return ok


def test_migration(tmp: Path) -> bool:
    presets_dir = tmp / "presets"
    presets_dir.mkdir()
    shutil.copy(UFO, presets_dir / "ufo-2026.json")
    state_path = tmp / "world_state.json"
    legacy = {
        "sim_date": "2026-11-18",
        "phase": "旧状态",
        "canon": ["旧 canon 事实"],
        "recent": [],
        "pages": {},
        "stages": [],
    }
    state_path.write_text(json.dumps(legacy, ensure_ascii=False), encoding="utf-8")

    store = presets.PresetStore(presets_dir, state_path)
    doc = json.loads(state_path.read_text(encoding="utf-8"))
    state = store.get_state("ufo-2026")
    ok = (
        store.active_id == "ufo-2026"
        and "presets" in doc
        and doc["presets"]["ufo-2026"]["canon"] == ["旧 canon 事实"]
        and state["sim_date"] == "2026-11-18"
        and state["phase"] == "旧状态"
    )
    print("migration:", "OK" if ok else "FAIL")
    return ok


def test_edit_agent() -> bool:
    cfg = presets.normalize_config(json.loads(UFO.read_text(encoding="utf-8")))
    preset_agent.stream_chat = make_stream(
        [
            tool_call("c1", "set_value", {"path": "search_editor.scope", "value": "只改天气相关的内容"}),
            tool_call("c2", "append_item", {"path": "canon", "value": "2026-11-16 城市上空出现第二个太阳。"}),
            tool_call("c3", "remove_item", {"path": "canon", "index": 6}),
        ]
    )
    updated = PresetAgent.edit_config(cfg, "把编辑范围改成天气，并加入第二个太阳的事实。")
    ok = (
        updated["search_editor"]["scope"] == "只改天气相关的内容"
        and any("第二个太阳" in f for f in updated["canon"])
        and len(updated["canon"]) == len(cfg["canon"])
        and updated["id"] == cfg["id"]
    )
    print("edit agent:", "OK" if ok else "FAIL")
    return ok


def test_import_agent(tmp: Path) -> bool:
    md = tmp / "rain-city.md"
    md.write_text(
        "# 雨城\n\n这是一座终年下雨的城市。2026-11-16 早晨，雨停了 10 分钟。\n"
        "市政厅要求所有媒体只报道雨停的官方通报。",
        encoding="utf-8",
    )
    calls = [
        tool_call("c1", "set_value", {"path": "name", "value": "雨城"}),
        tool_call("c2", "set_value", {"path": "phase", "value": "雨城终年下雨，今晨雨停了 10 分钟。"}),
        tool_call("c3", "set_value", {"path": "world.summary", "value": "一座终年下雨的城市。"}),
        tool_call("c4", "append_item", {"path": "canon", "value": "2026-11-16 07:10，雨城第一次停雨 10 分钟。"}),
        tool_call("c5", "append_item", {"path": "canon", "value": "市政厅要求媒体只报道官方通报。"}),
        tool_call("c6", "append_item", {"path": "canon", "value": "雨城全年平均降雨 300 天。"}),
        tool_call("c7", "append_item", {"path": "world.rules", "value": "搜索结果不得否认停雨事件。"}),
        tool_call("c8", "set_value", {"path": "search_editor.scope", "value": "只改与停雨相关的内容。"}),
    ]
    preset_agent.stream_chat = make_stream(calls)
    presets_dir = tmp / "imported_presets"
    presets_dir.mkdir()
    store = presets.PresetStore(presets_dir, tmp / "import_state.json")
    cfg = PresetAgent(store).import_markdown(md)
    saved = json.loads((presets_dir / f"{cfg['id']}.json").read_text(encoding="utf-8"))
    ok = (
        cfg["name"] == "雨城"
        and len(cfg["canon"]) >= 3
        and cfg["world"]["rules"]
        and saved["id"] == cfg["id"]
        and cfg["search_editor"]["scope"] == "只改与停雨相关的内容。"
    )
    print("import agent:", "OK" if ok else "FAIL")
    return ok


def main() -> int:
    ok = test_normalize()
    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)
        ok = test_migration(tmp) and ok
        ok = test_import_agent(tmp) and ok
    ok = test_edit_agent() and ok
    print("PRESET TEST:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
