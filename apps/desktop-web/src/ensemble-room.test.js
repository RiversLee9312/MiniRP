import assert from "node:assert/strict";
import test from "node:test";
import { applyEvents, initial, prependHistory } from "./workspace.js";

const scope = { preset: "preset", chat: "room" };
const scene = "kitchen";
const privateChat = "private-alice";
const speech = (id, content = id) => ({ id, role: "assistant", agent: "chat", content });
const room = (privateInStory = true) => ({
  kind: "loaded", preset: scope.preset, config: { chat_mode: "ensemble" },
  chat: { id: scope.chat, blocks: [] },
  ensemble_chats: [{ id: scene }, { id: "porch" }], private_in_story: privateInStory,
});
const opened = (id, fields = {}) => ({
  kind: "ensemble_chat_loaded", ...scope, conversation: id, blocks: [], ...fields,
});
const activity = (participant, conversation, running = true) => ({
  kind: "ensemble_activity", ...scope, conversation, participant, speaker: participant, running,
});
const speakers = (state, conversation) => Object.values(state.ensembleActivity[conversation] || {}).map((person) => person.speaker);

test("public scenes show posted words while excluding trace snapshots, live frames, and older trace rows", () => {
  const start = applyEvents(initial, [room(), opened(scene, {
    blocks: [speech("opening"), { id: "initial-tool", tool: "inspect" }, { ...speech("retained"), reasoning: "private" }],
    history: { ensemble: "before" },
  })]);
  const live = applyEvents(start, [
    { kind: "delta", ...scope, conversation: scene, block: "streamed", agent: "ChatAgent", text: "unposted" },
    { kind: "tool_end", ...scope, conversation: scene, block: "tool", agent: "ChatAgent", text: "diagnostics" },
    { kind: "ensemble_message", ...scope, conversation: scene, item: speech("said", "Hello there!") },
  ]);
  const next = prependHistory(live, "ensemble", live.history.ensemble.version, "before", {
    blocks: [{ id: "old-tool", tool: "read_notes" }, speech("older")], before: null,
  });
  assert.deepEqual(next.panes.ensemble, [speech("opening"), speech("retained"), speech("said", "Hello there!")]);
  assert.deepEqual(next.history.ensemble.pages, [[speech("older")]]);
  assert.equal(next.history.ensemble.before, null);
});

test("actor status follows the selected scene and clears when an actor finishes or opens a new room", () => {
  const first = applyEvents(initial, [
    room(), activity("Ari", scene), activity("Kai", "porch"),
    activity("Dana", privateChat),
    { ...activity("Unrelated", scene), chat: "other-room" },
  ]);
  assert.deepEqual(speakers(first, scene), ["Ari"]);
  assert.deepEqual(speakers(first, "porch"), ["Kai"]);
  const current = applyEvents(first, [
    opened(scene, { activity: [activity("Luz", scene)] }),
    activity("Ari", scene, false),
    activity("Kai", "porch", false),
  ]);
  assert.deepEqual(speakers(current, current.conversation), ["Luz"]);
  const idle = applyEvents(current, [activity("Luz", scene, false)]);
  assert.deepEqual(speakers(idle, idle.conversation), []);
  assert.deepEqual(applyEvents(first, [room()]).ensembleActivity, {});
});

test("a private route shows detailed blocks, preserves delivery mode, and returns to the public scene", () => {
  const start = applyEvents(initial, [room(false), opened(scene, { blocks: [speech("public")] })]);
  const direct = applyEvents(start, [
    opened(privateChat, { private_participant: "alice", private_in_story: false, blocks: [{ id: "tool", tool: "inspect" }] }),
    { kind: "delta", ...scope, conversation: privateChat, block: "thought", type: "reasoning", text: "Debugging" },
    { kind: "ensemble_message", ...scope, conversation: scene, item: speech("not-private") },
  ]);
  assert.equal(direct.privateParticipant, "alice");
  assert.equal(direct.privateInStory, false);
  assert.equal(direct.returnConversation, scene);
  assert.deepEqual(direct.panes.ensemble.map((block) => block.id), ["tool", "thought"]);
  const back = applyEvents(direct, [opened(direct.returnConversation, { blocks: [speech("public")] })]);
  assert.equal(back.privateParticipant, "");
  assert.deepEqual(back.panes.ensemble, [speech("public")]);
});
