"""本地 .env 配置读写；密钥只经过密码控件与本地文件。"""
from __future__ import annotations

import os
from pathlib import Path
from urllib.parse import urlsplit

import deepseek_client as client

DEFAULTS = {"DEEPSEEK_MODEL": "deepseek-flash", "DEEPSEEK_API_KEY": "",
            "DEEPSEEK_BASE_URL": "https://api.deepseek.com", "DEEPSEEK_THINKING": "enabled",
            "DEEPSEEK_REASONING_EFFORT": "max"}


class SettingsStore:
    def __init__(self, path=None):
        self.path = Path(path or Path(__file__).with_name(".env"))

    def current(self):
        return {key: os.environ.get(key, default) for key, default in DEFAULTS.items()}

    def save(self, values):
        values = {key: values.get(key, default).strip() for key, default in DEFAULTS.items()}
        if any("\n" in value or "\r" in value or "\x00" in value or value[:1] in ("'", '"') or value[-1:] in ("'", '"') for value in values.values()):
            raise ValueError("配置值不能包含换行或首尾引号。")
        if not values["DEEPSEEK_MODEL"] or not values["DEEPSEEK_API_KEY"]:
            raise ValueError("模型和 API Key 不能为空。")
        parts = urlsplit(values["DEEPSEEK_BASE_URL"])
        if parts.scheme not in ("http", "https") or not parts.hostname or parts.username or parts.password:
            raise ValueError("API 地址必须是有效的 http/https 地址，凭据请填写在 API Key 中。")
        if values["DEEPSEEK_THINKING"] not in ("enabled", "disabled"):
            raise ValueError("思考模式必须是 enabled 或 disabled。")
        if values["DEEPSEEK_REASONING_EFFORT"] not in ("low", "high", "max"):
            raise ValueError("思考强度必须是 low/high/max。")
        original = self.path.read_text(encoding="utf-8") if self.path.exists() else ""
        lines, written = [], set()
        for line in original.splitlines():
            key = line.split("=", 1)[0].strip() if "=" in line and not line.lstrip().startswith("#") else ""
            if key in values:
                if key not in written:
                    lines.append(f"{key}={values[key]}")
                    written.add(key)
            else:
                lines.append(line)
        lines.extend(f"{key}={value}" for key, value in values.items() if key not in written)
        temp = self.path.with_suffix(".env.tmp")
        temp.write_text("\n".join(lines) + "\n", encoding="utf-8")
        os.replace(temp, self.path)
        client.apply_settings(values)
