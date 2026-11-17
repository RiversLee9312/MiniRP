"""设定（preset）管理：JSON 配置、Markdown 设定书导入、按预设隔离的状态。"""
from __future__ import annotations

import copy
import json
import os
import re
import time
from datetime import date
from pathlib import Path

PRESET_DIR = Path(
    os.environ.get("MINICHAT_PRESET_DIR") or Path(__file__).with_name("presets")
)
STATE_PATH = Path(
    os.environ.get("MINICHAT_STATE_PATH") or Path(__file__).with_name("world_state.json")
)
DEFAULT_PRESET_ID = "ufo-2026"


class PresetError(RuntimeError):
    pass


_DEFAULTS = {
    "chat_agent": {"system_prompt": ""},
    "world": {
        "summary": "",
        "rules": [],
    },
    "search_editor": {
        "relevance": "判断每条结果是否与本世界设定相关；无关结果保持原样，不要为了符合设定而改写。",
        "scope": "只改与本世界设定相关的表述；无关的数字、公司、人物和技术细节保持原样。",
        "credibility": (
            "可信度由你判断，目标是 high 占多数、medium 次之、low 很少；"
            "参考 authority（0–4，≥3 表示高权威）和来源，但不要机械按域名分配。"
        ),
        "add_guidance": "仅在真实结果缺少必要角度时新增 0–2 条虚构结果；不要为了凑数而添加。",
        "max_results": 8,
        "max_visible": 6,
        "max_added": 2,
    },
    "page_editor": {
        "guidance": "只对与本世界设定相关的句子或段落做精确替换；无关内容保持原样，不要整篇重写。",
    },
    "page_generator": {
        "guidance": "依据世界设定生成一篇完整网页正文，遵守世界状态锁，并保持对应媒体风格。",
    },
    "stage": {
        "guidance": "基于已确立事实推进一小步剧情；一次只发生一件事，不要抛出多个重大事件。",
    },
}

_CONFIG_EXAMPLE = """{
  "id": "my-setting",
  "name": "设定名称",
  "subtitle": "一句话副标题",
  "chat_agent": {"system_prompt": "留空使用内置助手提示；可自定义 ChatAgent 的系统提示，支持 {date}"},
  "sim_date": "2026-11-16",
  "phase": "当前世界状态（一两句话，包含是否已发生关键事件、各方态度）",
  "canon": ["设定书中已确立的 8–20 条事实，具体到时间、机构、数字、地点"],
  "world": {
    "summary": "世界设定摘要，2–4 句",
    "rules": ["所有搜索结果/网页都必须遵守的底线规则，2–6 条"]
  },
  "search_editor": {
    "relevance": "搜索编辑 Agent 如何判断相关性与是否需要改写",
    "scope": "只允许修改的范围；明确哪些内容必须保持原样",
    "credibility": "期望的可信度分布与判断方式",
    "add_guidance": "什么时候可以新增虚构搜索结果，以及数量上限",
    "max_results": 8,
    "max_visible": 6,
    "max_added": 2
  },
  "page_editor": {"guidance": "网页编辑 Agent 的改写范围与最小编辑要求"},
  "page_generator": {"guidance": "真实链接不可达、但在本世界应该存在时，如何生成页面"},
  "stage": {"guidance": "剧情推进 Agent 每次只推进一小步的规则"}
}"""

def _slugify(value: str) -> str:
    s = re.sub(r"[^a-z0-9_-]+", "-", (value or "").lower()).strip("-_")
    return s[:48]


def _as_list(value) -> list[str]:
    if isinstance(value, list):
        return [" ".join(str(x).split()) for x in value if str(x).strip()]
    if isinstance(value, str) and value.strip():
        return [" ".join(value.split())]
    return []


def _as_int(value, default: int, lo: int, hi: int) -> int:
    try:
        n = int(value)
    except (TypeError, ValueError):
        n = default
    return max(lo, min(hi, n))


