import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import appIcon from "../../../assets/branding/launcher.png";
import { assetUrl, download, request, send, upload } from "./api.js";
import { resolveLocale, setDocumentLocale, translator } from "./i18n.js";
import { applyEvents, initial, prependHistory } from "./workspace.js";
import { Composer, Transcript } from "./Conversation.jsx";
import { EnsembleOverview, EnsembleRoster, EnsembleTraceDialog, OutOfStoryNotice, PrivateMessagesFab, RoomThinking } from "./EnsembleRoom.jsx";
import { OpenUi } from "./OpenUi.jsx";
import { ModelPicker } from "./ModelSettings.jsx";
import { Settings } from "./Settings.jsx";
import { Memory, Plugins, pluginAttachmentTypes } from "./WorkspaceTools.jsx";
import { Confirm, FileChoice, Icon, IconButton, Modal, Notice, Select, SettingToggle, Surface, TextDialog } from "./ui.jsx";
import { ActionMenu } from "./ActionMenu.jsx";
import { RoomLoading } from "./RoomLoading.jsx";
import { applyAccent, backgroundAccent } from "./theme.js";
import "./styles.css";

const systemTheme = matchMedia("(prefers-color-scheme: dark)");
const compactLayout = matchMedia("(width < 840px)");
const defaultPreferences = { locale: "", theme: "system_default", thoughts: false };

function loadPreferences() {
  try { return { ...defaultPreferences, ...JSON.parse(localStorage.getItem("minirp.ui")) }; }
  catch { return defaultPreferences; }
}

function useWorkspace(onError, onLink) {
  const [workspace, setWorkspace] = useState(initial);
  const [connected, setConnected] = useState(false);
  useEffect(() => {
    const source = new EventSource("/api/events");
    let replay = true;
    source.onopen = () => { replay = true; setConnected(true); };
    source.onerror = () => setConnected(false);
    source.onmessage = (message) => {
      const events = JSON.parse(message.data);
      const restart = replay;
      replay = false;
      setWorkspace((current) => applyEvents(restart ? { ...initial, historyVersion: current.historyVersion } : current, events));
      for (const event of events) {
        if (event.kind === "error" || event.kind === "plugin_error") onError(event.message);
        if (event.kind === "plugin_action") onLink(event.url);
      }
    };
    return () => source.close();
  }, [onError, onLink]);
  const addHistory = useCallback((pane, version, before, page) =>
    setWorkspace((current) => prependHistory(current, pane, version, before, page)), []);
  return { workspace, connected, addHistory };
}

function ArchiveImport({ t, disabled, run, visible = true }) {
  const [expanded, setExpanded] = useState(false);
  const [help, setHelp] = useState(false);
  const menuId = useId();
  const frame = useRef(null);
  const trigger = useRef(null);
  const closeMenu = () => {
    setExpanded(false);
    trigger.current?.focus();
  };
  useEffect(() => {
    if (visible) return;
    setExpanded(false);
    setHelp(false);
  }, [visible]);
  useEffect(() => {
    if (!expanded) return;
    function dismissOutside(event) {
      if (!frame.current?.contains(event.target)) setExpanded(false);
    }
    document.addEventListener("pointerdown", dismissOutside);
    return () => document.removeEventListener("pointerdown", dismissOutside);
  }, [expanded]);
  const importFiles = (type, files) => {
    closeMenu();
    return run(() => upload("/api/import/" + type, files));
  };

  return <div className="archive-import" ref={frame} onKeyDown={(event) => {
    if (event.key !== "Escape" || !expanded) return;
    event.preventDefault();
    event.stopPropagation();
    closeMenu();
  }}>
    <button type="button" className="import-fab" disabled={disabled} ref={trigger}
      aria-expanded={expanded} aria-controls={menuId} onClick={() => setExpanded(!expanded)}>
      <Icon name={expanded ? "close" : "download"} />{t("import_menu")}
    </button>
    <div id={menuId} className="import-options" inert={!expanded} data-open={expanded} role="group" aria-label={t("import_menu")}>
      <FileChoice className="import-action" label={t("import_preset")} icon="extension" disabled={disabled}
        accept=".minirp-preset,.preset,.zip" choose={(files) => importFiles("preset", files)} />
      <FileChoice className="import-action" label={t("import_save")} icon="download" disabled={disabled}
        accept=".minirp-save,.save,.zip" choose={(files) => importFiles("save", files)} />
      <button type="button" className="import-action" disabled={disabled} onClick={() => {
        closeMenu();
        setHelp(true);
      }}><Icon name="more_horiz" />{t("import_help")}</button>
    </div>
    <Surface>{help && <Modal title={t("import_help")} close={() => setHelp(false)} t={t}>
      <p>{t("import_preset_detail")}</p>
      <p>{t("import_save_detail")}</p>
    </Modal>}</Surface>
  </div>;
}

