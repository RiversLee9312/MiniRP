"""Agent 共用的世界状态；持久化仍由 PresetStore 提供。"""
from __future__ import annotations

import copy
import difflib
import json
from typing import Callable

from deepseek_client import COMPACT_AT, DeepSeekError, estimate_tokens
from agent_runtime import stream_chat
from presets import default_state

_MERGE_SYSTEM = """将已确立事实合并压缩成不超过 40 条。保留具体时间、机构、人名、数字、地点与因果关系。
删除重复内容，不得新增事实或改变含义。只输出 JSON：{"facts":["..."]}。"""


class WorldState:
    def __init__(self, config: dict, state: dict, on_save: Callable[[dict], None] | None = None):
        self.config = config
        self.state = copy.deepcopy(state)
        for key, value in default_state(config).items():
            self.state.setdefault(key, value)
        self._on_save = on_save
        self.updates: list[str] = []
        self._tracked = self.snapshot()

    @property
    def sim_date(self) -> str:
        return self.state["sim_date"]

    @property
    def canon(self) -> list[str]:
        return self.state["canon"]

    @property
    def phase(self) -> str:
        return self.state["phase"]

    def snapshot(self) -> dict:
        return {key: copy.deepcopy(self.state[key]) for key in ("sim_date", "phase", "canon")}

    def commit(self, state: dict) -> None:
        # 先完成磁盘写入，失败时所有 Agent 仍看到原状态。
        if self._on_save:
            self._on_save(copy.deepcopy(state))
        self.state = copy.deepcopy(state)
        self.track_updates()

    def track_updates(self) -> None:
        current = self.snapshot()
        changes = []
        if current["sim_date"] != self._tracked["sim_date"]:
            changes.append("日期：" + current["sim_date"])
        if current["phase"] != self._tracked["phase"]:
            changes.append("世界状态：" + current["phase"])
        if current["canon"] != self._tracked["canon"]:
            diffs = []
            for tag, i, j, k, l in difflib.SequenceMatcher(
                a=self._tracked["canon"], b=current["canon"], autojunk=False
            ).get_opcodes():
                if tag != "equal":
                    diffs.append({"操作": tag, "旧事实": self._tracked["canon"][i:j], "新事实": current["canon"][k:l]})
            changes.append("canon 变更：" + json.dumps(diffs, ensure_ascii=False))
        if changes:
            self.updates.append("\n".join(changes))
        self._tracked = current

    def update_config(self, config: dict) -> None:
        self.config = config
        self.updates.append("预设配置已更新（当前 phase/canon 仍以世界状态为准）：\n"
                            + json.dumps(config, ensure_ascii=False))

    def reset(self) -> None:
        self.commit(default_state(self.config))
        self.updates.append("世界已重置；此前搜索及网页缓存已清空，不得从旧工作历史恢复已清除的缓存。")

    def world_block(self) -> str:
        return (
            f"【当前虚构日期】{self.sim_date}\n"
            f"【世界设定摘要】{self.config['world']['summary']}\n"
            f"【当前世界状态（世界状态锁）】\n{self.phase}\n"
            f"【世界规则】\n" + "\n".join(self.config["world"]["rules"])
            + "\n【已确立事实】\n" + "\n".join(f"{i}. {fact}" for i, fact in enumerate(self.canon, 1))
        )

    def recent_text(self) -> str:
        return "\n".join(
            f"- {r.get('date', self.sim_date)} 查询「{r.get('query', '')}」→ "
            + "；".join(r.get("titles", [])[:2]) for r in self.state["recent"]
        ) or "（暂无检索记录）"

    @staticmethod
    def absorb_facts(state: dict, facts: list[str]) -> None:
        for fact in facts:
            fact = " ".join(fact.split())
            if 8 <= len(fact) <= 500 and not any(
                fact in old or old in fact or difflib.SequenceMatcher(None, fact, old).ratio() > 0.85
                for old in state["canon"]
            ):
                state["canon"].append(fact)

    def compact(self, state: dict) -> None:
        if len(state["canon"]) <= 120 and estimate_tokens(json.dumps(state["canon"], ensure_ascii=False)) < COMPACT_AT:
            return
        try:
            msg = stream_chat([
                {"role": "system", "content": _MERGE_SYSTEM},
                {"role": "user", "content": json.dumps(state["canon"], ensure_ascii=False)},
            ], agent="CanonSummary", content_visible=False, temperature=0.2, response_format={"type": "json_object"})
            facts = json.loads(msg.get("content") or "{}").get("facts")
            if isinstance(facts, list) and facts and all(isinstance(f, str) and f.strip() for f in facts):
                state["canon"] = facts[:50]
        except (DeepSeekError, ValueError, AttributeError):
            pass