def normalize_config(raw: dict, *, fallback_id: str | None = None) -> dict:
    if not isinstance(raw, dict):
        raise PresetError("配置必须是 JSON 对象")

    name = str(raw.get("name") or "").strip()
    pid = _slugify(str(raw.get("id") or "")) or _slugify(name) or fallback_id or ""
    if not pid:
        pid = f"preset-{time.strftime('%Y%m%d-%H%M%S')}"
    name = name or pid

    sim_date = str(raw.get("sim_date") or "").strip()
    try:
        date.fromisoformat(sim_date)
    except ValueError:
        sim_date = date.today().isoformat()

    phase = str(raw.get("phase") or "").strip() or "（当前世界状态未指定）"
    canon = _as_list(raw.get("canon"))

    world_raw = raw.get("world") if isinstance(raw.get("world"), dict) else {}
    world = {
        "summary": str(world_raw.get("summary") or "").strip(),
        "rules": _as_list(world_raw.get("rules")),
    }

    se_raw = raw.get("search_editor") if isinstance(raw.get("search_editor"), dict) else {}
    search_editor = {
        key: str(se_raw.get(key) or _DEFAULTS["search_editor"][key]).strip()
        for key in ("relevance", "scope", "credibility", "add_guidance")
    }
    search_editor["max_results"] = _as_int(
        se_raw.get("max_results"), _DEFAULTS["search_editor"]["max_results"], 1, 50
    )
    search_editor["max_visible"] = _as_int(
        se_raw.get("max_visible"), _DEFAULTS["search_editor"]["max_visible"], 1, 10
    )
    search_editor["max_added"] = _as_int(
        se_raw.get("max_added"), _DEFAULTS["search_editor"]["max_added"], 0, 3
    )

    page_editor_raw = raw.get("page_editor") if isinstance(raw.get("page_editor"), dict) else {}
    page_generator_raw = (
        raw.get("page_generator") if isinstance(raw.get("page_generator"), dict) else {}
    )
    stage_raw = raw.get("stage") if isinstance(raw.get("stage"), dict) else {}

    return {
        "id": pid,
        "name": name,
        "subtitle": str(raw.get("subtitle") or "").strip(),
        "chat_agent": {"system_prompt": str((raw.get("chat_agent") if isinstance(raw.get("chat_agent"), dict) else {}).get("system_prompt") or "")},
        "sim_date": sim_date,
        "phase": phase,
        "canon": canon,
        "world": world,
        "search_editor": search_editor,
        "page_editor": {
            "guidance": str(
                page_editor_raw.get("guidance") or _DEFAULTS["page_editor"]["guidance"]
            ).strip()
        },
        "page_generator": {
            "guidance": str(
                page_generator_raw.get("guidance") or _DEFAULTS["page_generator"]["guidance"]
            ).strip()
        },
        "stage": {
            "guidance": str(stage_raw.get("guidance") or _DEFAULTS["stage"]["guidance"]).strip()
        },
    }


def default_state(config: dict) -> dict:
    return {
        "sim_date": config["sim_date"],
        "search_index": 0,
        "canon": list(config.get("canon") or []),
        "recent": [],
        "pages": {},
        "search_cache": {},
        "phase": config["phase"],
        "stages": [],
    }


def _load_json(raw: str | None) -> dict:
    if not raw:
        return {}
    try:
        obj = json.loads(raw)
        return obj if isinstance(obj, dict) else {}
    except json.JSONDecodeError:
        return {}


class PresetStore:
    def __init__(self, preset_dir: Path | str = PRESET_DIR, state_path: Path | str = STATE_PATH):
        self.preset_dir = Path(preset_dir)
        self.preset_dir.mkdir(parents=True, exist_ok=True)
        self.state_path = Path(state_path)
        self._doc = self._load_doc()

    # ---------- 状态文件 ----------
    def _load_doc(self) -> dict:
        if self.state_path.exists():
            try:
                doc = json.loads(self.state_path.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                doc = {}
            if isinstance(doc.get("presets"), dict):
                doc.setdefault("active_preset", DEFAULT_PRESET_ID)
                return doc
            # 兼容旧版单设定平铺状态：整体搬到 ufo-2026 下，不丢数据
            if any(k in doc for k in ("canon", "phase", "sim_date", "pages")):
                migrated = {
                    "active_preset": DEFAULT_PRESET_ID,
                    "presets": {DEFAULT_PRESET_ID: doc},
                }
                self._write_doc(migrated)
                return migrated
        return {"active_preset": DEFAULT_PRESET_ID, "presets": {}}

    def _write_doc(self, doc: dict) -> None:
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.state_path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, self.state_path)

    def save_state(self, preset_id: str, state: dict) -> None:
        doc = copy.deepcopy(self._doc)
        doc.setdefault("presets", {})[preset_id] = copy.deepcopy(state)
        self._write_doc(doc)
        self._doc = doc

    def set_active(self, preset_id: str) -> None:
        doc = copy.deepcopy(self._doc)
        doc["active_preset"] = preset_id
        self._write_doc(doc)
        self._doc = doc

    @property
    def active_id(self) -> str:
        pid = os.environ.get("MINICHAT_PRESET") or self._doc.get("active_preset") or DEFAULT_PRESET_ID
        return pid if self.exists(pid) else DEFAULT_PRESET_ID

    # ---------- 配置 ----------
    def exists(self, preset_id: str) -> bool:
        return self._path(preset_id).exists()

    def _path(self, preset_id: str) -> Path:
        return self.preset_dir / f"{preset_id}.json"

    def list_presets(self) -> list[dict]:
        out = []
        for path in sorted(self.preset_dir.glob("*.json")):
            try:
                cfg = normalize_config(json.loads(path.read_text(encoding="utf-8")), fallback_id=path.stem)
            except (json.JSONDecodeError, OSError, PresetError):
                continue
            out.append({"id": path.stem, "name": cfg["name"], "subtitle": cfg["subtitle"]})
        return out

    def get_config(self, preset_id: str) -> dict:
        path = self._path(preset_id)
        if not path.exists():
            raise PresetError(f"找不到设定：{preset_id}")
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as e:
            raise PresetError(f"读取设定失败：{preset_id}: {e}") from e
        cfg = normalize_config(raw, fallback_id=preset_id)
        cfg["id"] = preset_id
        return cfg

    def save_config(self, config: dict) -> Path:
        path = self._path(config["id"])
        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, path)
        return path

    def get_state(self, preset_id: str) -> dict:
        config = self.get_config(preset_id)
        state = self._doc.get("presets", {}).get(preset_id)
        if not isinstance(state, dict):
            state = default_state(config)
        for key, value in default_state(config).items():
            state.setdefault(key, value)
        return state
