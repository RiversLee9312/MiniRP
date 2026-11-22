import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import Markdown from "react-markdown";
import { assetUrl, download, request, send, upload } from "./api.js";
import { Confirm, FileChoice, Icon, IconButton, Modal, SettingToggle } from "./ui.jsx";
import "./workspace-tools.css";

function ToolPane({ title, children }) {
  const pane = useRef(null);
  useLayoutEffect(() => {
    pane.current.parentElement.scrollTop = 0;
    pane.current.focus({ preventScroll: true });
  }, []);
  return <section ref={pane} className="workspace-tool-pane" tabIndex={-1} aria-label={title}>{children}</section>;
}

function ToolEntry({ icon, title, detail, navigate, onClick, disabled, route }) {
  return <button type="button" className="settings-row settings-link" data-route={route} disabled={disabled} onClick={onClick}>
    {icon && <Icon name={icon} />}
    <span className="setting-label">
      <span dir="auto">{title}</span>
      {detail && <small dir="auto">{detail}</small>}
    </span>
    {navigate && <span className="directional"><Icon name="chevron_right" /></span>}
  </button>;
}

function CopyValue({ value, t, run }) {
  const [copied, setCopied] = useState(false);
  return <span className="workspace-copy-value">
    <IconButton icon="content_copy" label={t(copied ? "copied" : "copy")} onClick={() => run(async () => {
      await navigator.clipboard.writeText(value);
      setCopied(true);
    })} />
    <span className="sr-only" role="status">{copied ? t("copied") : ""}</span>
  </span>;
}

export function settingTitle(item, locale, t) {
  const language = locale.split("-")[0];
  const localized = item.label && t(item.label);
  if (localized && localized !== item.label) return localized;
  return item.titles?.[locale] || item.titles?.[language] || item.title || item.key || item.value;
}

export function PluginMarkdown({ item, preset }) {
  const [loaded, setLoaded] = useState(null);
  const source = item.assetUrl ? assetUrl(item.assetUrl, { preset }) : null;
  useEffect(() => {
    if (!source) return;
    const abort = new AbortController();
    fetch(source, { signal: abort.signal })
      .then((response) => response.ok ? response.text() : "")
      .then((content) => setLoaded({ source, text: content.slice(0, 262144) }))
      .catch(() => {});
    return () => abort.abort();
  }, [source]);
  const text = (loaded?.source === source ? loaded.text : "") || item.markdown;
  return text && <div className="markdown workspace-plugin-markdown">
    <Markdown skipHtml components={{
      a: ({ node: _, ...link }) => <a {...link} target="_blank" rel="noreferrer" />,
    }}>{text}</Markdown>
  </div>;
}

function PluginTextSetting({ title, item, current, disabled, save, t }) {
  const original = String(current ?? "");
  const [text, setText] = useState(original);
  const Input = item.multiline ? "textarea" : "input";
  return <form className="workspace-plugin-text" onSubmit={(event) => {
    event.preventDefault();
    save(text);
  }}>
    <label>{title}<Input value={text} disabled={disabled} maxLength={16000} rows={item.multiline ? 4 : undefined}
      onChange={(event) => setText(event.target.value)} /></label>
    <button type="submit" className="tonal" disabled={disabled || text === original}>{t("save")}</button>
  </form>;
}

const hostActionPrefix = "minirp://actions/minirp.app/hooks/";

