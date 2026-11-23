export const initial = {
  ready: false, busy: false, generating: "", preset: "", chat: "", conversation: "",
  roomLoading: null,
  config: {}, title: "", presets: [], chats: [], ensemble: null, participants: [],
  templates: [], plugins: [], pluginSettings: [], interactions: {}, draft: null,
  opened: 0, panes: { chat: [], env: [], preset: [], ensemble: [] },
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

function upsert(state, pane, id, change) {
  if (!id) return state;
  const blocks = state.panes[pane];
  const index = blocks.findIndex((block) => block.id === id);
  const next = change(index < 0 ? { id } : blocks[index]);
  if (next === blocks[index]) return state;
  const items = blocks.slice();
  if (index < 0) items.push(next);
  else items[index] = next;
  return { ...state, panes: { ...state.panes, [pane]: items } };
}

function updateTrace(state, event) {
  if (event.preset && event.preset !== state.preset) return state;
  if (event.conversation && (event.conversation !== state.conversation || event.chat !== state.chat)) return state;
  const pane = event.conversation ? "ensemble" : paneFor(event.agent);
  const id = event.block || event.id;
  return upsert(state, pane, id, (block) => {
    if (event.plugin_sequence && block.plugin_sequence >= event.plugin_sequence) return block;
    const next = { ...block, agent: event.agent ?? block.agent, speaker: event.speaker ?? block.speaker };
    for (const key of ["plugin_agent", "plugin_run", "plugin_agent_name", "plugin_sequence"])
      if (event[key] !== undefined) next[key] = event[key];
    switch (event.kind) {
      case "user":
      case "system":
        return { ...next, kind: event.kind, content: event.text, version: event.version, attachments: event.attachments };
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
        return { ...next, tool: event.name, arguments: event.arguments, running: true };
      case "tool_end":
        return { ...next, tool: event.name, result: event.text, running: false };
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
  });
}

export function applyEvent(state, event) {
  switch (event.kind) {
    case "ready":
      return { ...state, ready: true, preset: event.preset, config: event.config, templates: event.catalog || [] };
    case "presets":
      return { ...state, presets: event.items || [] };
    case "chats":
      return event.preset === state.preset ? { ...state, chats: event.items || [] } : state;
    case "home":
      return { ...state, chats: state.preset === event.preset ? state.chats : [], preset: event.preset,
        config: event.config, chat: "", title: "", conversation: "", ensemble: null, participants: [], panes: split(), draft: null };
    case "loaded":
      return { ...state, preset: event.preset, config: event.config, chat: event.chat.id, title: event.chat.name,
        conversation: "", ensemble: event.ensemble_chats, participants: event.participants || [],
        panes: split(event.chat.blocks), draft: event.draft || null };
    case "preset_opened": {
      const selected = event.selected || event.created;
      const current = selected ? { ...state, chat: "", conversation: "", ensemble: null, participants: [], title: "", draft: null,
        panes: split(), chats: state.preset === event.preset ? state.chats : [] } : state;
      return { ...current, preset: event.preset, config: event.config || state.config, opened: state.opened + 1,
        panes: { ...current.panes, preset: event.blocks || [] } };
    }
    case "preset_config":
      return { ...state, config: event.config };
    case "title":
      return { ...state, title: event.id === state.chat ? event.name : state.title,
        chats: state.chats.map((chat) => chat.id === event.id ? { ...chat, name: event.name } : chat) };
    case "ensemble_chats":
      return event.chat === state.chat ? { ...state, ensemble: event.items || [] } : state;
    case "ensemble_chat_loaded":
      return event.chat === state.chat ? { ...state, conversation: event.conversation,
        panes: { ...state.panes, ensemble: event.blocks || [] } } : state;
    case "steer_queued":
    case "ensemble_message": {
      if (event.chat !== state.chat || (event.preset && event.preset !== state.preset) ||
        (event.conversation && event.conversation !== state.conversation)) return state;
      const item = event.item;
      return upsert(state, event.conversation ? "ensemble" : paneFor(item.agent), item.id, () => item);
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
    case "user": case "system": case "delta": case "tool_start": case "tool_end": case "context_trace":
    case "open_ui_delta": case "open_ui_end": case "open_ui_card": case "open_ui_card_update":
    case "plugin_agent_status": case "reply_end": case "turn_end":
      return updateTrace(state, event);
    default:
      return state;
  }
}
