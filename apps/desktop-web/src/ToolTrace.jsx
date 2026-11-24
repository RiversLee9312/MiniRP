import { memo, useEffect, useMemo, useState } from "react";
import { parseTree } from "jsonc-parser";
import { Icon, IconButton } from "./ui.jsx";
import { OpenUi } from "./OpenUi.jsx";

const fieldLimit = 180;
const fieldCountLimit = 16;
const nodeText = (source, node) => source.slice(node.offset, node.offset + node.length);

function FoldedField({ prefix, suffix, preview, render }) {
  const [expanded, setExpanded] = useState(false);
  return <details className="trace-json-field" onToggle={(event) => setExpanded(event.currentTarget.open)}>
    <summary><Icon name="expand_more" /><span>{prefix}<span className="trace-json-preview">{preview}</span>{suffix}</span></summary>
    {expanded && <div className="trace-json-children">{render()}</div>}
  </details>;
}

function JsonValue({ source, node, name, suffix = "", depth = 0 }) {
  const prefix = name === undefined ? null : <><span className="trace-json-key">{name}</span>{": "}</>;
  const container = node.type === "array" || node.type === "object";
  if (!container || depth >= 32) {
    const text = nodeText(source, node);
    const token = <span className="trace-json-token" data-type={node.type}>{text}</span>;
    if (text.length > fieldLimit) return <FoldedField {...{ prefix, suffix }} preview={text.slice(0, 72) + "…"}
      render={() => <div>{token}</div>} />;
    return <div>{prefix}{token}{suffix}</div>;
  }

  const array = node.type === "array";
  const open = array ? "[" : "{";
  const close = array ? "]" : "}";
  const fields = node.children || [];
  if (!fields.length) return <div>{prefix}{open}{close}{suffix}</div>;

  function contents() {
    return <>
      <div>{prefix}{open}</div>
      <div className="trace-json-children">
        {fields.map((field, index) => <JsonValue key={field.offset} source={source}
          node={array ? field : field.children[1]} depth={depth + 1}
          name={array ? undefined : nodeText(source, field.children[0])} suffix={index < fields.length - 1 ? "," : ""} />)}
      </div>
      <div>{close}{suffix}</div>
    </>;
  }

  if ((depth > 0 && node.length > fieldLimit) || fields.length > fieldCountLimit)
    return <FoldedField {...{ prefix, suffix }} preview={open + "…" + close} render={contents} />;
  return contents();
}

function PresentedTool({ active, presentation, blockId, phase, view, t, fallback }) {
  const [rendered, setRendered] = useState(null);
  useEffect(() => {
    if (!active || rendered !== null) return;
    const timeout = setTimeout(() => setRendered(false), 5000);
    return () => clearTimeout(timeout);
  }, [active, rendered]);
  if (rendered === false) return fallback;
  const block = {
    id: blockId, open_ui: presentation.program, open_ui_title: presentation.title,
    open_ui_state: presentation.state, open_ui_revision: 0,
  };
  const identity = JSON.stringify([view.scope.preset, view.scope.room, view.scope.conversation, blockId, phase]);
  return <>
    <OpenUi {...view} block={block} identity={identity} t={t} toolPresentation onRenderStatus={setRendered} />
    <details className="trace-json-source">
      <summary><Icon name="expand_more" />JSON</summary>
      {fallback}
    </details>
  </>;
}

export const ToolTrace = memo(function ToolTrace({ source, title, t, presentation, active, blockId, phase, view }) {
  const presentationKey = useMemo(() => presentation?.program
    && JSON.stringify([presentation.program, presentation.title, presentation.state]), [presentation]);
  const { text, node } = useMemo(() => {
    const text = typeof source === "string" ? source : JSON.stringify(source);
    try {
      const errors = [];
      const node = parseTree(text, errors, { disallowComments: true });
      return { text, node: errors.length ? undefined : node };
    }
    catch { return { text }; }
  }, [source]);
  const fallback = <div className="trace-json" dir="ltr">
      {node ? <JsonValue source={text} node={node} />
        : text.length > fieldLimit || text.split("\n").length > 4
          ? <FoldedField preview={text.replace(/\s+/g, " ").slice(0, 72) + "…"} render={() => <div dir="auto">{text}</div>} />
          : <div dir="auto">{text}</div>}
  </div>;
  return <section className="trace-tool" aria-label={title}>
    <div className="trace-tool-label">
      <span>{title}</span>
      <IconButton icon="content_copy" label={t("copy")} onClick={() => navigator.clipboard.writeText(text)} />
    </div>
    {presentationKey ? <PresentedTool key={presentationKey}
      {...{ active, presentation, blockId, phase, view, t, fallback }} /> : fallback}
  </section>;
});