function PresetChoices({ workspace, t, open, busy }) {
  return <div className="room-list preset-list">
    {workspace.presets.map((item) => <button type="button" className="room-select" key={item.id}
      aria-current={item.id === workspace.preset ? "true" : undefined} disabled={busy}
      onClick={() => open(item.id)}>
      <Icon name={item.ensemble ? "auto_awesome" : "chat_bubble"} />
      <span className="choice-label" dir="auto">{item.name || t("preset")}</span>
      {item.id === workspace.preset && <Icon name="check" />}
    </button>)}
  </div>;
}

function LibraryActions({ presets, chatLabel, createPreset, startConversation, t, busy, setupPending, run }) {
  const createLabel = t("create_preset");
  return <footer className="preset-actions library-footer" data-presets={presets}>
    <div className="library-import-slot" inert={!presets} data-visible={presets}>
      <div><ArchiveImport t={t} disabled={busy} run={run} visible={presets} /></div>
    </div>
    <button className="filled create-preset-fab" type="button" disabled={busy || !presets && setupPending}
      aria-label={presets ? createLabel : chatLabel} onClick={presets ? createPreset : startConversation}>
      <Icon name="add" />
      <span className="library-action-labels" aria-hidden="true">
        <span data-current={presets}>{createLabel}</span>
        <span data-current={!presets}>{chatLabel}</span>
      </span>
    </button>
  </footer>;
}

function Sidebar({ workspace, t, visible, compact, close, select, createPreset, startConversation, roomActions, busy, setupPending, run }) {
  const [filter, setFilter] = useState("");
  const [limit, setLimit] = useState(40);
  const [choosing, setChoosing] = useState(false);
  const presetTitle = (workspace.preset && workspace.config.name) || t("choose_preset");
  const name = (item, key) => item.name || t(key);
  const rooms = useMemo(() => {
    const query = filter.toLocaleLowerCase();
    return query ? workspace.chats.filter((item) => (item.name || t("chat")).toLocaleLowerCase().includes(query)) : workspace.chats;
  }, [workspace.chats, filter, t]);
  useEffect(() => { setLimit(40); }, [workspace.preset, filter]);
  function switchPreset(id) {
    setChoosing(false);
    select("home", { preset: id });
    if (compact) close();
  }
  function roomOp(item) {
    select("select_chat", { preset: workspace.preset, id: item.id });
    if (compact) close();
  }
  return <aside className={visible ? "sidebar visible" : "sidebar"} id="library" inert={compact && !visible} aria-label={t("conversations")}>
    <header className="sidebar-header">
      <span className="wordmark"><img className="app-mark" src={appIcon} alt="" />MiniRP</span>
      <IconButton icon="close" className="mobile-only drawer-close" label={t("close")} onClick={close} />
    </header>
    <div className="sidebar-body">
      <div className="row sidebar-heading">
        <button id="preset-select" type="button" className="preset-select text-button"
          aria-expanded={choosing} aria-controls="sidebar-preset-choices" disabled={busy}
          onClick={() => setChoosing(!choosing)}>
          <span className="choice-label" dir="auto">{choosing ? t("choose_preset") : presetTitle}</span>
          <Icon name="expand_more" />
        </button>
      </div>
      <div id="sidebar-preset-choices" className="sidebar-preset-choices" data-open={choosing} inert={!choosing}>
        <div><PresetChoices workspace={workspace} t={t} open={switchPreset} busy={busy} /></div>
      </div>
      {workspace.preset && !choosing && <>
        <div className="row sidebar-heading">
          <h2>{t("conversations")}</h2>
        </div>
        <div className="library-search">
          <Icon name="search" />
          <input placeholder={t("search_chats")} aria-label={t("search_chats")} type="search" value={filter} onChange={(event) => setFilter(event.target.value)} />
        </div>
        <nav className="room-list" aria-label={t("conversations")}>
          {rooms.slice(0, limit).map((item) => <div key={item.id} className="room-row" data-current={item.id === workspace.chat}>
            <button className="room-select" type="button" aria-current={item.id === workspace.chat ? "page" : undefined}
              disabled={busy} onClick={() => roomOp(item)}>
              <Icon name={item.ensemble ? "auto_awesome" : "chat_bubble"} />
              <span className="choice-label" dir="auto">{name(item, item.ensemble ? "new_room" : "chat")}</span>
            </button>
            <ActionMenu label={t("more_options") + " · " + name(item, "chat")}
              disabled={busy} groups={roomActions(item)} />
          </div>)}
        </nav>
        {rooms.length > limit && <button type="button" className="text-button" onClick={() => setLimit(limit + 40)}>{t("load_more")}</button>}
      </>}
    </div>
    <LibraryActions presets={choosing || !workspace.preset} chatLabel={t(workspace.config.chat_mode === "ensemble" ? "new_room" : "new_chat")}
      {...{ t, run, createPreset, startConversation, busy, setupPending }} />
  </aside>;
}

