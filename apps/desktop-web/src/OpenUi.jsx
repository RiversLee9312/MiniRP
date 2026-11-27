import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { Icon } from "./ui.jsx";
import { isRightToLeft } from "./i18n.js";

const savedState = new Map();
const foldHeight = 360;

async function previewImage(document, result, request) {
  const host = document?.getElementById("surface");
  if (!result.rendered || !host) return { captured: false, reason: "The card did not render." };
  const source_width = Math.ceil(host.clientWidth);
  const source_height = Math.min(1500, Math.ceil(host.getBoundingClientRect().height));
  const { x = 0, y = 0, width = source_width, height = source_height } = request.viewport || {};
  if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > source_width || y + height > source_height)
    return { captured: false, reason: "The viewport is outside the card." };
  try {
    const { toCanvas } = await import("html-to-image");
    await document.fonts.ready;
    const source = await toCanvas(host, { width: source_width, height: source_height, pixelRatio: 1, preferredFontFormat: "woff2" });
    const frame = document.createElement("canvas");
    frame.width = width;
    frame.height = height;
    frame.getContext("2d").drawImage(source, x, y, width, height, 0, 0, width, height);
    const image = frame.toDataURL("image/png").split(",")[1];
    if (image.length > 2_666_668) return { captured: false, reason: "The screenshot exceeded the image limit." };
    return { captured: true, width, height, source_width, source_height, cropped: Boolean(request.viewport), image };
  } catch {
    return { captured: false, reason: "The browser could not capture the card." };
  }
}

export function OpenUi({ block, scope, identity, locale, dark, theme, t, onAction, preview, onPreview, toolPresentation, onRenderStatus,
  readOnly = false, eager = false }) {
  const iframe = useRef(null);
  const frameId = useId();
  const [height, setHeight] = useState(48);
  const [ready, setReady] = useState(0);
  const [failed, setFailed] = useState(false);
  const [expandedCard, setExpandedCard] = useState(null);
  const presetHome = !preview && block.open_ui_host_action === "preset-home";
  const title = presetHome ? t("preset_return_home") : block.open_ui_title || t("interactive_reply");
  const program = presetHome
    ? `root = Col([Text(${JSON.stringify(t("preset_return_home_detail"))}), Button(${JSON.stringify(title)}, @ToAssistant("preset-home"))])`
    : block.open_ui;
  const long = !preview && !failed && height > foldHeight;
  const expanded = long && expandedCard === identity;
  const collapsed = long && !expanded;
  const callbacks = useRef({ onAction, onPreview, onRenderStatus });
  callbacks.current = { onAction, onPreview, onRenderStatus };
  useLayoutEffect(() => {
    function receive(event) {
      if (event.origin !== location.origin || event.source !== iframe.current?.contentWindow || !event.data?.minirp) return;
      const message = event.data.minirp;
      switch (message.type) {
        case "ready": setReady((seen) => seen + 1); break;
        case "height": {
          const measured = Math.max(40, Math.min(1800, Number(message.height) || 48));
          if (!preview && measured > foldHeight && document.activeElement === iframe.current)
            setExpandedCard(identity);
          setHeight(measured);
          break;
        }
        case "state":
          savedState.set(identity, message.value);
          if (savedState.size > 96) savedState.delete(savedState.keys().next().value);
          break;
        case "rendered": setFailed(false); callbacks.current.onRenderStatus?.(true); break;
        case "error": setFailed(true); callbacks.current.onRenderStatus?.(false); break;
        case "preview_result": {
          const data = message.result;
          if (preview?.capture_image) {
            previewImage(iframe.current.contentDocument, data, preview).then(({ image, ...screenshot }) =>
              callbacks.current.onPreview?.({ ...data, screenshot, image_base64: image }));
          } else callbacks.current.onPreview?.(data);
          break;
        }
        case "link":
        case "reply": callbacks.current.onAction?.(message); break;
      }
    }
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
  }, [identity, preview]);
  const { preset, room, conversation, agent } = scope;
  useEffect(() => {
    if (!ready) return;
    const state = {
      program, expanded, streaming: Boolean(block.streaming), locale,
      rtl: isRightToLeft(locale), dark, theme,
      managedState: block.open_ui_state, managedRevision: block.open_ui_revision,
      initialState: savedState.get(identity),
    };
    if (preview) state.previewSteps = preview.steps || [];
    iframe.current?.contentWindow.postMessage({ minirpRender: {
      scope: { preset, room, conversation, agent, mode: "display", block: block.id }, state,
    } }, location.origin);
  }, [ready, identity, locale, dark, theme, preset, room, conversation, agent,
    block.id, program, expanded, block.streaming, block.open_ui_state, block.open_ui_revision, preview]);
  return <div className="open-ui-card" data-card-expanded={expanded || undefined}
    role={long ? "group" : undefined} aria-label={long ? title : undefined}>
    {long && <div className="open-ui-controls">
      <button type="button" className="text-button open-ui-toggle" aria-expanded={expanded} aria-controls={frameId}
        onClick={() => setExpandedCard(expanded ? null : identity)}>
        <Icon name={expanded ? "expand_less" : "expand_more"} />
        {t(expanded ? "collapse" : "expand")}
      </button>
    </div>}
    <div className="open-ui-viewport" data-collapse-preview={collapsed || undefined}>
      <iframe ref={iframe} id={frameId} className="open-ui" src={toolPresentation ? "/openui/tool.html" : preview ? "/openui/preview.html" : "/openui/index.html"}
        title={title} inert={collapsed || readOnly} aria-hidden={collapsed || undefined}
        style={{ "--open-ui-height": `${height}px` }} data-expanded={expanded || undefined}
        loading={preview || eager ? "eager" : "lazy"} sandbox="allow-scripts allow-same-origin" />
    </div>
    {failed && !preview && !toolPresentation && <p className="muted">{t("interactive_reply_unavailable")}</p>}
  </div>;
}
