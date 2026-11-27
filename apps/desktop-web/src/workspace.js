import { subagentKind } from "./subagent-traces.js";

export const initial = {
  ready: false, busy: false, generating: "", preset: "", chat: "", conversation: "",
  roomLoading: null,
  config: {}, title: "", presets: [], chats: [], ensemble: null, participants: [],
  privateParticipant: "", privateInStory: true, returnConversation: "", ensembleActivity: {},
  templates: [], plugins: [], pluginSettings: [], interactions: {}, draft: null,
  opened: 0, panes: { chat: [], env: [], preset: [], ensemble: [] },
  history: {}, historyVersion: 0,
};

export function paneFor(agent) {
  if (agent === "env" || agent === "EnvAgent") return "env";
  if (["preset", "PresetAgent", "plugin", "PluginAgent"].includes(agent)) return "preset";
  return "chat";
}

function split(blocks) {
  const panes = { chat: [], env: [], preset: [], ensemble: [] };
  for (const block of blocks || []) panes[paneFor(block.agent)].push(block);
  return panes;
}

const positions = new WeakMap();

const publicMessage = (block) => ["user", "system", "assistant"].includes(block.kind || block.role) &&
  !block.tool && !block.compaction && !subagentKind(block);

function publicMessages(blocks) {
  return blocks.filter(publicMessage).map(withoutTrace);
}

function withoutTrace(block) {
  if (!block.reasoning && !block.running && !block.streaming) return block;
  const { reasoning, running, streaming, ...message } = block;
  return message;
}

function updateEnsembleActivity(activity, event) {
  const { conversation, participant, speaker, running } = event;
  if (!conversation || !participant) return activity;
  const prior = activity[conversation] || {};
  if (running ? Object.hasOwn(prior, participant) && prior[participant].speaker === speaker : !Object.hasOwn(prior, participant))
    return activity;
  const next = { ...activity };
  const { [participant]: _, ...remaining } = prior;
  if (running) next[conversation] = { ...remaining, [participant]: { participant, speaker } };
  else if (Object.keys(remaining).length) next[conversation] = remaining;
  else delete next[conversation];
  return next;
}

function snapshotEnsembleActivity(state, event) {
  if (!Array.isArray(event.activity)) return state.ensembleActivity;
  const next = { ...state.ensembleActivity };
  if (event.conversation) delete next[event.conversation];
  return event.activity.reduce(updateEnsembleActivity, next);
}

function blockPositions(blocks) {
  if (!positions.has(blocks)) positions.set(blocks, new Map(blocks.map((block, index) => [block.id, index])));
  return positions.get(blocks);
}

function snapshotHistory(state, event, panes, reset = false) {
  const historyVersion = state.historyVersion + 1;
  const history = reset ? {} : { ...state.history };
  for (const pane of panes) history[pane] = { version: historyVersion, before: event.history?.[pane] || null,
    subagents: event.history?._subagents?.[pane] || [], ruler: event.history?._ruler?.[pane], pages: [] };
  return { history, historyVersion };
}

export function prependHistory(state, pane, version, requestedBefore, response) {
  const current = state.history[pane];
  if (!current || current.version !== version || current.before !== requestedBefore) return state;
  const existing = [...current.pages, state.panes[pane]].map(blockPositions);
  const received = new Set();
  const rows = pane === "ensemble" && !state.privateParticipant ? publicMessages(response.blocks) : response.blocks;
  const blocks = rows.filter((block) => {
    if (!block?.id || received.has(block.id) || existing.some((index) => index.has(block.id))) return false;
    received.add(block.id);
    return true;
  });
  const pages = blocks.length ? [blocks, ...current.pages] : current.pages;
  return { ...state, history: { ...state.history, [pane]: { ...current, pages, before: response.before } } };
}

function upsert(state, pane, id, change, editing, existingOnly = false) {
  if (!id) return state;
  const history = state.history[pane];
  const tail = state.panes[pane];
  const page = blockPositions(tail).has(id) ? -1 : (history?.pages.findIndex((blocks) => blockPositions(blocks).has(id)) ?? -1);
  const blocks = page < 0 ? tail : history.pages[page];
  const index = blockPositions(blocks).get(id) ?? -1;
  if (existingOnly && index < 0) return state;
  const next = change(index < 0 ? { id } : blocks[index]);
  if (next === blocks[index]) return state;
  const writable = editing.has(blocks);
  const items = writable ? blocks : blocks.slice();
  if (index < 0) {
    if (writable) blockPositions(items).set(id, items.length);
    items.push(next);
  } else items[index] = next;
  if (writable) return state;
  editing.add(items);
  if (page < 0) return { ...state, panes: { ...state.panes, [pane]: items } };
  const pages = history.pages.slice();
  pages[page] = items;
  return { ...state, history: { ...state.history, [pane]: { ...history, pages } } };
}

