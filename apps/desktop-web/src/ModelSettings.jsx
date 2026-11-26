import { useCallback, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { send } from "./api.js";
import { Icon, IconButton, Modal, SaveBar, Select, SettingToggle, Surface } from "./ui.jsx";

const empty = Object.freeze({});
const primaryRoles = { default: "system_default", chat: "chat", title: "title_model",
  image_description: "image_description_model", audio_description: "audio_description_model" };
const descriptionKinds = { image_description: "image", audio_description: "audio" };
const agentRoles = { env: "world", preset: "preset", search: "search_agent", fetch: "fetch_agent", plugin: "plugin_agent" };
const choiceKey = (item) => item ? JSON.stringify([item.provider, item.model]) : "";
const inheritedModel = (role) => descriptionKinds[role] ? "media_description_off" : role === "plugin" ? "use_preset_model"
  : role === "chat" || role === "default" ? "system_default" : "use_chat_model";
const providerName = (provider) => provider.name || provider.url;

function modelChoices(settings) {
  const legacy = settings.environment;
  const choices = legacy?.model ? [{ provider: "environment", model: legacy.model, name: legacy.model,
    source: legacy.name, protocol: legacy.protocol, modalities: legacy.input_modalities }] : [];
  for (const provider of settings.providers || [])
    for (const model of new Set(provider.models || []))
      choices.push({ provider: provider.id, model, name: model, source: providerName(provider), protocol: provider.protocol,
        modalities: provider.model_options?.[model]?.input_modalities });
  return choices;
}

function withCurrent(choices, current, providers) {
  if (!current || choices.some((item) => choiceKey(item) === choiceKey(current))) return choices;
  const provider = providers.find((item) => item.id === current.provider);
  return [{ ...current, name: current.model, source: provider ? providerName(provider) : current.provider }, ...choices];
}

function includesQuery(query, ...values) {
  const terms = query.trim().toLocaleLowerCase().split(/\s+/);
  const haystack = values.filter(Boolean).join(" ").toLocaleLowerCase();
  return terms.every((term) => haystack.includes(term));
}

export function ModelPicker({ role, settings, select, t, disabled = false, label }) {
  const current = settings.agents?.[role];
  const choices = modelChoices(settings);
  return <label className="model-picker">
    <span className="sr-only">{label}</span>
    <Select value={choiceKey(current)} disabled={disabled} onChange={(event) => {
      const choice = choices.find((item) => choiceKey(item) === event.target.value);
      select(choice ? { provider: choice.provider, model: choice.model } : null);
    }}>
      <option value="">{t(inheritedModel(role))}</option>
      {current && !choices.some((item) => choiceKey(item) === choiceKey(current)) &&
        <option value={choiceKey(current)}>{current.model}</option>}
      {choices.map((item) => <option key={choiceKey(item)} value={choiceKey(item)}>
        {item.provider === "environment" ? item.model : [item.source, item.model].filter(Boolean).join(" · ")}
      </option>)}
    </Select>
  </label>;
}

function ModelAssignment({ role, label, choices, current, t, choose, close, manage }) {
  const [query, setQuery] = useState("");
  const search = useRef(null);
  const list = useRef(null);
  const selected = choiceKey(current);
  const filtered = choices.filter((item) => includesQuery(query, item.name, item.model, item.source));
  useEffect(() => { search.current.focus({ preventScroll: true }); }, []);

  function navigate(event) {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    const entries = Array.from(list.current.querySelectorAll("button"));
    const index = entries.indexOf(event.target);
    const next = event.key === "ArrowDown" ? index + 1 : index - 1;
    if (next < 0) search.current.focus();
    else entries[Math.min(next, entries.length - 1)].focus();
  }

  function row(item) {
    const key = choiceKey(item);
    return <button key={key} type="button" className="settings-row model-choice" aria-pressed={key === selected}
      onClick={() => choose(item)}>
      <span className="setting-label">
        <span dir="auto">{item?.name || t(inheritedModel(role))}</span>
        {item && <small dir="auto">{[item.source, item.name !== item.model ? item.model : ""].filter(Boolean).join(" · ")}</small>}
      </span>
      {key === selected && <Icon name="check" />}
    </button>;
  }
  return <Modal title={label} close={close} className="model-dialog" t={t}>
    {descriptionKinds[role] && <p className="settings-caption">{t(descriptionKinds[role] + "_description_help")}</p>}
    <label>{t("search_models")}<input ref={search} type="search" value={query}
      onChange={(event) => setQuery(event.target.value)} onKeyDown={navigate} /></label>
    <div className="model-results setting-group" role="group" aria-label={t("available_models")} ref={list} onKeyDown={navigate}>
      {row(null)}
      {filtered.map(row)}
      {!filtered.length && <p className="settings-caption" role="status">{t("no_models")}</p>}
    </div>
    {!choices.length && <button type="button" className="text-button" onClick={manage}>{t("manage_models")}</button>}
  </Modal>;
}

function ModelOptionFields({ options, change, t, efforts }) {
  const patch = (key, value) => {
    const next = { ...options };
    if (value === "") delete next[key];
    else next[key] = value;
    change(next);
  };
  return <>
    <div className="field-grid">
      {[["context_window", "context_size"], ["max_output", "max_output"],
        ["compact_at_percent", "compaction_threshold"], ["background_compact_at_percent", "background_compaction_threshold"],
      ].map(([key, label]) => <label key={key}>{t(label)}
        <input type="number" min="1" max={key.endsWith("_percent") ? 100 : undefined} value={options[key] ?? ""}
          onChange={(event) => patch(key, event.target.value === "" ? "" : Number(event.target.value))} />
      </label>)}
      <label>{t("thinking_effort")}
        {efforts?.length ? <Select value={options.reasoning_effort || ""} onChange={(event) => patch("reasoning_effort", event.target.value)}>
          <option value="">{t("thinking_effort_default")}</option>
          {options.reasoning_effort && !efforts.includes(options.reasoning_effort) &&
            <option value={options.reasoning_effort}>{options.reasoning_effort}</option>}
          {efforts.map((level) => <option key={level} value={level}>{level}</option>)}
        </Select> : <input value={options.reasoning_effort || ""} onChange={(event) => patch("reasoning_effort", event.target.value)} />}
      </label>
    </div>
    <div className="setting-group">
      <SettingToggle label={t("thinking")} checked={Boolean(options.thinking)} onChange={(value) => patch("thinking", value)} />
      <SettingToggle label={t("describe_images_for_model")} checked={Boolean(options.describe_images)}
        onChange={(value) => patch("describe_images", value)} />
      <SettingToggle label={t("describe_audio_for_model")} checked={Boolean(options.describe_audio)}
        onChange={(value) => patch("describe_audio", value)} />
      <SettingToggle label={t("background_compaction")} checked={options.background_compaction !== false}
        onChange={(value) => patch("background_compaction", value)} />
    </div>
  </>;
}

function reportedEfforts(model) {
  const levels = model?.effort_levels || model?.effort?.supported_levels || model?.thinking?.values;
  if (Array.isArray(levels)) return [...new Set(levels.filter((value) => typeof value === "string" && value.trim()))];
  const capabilities = model?.capabilities?.effort;
  return capabilities?.supported ? Object.keys(capabilities).filter((name) => capabilities[name]?.supported) : [];
}

function ModelOptionsDialog({ model, initial, t, close, save, provider, metadata }) {
  const [options, setOptions] = useState(initial);
  const [fetchedEfforts, setFetchedEfforts] = useState([]);
  const advertised = reportedEfforts(metadata);
  useEffect(() => {
    if (provider.protocol !== "ollama" || advertised.length) return;
    const abort = new AbortController();
    send("model_effort_levels", { provider: { ...provider, model } }, abort.signal)
      .then(({ effort_levels }) => {
        if (!abort.signal.aborted) setFetchedEfforts(reportedEfforts({ effort_levels }));
      }).catch(() => {});
    return () => abort.abort();
  }, [provider, model, advertised.length]);
  return <Modal title={t("model_settings")} close={close} className="model-dialog" t={t}>
    <form className="settings-form" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      save(options);
    }}>
      <h3 className="model-identifier" dir="auto">{model}</h3>
      <ModelOptionFields {...{ t, options }} change={setOptions} efforts={advertised.length ? advertised : fetchedEfforts} />
      <SaveBar t={t} changed={options !== initial} />
    </form>
  </Modal>;
}

