import assert from "node:assert/strict";
import test from "node:test";
import { applyEvents, initial, prependHistory } from "./workspace.js";

const block = (id, agent = "ChatAgent") => ({ id, agent, content: id });
const loaded = (id, blocks) => ({ kind: "loaded", preset: "fixture-preset", config: {}, chat: { id, blocks },
  history: { chat: "before-chat", env: "before-env", preset: null } });

test("older pages retain chronological order, reject stale scope and overlap, and keep live blocks separate", () => {
  const first = applyEvents(initial, [loaded("room", [block("last"), block("env", "EnvAgent")])]);
  const version = first.history.chat.version;
  assert.equal(first.history.env.before, "before-env");
  const middle = prependHistory(first, "chat", version, "before-chat", {
    blocks: [block("middle"), block("last"), block("middle")], before: "before-middle",
  });
  const complete = prependHistory(middle, "chat", version, "before-middle", { blocks: [block("first")], before: null });
  assert.deepEqual(complete.history.chat.pages.map((items) => items.map((item) => item.id)), [["first"], ["middle"]]);
  assert.equal(complete.history.chat.before, null);
  assert.equal(complete.panes.chat, first.panes.chat);
  assert.equal(complete.panes.env, first.panes.env);
  assert.equal(prependHistory(complete, "chat", version, "before-middle", { blocks: [], before: null }), complete);

  const switched = applyEvents(middle, [loaded("other-room", [block("other")])]);
  assert.equal(prependHistory(switched, "chat", version, "before-chat", { blocks: [block("stale")], before: null }), switched);
  assert.deepEqual(switched.history.chat.pages, []);
});

test("one streamed batch retains untouched items and updates loaded historical cards without duplicating live rows", () => {
  const first = applyEvents(initial, [loaded("room", [block("open"), block("live")])]);
  const history = prependHistory(first, "chat", first.history.chat.version, "before-chat", {
    blocks: [{ ...block("card"), open_ui: "fixture", open_ui_state: { value: "before" } }], before: null,
  });
  const second = applyEvents(history, [
    { kind: "delta", block: "live", text: " a", agent: "ChatAgent" },
    { kind: "delta", block: "live", text: " b", agent: "ChatAgent" },
    { kind: "delta", block: "new", text: "new", agent: "ChatAgent" },
    { kind: "delta", block: "new", text: " reply", agent: "ChatAgent" },
    { kind: "open_ui_card_update", block: "card", open_ui_state: { value: "updated" }, agent: "ChatAgent" },
    { kind: "open_ui_card_update", block: "unloaded", open_ui_state: { value: "unseen" }, agent: "ChatAgent" },
  ]);
  assert.deepEqual(second.panes.chat.map((item) => item.content), ["open", "live a b", "new reply"]);
  assert.equal(second.panes.chat[0], history.panes.chat[0]);
  assert.equal(history.panes.chat[1].content, "live");
  assert.deepEqual(second.history.chat.pages[0][0].open_ui_state, { value: "updated" });
  assert.deepEqual(history.history.chat.pages[0][0].open_ui_state, { value: "before" });
});

test("tool titles and cards refresh only known scoped rows while stable tool data stays intact", () => {
  const first = applyEvents(initial, [loaded("room", [])]);
  const history = prependHistory(first, "chat", first.history.chat.version, "before-chat", {
    blocks: [{ id: "tool", agent: "ChatAgent", tool: "set_value", arguments: '{"path":"name"}', result: "old",
      tool_title: "Edit preset field", skill_title: "Settings", plugin_sequence: 7 }], before: null,
  });
  const updated = applyEvents(history, [
    { kind: "tool_start", block: "live", name: "configure_date", tool_title: "Configure date", skill_title: "Calendar",
      arguments: "{}", agent: "ChatAgent" },
    { kind: "tool_end", block: "live", name: "configure_date", text: "{\"ok\":true}", agent: "ChatAgent" },
    { kind: "tool_trace_update", block: "tool", chat: "room", preset: "fixture-preset", agent: "ChatAgent", plugin_sequence: 7,
      tool_title: "Modifier le champ", skill_title: "Paramètres", tool_arguments_ui: { title: "Champ" }, tool_result_ui: null },
    { kind: "tool_trace_update", block: "tool", chat: "wrong", preset: "fixture-preset", agent: "ChatAgent", tool_title: "Wrong room" },
    { kind: "tool_trace_update", block: "unknown", chat: "room", preset: "fixture-preset", agent: "ChatAgent", tool_title: "Unknown" },
  ]);
  assert.deepEqual(updated.history.chat.pages[0][0], {
    id: "tool", agent: "ChatAgent", tool: "set_value", arguments: '{"path":"name"}', result: "old",
    tool_title: "Modifier le champ", skill_title: "Paramètres", plugin_sequence: 7,
    tool_arguments_ui: { title: "Champ" }, tool_result_ui: null,
  });
  assert.equal(updated.panes.chat.length, 1);
  assert.equal(updated.panes.chat[0].tool_title, "Configure date");
  assert.equal(updated.panes.chat[0].skill_title, "Calendar");
  assert.equal(updated.panes.chat[0].result, '{"ok":true}');
});

test("a queued interjection retains its marker when the native user block replaces the preview", () => {
  const first = applyEvents(initial, [loaded("room", [])]);
  const preview = applyEvents(first, [{ kind: "steer_queued", chat: "room", preset: "fixture-preset",
    item: { id: "steer", kind: "user", agent: "chat", content: "One more detail" } }]);
  const confirmed = applyEvents(preview, [{ kind: "user", block: "steer", text: "One more detail", agent: "chat" }]);
  assert.equal(confirmed.panes.chat.length, 1);
  assert.equal(confirmed.panes.chat[0].interjection, true);
});