const toolMetadata = ["tool_title", "skill_title", "tool_arguments_ui", "tool_result_ui"];

function updateTrace(state, event, editing) {
  if (event.preset && event.preset !== state.preset) return state;
  if (event.conversation && (event.conversation !== state.conversation || event.chat !== state.chat)) return state;
  const pane = event.conversation ? "ensemble" : paneFor(event.agent);
  if (pane === "ensemble" && !state.privateParticipant && event.kind !== "open_ui_card_update") return state;
  const refresh = event.kind === "tool_trace_update";
  if (refresh && pane !== "preset" && event.chat && event.chat !== state.chat) return state;
  const id = event.block || event.id;
  return upsert(state, pane, id, (block) => {
    if (refresh) {
      // A metadata refresh can repeat the recorded tool event's sequence number.
      const changed = toolMetadata.filter((key) => Object.hasOwn(event, key) && block[key] !== event[key]);
      return changed.length ? { ...block, ...Object.fromEntries(changed.map((key) => [key, event[key]])) } : block;
    }
    if (event.plugin_sequence && block.plugin_sequence >= event.plugin_sequence) return block;
    const next = { ...block, agent: event.agent ?? block.agent, speaker: event.speaker ?? block.speaker };
    for (const key of ["plugin_agent", "plugin_run", "plugin_agent_name", "plugin_sequence", "parent_agent"])
      if (event[key] !== undefined) next[key] = event[key];
    switch (event.kind) {
      case "user":
      case "system":
        return { ...next, kind: event.kind, content: event.text, version: event.version, attachments: event.attachments,
          interjection: event.interjection ?? block.interjection };
      case "delta": {
        const key = event.type === "reasoning" ? "reasoning" : "content";
        return { ...next, [key]: (block[key] || "") + event.text, streaming: true };
      }
      case "open_ui_delta":
        return { ...next, open_ui: (block.open_ui || "") + event.text, streaming: true };
      case "open_ui_card":
      case "open_ui_card_update":
        return { ...next, open_ui: event.open_ui || block.open_ui, open_ui_state: event.open_ui_state,
          open_ui_revision: event.open_ui_revision, open_ui_title: event.open_ui_title,
          open_ui_host_action: event.open_ui_host_action || block.open_ui_host_action };
      case "tool_start":
      case "tool_end":
        for (const key of toolMetadata) if (Object.hasOwn(event, key)) next[key] = event[key];
        return event.kind === "tool_start"
          ? { ...next, tool: event.name, arguments: event.arguments, running: true }
          : { ...next, tool: event.name, result: event.text, running: false };
      case "context_trace":
        return { ...next, compaction: true, result: event.text, running: event.running };
      case "plugin_agent_status":
        return { ...next, plugin_status: event.status, content: event.text };
      case "reply_end":
        return { ...next, ...event, id };
      case "open_ui_end":
      case "turn_end":
        return { ...next, streaming: false, running: false, version: event.version || block.version,
          asset_links: event.asset_links || block.asset_links };
      default:
        return block;
    }
  }, editing, refresh || event.kind === "open_ui_card_update");
}

export function applyEvents(state, events) {
  const editing = new WeakSet();
  for (const event of events) state = applyEvent(state, event, editing);
  return state;
}