function ProviderCatalog({ provider, t, saving, setEnabled, edit, searchInput, reportCatalog }) {
  const [query, setQuery] = useState("");
  const [id, setId] = useState("");
  const [reload, setReload] = useState(0);
  const [catalog, setCatalog] = useState({ items: [], loading: true, error: "" });
  const { id: providerId, protocol, url, key } = provider;
  useEffect(() => {
    const controller = new AbortController();
    setCatalog((current) => ({ ...current, loading: true, error: "" }));
    send("models", { provider: { id: providerId, protocol, url, key } }, controller.signal).then(({ items = [] }) => {
      if (controller.signal.aborted) return;
      setCatalog({ items, loading: false, error: "" });
      reportCatalog(providerId, items);
    }).catch((error) => {
      if (!controller.signal.aborted) setCatalog((current) => ({ ...current, loading: false, error: error.message || t("model_fetch_failed") }));
    });
    return () => controller.abort();
  }, [providerId, protocol, url, key, reload, reportCatalog, t]);

  const saved = new Set(provider.models || []);
  const models = new Map(catalog.items.map((item) => [item.id, item]));
  for (const entry of saved) if (!models.has(entry)) models.set(entry, { id: entry, name: entry });
  const filtered = [...models.values()].filter((entry) => includesQuery(query, entry.id, entry.name));
  function addModel() {
    const model = id.trim();
    if (!model || saved.has(model) || saving) return;
    setEnabled(model, true);
    setQuery("");
    setId("");
  }
  return <>
    <div className="model-catalog-actions">
      <label>{t("search_models")}<input ref={searchInput} type="search" value={query} onChange={(event) => setQuery(event.target.value)}
        onKeyDown={(event) => { if (event.key === "Enter" && !event.nativeEvent.isComposing) event.preventDefault(); }} /></label>
      <button type="button" className="tonal" disabled={catalog.loading || saving} onClick={() => setReload((current) => current + 1)}>
        <Icon name="download" />{t("refresh_models")}
      </button>
    </div>
    {catalog.loading && <progress className="browser-runtime-progress" aria-label={t("running")} />}
    {catalog.error && <p className="setting-error" role="alert">{catalog.error}</p>}
    <div className="inline-input model-add">
      <label>{t("model_id")}<input dir="ltr" value={id} onChange={(event) => setId(event.target.value)}
        onKeyDown={(event) => {
          if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
          event.preventDefault();
          addModel();
        }} /></label>
      <button type="button" className="text-button" disabled={!id.trim() || saved.has(id.trim()) || saving} onClick={addModel}>
        <Icon name="add" />{t("add_saved_model")}
      </button>
    </div>
    {filtered.length ? <div className="model-results setting-group" role="group" aria-label={t("available_models")}>
      {filtered.map((item) => <div className="model-catalog-row" key={item.id}>
        <SettingToggle label={item.name || item.id} detail={item.name !== item.id ? item.id : undefined} disabled={saving}
          checked={saved.has(item.id)} onChange={(enabled) => setEnabled(item.id, enabled, item.input_modalities)} />
        {saved.has(item.id) && <IconButton icon="settings" label={t("model_settings") + " · " + (item.name || item.id)}
          onClick={() => edit(item.id)} disabled={saving} />}
      </div>)}
    </div> : !catalog.loading && !catalog.error && <p className="settings-caption" role="status">{t("no_models")}</p>}
  </>;
}

