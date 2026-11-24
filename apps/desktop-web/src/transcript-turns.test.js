import assert from "node:assert/strict";
import test from "node:test";
import { transcriptTurns } from "./transcript-turns.js";

test("the ruler groups a turn split across pages and gives each interjection its own anchor", () => {
  const older = [
    { id: "question", kind: "user", content: "The first question", version: "snapshot" },
    { id: "search", agent: "SearchAgent", content: "Search trace" },
    { id: "reasoning", reasoning: "An internal thought" },
    { id: "rough", content: "Starting reply content" },
  ];
  const latest = [
    { id: "next", kind: "user", content: "An explicit interjection", interjection: true },
    { id: "old-steer", kind: "user", content: "An older interjection" },
    { id: "tool", tool: "fetch", content: "Tool trace" },
    { id: "reply", content: "Last reply content" },
    { id: "finish", kind: "reply_end", first_block: "question", reply: "The completed first answer." },
    { id: "open", kind: "user", content: "New turn" },
    { id: "loading", content: "Streamed second answer", streaming: true },
  ];
  const turns = transcriptTurns([older], latest);
  assert.deepEqual(turns.map(({ id, kind, prompt, answer }) => [id, kind, prompt, answer]), [
    ["question", "user", "The first question", "The completed first answer."],
    ["next", "interjection", "An explicit interjection", undefined],
    ["old-steer", "interjection", "An older interjection", ""],
    ["open", "user", "New turn", "Streamed second answer"],
  ]);
});

test("an assistant-first turn or an attachment-only prompt has a real preview while trace-only blocks add no ticks", () => {
  const turns = transcriptTurns([], [
    { id: "thought", reasoning: "Internal reasoning" },
    { id: "greeting", open_ui: "Card() ", open_ui_title: "Opening card" },
    { id: "question", kind: "user", attachments: [{ name: "manual.txt" }] },
    { id: "response", content: "The answer" },
    { id: "another", kind: "user", content: "A regular Room message" },
    { id: "quotation", kind: "user", attachments: [{ kind: "quote", text: " An excerpt\n from a reply. " }] },
  ]);
  assert.deepEqual(turns.map(({ id, kind, prompt, answer, card }) => [id, kind, prompt, answer, card]), [
    ["greeting", "assistant", undefined, undefined, "Opening card"],
    ["question", "user", "manual.txt", "The answer", undefined],
    ["another", "user", "A regular Room message", undefined, undefined],
    ["quotation", "user", "An excerpt from a reply.", undefined, undefined],
  ]);
});