export function PluginSetting({ item, settings }) {
  const { t, locale, preset, openPage, work, disabled, bindings, hostActions, update } = settings;
  const title = settingTitle(item, locale, t);
  const binding = item.binding && bindings?.[item.binding];
  if (item.binding && !binding) return null;
  const current = binding?.value ?? item.value ?? item.default;
  const keys = { id: item.plugin, key: item.key };
  const save = binding ? binding.set : (value) => update(keys, value);
  switch (item.type) {
    case "group":
      return <fieldset className="workspace-plugin-group"><legend>{title}</legend>
        <div className="setting-group">
          {item.items?.map((child) => <PluginSetting key={child.key} item={child} settings={settings} />)}
        </div>
      </fieldset>;
    case "page":
      return <ToolEntry title={title} icon={item.icon} route={item.key} navigate disabled={disabled} onClick={() => openPage(keys)} />;
    case "link": {
      if (item.plugin === "base" && item.url?.startsWith(hostActionPrefix)) {
        const action = item.url.slice(hostActionPrefix.length);
        const open = Object.hasOwn(hostActions || {}, action) ? hostActions[action] : null;
        return open && <ToolEntry title={title} icon={item.icon} route={item.key} navigate disabled={disabled} onClick={() => open(item)} />;
      }
      return <ToolEntry title={title} icon="arrow_forward" disabled={disabled} onClick={() => work(() => send("plugin_action", keys))} />;
    }
    case "markdown":
      return <PluginMarkdown item={item} preset={preset} />;
    case "boolean":
      return <SettingToggle label={title} checked={Boolean(current)} onChange={save} disabled={disabled} />;
    case "choice":
      return <label className="settings-row settings-control"><span>{title}</span>
        <select aria-label={title} value={current ?? ""} disabled={disabled} onChange={(event) => save(event.target.value)}>
          {item.options?.map((option) => <option key={option.value} value={option.value}>{settingTitle(option, locale, t)}</option>)}
        </select>
      </label>;
    case "text":
      return <PluginTextSetting key={String(current ?? "")} {...{ title, item, current, disabled, save, t }} />;
    default:
      return null;
  }
}

const pluginScopeModes = ["global", "include", "exclude"];
const globalScope = { mode: "global", presets: [] };
const pluginListRoute = { id: "", pages: [], scope: false };
const pluginStatus = (plugin) => plugin.builtIn ? "built_in" : plugin.enabled ? "plugin_enabled" : "plugin_disabled";
const pluginTitle = (plugin) => plugin.builtIn && plugin.id === "base" ? "MiniRP" : plugin.name;

function PluginScopeEditor({ initial, presets, t, disabled, save }) {
  const [mode, setMode] = useState(initial.mode);
  const [chosen, setChosen] = useState(initial.presets);
  const toggle = (id, checked) => setChosen((previous) => checked ? [...previous, id] : previous.filter((value) => value !== id));
  return <form className="workspace-tool-pane" onSubmit={(event) => {
    event.preventDefault();
    save({ mode, presets: mode === "global" ? [] : chosen });
  }}>
    <fieldset className="workspace-radio-group setting-group" disabled={disabled} aria-label={t("plugin_scope")}>
      {pluginScopeModes.map((choice) => <label className="settings-row" key={choice}>
        <input type="radio" name="plugin-scope" checked={mode === choice} onChange={() => setMode(choice)} />
        <span>{t("plugin_scope_" + choice)}</span>
      </label>)}
    </fieldset>
    {mode !== "global" && presets.length > 0 && <fieldset className="workspace-plugin-group" disabled={disabled}>
      <legend>{t("presets")}</legend>
      <div className="workspace-radio-group setting-group">
        {presets.map((preset) => <label className="settings-row" key={preset.id}>
          <input type="checkbox" checked={chosen.includes(preset.id)} onChange={(event) => toggle(preset.id, event.target.checked)} />
          <span dir="auto">{preset.name || t("preset")}</span>
        </label>)}
      </div>
    </fieldset>}
    <footer className="settings-save">
      <button type="submit" className="filled" disabled={disabled || mode === "include" && chosen.length === 0}>{t("save")}</button>
    </footer>
  </form>;
}

