import { useLayoutEffect, useRef, useState } from "react";
import { languageName, locales } from "./i18n.js";
import { send } from "./api.js";
import { Confirm, Icon, IconButton, Modal, SaveBar, Select, Surface } from "./ui.jsx";
import { Models } from "./ModelSettings.jsx";
import { PluginMarkdown, PluginSetting, settingTitle } from "./WorkspaceTools.jsx";

const protocols = {
  openai: "OpenAI Chat Completions", responses: "OpenAI Responses", anthropic: "Anthropic Messages",
  gemini: "Gemini", ollama: "Ollama",
};
const empty = Object.freeze({});

function SettingsLink({ icon, label, detail, route, onClick }) {
  return <button type="button" className="settings-row settings-link" data-route={route} onClick={onClick}>
    {icon && <Icon name={icon} />}
    <span className="setting-label">
      <span>{label}</span>
      {detail && <small dir="auto">{detail}</small>}
    </span>
    <span className="directional"><Icon name="chevron_right" /></span>
  </button>;
}

function SettingChoice({ label, value, onChange, children }) {
  return <label className="settings-row settings-control">
    <span>{label}</span>
    <Select aria-label={label} value={value} onChange={(event) => onChange(event.target.value)}>{children}</Select>
  </label>;
}

function Disclosure({ label, children }) {
  return <details className="settings-disclosure">
    <summary className="settings-row"><span>{label}</span><Icon name="expand_more" /></summary>
    <div className="settings-disclosure-content">{children}</div>
  </details>;
}

function Language({ preferences, change, t }) {
  return <div className="setting-group">
    <SettingChoice label={t("language")} value={preferences.locale} onChange={(locale) => change({ ...preferences, locale })}>
      <option value="">{t("system_default")}</option>
      {locales.map((locale) => <option key={locale} value={locale} lang={locale}>{languageName(locale)}</option>)}
    </SettingChoice>
  </div>;
}

function ProviderEditor({ value, change, templates, t, submit, remove, saving }) {
  const [showKey, setShowKey] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [model, setModel] = useState("");
  const [failure, setFailure] = useState("");
  const patch = (key, field) => change((current) => ({ ...current, [key]: field }));
  async function fetchModels() {
    setFailure("");
    setFetching(true);
    try {
      const { items = [] } = await send("models", { provider: value });
      change((current) => {
        if (current.url !== value.url || current.protocol !== value.protocol || current.key !== value.key) return current;
        return { ...current, models: [...new Set([...current.models, ...items.map((item) => item.id).filter(Boolean)])] };
      });
    } catch (error) { setFailure(error.message || t("model_fetch_failed")); }
    finally { setFetching(false); }
  }
  return <form className="settings-form" onSubmit={(event) => { event.preventDefault(); submit(); }}>
    <fieldset className="settings-fields stack" disabled={saving || fetching}>
      {templates.length > 0 && <label>{t("provider_type")}
        <Select value="" onChange={(event) => {
          const template = templates[event.target.value];
          if (template) change((current) => ({
            ...current, name: template.name, protocol: template.protocol, url: template.url,
            key: current.url === template.url ? current.key : "",
          }));
        }}>
          <option value="">{t("custom_provider")}</option>
          {templates.map((item, index) => <option key={index} value={index}>{item.name || t("custom_provider")}</option>)}
        </Select>
      </label>}
      <div className="field-grid">
        <label>{t("provider_name")}<input value={value.name} onChange={(event) => patch("name", event.target.value)} /></label>
        <label>{t("protocol")}
          <Select value={value.protocol} onChange={(event) => patch("protocol", event.target.value)}>
            {Object.entries(protocols).map(([id, name]) => <option key={id} value={id}>{name}</option>)}
          </Select>
        </label>
      </div>
      <label>{t("base_url")}<input dir="ltr" type="url" required value={value.url} onChange={(event) => patch("url", event.target.value)} /></label>
      <label>{t("api_key")}<span className="inline-input credential-input">
        <input dir="ltr" type={showKey ? "text" : "password"} value={value.key} autoComplete="off" onChange={(event) => patch("key", event.target.value)} />
        <IconButton icon={showKey ? "visibility_off" : "visibility"} label={t(showKey ? "hide_key" : "show_key")}
          aria-pressed={showKey} onClick={() => setShowKey(!showKey)} />
      </span></label>
    </fieldset>
    <Disclosure label={t("manage_models")}>
      <div className="stack">
        <button type="button" className="tonal" disabled={fetching || saving || !value.url} onClick={fetchModels}>
          <Icon name="download" />{t(fetching ? "running" : "fetch_models")}
        </button>
        {failure && <p className="setting-error" role="alert">{failure}</p>}
        <div className="inline-input">
          <label>{t("model_id")}<input dir="ltr" value={model} onChange={(event) => setModel(event.target.value)} /></label>
          <IconButton icon="add" label={t("add_saved_model")} disabled={!model.trim() || saving} onClick={() => {
            change((current) => ({ ...current, models: [...new Set([...current.models, model.trim()])] }));
            setModel("");
          }} />
        </div>
        {value.models.length > 0 && <ul className="settings-list provider-models">
          {value.models.map((entry) => <li key={entry}><span dir="auto">{entry}</span>
            <IconButton icon="close" label={t("delete") + " " + entry} disabled={saving}
              onClick={() => change((current) => ({ ...current, models: current.models.filter((item) => item !== entry) }))} />
          </li>)}
        </ul>}
      </div>
    </Disclosure>
    {remove && <button type="button" className="text-button destructive" disabled={saving} onClick={remove}>{t("delete_provider")}</button>}
    <SaveBar t={t} saving={saving || fetching} />
  </form>;
}

