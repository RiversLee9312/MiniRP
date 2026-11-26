import { memo, useCallback, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import Markdown, { defaultUrlTransform } from "react-markdown";
import remarkGfm from "remark-gfm";
import { assetUrl, attachmentUrl, transcriptPage } from "./api.js";
import { OpenUi } from "./OpenUi.jsx";
import { ReplySelectionMenu } from "./ReplySelectionMenu.jsx";
import { ToolTrace } from "./ToolTrace.jsx";
import { TranscriptRuler } from "./TranscriptRuler.jsx";
import { researchAgentLabel, transcriptTurns } from "./transcript-turns.js";
import { FileChoice, Icon, IconButton } from "./ui.jsx";

const markdownPlugins = [remarkGfm];
const isAsset = (url) => typeof url === "string" && url.startsWith("minirp://assets/");
const external = (url) => /^https?:\/\//i.test(url || "");

function Trace({ block, expanded, t, view }) {
  const [open, setOpen] = useState(expanded);
  const title = block.compaction ? t("compaction_threshold") : block.tool_title?.trim() || block.tool || t("reasoning");
  const skill = !block.compaction && block.skill_title?.trim();
  return <details className="trace" open={open} onToggle={(event) => setOpen(event.currentTarget.open)}>
    <summary>
      <Icon name="expand_more" />
      <span className="trace-caption" title={block.tool && block.tool !== title ? block.tool : undefined}>
        <span className="trace-title" dir="auto">{title}</span>
        {skill && skill !== title && <small className="trace-skill" dir="auto">{skill}</small>}
      </span>
      {block.running && <span className="muted">{t("running")}</span>}
    </summary>
    {block.reasoning && <p className="trace-reasoning" dir="auto">{block.reasoning}</p>}
    {block.arguments != null && block.arguments !== "" && <ToolTrace source={block.arguments} title={t("tool_arguments")} t={t}
      presentation={block.tool_arguments_ui} active={open} blockId={block.id} phase="arguments" view={view} />}
    {block.result != null && block.result !== "" && (block.compaction
      ? <p className="trace-reasoning" dir="auto">{block.result}</p>
      : <ToolTrace source={block.result} title={t("tool_result")} t={t}
        presentation={block.tool_result_ui} active={open} blockId={block.id} phase="result" view={view} />)}
  </details>;
}

function QuoteAttachment({ file, t, onRemove }) {
  return <div className="attachment quote-attachment">
    <details>
      <summary aria-label={t("quote_reply")}>
        <Icon name="format_quote" />
        <span className="quote-label">{t("quote_reply")}</span>
        <span className="quote-preview" dir="auto" aria-hidden="true">{file.text}</span>
        <Icon name="expand_more" />
      </summary>
      <blockquote dir="auto">{file.text}</blockquote>
    </details>
    {onRemove && <IconButton icon="close" label={t("remove_attachment", t("quote_reply"))} onClick={onRemove} />}
  </div>;
}

function Attachment({ file, scope, t }) {
  if (file.kind === "quote") return <QuoteAttachment file={file} t={t} />;
  const thumbnail = file.thumbnail_url || (file.thumbnail && "minirp://assets/minirp.legacy/attachment-thumbnail?attachment=" + file.id);
  return <a className="attachment" href={attachmentUrl(file)} download>
    {thumbnail && <img alt="" loading="lazy" src={assetUrl(thumbnail, scope, { inline: "1", attachment: JSON.stringify(file) })} />}
    <span>{file.name}</span>
  </a>;
}

function BlockView({ block, scope, locale, dark, theme, expanded, busy, t, branch, openAction, runExport, rowPrefix, nested = false }) {
  const user = ["user", "system"].includes(block.kind || block.role);
  const search = researchAgentLabel(block.agent);
  const plugin = block.plugin_agent_name && block.plugin_status;
  const row = nested ? undefined : block.id;
  const display = (url, inline = false) => assetUrl(url, scope, { mode: "display", block: block.id, inline: inline ? "1" : "0" });
  const branchButton = (id, mode) =>
    <IconButton icon="fork_right" label={t(mode === "edit" ? "branch_edit_message" : "branch_chat")}
      disabled={busy} onClick={() => branch(id, mode)} />;

  if (block.kind === "reply_end") {
    return <div className="message-actions" data-transcript-row={row}>
      {block.reply && <IconButton icon="content_copy" label={t("copy")} onClick={() => navigator.clipboard.writeText(block.reply)} />}
      {block.first_block && !scope.conversation && branchButton(block.first_block, "reply")}
    </div>;
  }

  const trace = block.reasoning || block.tool || block.compaction;
  if (!trace && !block.content && !block.open_ui && !plugin && !block.attachments?.length) return null;

  return <article id={row && rowPrefix ? rowPrefix + row : undefined} data-transcript-row={row}
    className={["message", user ? "user-message" : "", search ? "nested-trace" : ""].filter(Boolean).join(" ")}>
    {(block.speaker || search || plugin || (block.kind || block.role) === "system") &&
      <header className="message-label">
        {block.speaker || block.plugin_agent_name || t(search || "system_message")}
        {plugin && <span className="muted">{t(block.plugin_status === "running" ? "running" : "plugin_run_" + block.plugin_status)}</span>}
        {plugin && block.plugin_status !== "running" && block.plugin_run &&
          <IconButton icon="share" label={t("export_transcript")} disabled={busy} onClick={() => runExport(block.plugin_run)} />}
      </header>}
    {trace && <Trace block={block} expanded={expanded && Boolean(block.reasoning)} t={t} view={{ scope, locale, dark, theme }} />}
    {block.content && (user
      ? <p className="plain">{block.content}</p>
      : <div className="markdown assistant-reply">
        <Markdown remarkPlugins={markdownPlugins} skipHtml
          urlTransform={(url) => isAsset(url) ? url : defaultUrlTransform(url)}
          components={{
            a: ({ href, children }) => isAsset(href)
              ? block.asset_links?.[href] ? <a href={display(href)} download>{children}</a>
                : openAction ? <button className="inline-link" type="button" onClick={() => openAction(block, { type: "link", url: href })}>{children}</button> : <span>{children}</span>
              : external(href) ? <a href={href} target="_blank" rel="noreferrer">{children}</a> : <span>{children}</span>,
            img: ({ src, alt }) => isAsset(src) && block.asset_links?.[src]
              ? <img loading="lazy" src={display(src, true)} alt={alt || ""} />
              : external(src) ? <img loading="lazy" src={src} alt={alt || ""} referrerPolicy="no-referrer" /> : null,
            table: ({ children }) => <div className="table-scroll"><table>{children}</table></div>,
          }}>
          {block.content}
        </Markdown>
      </div>)}
    {block.open_ui && <OpenUi block={block} scope={scope} identity={JSON.stringify([scope.preset, scope.room, scope.conversation, block.id])}
      locale={locale} dark={dark} theme={theme} t={t} readOnly={!openAction}
      onAction={openAction && ((action) => openAction(block, action))} />}
    {block.attachments?.length > 0 && <div className="attachments">
      {block.attachments.map((file) => <Attachment key={file.id} file={file} scope={scope} t={t} />)}
    </div>}
    {user && block.version && scope.agent === "chat" && !scope.conversation && <footer className="message-actions">
      {branchButton(block.id, "edit")}
    </footer>}
  </article>;
}

export const MessageBlock = memo(BlockView);

function groupTraces(blocks) {
  const groups = [];
  for (const block of blocks) {
    const previous = groups.at(-1);
    if (!researchAgentLabel(block.agent)) groups.push(block);
    else if (previous?.items && previous.agent === block.agent) previous.items.push(block);
    else groups.push({ id: block.id, agent: block.agent, items: [block] });
  }
  return groups;
}

const TranscriptRows = memo(function TranscriptRows({ blocks, t, ...view }) {
  const chunks = useMemo(() => {
    const pages = [];
    for (const row of groupTraces(blocks)) {
      const last = pages.at(-1);
      if (!last || (last.length >= 24 && row.kind !== "reply_end")) pages.push([row]);
      else last.push(row);
    }
    return pages;
  }, [blocks]);
  return chunks.map((chunk) => <div key={chunk[0].id} className="transcript-chunk"
    style={{ "--transcript-estimate": chunk.length * 84 + "px" }}>
    {chunk.map((block) => block.items ? <details className="trace nested-trace" key={block.id} data-transcript-row={block.id}>
      <summary><Icon name="expand_more" /><span className="trace-title">
        {t(researchAgentLabel(block.agent))}
      </span></summary>
      {block.items.map((item) => <MessageBlock key={item.id} block={item} t={t} nested {...view} />)}
    </details> : <MessageBlock key={block.id} block={block} t={t} {...view} />)}
  </div>);
});

function firstBelow(elements, edge) {
  let start = 0;
  let end = elements.length;
  while (start < end) {
    const middle = (start + end) >>> 1;
    if (elements[middle].getBoundingClientRect().bottom <= edge) start = middle + 1;
    else end = middle;
  }
  return elements[start];
}

function readingAnchor(element) {
  const top = element.getBoundingClientRect().top;
  const chunk = firstBelow(element.querySelectorAll(".transcript-chunk"), top);
  const row = chunk && firstBelow(chunk.querySelectorAll("[data-transcript-row]"), top);
  return row && { row, offset: row.getBoundingClientRect().top - top };
}

export function Transcript({ blocks, contextKey, history, pane, addHistory, onHistoryError, t, quoteReply,
  loadPage = transcriptPage, ...view }) {
  const controls = useId();
  const rowPrefix = controls + "-row-";
  const turns = useMemo(() => transcriptTurns(history?.pages, blocks), [history?.pages, blocks]);
  const list = useRef(null);
  const loadButton = useRef(null);
  const pending = useRef(null);
  const load = useRef(null);
  const follow = useRef(true);
  const layout = useRef({ viewport: 0, content: 0 });
  const [atEnd, setAtEnd] = useState(true);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [historyError, setHistoryError] = useState(false);
  const before = history?.before;
  const jump = useCallback(() => {
    const element = list.current;
    if (!element) return;
    layout.current = { viewport: element.clientHeight, content: element.scrollHeight };
    element.scrollTop = layout.current.content;
    follow.current = true;
    setAtEnd(true);
  }, []);
  const loadOlder = useCallback(async () => {
    if (!before || load.current || pending.current) return;
    const controller = new AbortController();
    const operation = { controller, locale: view.locale };
    load.current = operation;
    setLoadingOlder(true);
    setHistoryError(false);
    try {
      const page = await loadPage(view.scope, before, view.locale, controller.signal);
      if (controller.signal.aborted) return;
      if (!Array.isArray(page.blocks) || (page.before !== null && typeof page.before !== "string") || page.before === before)
        throw new Error(t("connection_failed"));
      const element = list.current;
      pending.current = {
        before,
        anchor: follow.current ? null : readingAnchor(element),
        focused: document.activeElement === loadButton.current,
      };
      addHistory(pane, history.version, before, page);
    } catch (error) {
      if (!controller.signal.aborted) {
        setHistoryError(true);
        onHistoryError(error.message);
      }
    } finally {
      if (load.current === operation) load.current = null;
      if (!controller.signal.aborted) setLoadingOlder(false);
    }
  }, [addHistory, before, history?.version, loadPage, onHistoryError, pane, t, view.locale, view.scope]);

  useLayoutEffect(() => () => load.current?.controller.abort(), []);
  useLayoutEffect(() => {
    const previous = load.current;
    if (previous && previous.locale !== view.locale) {
      previous.controller.abort();
      load.current = null;
      loadOlder();
    }
  }, [loadOlder, view.locale]);
  useLayoutEffect(() => {
    const element = list.current;
    const restore = pending.current;
    if (restore && restore.before !== before) {
      pending.current = null;
      if (!follow.current && restore.anchor && element.contains(restore.anchor.row)) {
        const actual = restore.anchor.row.getBoundingClientRect().top - element.getBoundingClientRect().top;
        element.scrollTop += actual - restore.anchor.offset;
      }
      if (restore.focused && !before) element.focus({ preventScroll: true });
    }
    if (follow.current) jump();
  }, [blocks, before, history, jump]);
  useLayoutEffect(() => {
    const element = list.current;
    const observer = new ResizeObserver(() => { if (follow.current) jump(); });
    observer.observe(element);
    observer.observe(element.firstElementChild);
    return () => observer.disconnect();
  }, [jump]);
  return <div className="transcript-shell">
    <div id={controls} className="transcript" ref={list} role="region" aria-label={t("conversations")} tabIndex={0} data-following={atEnd}
      onScroll={(event) => {
        const element = event.currentTarget;
        const { clientHeight: viewport, scrollHeight: content, scrollTop } = element;
        const previous = layout.current;
        layout.current = { viewport, content };
        if (follow.current && (viewport !== previous.viewport || content !== previous.content)) {
          jump();
          return;
        }
        const pinned = content - viewport - scrollTop < 48;
        if (follow.current !== pinned) {
          follow.current = pinned;
          setAtEnd(pinned);
        }
        if (pending.current) pending.current.anchor = pinned ? null : readingAnchor(element);
        if (!pinned && element.scrollTop < 140 && !historyError) loadOlder();
      }}>
      <div className="message-column">
        {before && <button className="text-button transcript-load-older" type="button" ref={loadButton}
          aria-disabled={loadingOlder} onClick={loadOlder}>{t(loadingOlder ? "running" : "load_more")}</button>}
        {history?.pages.map((page) => <TranscriptRows key={page[0].id} blocks={page} rowPrefix={rowPrefix} t={t} {...view} />)}
        <TranscriptRows key="latest" blocks={blocks} rowPrefix={rowPrefix} t={t} {...view} />
      </div>
    </div>
    <TranscriptRuler key={contextKey} scroller={list} controls={controls} rowPrefix={rowPrefix} turns={turns}
      hasOlder={Boolean(before)} loadOlder={loadOlder} t={t} />
    <ReplySelectionMenu key={"selection:" + contextKey} transcript={list} onQuote={quoteReply} t={t} />
    {!atEnd && <IconButton className="jump" icon="arrow_downward" label={t("latest_message")} onClick={jump} />}
  </div>;
}

export function Composer({ draft, change, attachments, addFiles, removeFile, submit, stop, t, role,
  generating, sending, disabled, modelPicker, delivery = "both" }) {
  const input = useRef(null);
  const systemOnly = delivery === "system";
  const canSend = !disabled && (draft.trim() || !systemOnly && attachments.length > 0)
    && (!generating || systemOnly || attachments.length === 0);
  const inputLabel = t(systemOnly ? "system_message" : "input_hint");
  useLayoutEffect(() => {
    const area = input.current;
    area.style.height = "auto";
    area.style.height = Math.min(area.scrollHeight, 160) + "px";
  }, [draft]);
  return <form className="composer" onSubmit={(event) => { event.preventDefault(); if (canSend) submit(systemOnly); }}
    onDragOver={(event) => { if (!disabled || systemOnly) event.preventDefault(); }}
    onDrop={(event) => { event.preventDefault(); if (!systemOnly && !disabled && !generating) addFiles(Array.from(event.dataTransfer.files)); }}>
    {!systemOnly && attachments.length > 0 && <div className="attachments">
      {attachments.map((file) => file.kind === "quote"
        ? <QuoteAttachment key={file.id} file={file} t={t} onRemove={() => removeFile(file.id)} />
        : <span className="attachment" key={file.id}>
          {file.name}<IconButton icon="close" label={t("remove_attachment", file.name)} onClick={() => removeFile(file.id)} />
        </span>)}
    </div>}
    <textarea ref={input} value={draft} onChange={(event) => change(event.target.value)} rows="1"
      aria-label={inputLabel} placeholder={role === "preset" ? t("describe_changes") : inputLabel}
      onKeyDown={(event) => {
        if (event.key !== "Enter" || event.shiftKey || event.nativeEvent.isComposing || event.keyCode === 229) return;
        event.preventDefault();
        if (canSend) submit(systemOnly);
      }} />
    <div className="composer-actions">
      <div className="composer-tools">
        {!systemOnly && <FileChoice label={t("add_attachment")} icon="add" disabled={disabled || generating} choose={addFiles} multiple
          iconOnly className="icon-button tonal attach-button" />}
        {modelPicker}
      </div>
      <div className="composer-submit">
        {delivery === "both" && draft.trim() && !attachments.length && <button className="text-button" type="button" disabled={disabled} onClick={() => submit(true)}>
          {t("send_as_system")}
        </button>}
        {generating && <IconButton className="stop-action" icon="stop" label={t("stop")} onClick={stop} />}
        <button className="filled send-action" disabled={!canSend} type="submit"><Icon name="arrow_upward" />
          {t(sending ? "sending" : systemOnly ? "send_as_system" : generating ? "steer" : "send")}</button>
      </div>
    </div>
  </form>;
}