function PluginIdentity({ plugin, t, disabled, work }) {
  const title = t(pluginStatus(plugin));
  const detail = plugin.packageName || plugin.id;
  return <div className="workspace-plugin-identity">
    <span className="workspace-tool-icon"><Icon name="extension" /></span>
    {plugin.builtIn ? <span className="setting-label"><span>{title}</span><small dir="ltr">{detail}</small></span>
      : <SettingToggle label={title} detail={detail} checked={plugin.enabled} disabled={disabled}
        onChange={(enabled) => work(() => send("plugin_enabled", { id: plugin.id, enabled }))} />}
  </div>;
}

function PluginDetail({ plugin, workspace, settings, openScope, onRemove }) {
  const { t, disabled, work } = settings;
  const scope = plugin.scope || globalScope;
  const includes = scope.presets.includes(workspace.preset);
  const available = plugin.enabled && (scope.mode === "global" || includes === (scope.mode === "include"));
  const details = [...(plugin.permissions || []), ...(plugin.network || [])];
  const children = plugin.id === "base" ? [] : workspace.pluginSettings.filter((item) => item.plugin === plugin.id);
  return <>
    <PluginIdentity {...{ plugin, t, disabled, work }} />
    {(available && plugin.command || !plugin.builtIn) && <div className="setting-group">
      {available && plugin.command && <ToolEntry title={t("run_plugin")} icon="play_arrow" disabled={disabled}
        onClick={() => work(() => send("plugin_command", { id: plugin.id }))} />}
      {!plugin.builtIn && <>
        <ToolEntry title={t("plugin_scope")} icon="tune" detail={t("plugin_scope_" + scope.mode)} navigate
          disabled={disabled} onClick={openScope} />
        <ToolEntry title={t("export_archive")} icon="download" disabled={disabled}
          onClick={() => work(() => download({ type: "plugin", id: plugin.id, name: plugin.name }))} />
      </>}
    </div>}
    {available && children.length > 0 && <div className="setting-group">
      {children.map((item) => <PluginSetting key={item.key} item={item} settings={settings} />)}
    </div>}
    {(plugin.signer || details.length > 0) && <details className="settings-disclosure">
      <summary className="settings-row"><span>{t("about")}</span><Icon name="expand_more" /></summary>
      <div className="settings-disclosure-content">
        {plugin.signer && <div className="workspace-key-row">
          <code dir="ltr">{plugin.signer}</code><CopyValue key={plugin.signer} value={plugin.signer} t={t} run={work} />
        </div>}
        {details.length > 0 && <ul className="workspace-plugin-permissions">
          {details.map((value) => <li key={value}><code dir="ltr">{value}</code></li>)}
        </ul>}
      </div>
    </details>}
    {!plugin.builtIn && <div className="workspace-tool-actions">
      <button type="button" className="outlined destructive" disabled={disabled} onClick={onRemove}>
        <Icon name="delete" />{t("remove_plugin")}
      </button>
    </div>}
  </>;
}