const services = { deepseek: "DeepSeek", tavily: "Tavily", exa: "Exa", brave: "Brave Search" };
function Research({ value, defaults, change, submit, t, saving, changed, backend }) {
  const search = value.search_provider || defaults.search_provider || "deepseek";
  const fetch = value.fetch_provider || defaults.fetch_provider || "deepseek";
  const activeServices = [...new Set(backend === "local" ? [search] : [search, fetch])];
  const keys = { ...defaults.research_keys, ...value.research_keys };
  const patch = (key, field) => change({ ...value, [key]: field });
  const options = (names) => names.map((name) => <option value={name} key={name}>{services[name]}</option>);
  return <form className="settings-form" onSubmit={(event) => { event.preventDefault(); submit(); }}>
    <div className="setting-group">
      <SettingChoice label={t("research_search_provider")} value={search} onChange={(field) => patch("search_provider", field)}>
        {options(Object.keys(services))}
      </SettingChoice>
      {backend !== "local" && <SettingChoice label={t("research_fetch_provider")} value={fetch} onChange={(field) => patch("fetch_provider", field)}>
        {options(["deepseek", "tavily", "exa"])}
      </SettingChoice>}
    </div>
    <div className="settings-fields stack">
      {activeServices.map((service) => <label key={service}>{services[service]} · {t("api_key")}
        <input dir="ltr" type="password" autoComplete="off"
          value={keys[service] ?? (service === "deepseek" ? value.search_key ?? defaults.search_key ?? "" : "")}
          onChange={(event) => {
            const key = event.target.value;
            change({
              ...value, ...(service === "deepseek" ? { search_key: key } : {}),
              research_keys: { ...keys, [service]: key },
            });
          }} />
      </label>)}
    </div>
    <SaveBar {...{ t, saving, changed }} />
  </form>;
}

const withoutProvider = (agents, id) =>
  Object.fromEntries(Object.entries(agents).filter(([, model]) => model.provider !== id));