function applyEvent(state, event, editing) {
  switch (event.kind) {
    case "ready":
      return { ...state, ready: true, preset: event.preset, config: event.config, templates: event.catalog || [] };
    case "presets":
      return { ...state, presets: event.items || [] };
    case "chats":
      return event.preset === state.preset ? { ...state, chats: event.items || [] } : state;
    case "home":
      return { ...state, chats: state.preset === event.preset ? state.chats : [], preset: event.preset,
        config: event.config, chat: "", title: "", conversation: "", ensemble: null, participants: [],
        privateParticipant: "", privateInStory: true, returnConversation: "", ensembleActivity: {},
        panes: split(), history: {}, draft: null, presetDraft: null };
    case "loaded":
      return { ...state, preset: event.preset, config: event.config, chat: event.chat.id, title: event.chat.name,
        conversation: "", ensemble: event.ensemble_chats, participants: event.participants || [],
        privateParticipant: "", privateInStory: event.private_in_story !== false, returnConversation: "",
        ensembleActivity: (event.activity || []).reduce(updateEnsembleActivity, {}),
        panes: split(event.chat.blocks), ...snapshotHistory(state, event, ["chat", "env", "preset"], true),
        draft: event.draft || null, presetDraft: null };
    case "preset_opened": {
      const selected = event.selected || event.created;
      const current = selected ? { ...state, chat: "", conversation: "", ensemble: null, participants: [], title: "", draft: null,
        privateParticipant: "", privateInStory: true, returnConversation: "", ensembleActivity: {},
        panes: split(), chats: state.preset === event.preset ? state.chats : [] } : state;
      return { ...current, preset: event.preset, config: event.config || state.config, opened: state.opened + 1,
        presetDraft: event.draft || null,
        panes: { ...current.panes, preset: event.blocks || [] }, ...snapshotHistory(state, event, ["preset"], selected) };
    }
    case "preset_config":
      return { ...state, config: event.config };
    case "title":
      return { ...state, title: event.id === state.chat ? event.name : state.title,
        chats: state.chats.map((chat) => chat.id === event.id ? { ...chat, name: event.name } : chat) };
    case "ensemble_chats": {
      if (event.chat !== state.chat || event.preset && event.preset !== state.preset) return state;
      if (!event.removed) return { ...state, ensemble: event.items || [] };
      const { [event.removed]: _, ...ensembleActivity } = state.ensembleActivity;
      return { ...state, ensemble: event.items || [], ensembleActivity,
        returnConversation: state.returnConversation === event.removed ? "" : state.returnConversation };
    }
    case "ensemble_chat_loaded": {
      if (event.chat !== state.chat || event.preset && event.preset !== state.preset) return state;
      const privateParticipant = event.private_participant || "";
      return { ...state, conversation: event.conversation, privateParticipant,
        returnConversation: privateParticipant ? (state.privateParticipant ? state.returnConversation : state.conversation) : "",
        privateInStory: event.private_in_story ?? state.privateInStory,
        ensembleActivity: snapshotEnsembleActivity(state, event),
        panes: { ...state.panes, ensemble: privateParticipant ? event.blocks || [] : publicMessages(event.blocks || []) },
        ...snapshotHistory(state, event, ["ensemble"]) };
    }
    case "ensemble_activity": {
      if (event.chat !== state.chat || event.preset && event.preset !== state.preset || state.ensemble == null) return state;
      const ensembleActivity = updateEnsembleActivity(state.ensembleActivity, event);
      return ensembleActivity === state.ensembleActivity ? state : { ...state, ensembleActivity };
    }
    case "steer_queued":
    case "ensemble_message": {
      if (event.chat !== state.chat || (event.preset && event.preset !== state.preset) ||
        (event.conversation && event.conversation !== state.conversation)) return state;
      const item = event.kind === "ensemble_message" && !state.privateParticipant ? withoutTrace(event.item) : event.item;
      if (event.kind === "ensemble_message" && !state.privateParticipant && !publicMessage(item)) return state;
      return upsert(state, event.conversation ? "ensemble" : paneFor(item.agent), item.id, () =>
        event.kind === "steer_queued" && item.kind === "user" && item.interjection === undefined
          ? { ...item, interjection: true } : item, editing);
    }
    case "busy":
      return { ...state, busy: event.value };
    case "room_loading":
      return { ...state, roomLoading: event.value ? event : null };
    case "generating":
      return { ...state, generating: event.value ? paneFor(event.agent) : "" };
    case "plugins":
      return { ...state, plugins: event.items || [] };
    case "plugin_settings":
      return { ...state, pluginSettings: event.items || [] };
    case "web_picker":
    case "web_preview":
    case "web_browser_setup":
      return { ...state, interactions: { ...state.interactions, [event.id]: event } };
    case "web_interaction_closed": {
      const { [event.id]: _, ...interactions } = state.interactions;
      return { ...state, interactions };
    }
    case "user": case "system": case "delta": case "tool_start": case "tool_end": case "tool_trace_update": case "context_trace":
    case "open_ui_delta": case "open_ui_end": case "open_ui_card": case "open_ui_card_update":
    case "plugin_agent_status": case "reply_end": case "turn_end":
      return updateTrace(state, event, editing);
    default:
      return state;
  }
}