export function Plugins({ workspace, locale, t, close }) {
  const [route, setRoute] = useState(pluginListRoute);
  const [confirm, setConfirm] = useState(null);
  const [problem, setProblem] = useState("");
  const [pending, setPending] = useState(false);
  useEffect(() => {
    send("plugin_settings").catch((error) => setProblem(error.message));
  }, []);
  const plugin = workspace.plugins.find((item) => item.id === route.id);
  const page = plugin && route.pages.at(-1);
  const editingScope = plugin && route.scope;
  const title = editingScope ? t("plugin_scope") : page ? settingTitle(page, locale, t) : plugin ? pluginTitle(plugin) : t("plugins");
  const pane = editingScope ? route.id + "/scope" : page ? route.id + "/" + page.key : plugin?.id || "plugins";
  const disabled = workspace.busy || pending;
  const navigate = (next) => {
    setProblem("");
    setRoute(next);
  };
  function back() {
    if (editingScope) navigate({ ...route, scope: false });
    else if (page) navigate({ ...route, pages: route.pages.slice(0, -1) });
    else if (plugin) navigate(pluginListRoute);
    else close();
  }
  async function work(operation) {
    if (pending) return;
    setProblem("");
    setPending(true);
    try { await operation(); }
    catch (error) { setProblem(error.message); }
    finally { setPending(false); }
  }
  function ask(next) {
    setProblem("");
    setConfirm(next);
  }
  function install(files) {
    work(async () => {
      const response = await upload("/api/import/plugin", files);
      const item = response.item;
      ask({
        title: item.name,
        label: t("install_plugin"),
        message: [item.packageName, item.signer, ...(item.permissions || []), ...(item.network || [])].filter(Boolean),
        submit: () => request("/api/plugin-install/" + response.install, {}),
      });
    });
  }
  const settings = {
    t, locale, preset: workspace.preset, work, disabled,
    openPage: (keys) => work(async () => {
      const result = (await send("plugin_page", keys)).item;
      setRoute((current) => current === route ? { ...route, pages: [...route.pages, result] } : current);
    }),
    update: (keys, value) => work(async () => {
      await send("plugin_setting", { ...keys, value });
      if (!page) return;
      const result = (await send("plugin_page", { id: page.plugin, key: page.key })).item;
      setRoute((current) => current === route ? { ...route, pages: [...route.pages.slice(0, -1), result] } : current);
    }),
  };
  return <Modal title={title} close={close} back={back} t={t} className="settings-modal workspace-tools">
    <ToolPane key={pane} title={title}>
      {problem && !confirm && <p className="setting-error" role="alert">{problem}</p>}
      {editingScope ? <PluginScopeEditor initial={plugin.scope || globalScope} presets={workspace.presets} {...{ t, disabled }}
        save={(scope) => work(async () => {
          await send("plugin_scope", { id: plugin.id, scope });
          setRoute((current) => current === route ? { ...route, scope: false } : current);
        })} /> : page ? <>
        <PluginMarkdown item={page} preset={workspace.preset} />
        <div className="setting-group">
          {page.items?.map((item) => <PluginSetting key={item.key} item={item} settings={settings} />)}
        </div>
      </> : plugin ? <PluginDetail {...{ plugin, workspace, settings }} openScope={() => navigate({ ...route, scope: true })}
        onRemove={() => ask({
          title: t("uninstall_plugin_title", plugin.name), label: t("remove_plugin"), danger: true,
          submit: async () => {
            await send("plugin_uninstall", { id: plugin.id });
            navigate(pluginListRoute);
          },
        })} /> : <>
        <div className="setting-group">
          {workspace.plugins.map((item) => <ToolEntry key={item.id} title={pluginTitle(item)} detail={t(pluginStatus(item))}
            icon="extension" navigate disabled={pending} onClick={() => navigate({ ...pluginListRoute, id: item.id })} />)}
        </div>
        <div className="workspace-tool-actions">
          <FileChoice label={t("install_plugin")} icon="add" accept=".plugin" choose={install} disabled={disabled} className="tonal" />
        </div>
      </>}
    </ToolPane>
    {confirm && <Confirm dialog={{ ...confirm, message: (confirm.message?.length || problem) ? <>
      {confirm.message && <span className="workspace-plugin-confirmation" dir="auto">{confirm.message.join("\n")}</span>}
      {problem && <span className="setting-error" role="alert">{problem}</span>}
    </> : undefined }} close={() => {
      setConfirm(null);
      setProblem("");
    }} run={work} t={t} />}
  </Modal>;
}