export function Models({ settings, t, saving, submit, addProvider }) {
  const [draft, setDraft] = useState({ agents: settings.agents || empty, providers: settings.providers || [] });
  const [providerId, setProviderId] = useState("");
  const [role, setRole] = useState("");
  const [editing, setEditing] = useState("");
  const [catalogs, setCatalogs] = useState(() => new Map());
  const assignmentId = useId();
  const reportCatalog = useCallback((id, items) => setCatalogs((current) => new Map(current)
    .set(id, new Map(items.map((item) => [item.id, item])))), []);
  const catalogInput = useRef(null);
  const catalogSection = useRef(null);
  const provider = draft.providers.find((item) => item.id === providerId) || draft.providers[0];
  const choices = modelChoices({ ...settings, ...draft }).map((item) => ({
    ...item, name: catalogs.get(item.provider)?.get(item.model)?.name || item.name,
    modalities: catalogs.get(item.provider)?.get(item.model)?.input_modalities ?? item.modalities,
  }));
  const changed = draft.agents !== (settings.agents || empty) || draft.providers !== (settings.providers || draft.providers);

  function assign(role, model, modalities) {
    setDraft((previous) => {
      const agents = { ...previous.agents };
      if (model) agents[role] = model;
      else delete agents[role];
      const providers = !model || !Array.isArray(modalities) ? previous.providers : previous.providers.map((item) => {
        if (item.id !== model.provider) return item;
        const options = item.model_options || empty;
        return { ...item, model_options: { ...options, [model.model]: { ...options[model.model], input_modalities: modalities } } };
      });
      return { agents, providers };
    });
  }

  function updateProvider(transform, removeModel) {
    setDraft((previous) => ({
      providers: previous.providers.map((item) => item.id === provider.id ? transform(item) : item),
      agents: removeModel ? Object.fromEntries(Object.entries(previous.agents).filter(([, item]) =>
        item.provider !== provider.id || item.model !== removeModel)) : previous.agents,
    }));
  }

  function modelRow([id, label]) {
    const current = draft.agents[id];
    const choice = choices.find((item) => choiceKey(item) === choiceKey(current));
    const value = choice?.name || current?.model || t(inheritedModel(id));
    const valueId = assignmentId + "-" + id;
    return <section className="model-setting" key={id}>
      <button type="button" className="settings-row settings-link model-assignment" aria-haspopup="dialog" disabled={saving}
        aria-label={t(label)} aria-describedby={valueId}
        onClick={() => setRole(id)}>
        <span className="setting-label"><span>{t(label)}</span><small className="model-assignment-value" dir="auto"
          id={valueId} title={value}>{value}</small></span>
        <span className="directional"><Icon name="chevron_right" /></span>
      </button>
      {current && !descriptionKinds[id] && (current.options || current.provider === "environment") && <details className="settings-disclosure">
        <summary className="settings-row"><span>{t("model_settings")}</span><Icon name="expand_more" /></summary>
        <div className="settings-disclosure-content">
          <ModelOptionFields t={t} options={current.options || empty} change={(options) => assign(id, { ...current, options })} />
        </div>
      </details>}
    </section>;
  }

  const closeAssignment = () => setRole("");
  const closeOptions = () => setEditing("");
  const storedOptions = provider?.model_options || empty;
  const editingOptions = editing && Object.hasOwn(storedOptions, editing) ? storedOptions[editing] || empty : empty;
  const kind = descriptionKinds[role];
  const assignmentChoices = kind ? choices.filter(({ protocol, modalities }) =>
    (kind !== "audio" || !protocol || ["openai", "gemini"].includes(protocol)) &&
    (!Array.isArray(modalities) || modalities.some((value) => typeof value === "string" &&
      (value.toLowerCase() === kind || value.toLowerCase().startsWith(kind + "/"))))) : choices;
  return <form className="settings-form" onSubmit={(event) => {
    event.preventDefault();
    if (changed && !saving) submit(draft);
  }}>
    <div className="setting-group">{Object.entries(primaryRoles).map(modelRow)}</div>
    <details className="settings-disclosure">
      <summary className="settings-row"><span>{t("agent_models")}</span><Icon name="expand_more" /></summary>
      <div className="settings-disclosure-content"><div className="setting-group">{Object.entries(agentRoles).map(modelRow)}</div></div>
    </details>
    <section className="stack model-catalog" ref={catalogSection} tabIndex={-1} aria-label={t("available_models")}>
      <h3>{t("available_models")}</h3>
      {provider ? <>
        <label>{t("provider_type")}<Select value={provider.id} onChange={(event) => setProviderId(event.target.value)} disabled={saving}>
          {draft.providers.map((item) => <option key={item.id} value={item.id}>{providerName(item)}</option>)}
        </Select></label>
        <ProviderCatalog key={provider.id} {...{ provider, t, saving, reportCatalog }} searchInput={catalogInput}
          setEnabled={(model, enabled, modalities) => updateProvider((item) => ({
            ...item, models: enabled ? [...new Set([...(item.models || []), model])] : (item.models || []).filter((id) => id !== model),
            ...(enabled && Array.isArray(modalities) && { model_options: { ...item.model_options,
              [model]: { ...item.model_options?.[model], input_modalities: modalities } } }),
          }), enabled ? undefined : model)}
          edit={setEditing} />
      </> : <button type="button" className="tonal" onClick={addProvider} disabled={saving}>
        <Icon name="add" />{t("add_provider")}
      </button>}
    </section>
    <SaveBar {...{ t, saving, changed }} />
    {createPortal(<>
      <Surface>{role && <ModelAssignment key={role} role={role} label={t(primaryRoles[role] || agentRoles[role])} t={t}
        current={draft.agents[role]} choices={withCurrent(assignmentChoices, draft.agents[role], draft.providers)}
        close={closeAssignment} manage={() => {
          closeAssignment();
          catalogSection.current.scrollIntoView({ block: "nearest" });
          requestAnimationFrame(() => (catalogInput.current || catalogSection.current)?.focus());
        }} choose={(item) => {
          if (choiceKey(item) !== choiceKey(draft.agents[role]))
            assign(role, item ? { provider: item.provider, model: item.model } : null, item?.modalities);
          closeAssignment();
        }} />}</Surface>
      <Surface>{editing && <ModelOptionsDialog model={editing} initial={editingOptions} t={t}
        provider={provider} metadata={catalogs.get(provider.id)?.get(editing)}
        close={closeOptions} save={(options) => {
          updateProvider((item) => ({ ...item, model_options: { ...item.model_options, [editing]: options } }));
          closeOptions();
        }} />}</Surface>
    </>, document.getElementById("app"))}
  </form>;
}