function PresetHome({ name, startLabel, createPreset, openPreset, startConversation, busy, t }) {
  return <section className="preset-home" aria-labelledby="preset-home-title">
    <div className="preset-home-content">
      {name ? <>
        <p className="preset-home-caption">{t("welcome_to_preset")}</p>
        <h1 id="preset-home-title"><button type="button" className="text-button home-preset-name" title={t("edit_preset")}
          dir="auto" disabled={busy} onClick={openPreset}>{name}</button></h1>
        {startLabel && <button type="button" className="filled" disabled={busy} onClick={startConversation}>{startLabel}</button>}
      </> : <>
        <h1 id="preset-home-title" className="preset-home-caption">{t("no_presets_yet")}</h1>
        <button type="button" className="filled" disabled={busy} onClick={createPreset}>
          <Icon name="add" />{t("create_preset")}
        </button>
      </>}
    </div>
  </section>;
}

function WorkspaceInteraction({ item, t, run, theme, dark }) {
  const reply = (result) => request("/api/interactions/" + item.id, result);
  const [responding, setResponding] = useState(false);
  if (item.kind === "web_browser_setup") {
    const offer = item.phase === "offer";
    const answer = (accept) => {
      if (responding) return;
      setResponding(true);
      run(async () => {
        try { await reply({ accept }); }
        finally { setResponding(false); }
      });
    };
    const bytes = item.total || 0;
    const progress = item.phase === "downloading" && bytes > 0;
    const size = new Intl.NumberFormat(document.documentElement.lang, { maximumFractionDigits: 0 }).format(bytes / 1048576);
    const label = offer ? t("browser_runtime_explanation", size) : t("browser_runtime_" + item.phase);
    return <Modal title={t("browser_runtime_title")} t={t} close={() => answer(false)}>
      <p>{label}</p>
      {!offer && <progress className="browser-runtime-progress" aria-label={label} max={bytes || 1}
        value={progress ? Math.min(item.received || 0, bytes) : undefined} />}
      <div className="actions">
        <button className="text-button" type="button" disabled={responding} onClick={() => answer(false)}>
          {t(offer ? "browser_runtime_later" : "cancel")}
        </button>
        {offer && <button className="filled" type="button" disabled={responding} onClick={() => answer(true)}>
          {t("browser_runtime_continue")}
        </button>}
      </div>
    </Modal>;
  }
  if (item.kind === "web_preview") {
    return <div className="preview-renderer" aria-hidden="true" style={{ width: Math.max(240, Math.min(768, Number(item.width) || 580)) }}>
      <OpenUi block={item} scope={{}} identity={item.id} preview={item} locale={document.documentElement.lang} dark={Boolean(item.dark)}
        theme={Boolean(item.dark) === dark ? theme : undefined}
        t={t} onPreview={(result) => run(() => reply({ ...result, available: true, offline: true, title: item.open_ui_title }))} />
    </div>;
  }
  const close = () => run(() => reply({ files: [] }));
  return <Modal title={item.name || t("plugin_resource_request")} t={t} close={close}>
    <div className="actions">
      <button className="text-button" type="button" onClick={close}>{t("cancel")}</button>
      <FileChoice label={t("choose_files")} multiple
        accept={item.arguments?.mimeTypes?.join(",") || (item.picker === "media" ? "image/*,video/*" : undefined)}
        choose={(files) => run(() => upload("/api/picker/" + item.id, files))} />
    </div>
  </Modal>;
}