function MemoryEditor({ draft, setDraft, t, close, call, mutate }) {
  const [deleting, setDeleting] = useState(false);
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState("");
  const keyId = useId();
  const existing = draft.revision !== undefined;
  const readOnly = existing && draft.key.startsWith("_");
  const title = t(readOnly ? "entry_content" : existing ? "edit_entry" : "new_note");
  const changed = !existing || draft.original !== draft.content;
  async function work(action) {
    if (pending) return;
    setPending(true);
    setProblem("");
    try { await action(); }
    catch (error) { setProblem(error.message); }
    finally { setPending(false); }
  }
  const closeEditor = () => { if (!pending) close(); };
  return <Modal title={title} close={closeEditor} back={closeEditor} t={t} className="settings-modal workspace-tools">
    <ToolPane title={title}>
      {problem && !deleting && <p className="setting-error" role="alert">{problem}</p>}
      <form className="workspace-tool-pane" onSubmit={(event) => {
        event.preventDefault();
        if (pending || readOnly || !changed) return;
        work(async () => {
          await call("db_write", { key: draft.key, content: draft.content, expected_revision: draft.revision ?? 0 });
          mutate();
        });
      }}>
        <div className="workspace-memory-key">
          <label htmlFor={keyId}>{t("entry_key")}</label>
          <span className="workspace-key-row">
            <input id={keyId} required maxLength={500} readOnly={existing} disabled={pending} value={draft.key} dir="auto"
              onChange={(event) => setDraft({ ...draft, key: event.target.value })} />
            {existing && <CopyValue value={draft.key} t={t} run={work} />}
          </span>
        </div>
        <label>{t("entry_content")}
          <textarea rows="12" readOnly={readOnly} disabled={pending} value={draft.content} dir="auto"
            onChange={(event) => setDraft({ ...draft, content: event.target.value })} />
        </label>
        {readOnly ? <div className="workspace-tool-actions">
          <CopyValue value={draft.content} t={t} run={work} />
        </div> : <div className="workspace-memory-actions">
          {existing && <button type="button" className="text-button destructive" disabled={pending} onClick={() => {
            setProblem("");
            setDeleting(true);
          }}>{t("delete")}</button>}
          <button type="submit" className="filled" disabled={pending || !changed || !draft.key.trim() || draft.key.startsWith("_")}>
            {t("save")}
          </button>
        </div>}
      </form>
    </ToolPane>
    {deleting && <Confirm t={t} run={work} close={() => setDeleting(false)} dialog={{
      title: t("delete_note_question"), label: t("delete"), message: problem || draft.key,
      submit: async () => {
        await call("db_delete", { key: draft.key, expected_revision: draft.revision });
        mutate();
      },
    }} />}
  </Modal>;
}

const memoryRoles = { chat: "chat", env: "world", preset: "preset", search: "search_agent" };

