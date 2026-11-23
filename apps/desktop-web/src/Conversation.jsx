import { memo, useLayoutEffect, useRef, useState } from "react";
import Markdown, { defaultUrlTransform } from "react-markdown";
import remarkGfm from "remark-gfm";
import { assetUrl, attachmentUrl } from "./api.js";
import { OpenUi } from "./OpenUi.jsx";
import { FileChoice, Icon, IconButton } from "./ui.jsx";

const markdownPlugins = [remarkGfm];
const isAsset = (url) => typeof url === "string" && url.startsWith("minirp://assets/");
const external = (url) => /^https?:\/\//i.test(url || "");
const agentTrace = (agent) => ["search", "SearchAgent", "fetch", "FetchAgent"].includes(agent);

function Trace({ block, expanded, t }) {
  const [open, setOpen] = useState(expanded);
  return <details className="trace" open={open} onToggle={(event) => setOpen(event.currentTarget.open)}>
    <summary>
      <Icon name="expand_more" />
      <span className="trace-title">{block.compaction ? t("compaction_threshold") : block.tool || t("reasoning")}</span>
      {block.running && <span className="muted">{t("running")}</span>}
    </summary>
    {block.reasoning && <pre>{block.reasoning}</pre>}
    {block.arguments && <pre>{block.arguments}</pre>}
    {block.result && <pre>{block.result}</pre>}
  </details>;
}

function Attachment({ file, scope }) {
  const thumbnail = file.thumbnail_url || (file.thumbnail && "minirp://assets/minirp.legacy/attachment-thumbnail?attachment=" + file.id);
  return <a className="attachment" href={attachmentUrl(file)} download>
    {thumbnail && <img alt="" loading="lazy" src={assetUrl(thumbnail, scope, { inline: "1", attachment: JSON.stringify(file) })} />}
    <span>{file.name}</span>
  </a>;
}

