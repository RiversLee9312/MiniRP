import { useEffect, useId, useMemo, useRef, useState } from "react";
import { presetActivityPage } from "./api.js";
import { Transcript } from "./Conversation.jsx";
import { Icon, IconButton, Modal, Select, Surface } from "./ui.jsx";
import { researchSubagents, subagentIcon, subagentSummary, traceHistory, traceRows } from "./subagent-traces.js";

export function SubagentButton({ count, hideWhenEmpty = false, t, ...props }) {
  const badge = useId();
  if (hideWhenEmpty && count === 0) return null;
  return <button type="button" className="icon-button subagent-action" aria-label={t("subagents")} aria-describedby={count > 0 ? badge : undefined}
    aria-haspopup="dialog" {...props}>
    <Icon name={subagentIcon} />
    {count > 0 && <span className="subagent-count" id={badge} dir="ltr">{count > 99 ? "99+" : count}</span>}
  </button>;
}

function useActivityPage(scope, locale, refresh) {
  const [page, setPage] = useState(null);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const start = useRef(null);
  useEffect(() => {
    if (!scope) return;
    const controller = new AbortController();
    let timer;
    async function load() {
      try {
        const query = start.current === null ? scope : { ...scope, after: start.current };
        const result = await presetActivityPage(query, null, locale, controller.signal);
        if (controller.signal.aborted) return;
        if (scope.run) start.current ??= result.before ?? "0";
        setPage((previous) => result.ruler === undefined && previous?.ruler
          ? { ...result, ruler: previous.ruler } : result);
        setFailed(false);
        if (refresh) timer = setTimeout(load, 1500);
      } catch {
        if (!controller.signal.aborted) setFailed(true);
      }
    }
    load();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [scope, locale, refresh, attempt]);
  return { page, failed, retry: () => setAttempt((count) => count + 1) };
}

function runStatus(status, t) {
  return status === "running" ? t("running") : status && t("plugin_run_" + status);
}

const hasTraceContent = (block) => !block.plugin_status || Boolean(block.content);

function ActivityStatus({ failed, retry, loaded, t }) {
  if (!failed && !loaded) return <progress className="preset-activity-status" aria-label={t("subagents")} />;
  return <div className="preset-activity-status" role="status">
    <p>{t(failed ? "ensemble_trace_load_failed" : "ensemble_no_trace")}</p>
    {failed && <button className="text-button" type="button" onClick={retry}>{t("retry")}</button>}
  </div>;
}

function RunActivity({ preset, run, locale, dark, theme, t, onError, title, close, dismiss, controls }) {
  const scope = useMemo(() => ({ preset, room: "", agent: "preset", run: run.id }), [preset, run.id]);
  const { page, failed, retry } = useActivityPage(scope, locale, run.status === "running");
  const [older, setOlder] = useState(null);
  const [childrenOpen, setChildrenOpen] = useState(false);
  const history = older || { before: page?.before, pages: [], ruler: page?.ruler };
  const rawBlocks = page?.blocks || [];
  const blocks = traceRows(rawBlocks, "plugin").filter(hasTraceContent);
  const mainHistory = traceHistory(history, "plugin");
  function addHistory(_pane, _version, before, result) {
    if (before !== history.before) return;
    setOlder({
      ...history,
      before: result.before,
      pages: result.blocks.length ? [result.blocks, ...history.pages] : history.pages,
    });
  }
  const showBrowser = page?.has_browser || traceRows(rawBlocks, "plugin/browser").length > 0 ||
    history.pages.some((items) => traceRows(items, "plugin/browser").length);
  return <Modal title={title} back={close} close={dismiss} t={t} wide className="preset-run-modal"
    actions={page && <SubagentButton count={Number(Boolean(showBrowser))} hideWhenEmpty t={t} onClick={() => setChildrenOpen(true)} />}>
    {controls}
    {failed && blocks.length > 0 && <ActivityStatus failed retry={retry} t={t} />}
    {blocks.length > 0 || history.before || history.pages.length > 0
      ? <Transcript blocks={blocks} contextKey={run.id} history={mainHistory}
        pane="plugin" addHistory={addHistory} onHistoryError={onError} loadPage={presetActivityPage}
        expanded {...{ scope, locale, dark, theme, t }} />
      : <ActivityStatus {...{ failed, retry, t }} loaded={Boolean(page)} />}
    <Surface>{childrenOpen && <BrowserChildren projection="plugin/browser" blocks={rawBlocks} pane="plugin"
      loadPage={presetActivityPage} close={() => setChildrenOpen(false)}
      {...{ history, scope, addHistory, locale, dark, theme, t, onError }} />}</Surface>
  </Modal>;
}

function WorkerActivity({ worker, close, dismiss, onExport, busy, t, ...view }) {
  const [selected, setSelected] = useState("");
  const latest = worker.runs.at(-1);
  const run = worker.runs.find((item) => item.id === selected) || latest;
  const title = worker.name || t("plugin_agent");
  const controls = <div className="preset-run-controls">
      {worker.runs.length > 1 && <Select aria-label={t("plugin_run", worker.runs.indexOf(run) + 1)}
        value={run.id} onChange={(event) => setSelected(event.target.value)}>
        {worker.runs.slice().reverse().map((item, index) =>
          <option key={item.id} value={item.id}>
            {[t("plugin_run", worker.runs.length - index), runStatus(item.status, t)].filter(Boolean).join(" · ")}
          </option>)}
      </Select>}
      <IconButton icon="share" label={t("export_transcript")} disabled={busy || run.status === "running"}
        onClick={() => onExport(run.id, title)} />
    </div>;
  return <RunActivity key={run.id + ":" + view.locale} {...{ run, t, title, close, dismiss, controls, ...view }} />;
}

function AgentRow({ icon, children, open }) {
  return <li><button className="room-select preset-worker" type="button" aria-haspopup="dialog" onClick={open}>
    <Icon name={icon} /><span>{children}</span><span className="directional"><Icon name="chevron_right" /></span>
  </button></li>;
}

function BrowserChildren({ close, t, ...view }) {
  const [open, setOpen] = useState(false);
  const browser = researchSubagents.find((item) => item.id === "browser");
  return <Modal title={t("subagents")} close={close} back={close} t={t}>
    <ul className="preset-worker-list"><AgentRow icon={browser.icon} open={() => setOpen(true)}>{t(browser.label)}</AgentRow></ul>
    <Surface>{open && <ResearchActivity type={browser} close={close} back={() => setOpen(false)} t={t} {...view} />}</Surface>
  </Modal>;
}

function ResearchActivity({ type, projection = type.id, blocks, history, scope, pane, addHistory, loadPage,
  locale, dark, theme, t, close, back, onError }) {
  const [childrenOpen, setChildrenOpen] = useState(false);
  const rows = traceRows(blocks, projection);
  const older = useMemo(() => traceHistory(history, projection), [history, projection]);
  const hasBrowser = type.id === "search" && subagentSummary(blocks, history).searchBrowser;
  return <Modal title={t(type.label)} close={close} back={back} t={t} wide className="preset-run-modal subagent-research-modal"
    actions={<SubagentButton count={Number(hasBrowser)} hideWhenEmpty t={t} onClick={() => setChildrenOpen(true)} />}>
    {rows.length || older?.before || older?.pages.some((page) => page.length)
      ? <Transcript blocks={rows} history={older} contextKey={projection} pane={pane} addHistory={addHistory} loadPage={loadPage}
        onHistoryError={onError} expanded {...{ scope, locale, dark, theme, t }} />
      : <ActivityStatus loaded t={t} />}
    <Surface>{childrenOpen && <BrowserChildren projection="search/browser" close={() => setChildrenOpen(false)}
      {...{ blocks, history, scope, pane, addHistory, loadPage, locale, dark, theme, t, onError }} />}</Surface>
  </Modal>;
}

export function SubagentActivity({ blocks, history, scope, pane, addHistory, locale, t, close, ...view }) {
  const { preset } = scope;
  const includesPlugins = scope.agent === "preset";
  const pluginScope = useMemo(() => includesPlugins ? { preset } : null, [includesPlugins, preset]);
  const { page, failed, retry } = useActivityPage(pluginScope, locale, true);
  const [selected, setSelected] = useState("");
  const workers = useMemo(() => {
    const grouped = new Map();
    for (const run of page?.items || []) {
      if (!grouped.has(run.worker)) grouped.set(run.worker, { id: run.worker, name: "", runs: [] });
      const worker = grouped.get(run.worker);
      if (run.name) worker.name = run.name;
      worker.runs.push(run);
    }
    return [...grouped.values()].reverse().sort((a, b) =>
      Number(b.runs.at(-1).status === "running") - Number(a.runs.at(-1).status === "running"));
  }, [page]);
  const worker = workers.find((item) => item.id === selected);
  const summary = useMemo(() => subagentSummary(blocks, history), [blocks, history]);
  const research = researchSubagents.filter(({ id }) => summary[id]);
  const type = researchSubagents.find((item) => item.id === selected);
  const back = () => setSelected("");
  return <Modal title={t("subagents")} close={close} t={t}>
    {research.length > 0 || workers.length > 0 ? <ul className="preset-worker-list">
      {research.map((item) => <AgentRow key={item.id} icon={item.icon} open={() => setSelected(item.id)}>{t(item.label)}</AgentRow>)}
      {workers.map((item) => <AgentRow key={item.id} icon={subagentIcon} open={() => setSelected(item.id)}>
        <bdi className="choice-label">{item.name || t("plugin_agent")}</bdi>
        {item.runs.at(-1).status && <small>{runStatus(item.runs.at(-1).status, t)}</small>}
      </AgentRow>)}
    </ul> : <ActivityStatus {...{ failed, retry, t }} loaded={!includesPlugins || Boolean(page)} />}
    {includesPlugins && failed && (research.length > 0 || workers.length > 0) && <ActivityStatus failed retry={retry} t={t} />}
    <Surface>{worker && <WorkerActivity key={worker.id} close={back}
      dismiss={() => { setSelected(""); close(); }}
      {...{ worker, preset, locale, t, ...view }} />}</Surface>
    <Surface>{type && <ResearchActivity key={type.id} {...{ type, blocks, history, scope, pane, addHistory, locale, t, close, back, ...view }} />}</Surface>
  </Modal>;
}