export function Memory({ workspace, role, t, close }) {
  const [agent, setAgent] = useState(role);
  const [participant, setParticipant] = useState("");
  const [query, setQuery] = useState("");
  const [entries, setEntries] = useState([]);
  const [cursor, setCursor] = useState(null);
  const [draft, setDraft] = useState(null);
  const [internal, setInternal] = useState(false);
  const [loading, setLoading] = useState(false);
  const [reading, setReading] = useState(false);
  const [problem, setProblem] = useState("");
  const [refresh, setRefresh] = useState(0);
  const version = useRef(0);
  const { preset, chat, conversation, participants } = workspace;
  const actor = agent === "chat" || agent === "search"
    ? participants.find((item) => item.id === participant) || participants[0] : null;
  const call = useCallback(async (tool, args) => (await send("memory", {
    tool, arguments: args, agent, participant: actor?.id || "", preset, chat, conversation,
  })).result, [actor?.id, agent, preset, chat, conversation]);
  const search = query.trim();

  const load = useCallback(async (id, after) => {
    try {
      const value = await call(search ? "db_search" : "db_list", {
        query: search, notes_only: !internal, limit: 40, ...(after || {}),
      });
      if (id !== version.current) return;
      setEntries((previous) => {
        const page = value.entries || value.matches || [];
        return after ? [...new Map([...previous, ...page].map((entry) => [entry.key, entry])).values()] : page;
      });
      setCursor(value.next_after ? { after: value.next_after } : value.next_offset ? { offset: value.next_offset } : null);
    } catch (error) {
      if (id === version.current) setProblem(error.message);
    } finally {
      if (id === version.current) setLoading(false);
    }
  }, [call, search, internal]);

  useEffect(() => {
    const id = ++version.current;
    setEntries([]);
    setCursor(null);
    setProblem("");
    setLoading(true);
    const timer = setTimeout(() => load(id, null), search ? 200 : 0);
    return () => {
      clearTimeout(timer);
      version.current++;
    };
  }, [load, refresh, search]);

  async function openEntry(key) {
    const id = version.current;
    setReading(true);
    setProblem("");
    try {
      const parts = [];
      let start = 0;
      let revision;
      do {
        const value = await call("db_read", { key, start, length: 64000 });
        if (id !== version.current) return;
        if (revision !== undefined && revision !== value.revision) {
          setProblem(t("retry"));
          return;
        }
        parts.push(value.content);
        revision = value.revision;
        start = value.next_offset;
      } while (start);
      const content = parts.join("");
      setDraft({ key, content, revision, original: content });
    } catch (error) {
      if (id === version.current) setProblem(error.message);
    } finally { setReading(false); }
  }

  return <Modal title={t("memory")} close={close} t={t} className="settings-modal workspace-tools">
    <ToolPane title={t("memory")}>
      <div className="setting-group">
        <label className="settings-row settings-control"><span>{t("notes")}</span>
          <select value={agent} disabled={reading} onChange={(event) => setAgent(event.target.value)}>
            {Object.entries(memoryRoles).map(([value, label]) => <option key={value} value={value}>{t(label)}</option>)}
          </select>
        </label>
        {actor && <label className="settings-row settings-control"><span>{t(memoryRoles[agent])}</span>
          <select value={actor.id} disabled={reading} onChange={(event) => setParticipant(event.target.value)}>
            {participants.map((item) => <option value={item.id} key={item.id}>{item.name || item.id}</option>)}
          </select>
        </label>}
        <SettingToggle label={t("internal_memory")} checked={internal} onChange={setInternal} disabled={reading} />
      </div>
      <div className="workspace-memory-search">
        <input type="search" aria-label={t("search_entries")} placeholder={t("search_entries")} value={query}
          disabled={reading} onChange={(event) => setQuery(event.target.value)} />
        <button className="tonal" type="button" disabled={reading} onClick={() => setDraft({ key: "", content: "" })}>
          <Icon name="add" />{t("new_note")}
        </button>
      </div>
      {problem && <p className="setting-error" role="alert">{problem}</p>}
      {(loading || reading || !entries.length && !problem) && <p className="settings-caption" role="status">
        {t(loading || reading ? "running" : "database_empty")}
      </p>}
      {entries.length > 0 && <ul className="workspace-memory-list setting-group" aria-busy={loading || reading}>
        {entries.map((entry) => <li key={entry.key}>
          <ToolEntry title={entry.key} detail={entry.snippet} navigate disabled={loading || reading}
            onClick={() => openEntry(entry.key)} />
        </li>)}
      </ul>}
      {cursor && <div className="workspace-tool-actions">
        <button type="button" className="text-button" disabled={loading || reading} onClick={() => {
          setLoading(true);
          setProblem("");
          load(++version.current, cursor);
        }}>{t("load_more")}</button>
      </div>}
    </ToolPane>
    {draft && <MemoryEditor {...{ draft, setDraft, t, call }} close={() => setDraft(null)} mutate={() => {
      setDraft(null);
      setRefresh((value) => value + 1);
    }} />}
  </Modal>;
}

export function pluginAttachmentTypes(plugins, preset) {
  return plugins.flatMap((plugin) => {
    const allowed = plugin.scope?.presets?.includes(preset);
    if (!plugin.enabled || (plugin.scope?.mode === "include" && !allowed) || (plugin.scope?.mode === "exclude" && allowed)) return [];
    return (plugin.attachments || []).map((item) => ({ ...item, plugin: plugin.id }));
  });
}