export function Settings({ settings, workspace, preferences, setPreferences, update, locale, t, close, renderPlugins }) {
  const [route, setRoute] = useState([{ kind: "home", key: "home" }]);
  const [direction, setDirection] = useState();
  const [plugins, setPlugins] = useState(false);
  const [draftProvider, setDraftProvider] = useState(null);
  const [draftResearch, setDraftResearch] = useState({});
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState("");
  const [confirm, setConfirm] = useState(null);
  const pane = useRef(null);
  const returnFocus = useRef(null);
  const scrollPositions = useRef(new Map());
  const { kind, key, item } = route.at(-1);
  const { templates, preset } = workspace;
  const providers = settings.providers || [];
  const agents = settings.agents || empty;
  const research = settings.research || empty;
  const backend = research.fetch_backend || settings.default_research?.fetch_backend || "api_then_local";
  const existing = providers.some((item) => item.id === draftProvider?.id);
  const title = item ? settingTitle(item, locale, t) : t(kind === "provider" ? existing ? "edit_provider" : "add_provider" : "settings");
  const nodes = kind === "home" ? workspace.pluginSettings.filter((node) => node.plugin === "base") : item?.items;

  useLayoutEffect(() => {
    if (key === "home" && returnFocus.current === null) return;
    const root = pane.current;
    const origin = Array.from(root.querySelectorAll("[data-route]")).find((element) => element.dataset.route === returnFocus.current);
    (origin || root).focus({ preventScroll: true });
    root.closest(".dialog-content").scrollTop = returnFocus.current === null
      ? 0 : scrollPositions.current.get(key) || 0;
    returnFocus.current = null;
  }, [key]);

  function forward(next) {
    scrollPositions.current.set(key, pane.current.closest(".dialog-content").scrollTop);
    setDirection("forward");
    setProblem("");
    setRoute((current) => [...current, next]);
  }

  function enter(kind, item) {
    forward({ kind, key: item.key, item });
  }

  function back() {
    returnFocus.current = key;
    setDirection("back");
    setProblem("");
    setConfirm(null);
    setRoute((current) => current.slice(0, -1));
  }

  async function work(operation) {
    if (saving) return;
    setSaving(true);
    setProblem("");
    try { await operation(); }
    catch (error) { setProblem(error.message); }
    finally { setSaving(false); }
  }

  function persist(next, returnToParent = true) {
    return work(async () => {
      await update(next);
      if (returnToParent) back();
    });
  }

  function beginProvider(provider) {
    setDraftProvider(provider
      ? { ...provider, models: provider.models || [] }
      : { id: crypto.randomUUID(), name: "", url: "", key: "", protocol: "openai", models: [] });
    forward({ kind: "provider", key: provider?.id || "new-provider" });
  }

  function updateProvider(transform) {
    setDraftProvider((current) => current?.id === draftProvider.id ? transform(current) : current);
  }

  const declaredSettings = {
    t, locale, preset, work, disabled: saving,
    openPage: (keys) => work(async () => enter("declared", (await send("plugin_page", { ...keys, locale })).item)),
    update: (keys, value) => work(async () => {
      await send("plugin_setting", { ...keys, value });
      if (kind !== "declared") return;
      const refreshed = (await send("plugin_page", { id: item.plugin, key, locale })).item;
      setRoute((current) => [...current.slice(0, -1), { kind, key, item: refreshed }]);
    }),
    bindings: {
      theme: { value: preferences.theme, set: (theme) => setPreferences({ ...preferences, theme }) },
      thoughts: { value: preferences.thoughts, set: (thoughts) => setPreferences({ ...preferences, thoughts }) },
      fetch: { value: backend, set: (fetch_backend) => persist({ ...settings, research: { ...research, fetch_backend } }, false) },
    },
    hostActions: {
      providers: (node) => enter("providers", node),
      models: (node) => enter("models", node),
      "research-services": (node) => { setDraftResearch(research); enter("research", node); },
      language: (node) => enter("language", node),
      plugins: () => setPlugins(true),
    },
  };

  return <Modal title={title} t={t} close={() => { if (!saving) close(); }}
    back={route.length > 1 ? () => { if (!saving) back(); } : undefined} closeDisabled={saving}
    className="settings-modal" pageKey={key} pageDirection={direction}>
    <section ref={pane} tabIndex={-1} aria-label={title} className="settings-page page-motion" key={key}>
      {problem && <p className="setting-error" role="alert">{problem}</p>}
      {kind === "declared" && <PluginMarkdown item={item} preset={preset} />}
      {nodes?.length > 0 && <div className="setting-group">
        {nodes.map((node) => <PluginSetting key={node.key} item={node} settings={declaredSettings} />)}
      </div>}
      {kind === "home" && !nodes.length && <p role="status" className="settings-caption">{t("running")}</p>}
      {kind === "language" && <Language {...{ t, preferences }} change={setPreferences} />}
      {kind === "models" && <Models {...{ settings, t, saving }} addProvider={() => beginProvider(null)}
        submit={(draft) => persist({ ...settings, ...draft })} />}
      {kind === "providers" && <>
        {providers.length > 0 ? <div className="setting-group">
          {providers.map((provider) => <SettingsLink key={provider.id} icon="tune" label={provider.name || t("custom_provider")}
            detail={provider.url} route={provider.id}
            onClick={() => beginProvider(provider)} />)}
        </div> : <p className="settings-caption">{t("no_provider_configured")}</p>}
        <div className="settings-save"><button type="button" className="tonal" data-route="new-provider" onClick={() => beginProvider(null)}>
          <Icon name="add" />{t("add_provider")}
        </button></div>
      </>}
      {kind === "provider" && <ProviderEditor key={draftProvider.id} value={draftProvider} change={updateProvider}
        {...{ t, templates, saving }}
        submit={() => persist({
          ...settings, providers: existing ? providers.map((item) => item.id === draftProvider.id ? draftProvider : item) : [...providers, draftProvider],
        })}
        remove={existing ? () => setConfirm({
          title: t("delete_named", draftProvider.name || draftProvider.url), danger: true,
          submit: async () => {
            await update({ ...settings, providers: providers.filter((item) => item.id !== draftProvider.id),
              agents: withoutProvider(agents, draftProvider.id) });
            back();
          },
        }) : undefined} />}
      {kind === "research" && <Research value={draftResearch} defaults={settings.default_research || {}}
        change={setDraftResearch} {...{ t, saving }} changed={draftResearch !== research}
        backend={backend} submit={() => persist({ ...settings, research: draftResearch })} />}
    </section>
    <Surface>{confirm && <Confirm dialog={confirm} close={() => setConfirm(null)} t={t} run={(action) => work(async () => {
      setConfirm(null);
      await action();
    })} />}</Surface>
    <Surface>{plugins && renderPlugins(() => setPlugins(false))}</Surface>
  </Modal>;
}