function App() {
  const [preferences, setPreferences] = useState(loadPreferences);
  const [systemDark, setSystemDark] = useState(systemTheme.matches);
  const [selectedRole, setRole] = useState("chat");
  const [settings, setSettings] = useState(null);
  const [dialog, setDialog] = useState(null);
  const [sidebar, setSidebar] = useState(false);
  const [compact, setCompact] = useState(compactLayout.matches);
  const [drafts, setDrafts] = useState({});
  const [sending, setSending] = useState(false);
  const [creatingPreset, setCreatingPreset] = useState(false);
  const [notice, setNotice] = useState("");
  const [link, setLink] = useState("");
  const [attachmentType, setAttachmentType] = useState("");
  const [sampledAccent, setSampledAccent] = useState(null);
  const [theme, setTheme] = useState(undefined);
  const seenOpened = useRef(0);
  const currentAction = useRef(null);
  const menuButton = useRef(null);
  const privateFab = useRef(null);
  const locale = resolveLocale(preferences.locale ? [preferences.locale] : navigator.languages);
  const t = useMemo(() => translator(locale), [locale]);
  const onError = useCallback((message) => setNotice(message), []);
  const onLink = useCallback((url) => { if (/^https?:\/\//i.test(url || "")) setLink(url); }, []);
  const { workspace, connected, addHistory } = useWorkspace(onError, onLink);
  const dark = preferences.theme === "dark" || preferences.theme === "system_default" && systemDark;
  const { preset, chat, conversation, config, busy, generating, privateParticipant, privateInStory } = workspace;
  const setupPending = config.setup_status === "draft";
  const role = setupPending ? "preset" : selectedRole;
  const presetRunning = generating === "preset" ||
    workspace.panes.preset.some((block) => block.plugin_status === "running");
  const presetWorking = presetRunning || role === "preset" && sending;
  const navigationBusy = !connected || !workspace.ready || busy || presetWorking || creatingPreset;
  const presetExitLocked = setupPending || navigationBusy;
  const background = config.appearance?.background_url;
  const explicitAccent = config.appearance?.theme_color;
  const seed = explicitAccent || (sampledAccent && sampledAccent.background === background && sampledAccent.preset === preset
    ? sampledAccent.color : "");
  const pane = role === "chat" && workspace.ensemble !== null ? "ensemble" : role;
  const ensembleRoom = pane === "ensemble" && Boolean(chat);
  const privateChat = ensembleRoom && Boolean(privateParticipant);
  const systemOnly = privateChat && !privateInStory;
  const privatePerson = privateChat && workspace.participants.find((person) => person.id === privateParticipant);
  const history = workspace.history[pane];
  const scope = useMemo(() => ({ preset, room: role === "preset" ? "" : chat,
    conversation: role === "chat" ? conversation : "", agent: role }), [preset, chat, conversation, role]);
  const context = (extra) => ({ preset, chat, conversation: role === "chat" ? conversation : "", ...extra });
  const draftKey = JSON.stringify([role, preset, role === "preset" ? "" : chat, role === "chat" ? conversation : ""]);
  const transcriptKey = draftKey + ":" + (history?.version ?? "");
  const draft = drafts[draftKey] || { text: "", files: [], action: null };
  const changeDraft = (data) => setDrafts((current) => ({ ...current, [draftKey]: data }));
  function quoteReply(text) {
    const excerpt = text.trim();
    if (!excerpt) return;
    if (excerpt.length > 32768 || draft.files.length >= 20) {
      setNotice(t("attachment_too_large"));
      return;
    }
    const quote = { id: crypto.randomUUID(), kind: "quote", text: excerpt };
    setDrafts((current) => {
      const previous = current[draftKey] || draft;
      return { ...current, [draftKey]: { ...previous, files: [...previous.files, quote] } };
    });
    requestAnimationFrame(() => {
      const input = document.querySelector(".composer textarea");
      input?.focus({ preventScroll: true });
      if (input) input.setSelectionRange(input.value.length, input.value.length);
    });
  }
  const title = role === "preset" ? t("edit_preset")
    : role === "env" ? t("world")
      : privateChat ? privatePerson?.name || t("ensemble_private_messages")
      : workspace.ensemble?.find((room) => room.id === conversation)?.name || workspace.title || config.name || "MiniRP";

  const run = useCallback(async (action) => {
    setNotice("");
    try { return await action(); }
    catch (error) { setNotice(error.message); }
  }, []);
  useEffect(() => {
    run(async () => {
      const current = await request("/api/settings");
      setSettings(current);
      if (current.preferences) setPreferences({ ...defaultPreferences, ...current.preferences });
    });
    const change = (event) => setSystemDark(event.matches);
    systemTheme.addEventListener("change", change);
    return () => systemTheme.removeEventListener("change", change);
  }, [run]);
  useEffect(() => {
    function layout(event) {
      setCompact(event.matches);
      if (!event.matches) setSidebar(false);
    }
    compactLayout.addEventListener("change", layout);
    return () => compactLayout.removeEventListener("change", layout);
  }, []);
  useEffect(() => {
    if (!sidebar || !compact) return;
    document.querySelector(".drawer-close")?.focus({ preventScroll: true });
    function back(event) {
      if (event.key === "Escape" && !document.querySelector("dialog[open]") && !event.defaultPrevented) closeLibrary();
    }
    document.addEventListener("keydown", back);
    return () => document.removeEventListener("keydown", back);
  }, [sidebar, compact]);
  useEffect(() => {
    setDocumentLocale(locale);
    localStorage.setItem("minirp.ui", JSON.stringify(preferences));
  }, [locale, preferences]);
  useLayoutEffect(() => { setTheme(applyAccent(seed, dark)); }, [seed, dark]);
  useLayoutEffect(() => {
    const opened = workspace.opened !== seenOpened.current && workspace.opened > 0;
    if (opened) setRole("preset");
    if (opened || presetWorking) setSidebar(false);
    seenOpened.current = workspace.opened;
  }, [workspace.opened, presetWorking]);
  useEffect(() => {
    if (!workspace.draft || !chat || workspace.ensemble !== null) return;
    const key = JSON.stringify(["chat", preset, chat, conversation]);
    setDrafts((values) => values[key] ? values : {
      ...values, [key]: { text: workspace.draft.text || "", files: workspace.draft.attachments || [], action: workspace.draft.open_ui_action },
    });
  }, [workspace.draft, preset, chat, conversation, workspace.ensemble]);
  useEffect(() => {
    if (notice.length === 0) return;
    const timer = setTimeout(() => setNotice(""), 10000);
    return () => clearTimeout(timer);
  }, [notice]);

  useEffect(() => {
    if (connected) send("plugin_locale", { locale }).catch((error) => onError(error.message));
  }, [connected, locale, onError]);

  async function saveSettings(value) {
    const { environment, default_research, preferences: _, ...editable } = value;
    await request("/api/settings", editable);
    setSettings(value);
    if (setupPending && (value.agents !== settings.agents || value.providers !== settings.providers) &&
      !workspace.panes.preset.some((block) => block.content?.trim())) {
      await send("resume_preset_interview", { preset, locale });
    }
  }
  function savePreferences(value) {
    setPreferences(value);
    run(() => request("/api/preferences", value));
  }
  const select = (op, args = {}) => run(async () => {
    setRole("chat");
    await send(op, { locale, ...args });
  });
  const openScene = (id) => select("select_ensemble_chat", { preset, chat, conversation: id });
  const openPrivateChat = (participant) => run(async () => {
    await send("open_ensemble_dm", { preset, chat, participant, locale });
    return true;
  });
  const roomPreset = config.chat_mode === "ensemble";
  const startConversation = () => select(roomPreset ? "select_chat" : "start_chat", { preset });
  function createPreset() {
    if (navigationBusy) return;
    setCreatingPreset(true);
    setSidebar(false);
    run(async () => {
      try { await send("create_preset", { name: t("create_preset"), locale, open: true }); }
      finally { setCreatingPreset(false); }
    });
  }
  const openPreset = () => run(() => send("open_preset", context({ locale })));
  const branch = useCallback((block, mode) => run(() => send("branch", { preset, chat, block, mode })), [chat, preset, run]);
  const exportType = (type, extra = {}) => run(() => download({
    type, preset, room: chat, chat, conversation: role === "chat" ? conversation : "",
    agent: role, name: workspace.title || config.name || "MiniRP", ...extra,
  }));
  const runExport = useCallback((runId) => run(() => download({
    type: "transcript", preset, agent: "plugin", run: runId, name: "MiniRP",
  })), [preset, run]);
  function chooseFiles(files) {
    return run(async () => {
      if (files.length + draft.files.length > 20 || files.reduce((total, file) => total + file.size, 0) +
        draft.files.reduce((total, file) => total + (file.size || 0), 0) > 16_000_000)
        throw new Error(t("attachment_too_large"));
      const result = await upload("/api/attachments", files);
      const type = pluginTypes.find((item) => [item.plugin, item.id].join("/") === attachmentType);
      const items = result.files.map((file) => type ? { ...file, plugin: type.plugin, type: type.id } : file);
      setDrafts((current) => ({
        ...current, [draftKey]: { ...draft, ...current[draftKey], files: [...(current[draftKey]?.files || draft.files), ...items] },
      }));
    });
  }
  function submit(asSystem = false, reply = null) {
    if (sending || !connected) return;
    if (privateChat) asSystem = systemOnly;
    const text = (reply?.text || draft.text).trim();
    if (!text && (asSystem || !draft.files.length)) return;
    const steering = !ensembleRoom && generating === role;
    const operation = steering ? "steer" : asSystem && !privateChat ? "system_message" : "message";
    const attachments = reply || systemOnly ? [] : draft.files;
    if (attachments.length > 20) {
      setNotice(t("attachment_too_large"));
      return;
    }
    setSending(true);
    run(async () => {
      try {
        await send(operation, context({
          text, agent: role, role: asSystem ? "system" : "user",
          editor_turn: role === "preset", attachments, open_ui_action: reply?.action || draft.action, locale,
        }));
        if (!reply) changeDraft({ text: "", files: [], action: null });
      } finally { setSending(false); }
    });
  }
  currentAction.current = (block, action) => {
    if (action.type === "link") {
      if (/^https?:\/\//i.test(action.url || "")) window.open(action.url, "_blank", "noopener,noreferrer");
      else if (action.url?.startsWith("minirp://assets/") && block.asset_links?.[action.url]) {
        const link = document.createElement("a");
        link.href = assetUrl(action.url, scope, { mode: "display", block: block.id });
        link.download = "";
        link.click();
      } else if (action.url?.startsWith("minirp://actions/")) run(() => send("interaction_open", { url: action.url }));
      return;
    }
    if (block.open_ui_host_action === "preset-home") {
      if (presetExitLocked) {
        setNotice(t(setupPending ? "preset_return_home_setup_pending" : "preset_return_home_working"));
        return;
      }
      select("home", { preset });
      return;
    }
    if (busy || sending || systemOnly) return;
    if (action.text?.trim()) submit(false, { text: action.text,
      action: { block: block.id, data: { form: action.form, context: action.context } } });
  };
  const openAction = useCallback((block, action) => currentAction.current(block, action), []);
  const confirmDelete = (item, type) => setDialog({
    type: "confirm", title: t("delete_named", item.name),
    message: type === "preset" ? t("delete_preset_detail") : undefined,
    submit: () => send("delete_" + type, { preset, id: item.id }),
  });
  const action = (key, icon, select, options) => ({ label: t(key), icon, select, ...options });
  const roomActions = (item) => [
    [action("export_chat", "share", () => exportType("room", { room: item.id, conversation: "", agent: "chat", name: item.name }))],
    [action("delete_chat", "delete", () => confirmDelete({ ...item, name: item.name || t("chat") }, "chat"), { danger: true })],
  ];
  const canRename = role === "chat" && Boolean(chat) && !privateChat;
  const canGenerateTitle = canRename && !conversation &&
    (workspace.panes.chat.some((block) => (block.kind || block.role) === "user") || Boolean(workspace.ensemble));
  const rename = () => setDialog({
    type: "text", title: t("rename"), value: title,
    submit: (name) => send(conversation ? "rename_ensemble_chat" : "rename", context({ name })),
    secondary: canGenerateTitle ? { label: t("generate_title"), submit: () => send("generate_title", context()) } : undefined,
  });
  const closeDialog = () => setDialog(null);
  const show = (type) => setDialog({ type });
  const editPrompt = () => setDialog({
    type: "text", title: t("system_prompt"), label: "system_prompt", multiline: true,
    value: config[role + "_agent"]?.system_prompt,
    submit: (text) => send("save_prompt", context({ text, agent: role })),
    returnTo: "contextSettings",
  });
  const pluginTypes = pluginAttachmentTypes(workspace.plugins, preset);
  const home = connected && workspace.ready && role === "chat" && !chat && !workspace.panes[pane].length;
  const requiresStart = home && (roomPreset || Boolean(config.chat_agent?.greeting_system_message?.trim()));
  const canCompose = Boolean(preset && (role !== "chat" || conversation || !requiresStart && !roomPreset && workspace.ensemble == null));
  const canExportTranscript = role === "preset" || Boolean(chat && (role === "env" || workspace.ensemble === null || conversation));
  const contextSettingsTitle = role === "chat" ? t("conversation_settings") : t("settings") + " · " + t(role === "env" ? "world" : "preset");
  const conversationActions = [
    [
      role === "chat" && chat && action("world", "auto_awesome", () => setRole("env"), { next: true }),
      role !== "preset" && !home && action("edit_preset", "edit", openPreset, { next: true, disabled: navigationBusy }),
      chat && action("memory", "visibility", () => show("memory"), { next: true, disabled: busy }),
    ].filter(Boolean),
    [
      canExportTranscript && action("export_transcript", "share", () => exportType("transcript"), { disabled: navigationBusy }),
    ].filter(Boolean),
    [
      (chat || role === "preset") && action(role === "chat" ? "conversation_settings" : "settings",
        "tune", () => show("contextSettings"), { next: true }),
      action("preset_management", "more_horiz", () => show("presetManagement"), { next: true, disabled: navigationBusy }),
    ].filter(Boolean),
  ];
  function closeLibrary() {
    setSidebar(false);
    requestAnimationFrame(() => menuButton.current?.focus({ preventScroll: true }));
  }
  const emptyAgent = role !== "chat" && !workspace.panes[pane].length;
  const loadingPreset = workspace.presets.find((item) => item.id === workspace.roomLoading?.preset);
  const canQuote = canCompose && connected && !systemOnly && !sending && !busy && (ensembleRoom || generating !== role);
  const composer = canCompose && <div className="composer-deck">
    {!systemOnly && pluginTypes.length > 0 && <label className="model-picker plugin-attachment-type">
      <span>{t("add_attachment")}</span>
      <Select value={attachmentType} onChange={(event) => setAttachmentType(event.target.value)}>
        <option value="">{t("choose_files")}</option>
        {pluginTypes.map((item) => <option key={item.plugin + "/" + item.id} value={item.plugin + "/" + item.id}>{item.title || item.id}</option>)}
      </Select>
    </label>}
    <Composer t={t} role={role} draft={draft.text} change={(text) => changeDraft({ ...draft, text })}
      attachments={draft.files} addFiles={chooseFiles} removeFile={(id) => changeDraft({ ...draft, files: draft.files.filter((file) => file.id !== id) })}
      generating={!ensembleRoom && generating === role} sending={sending}
      disabled={!connected || sending || (busy && generating !== role)}
      delivery={systemOnly ? "system" : privateChat ? "user" : "both"}
      submit={submit} stop={() => run(() => send("cancel", { agent: role }))}
      modelPicker={settings && <ModelPicker role={role} settings={settings} t={t} disabled={busy} label={t("select_model")}
        select={(model) => run(() => {
          const agents = { ...(settings.agents || {}) };
          if (model) agents[role] = model;
          else delete agents[role];
          return saveSettings({ ...settings, agents });
        })} />} />
  </div>;

  return <div className="app-layout">
    <button aria-label={t("close")} className="scrim" type="button" data-open={sidebar && compact}
      inert={!sidebar || !compact} onClick={closeLibrary} />
    <Sidebar workspace={workspace} visible={sidebar} compact={compact} close={closeLibrary} select={select} createPreset={createPreset}
      busy={navigationBusy} setupPending={setupPending}
      startConversation={startConversation}
      roomActions={roomActions} run={run} t={t} />
    <main className="main" inert={sidebar && compact}>
      <header className="toolbar" data-home={home}>
        <IconButton icon="menu" className="mobile-only" ref={menuButton} label={t("open_menu")}
          aria-expanded={sidebar} aria-controls="library" disabled={role === "preset" && presetWorking} onClick={() => setSidebar(true)} />
        <div className="toolbar-title">
          {role !== "chat" && <IconButton icon="arrow_back" className="directional" label={t("back")}
            disabled={role === "preset" && presetExitLocked} onClick={() => setRole("chat")} />}
          {role === "chat" && conversation && <IconButton icon="arrow_back" className="directional" label={t("back")} disabled={busy}
            onClick={() => openScene(privateChat ? workspace.returnConversation : "")} />}
          {!home && <h1>{canRename ? <button type="button" className="toolbar-rename" title={t("rename")} disabled={busy}
            onClick={rename}>
            <span className="choice-label" dir="auto">{title}</span><Icon name="edit" />
          </button> : privateChat ? <span className="ensemble-private-title">
            <small>{t("ensemble_private_messages")}</small><bdi className="choice-label">{title}</bdi>
          </span> : title}</h1>}
        </div>
        <div className="toolbar-actions">
          {privateChat && <IconButton icon="visibility" label={t("ensemble_member_trace")}
            onClick={() => setDialog({ type: "ensembleTrace", chat, participant: privateParticipant })} />}
          <IconButton icon="settings" label={t("settings")} disabled={!settings} onClick={() => show("settings")} />
          {preset && <ActionMenu label={t("more_options")} groups={conversationActions} />}
        </div>
      </header>
      {ensembleRoom && conversation && !privateChat && <nav className="ensemble-bar" aria-label={t("conversations")}>
        {workspace.ensemble.map((room) => <button type="button" className="chip" key={room.id} disabled={busy}
          aria-pressed={conversation === room.id} onClick={() => openScene(room.id)}>
          <bdi className="choice-label">{room.name || t("chat")}</bdi>
        </button>)}
      </nav>}
      {background && <img className={"workspace-background" + (!home && config.appearance.background_blur !== false ? " blurred" : "")}
        aria-hidden="true" alt="" src={assetUrl(background, { preset }, { mode: "background", inline: "1" })}
        onLoad={(event) => { if (!explicitAccent) setSampledAccent({ background, preset, color: backgroundAccent(event.currentTarget) }); }} />}
      <div className="chat-body">
        {role === "preset" && presetRunning && <p className="preset-working" role="status">{t("running")}</p>}
        {systemOnly && <div className="ensemble-ooc-inset"><OutOfStoryNotice t={t} /></div>}
        {!connected || !workspace.ready ? <p className="empty" role="status">{t(connected ? "running" : "connection_failed")}</p>
          : home ? <PresetHome key={preset} name={preset ? config.name || t("preset") : ""}
            startLabel={requiresStart ? t(roomPreset ? "new_room" : "new_chat") : undefined}
            busy={navigationBusy} {...{ t, createPreset, openPreset, startConversation }} />
          : ensembleRoom && !conversation ? <EnsembleOverview scenes={workspace.ensemble} activity={workspace.ensembleActivity}
            open={openScene} disabled={navigationBusy} t={t} />
          : preset && (emptyAgent ? <section className="agent-empty">
            {!presetWorking && <><span className="welcome-emblem"><Icon name={role === "env" ? "auto_awesome" : "edit"} /></span>
              <h2>{t("describe_changes")}</h2></>}
          </section> : <Transcript key={transcriptKey} blocks={workspace.panes[pane]} contextKey={transcriptKey} history={history} pane={pane}
            addHistory={addHistory} onHistoryError={onError} quoteReply={canQuote ? quoteReply : undefined} {...{ scope, locale, dark, theme,
            expanded: preferences.thoughts, busy, t, branch, openAction, runExport }} />)}
        {ensembleRoom && !privateChat ? <footer className="ensemble-footer" data-composer={canCompose}>
          {connected && conversation && <RoomThinking people={Object.values(workspace.ensembleActivity[conversation] || {})} t={t} />}
          {composer}
          <PrivateMessagesFab extended={!canCompose} disabled={!connected || navigationBusy || !workspace.participants.length}
            open={() => setDialog({ type: "ensembleRoster", chat })} buttonRef={privateFab} t={t} />
        </footer> : composer}
      </div>
    </main>

    <Surface>{notice && <Notice key={notice} message={notice} close={() => setNotice("")} t={t} />}</Surface>
    <Surface>{workspace.roomLoading && <RoomLoading key={workspace.roomLoading.room}
      loading={workspace.roomLoading} title={loadingPreset?.name} stop={() => run(() => send("cancel"))} t={t} />}</Surface>
    <Surface>{link && <Modal title={t("open_menu")} close={() => setLink("")} t={t}>
      <a href={link} target="_blank" rel="noreferrer" onClick={() => setLink("")}>{link}</a>
    </Modal>}</Surface>
    <Surface>{dialog && <>
      {dialog?.type === "ensembleRoster" && dialog.chat === chat && <EnsembleRoster people={workspace.participants} inStory={privateInStory}
        open={openPrivateChat} t={t} close={(restoreFocus) => {
          closeDialog();
          if (restoreFocus) requestAnimationFrame(() => privateFab.current?.focus({ preventScroll: true }));
        }} />}
      {dialog?.type === "ensembleTrace" && dialog.chat === chat && dialog.participant === privateParticipant &&
        <EnsembleTraceDialog participant={privateParticipant} active={workspace.ensemble.some((scene) =>
          Object.hasOwn(workspace.ensembleActivity[scene.id] || {}, privateParticipant))}
          expanded={preferences.thoughts} close={closeDialog} {...{ scope, locale, dark, theme, t }} />}
      {dialog?.type === "settings" && settings && <Settings {...{ settings, workspace, preferences, locale, t }}
        setPreferences={savePreferences} close={closeDialog} update={saveSettings}
        renderPlugins={(close) => <Plugins {...{ workspace, locale, t, close }} />} />}
      {dialog?.type === "memory" && <Memory {...{ workspace, role, t, run }} close={closeDialog} />}
      {dialog?.type === "text" && <TextDialog key={dialog.title} {...{ dialog, t, run }}
        close={dialog.returnTo ? () => show(dialog.returnTo) : closeDialog} />}
      {dialog?.type === "confirm" && <Confirm {...{ dialog, t, run }} close={closeDialog} />}
      {dialog?.type === "contextSettings" && <Modal title={contextSettingsTitle} t={t} close={closeDialog}>
        <div className="setting-group">
          <button type="button" className="settings-row settings-link" disabled={busy} onClick={editPrompt}>
            <Icon name="edit" /><span className="setting-label">{t("system_prompt")}</span>
            <span className="directional"><Icon name="chevron_right" /></span>
          </button>
          <SettingToggle label={t("agentic_search")} detail={t("agentic_search_hint")}
            disabled={busy} checked={config.agentic_search !== false}
            onChange={(enabled) => run(() => send("save_search_mode", context({ enabled })))} />
        </div>
        {chat && role === "env" && <button type="button" className="settings-row settings-link destructive" disabled={busy} onClick={() => setDialog({
          type: "confirm", title: t("reset_world"), message: t("reset_world_question"), label: t("reset"), danger: true,
          submit: () => send("reset_world", context()),
        })}><Icon name="delete" />{t("reset_world")}</button>}
      </Modal>}
      {dialog?.type === "presetManagement" && <Modal title={t("preset_management")} t={t} close={closeDialog}>
        <div className="setting-group">
          {["preset", "save"].map((type) => <button type="button" key={type} className="settings-row settings-link" disabled={busy}
            onClick={() => { exportType(type, { name: config.name || "MiniRP" }); closeDialog(); }}>
            <Icon name="download" /><span className="setting-label">{t("export_" + type)}</span>
          </button>)}
        </div>
        <button type="button" className="settings-row settings-link destructive" disabled={busy}
          onClick={() => confirmDelete({ id: preset, name: config.name || t("preset") }, "preset")}>
          <Icon name="delete" />{t("delete_preset")}
        </button>
      </Modal>}
    </>}</Surface>
    {Object.values(workspace.interactions).map((item) => <WorkspaceInteraction key={item.id} {...{ item, t, run, theme, dark }} />)}
  </div>;
}

createRoot(document.getElementById("app")).render(<App />);
