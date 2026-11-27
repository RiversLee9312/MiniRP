import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
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
      choices.push({ provider: provider.id, model, name: provider.model_options?.[model]?.display_name || model,
        source: providerName(provider), protocol: provider.protocol,
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

export function ComposerModelPickers({ role, settings, select, manage, t, disabled, inline, agenticSearch }) {
  const [active, setActive] = useState("");
  const preset = role === "preset";
  const roles = preset ? [role, "plugin"] : role === "chat" && agenticSearch ? [role, "search"] : [role];
  const choices = modelChoices(settings);
  const label = (agent) => t(primaryRoles[agent] || agentRoles[agent]);
  const current = settings.agents?.[active];
  return <>
    <div className="composer-models" data-preset={preset} data-inline={inline}>
      {roles.map((agent) => {
        const assigned = settings.agents?.[agent];
        const name = choices.find((item) => choiceKey(item) === choiceKey(assigned))?.name || assigned?.model || t("select_model");
        const title = label(agent);
        return <button key={agent} type="button" className="outlined composer-model-chip" disabled={disabled}
          aria-label={`${title} · ${name}`} title={name} aria-haspopup="dialog" onClick={() => setActive(agent)}>
          <span className="composer-model-label">
            {preset ? <><span>{title}</span><small dir="auto">{name}</small></> : <span>{title} · <bdi>{name}</bdi></span>}
          </span>
          <Icon name="expand_more" />
        </button>;
      })}
    </div>
    {createPortal(<Surface>{active && <ModelAssignment key={active} role={active} label={label(active)} t={t} sheet
      current={current} choices={withCurrent(choices, current, settings.providers || [])}
      close={() => setActive("")} manage={() => { setActive(""); manage(); }} choose={(choice) => {
        setActive("");
        if (choiceKey(choice) !== choiceKey(current))
          select(active, choice ? { provider: choice.provider, model: choice.model } : null);
      }} />}</Surface>, document.getElementById("app"))}
  </>;
}

function ModelAssignment({ role, label, choices, current, t, choose, close, manage, sheet = false }) {
  const [query, setQuery] = useState("");
  const search = useRef(null);
  const list = useRef(null);
  const heading = useRef(null);
  const drag = useRef(0);
  const selected = choiceKey(current);
  const filtered = choices.filter((item) => includesQuery(query, item.name, item.model, sheet ? undefined : item.source));
  useLayoutEffect(() => { (sheet ? heading : search).current?.focus({ preventScroll: true }); }, [sheet]);

  function navigate(event) {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    const entries = Array.from(list.current.querySelectorAll("button"));
    const index = entries.indexOf(event.target);
    const next = event.key === "ArrowDown" ? index + 1 : index - 1;
    if (next < 0) (search.current || heading.current)?.focus();
    else entries[Math.min(next, entries.length - 1)]?.focus();
  }

  function finishDrag(event) {
    const handle = event.currentTarget;
    if (!handle.hasPointerCapture(event.pointerId)) return;
    const modal = handle.closest("dialog");
    delete modal.dataset.dragging;
    modal.style.removeProperty("--model-sheet-offset");
    if (event.type === "pointerup" && event.clientY - drag.current > 56) close();
  }

  function row(item) {
    const key = choiceKey(item);
    return <button key={key} type="button" className="settings-row model-choice"
      {...(sheet ? { role: "radio", "aria-checked": key === selected } : { "aria-pressed": key === selected })}
      onClick={() => choose(item)}>
      <span className="setting-label">
        <span dir="auto">{item?.name || t(inheritedModel(role))}</span>
        {item && <small dir="auto">{[item.source, !sheet && item.name !== item.model ? item.model : ""].filter(Boolean).join(" · ")}</small>}
      </span>
      {sheet ? <span className="model-radio" aria-hidden="true" /> : key === selected && <Icon name="check" />}
    </button>;
  }
  return <Modal title={label} close={close} className={sheet ? "model-sheet" : "model-dialog"}
    heading={!sheet} dismissOnBackdrop={sheet} t={t}>
    {sheet && <>
      <div className="model-sheet-handle" aria-hidden="true" onPointerDown={(event) => {
        if (event.button !== 0) return;
        const handle = event.currentTarget;
        drag.current = event.clientY;
        handle.setPointerCapture(event.pointerId);
        handle.closest("dialog").dataset.dragging = "true";
      }} onPointerMove={(event) => {
        const handle = event.currentTarget;
        if (handle.hasPointerCapture(event.pointerId))
          handle.closest("dialog").style.setProperty("--model-sheet-offset", Math.max(0, event.clientY - drag.current) + "px");
      }} onPointerUp={finishDrag} onPointerCancel={finishDrag} />
      <h2 ref={heading} tabIndex={-1}>{label}</h2>
    </>}
    {descriptionKinds[role] && <p className="settings-caption">{t(descriptionKinds[role] + "_description_help")}</p>}
    {(!sheet || choices.length > 0) && <label className={sheet ? "model-sheet-search" : undefined}>
      <span>{t("search_models")}</span><input ref={search} type="search" placeholder={sheet ? " " : undefined} value={query}
        onChange={(event) => setQuery(event.target.value)} onKeyDown={navigate} /></label>}
    <div className={sheet ? "model-results model-sheet-results" : "model-results setting-group"}
      role={sheet ? "radiogroup" : "group"} aria-label={t("available_models")} ref={list} onKeyDown={navigate}>
      {row(null)}
      {filtered.map(row)}
      {!filtered.length && <p className="settings-caption" role="status">{t("no_models")}</p>}
    </div>
    {!choices.length && <button type="button" className="text-button" onClick={manage}>{t("manage_models")}</button>}
  </Modal>;
}

const defaultCompaction = 80;
const defaultBackgroundCompaction = 70;
const effortRanks = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
function highestEffort(levels) {
  return levels.every((level) => effortRanks.includes(level.toLowerCase()))
    ? [...levels].sort((a, b) => effortRanks.indexOf(a.toLowerCase()) - effortRanks.indexOf(b.toLowerCase())).at(-1)
    : levels.at(-1);
}

function validCompaction(options) {
  const limit = options.compact_at_percent ?? defaultCompaction;
  const start = options.background_compact_at_percent ?? defaultBackgroundCompaction;
  return Number.isInteger(limit) && limit >= 1 && limit <= 100 &&
    (options.background_compaction === false || Number.isInteger(start) && start >= 1 && start < limit);
}

function changedCompaction(options, saved) {
  return (options.compact_at_percent ?? defaultCompaction) !== (saved.compact_at_percent ?? defaultCompaction) ||
    (options.background_compact_at_percent ?? defaultBackgroundCompaction) !==
      (saved.background_compact_at_percent ?? defaultBackgroundCompaction) ||
    (options.background_compaction !== false) !== (saved.background_compaction !== false);
}

function ModelOptionFields({ options, change: patch, t, efforts, effortFailed, context, fallbackContext }) {
  const limit = options.compact_at_percent ?? defaultCompaction;
  const start = options.background_compact_at_percent ?? defaultBackgroundCompaction;
  const background = options.background_compaction !== false;
  const chosen = efforts?.includes(options.reasoning_effort);
  const contextHelp = useId();
  const backgroundHelp = useId();
  return <>
    <label>{t("thinking_effort")}
      {efforts?.length ? <Select value={chosen ? options.reasoning_effort : highestEffort(efforts)}
        onChange={(event) => patch("reasoning_effort", event.target.value)}>
        {efforts.map((level) => <option key={level} value={level}>{level}</option>)}
      </Select> : <small className="muted" role="status">{t(effortFailed ? "thinking_effort_load_failed"
        : efforts ? "thinking_effort_unavailable" : "thinking_effort_loading")}</small>}
      {efforts?.length > 0 && !chosen && <small className="muted">{t("thinking_effort_default")}</small>}
    </label>
    <div className="setting-group">
      <SettingToggle label={t("describe_images_for_model")} checked={Boolean(options.describe_images)}
        onChange={(value) => patch("describe_images", value)} />
      <SettingToggle label={t("describe_audio_for_model")} checked={Boolean(options.describe_audio)}
        onChange={(value) => patch("describe_audio", value)} />
    </div>
    <section className="stack">
      <h3>{t("compaction_threshold")}</h3>
      <p className="settings-caption">{context ? t("compaction_model_context", context)
        : t("compaction_fallback_context", fallbackContext || 128000)}</p>
      <label><span id={contextHelp + "-label"}>{t("compaction_context_percentage")}</span>
        <input type="number" required min="1" max="100" value={limit} aria-labelledby={contextHelp + "-label"} aria-describedby={contextHelp}
          aria-invalid={!Number.isInteger(limit) || limit < 1 || limit > 100}
          onChange={(event) => patch("compact_at_percent", event.target.value === "" ? "" : event.target.valueAsNumber)} />
        <small id={contextHelp} className="muted">{t("compaction_percentage_range")}</small>
      </label>
      <div className="setting-group"><SettingToggle label={t("background_compaction")} checked={background}
        onChange={(value) => patch("background_compaction", value)} /></div>
      {background && <label><span id={backgroundHelp + "-label"}>{t("background_compaction_threshold")}</span>
        <input type="number" required min="1" max={limit > 1 ? limit - 1 : 1} value={start}
          aria-labelledby={backgroundHelp + "-label"} aria-describedby={backgroundHelp}
          aria-invalid={!Number.isInteger(start) || start < 1 || start >= limit}
          onChange={(event) => patch("background_compact_at_percent", event.target.value === "" ? "" : event.target.valueAsNumber)} />
        <small id={backgroundHelp} className="muted">{t("background_compaction_range")}</small>
      </label>}
    </section>
  </>;
}

function reportedEfforts(model) {
  const levels = model?.effort_levels || model?.effort?.supported_levels || model?.thinking?.values;
  if (Array.isArray(levels)) return [...new Set(levels.filter((value) => typeof value === "string" && value.trim()))];
  const capabilities = model?.capabilities?.effort;
  return capabilities?.supported ? Object.keys(capabilities).filter((name) => capabilities[name]?.supported) : [];
}

export function modelMetadata(model) {
  const context = [model?.context_length, model?.context_window, model?.inputTokenLimit, model?.max_input_tokens]
    .find((value) => Number.isInteger(value) && value > 0);
  const output = [model?.outputTokenLimit, model?.max_output_tokens, model?.top_provider?.max_completion_tokens]
    .find((value) => Number.isInteger(value) && value > 0);
  return { ...(context && { context_window: context }), ...(output && { max_output: output }),
    ...(model?.name && model.name !== model.id && { display_name: model.name }),
    ...(Array.isArray(model?.input_modalities) && { input_modalities: model.input_modalities }), effort_levels: reportedEfforts(model) };
}

function ModelOptionsDialog({ model, initial, t, close, save, provider, metadata }) {
  const [options, setOptions] = useState(initial);
  const [fetchedEfforts, setFetchedEfforts] = useState(null);
  const [effortFailed, setEffortFailed] = useState(false);
  const listed = reportedEfforts(metadata);
  const advertised = listed.length || provider.protocol !== "ollama" && metadata ? listed : reportedEfforts(initial);
  const fetchDetails = provider.protocol === "ollama" && !advertised.length;
  const efforts = fetchDetails ? fetchedEfforts : advertised;
  const defaultsChanged = efforts?.length > 0 && JSON.stringify(efforts) !== JSON.stringify(initial.effort_levels);
  const effectiveOptions = { ...provider, ...options };
  const validThresholds = validCompaction(effectiveOptions);
  const canSave = validThresholds || !changedCompaction(effectiveOptions, { ...provider, ...initial });
  useEffect(() => {
    if (!fetchDetails) return;
    const abort = new AbortController();
    send("model_effort_levels", { provider: { ...provider, model } }, abort.signal)
      .then(({ effort_levels }) => {
        if (!abort.signal.aborted) setFetchedEfforts(reportedEfforts({ effort_levels }));
      }).catch(() => { if (!abort.signal.aborted) setEffortFailed(true); });
    return () => abort.abort();
  }, [provider, model, fetchDetails]);
  return <Modal title={t("model_settings")} close={close} className="model-dialog" t={t}>
    <form className="settings-form" noValidate onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      if (!canSave) return;
      const next = { ...options };
      if (validThresholds) {
        next.compact_at_percent = effectiveOptions.compact_at_percent ?? defaultCompaction;
        next.background_compact_at_percent = effectiveOptions.background_compact_at_percent || defaultBackgroundCompaction;
      }
      if (efforts !== null) {
        next.effort_levels = efforts;
        if (efforts.length && !efforts.includes(next.reasoning_effort)) delete next.reasoning_effort;
      }
      save(next);
    }}>
      <h3 className="model-identifier" dir="auto">{model}</h3>
      <ModelOptionFields {...{ t, efforts, effortFailed }} options={effectiveOptions}
        change={(key, value) => setOptions((previous) => ({ ...previous, [key]: value }))}
        context={modelMetadata(metadata).context_window || initial.context_window} fallbackContext={provider.context_window} />
      <SaveBar t={t} changed={(options !== initial || defaultsChanged) && canSave} />
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
  const reportCatalog = useCallback((id, items) => {
    const catalog = new Map(items.map((item) => [item.id, item]));
    setCatalogs((current) => new Map(current).set(id, catalog));
    setDraft((current) => {
      let changed = false;
      const providers = current.providers.map((provider) => {
        if (provider.id !== id) return provider;
        const modelOptions = { ...provider.model_options };
        let updated = false;
        for (const model of provider.models || []) {
          if (!catalog.has(model)) continue;
          const previous = modelOptions[model] || empty;
          const metadata = modelMetadata(catalog.get(model));
          if (provider.protocol === "ollama" && !metadata.effort_levels.length) delete metadata.effort_levels;
          const next = { ...previous, ...metadata };
          if (JSON.stringify(next) === JSON.stringify(previous)) continue;
          modelOptions[model] = next;
          updated = true;
        }
        changed ||= updated;
        return updated ? { ...provider, model_options: modelOptions } : provider;
      });
      return changed ? { ...current, providers } : current;
    });
  }, []);
  const catalogInput = useRef(null);
  const catalogSection = useRef(null);
  const provider = draft.providers.find((item) => item.id === providerId) || draft.providers[0];
  const choices = modelChoices({ ...settings, ...draft }).map((item) => ({
    ...item, name: catalogs.get(item.provider)?.get(item.model)?.name || item.name,
    modalities: catalogs.get(item.provider)?.get(item.model)?.input_modalities ?? item.modalities,
  }));
  const changed = draft.agents !== (settings.agents || empty) || draft.providers !== (settings.providers || draft.providers);
  const modelSource = (assignment) => assignment?.provider === "environment" ? settings.environment
    : draft.providers.find((item) => item.id === assignment?.provider);
  const validAgentOptions = Object.entries(draft.agents).every(([role, assignment]) => {
    const source = modelSource(assignment);
    const inherited = { ...source, ...source?.model_options?.[assignment.model] };
    const effective = { ...inherited, ...assignment.options };
    const original = settings.agents?.[role];
    const saved = { ...inherited, ...(choiceKey(assignment) === choiceKey(original) && original.options) };
    return !changedCompaction(effective, saved) || validCompaction(effective);
  });

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
    const source = modelSource(current);
    const metadata = source?.model_options?.[current?.model];
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
          <ModelOptionFields t={t} options={{ ...source, ...metadata, ...current.options }}
            change={(key, value) => assign(id, { ...current, options: { ...current.options, [key]: value } })}
            efforts={reportedEfforts(metadata)}
            context={metadata?.context_window} fallbackContext={source?.context_window} />
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
  return <form className="settings-form" noValidate onSubmit={(event) => {
    event.preventDefault();
    if (changed && !saving && validAgentOptions) submit(draft);
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
          setEnabled={(model, enabled) => updateProvider((item) => ({
            ...item, models: enabled ? [...new Set([...(item.models || []), model])] : (item.models || []).filter((id) => id !== model),
            ...(enabled && catalogs.get(item.id)?.has(model) && { model_options: { ...item.model_options,
              [model]: { ...item.model_options?.[model], ...modelMetadata(catalogs.get(item.id).get(model)) } } }),
          }), enabled ? undefined : model)}
          edit={setEditing} />
      </> : <button type="button" className="tonal" onClick={addProvider} disabled={saving}>
        <Icon name="add" />{t("add_provider")}
      </button>}
    </section>
    <SaveBar {...{ t, saving }} changed={changed && validAgentOptions} />
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
