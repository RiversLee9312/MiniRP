import { researchAgentLabel } from "./transcript-turns.js";

export const subagentIcon = "smart_toy";

export const researchSubagents = [
  { id: "search", label: "search_agent", icon: "search" },
  { id: "browser", label: "fetch_agent", icon: "language" },
];

export function subagentKind(block) {
  const research = researchAgentLabel(block.agent);
  if (research === "search_agent") return "search";
  if (research === "fetch_agent" ||
      ["env", "EnvAgent", "preset", "PresetAgent", "plugin", "PluginAgent"].includes(block.agent) &&
      ["BrowserAgent", "FetchAgent"].includes(block.speaker)) return "browser";
  if (block.plugin_run || block.plugin_agent || ["plugin", "PluginAgent"].includes(block.agent)) return "plugin";
  return "";
}

function subagentKey(block) {
  const kind = subagentKind(block);
  if (kind !== "browser") return kind;
  if (block.plugin_run || block.plugin_agent) return "plugin/browser";
  return ["search", "SearchAgent"].includes(block.parent_agent) ? "search/browser" : kind;
}

export function subagentSummary(blocks, history) {
  const keys = new Set(history?.subagents);
  for (const page of [...(history?.pages || []), blocks]) {
    for (const block of page) {
      const kind = subagentKey(block);
      if (block.plugin_agent || block.plugin_run) keys.add("plugin:" + (block.plugin_agent || block.plugin_run));
      else if (kind) keys.add(kind);
    }
  }
  const search = keys.has("search") || keys.has("search/browser");
  const browser = keys.has("browser");
  const plugins = Array.from(keys).filter((key) => key.startsWith("plugin:")).length;
  return { search, browser, searchBrowser: keys.has("search/browser"), count: Number(search) + Number(browser) + plugins };
}

const projected = new WeakMap();

// Keep the underlying transcript and its cursors intact. Both views can load the same older pages.
export function traceRows(blocks, kind) {
  if (!projected.has(blocks)) projected.set(blocks, new Map());
  const views = projected.get(blocks);
  if (!views.has(kind)) views.set(kind, blocks.filter((block) => subagentKey(block) === kind));
  return views.get(kind);
}

export function traceHistory(history, kind) {
  return history && { ...history, pages: history.pages.map((blocks) => traceRows(blocks, kind)),
    ruler: history.ruler && traceRows(history.ruler, kind) };
}
