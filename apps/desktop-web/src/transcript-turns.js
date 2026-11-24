const pageItems = new WeakMap();
const positionKind = (block) => block.kind || block.role;

function turnItems(blocks) {
  if (!pageItems.has(blocks)) pageItems.set(blocks, blocks.filter((block) => {
    const kind = positionKind(block);
    if (kind === "reply_end") return true;
    if (kind === "user" || kind === "system") return Boolean(block.content || block.attachments?.length);
    return !["SearchAgent", "FetchAgent", "search", "fetch"].includes(block.agent) &&
      !block.tool && !block.compaction && !block.plugin_status && (block.content?.trim() || block.open_ui);
  }));
  return pageItems.get(blocks);
}

function excerpt(text) {
  return text?.trim().slice(0, 320).replace(/\s+/g, " ") || "";
}

export function transcriptTurns(pages, recent) {
  const turns = [];
  const starts = new Map();
  let active;
  let firstOpen = 0;
  for (const blocks of [...(pages || []), recent]) {
    for (const block of turnItems(blocks)) {
      const kind = positionKind(block);
      if (kind === "reply_end") {
        const start = starts.get(block.first_block);
        const turn = start !== undefined ? turns[start] : active;
        if (turn && turn.kind !== "interjection") turn.answer = excerpt(block.reply) || turn.answer;
        // Older records can identify a steer when a reply checkpoint brackets
        // the user message and it has no independent user checkpoint.
        if (start !== undefined && start >= firstOpen) {
          for (let index = start + 1; index < turns.length; index++) {
            const candidate = turns[index];
            if (candidate.kind === "user" && !candidate.checkpoint) {
              candidate.kind = "interjection";
              candidate.answer = "";
              candidate.card = undefined;
            }
          }
        }
        firstOpen = turns.length;
        active = undefined;
        continue;
      }
      if (kind === "user" || kind === "system") {
        const turn = {
          id: block.id, kind: block.interjection === true ? "interjection" : kind,
          checkpoint: Boolean(block.version),
          prompt: excerpt(block.content) || block.attachments?.[0]?.name || "",
        };
        starts.set(turn.id, turns.length);
        turns.push(turn);
        if (turn.kind !== "interjection") active = turn;
        continue;
      }
      if (!active) {
        active = { id: block.id, kind: "assistant" };
        starts.set(active.id, turns.length);
        turns.push(active);
      }
      if (!active.answer && block.content?.trim()) active.answer = excerpt(block.content);
      if (!active.card && block.open_ui) active.card = block.open_ui_title || true;
    }
  }
  return turns;
}