function BlockView({ block, scope, locale, dark, theme, expanded, busy, t, branch, openAction, runExport }) {
  const user = ["user", "system"].includes(block.kind || block.role);
  const search = agentTrace(block.agent);
  const plugin = block.plugin_agent_name && block.plugin_status;
  const display = (url, inline = false) => assetUrl(url, scope, { mode: "display", block: block.id, inline: inline ? "1" : "0" });
  const branchButton = (id, mode) =>
    <IconButton icon="fork_right" label={t(mode === "edit" ? "branch_edit_message" : "branch_chat")}
      disabled={busy} onClick={() => branch(id, mode)} />;

  if (block.kind === "reply_end") {
    return <div className="message-actions">
      {block.reply && <IconButton icon="content_copy" label={t("copy")} onClick={() => navigator.clipboard.writeText(block.reply)} />}
      {block.first_block && !scope.conversation && branchButton(block.first_block, "reply")}
    </div>;
  }

  const trace = block.reasoning || block.tool || block.compaction;
  if (!trace && !block.content && !block.open_ui && !plugin && !block.attachments?.length) return null;

  return <article className={["message", user ? "user-message" : "", search ? "nested-trace" : ""].filter(Boolean).join(" ")}>
    {(block.speaker || search || plugin || (block.kind || block.role) === "system") &&
      <header className="message-label">
        {block.speaker || block.plugin_agent_name || t(search ? block.agent.toLowerCase().startsWith("fetch") ? "fetch_agent" : "search_agent" : "system_message")}
        {plugin && <span className="muted">{t(block.plugin_status === "running" ? "running" : "plugin_run_" + block.plugin_status)}</span>}
        {plugin && block.plugin_status !== "running" && block.plugin_run &&
          <IconButton icon="share" label={t("export_transcript")} disabled={busy} onClick={() => runExport(block.plugin_run)} />}
      </header>}
    {trace && <Trace block={block} expanded={expanded && Boolean(block.reasoning)} t={t} />}
    {block.content && (user
      ? <p className="plain">{block.content}</p>
      : <div className="markdown">
        <Markdown remarkPlugins={markdownPlugins} skipHtml
          urlTransform={(url) => isAsset(url) ? url : defaultUrlTransform(url)}
          components={{
            a: ({ href, children }) => isAsset(href)
              ? block.asset_links?.[href] ? <a href={display(href)} download>{children}</a>
                : <button className="inline-link" type="button" onClick={() => openAction(block, { type: "link", url: href })}>{children}</button>
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
      locale={locale} dark={dark} theme={theme} t={t} onAction={(action) => openAction(block, action)} />}
    {block.attachments?.length > 0 && <div className="attachments">
      {block.attachments.map((file) => <Attachment key={file.id} file={file} scope={scope} />)}
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
    if (!agentTrace(block.agent)) groups.push(block);
    else if (previous?.items && previous.agent === block.agent) previous.items.push(block);
    else groups.push({ id: block.id, agent: block.agent, items: [block] });
  }
  return groups;
}

export function Transcript({ blocks, contextKey, t, ...view }) {
  const list = useRef(null);
  const follow = useRef(true);
  const layout = useRef({ viewport: 0, content: 0 });
  const [atEnd, setAtEnd] = useState(true);
  function measure(element) {
    const viewport = element.clientHeight;
    const content = element.scrollHeight;
    const changed = viewport !== layout.current.viewport || content !== layout.current.content;
    layout.current = { viewport, content };
    return changed;
  }
  function jump() {
    const element = list.current;
    if (!element) return;
    measure(element);
    element.scrollTop = layout.current.content;
    follow.current = true;
    setAtEnd(true);
  }
  useLayoutEffect(jump, [contextKey]);
  useLayoutEffect(() => { if (follow.current) jump(); }, [blocks]);
  useLayoutEffect(() => {
    const element = list.current;
    const observer = new ResizeObserver(() => { if (measure(element) && follow.current) jump(); });
    observer.observe(element);
    observer.observe(element.firstElementChild);
    return () => observer.disconnect();
  }, []);
  return <div className="transcript-shell">
    <div className="transcript" ref={list} role="region" aria-label={t("conversations")} tabIndex={0} data-following={atEnd}
      onScroll={() => {
        const element = list.current;
        if (measure(element) && follow.current) {
          jump();
          return;
        }
        follow.current = layout.current.content - layout.current.viewport - element.scrollTop < 48;
        setAtEnd(follow.current);
      }}>
      <div className="message-column">
        {groupTraces(blocks).map((block) => block.items ? <details className="trace nested-trace" key={block.id}>
          <summary><Icon name="expand_more" />{t(block.agent.toLowerCase().startsWith("fetch") ? "fetch_agent" : "search_agent")}</summary>
          {block.items.map((item) => <MessageBlock key={item.id} block={item} t={t} {...view} />)}
        </details> : <MessageBlock key={block.id} block={block} t={t} {...view} />)}
      </div>
    </div>
    {!atEnd && <IconButton className="jump" icon="arrow_downward" label={t("latest_message")} onClick={jump} />}
  </div>;
}

export function Composer({ draft, change, attachments, addFiles, removeFile, submit, stop, t, role,
  generating, disabled, modelPicker }) {
  const input = useRef(null);
  const canSend = !disabled && (draft.trim() || attachments.length > 0) && (!generating || attachments.length === 0);
  useLayoutEffect(() => {
    const area = input.current;
    area.style.height = "auto";
    area.style.height = Math.min(area.scrollHeight, 160) + "px";
  }, [draft]);
  return <form className="composer" onSubmit={(event) => { event.preventDefault(); if (canSend) submit(false); }}
    onDragOver={(event) => { if (!disabled) event.preventDefault(); }}
    onDrop={(event) => { event.preventDefault(); if (!disabled && !generating) addFiles(Array.from(event.dataTransfer.files)); }}>
    {attachments.length > 0 && <div className="attachments">
      {attachments.map((file) => <span className="attachment" key={file.id}>
        {file.name}<IconButton icon="close" label={t("remove_attachment")} onClick={() => removeFile(file.id)} />
      </span>)}
    </div>}
    <textarea ref={input} value={draft} onChange={(event) => change(event.target.value)} rows="1"
      aria-label={t("input_hint")} placeholder={t(role === "preset" ? "describe_changes" : "input_hint")}
      onKeyDown={(event) => {
        if (event.key !== "Enter" || event.shiftKey || event.nativeEvent.isComposing || event.keyCode === 229) return;
        event.preventDefault();
        if (canSend) submit(false);
      }} />
    <div className="composer-actions">
      <div className="composer-tools">
        <FileChoice label={t("add_attachment")} icon="add" disabled={disabled || generating} choose={addFiles} multiple
          iconOnly className="icon-button tonal attach-button" />
        {modelPicker}
      </div>
      <div className="composer-submit">
        {draft.trim() && !attachments.length && <button className="text-button" type="button" disabled={disabled} onClick={() => submit(true)}>
          {t("send_as_system")}
        </button>}
        {generating && <IconButton className="stop-action" icon="stop" label={t("stop")} onClick={stop} />}
        <button className="filled send-action" disabled={!canSend} type="submit"><Icon name="arrow_upward" />
          {t(generating ? "steer" : "send")}</button>
      </div>
    </div>
  </form>;
}
