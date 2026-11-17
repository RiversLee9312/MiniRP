"""按预设隔离的对话存档。世界进度由 world_state.json 独立管理。"""
from __future__ import annotations

import json
import os
import re
import uuid
from datetime import datetime
from pathlib import Path


class ConversationError(ValueError):
    pass


class ConversationStore:
    def __init__(self, root: Path | str):
        self.root = Path(os.environ.get("MINICHAT_CONVERSATION_DIR") or root)

    def _path(self, preset_id, ident):
        if any(not isinstance(value, str) or not re.fullmatch(r"[a-zA-Z0-9_-]+", value) for value in (preset_id, ident)):
            raise ConversationError("预设或对话 ID 无效。")
        return self.root / preset_id / (ident + ".json")

    @staticmethod
    def new(preset_id, name=""):
        return {"id": uuid.uuid4().hex[:12], "preset_id": preset_id, "name": name or "新对话", "version": 1}

    def save(self, conversation):
        path = self._path(conversation["preset_id"], conversation["id"])
        path.parent.mkdir(parents=True, exist_ok=True)
        conversation["updated_at"] = datetime.now().astimezone().isoformat(timespec="seconds")
        temp = path.with_suffix(".json.tmp")
        temp.write_text(json.dumps(conversation, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(temp, path)

    def load(self, preset_id, ident):
        try:
            value = json.loads(self._path(preset_id, ident).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise ConversationError(f"无法加载对话 {ident}：{error}") from error
        if not isinstance(value, dict) or value.get("preset_id") != preset_id or value.get("id") != ident:
            raise ConversationError("对话存档与当前预设不匹配。")
        for key in ("chat", "search", "env", "preset"):
            context = value.get(key, {})
            if not isinstance(context, dict) or not isinstance(context.get("history", []), list) or not isinstance(context.get("summary", ""), str):
                raise ConversationError("对话历史格式无效。")
            if any(not isinstance(message, dict) or message.get("role") not in ("user", "assistant", "tool", "system") for message in context.get("history", [])):
                raise ConversationError("对话消息格式无效。")
        if not isinstance(value.get("trace", []), list) or not isinstance(value.get("ui", {}), dict):
            raise ConversationError("对话界面存档格式无效。")
        return value

    def list(self, preset_id):
        # 使用同一个路径验证，禁止跨预设读取。
        directory = self._path(preset_id, "index").parent
        values = []
        for path in directory.glob("*.json"):
            try:
                value = self.load(preset_id, path.stem)
                values.append({key: value.get(key, "") for key in ("id", "name", "updated_at")})
            except ConversationError:
                continue
        return sorted(values, key=lambda value: value["updated_at"], reverse=True)
