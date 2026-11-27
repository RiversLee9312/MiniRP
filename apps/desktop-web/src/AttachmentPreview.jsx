import { lazy, Suspense, useEffect, useState } from "react";
import { assetUrl, attachmentUrl } from "./api.js";
import AttachmentAudioPreview from "./AttachmentAudioPreview.jsx";
import { Icon, IconButton, Modal, Surface } from "./ui.jsx";

const ImagePreview = lazy(() => import("./AttachmentImagePreview.jsx"));
const PdfPreview = lazy(() => import("./AttachmentPdfPreview.jsx"));

const imageTypes = /^(image\/(png|jpeg|gif|webp|avif|bmp|x-icon|vnd\.microsoft\.icon))$/;
const imageNames = /\.(png|jpe?g|gif|webp|avif|bmp|ico)$/i;

function attachmentKind(file) {
  const mime = file.mime || "";
  if (imageTypes.test(mime) || imageNames.test(file.name)) return "image";
  if (mime.startsWith("audio/") || /\.(mp3|wav|m4a|aac|flac|oga|opus)$/i.test(file.name)) return "audio";
  if (mime.startsWith("video/") || /\.(mp4|webm|mov|mkv|ogv)$/i.test(file.name)) return "video";
  if (mime === "application/pdf" || /\.pdf$/i.test(file.name)) return "pdf";
  if (mime.startsWith("text/") || ["application/json", "application/xml"].includes(mime) ||
      /\.(txt|csv|md|log|json|xml|html?)$/i.test(file.name)) return "text";
  return "";
}

function Thumbnail({ file, scope, kind }) {
  const [failed, setFailed] = useState(false);
  const thumbnail = file.thumbnail_url || (file.thumbnail &&
    "minirp://assets/minirp.legacy/attachment-thumbnail?attachment=" + file.id);
  const source = thumbnail ? assetUrl(thumbnail, scope, { inline: "1", attachment: JSON.stringify(file) })
    : kind === "image" ? attachmentUrl(file, true) : "";
  return !source || failed ? <Icon name={kind === "audio" || kind === "video" ? "play_arrow" : "browse"} />
    : <img src={source} loading="lazy" alt="" onError={() => setFailed(true)} />;
}

function TextPreview({ src, t }) {
  const [result, setResult] = useState(null);
  useEffect(() => {
    const request = new AbortController();
    const limit = 256 * 1024;
    async function read() {
      try {
        const response = await fetch(src, { signal: request.signal, headers: { Range: "bytes=0-" + (limit - 1) } });
        if (!response.ok || !response.headers.get("Content-Type")?.startsWith("text/plain")) throw new Error();
        const bytes = new Uint8Array(await response.arrayBuffer());
        const encoding = bytes[0] === 0xff && bytes[1] === 0xfe ? "utf-16le"
          : bytes[0] === 0xfe && bytes[1] === 0xff ? "utf-16be" : "utf-8";
        const total = Number(response.headers.get("Content-Range")?.split("/").at(-1));
        if (!request.signal.aborted) setResult({
          text: new TextDecoder(encoding).decode(bytes.subarray(0, limit)), truncated: total > limit || bytes.length > limit,
        });
      } catch {
        if (!request.signal.aborted) setResult({ error: true });
      }
    }
    void read();
    return () => request.abort();
  }, [src]);
  if (!result) return <p role="status">{t("attachment_loading")}</p>;
  if (result.error) return <p role="status">{t("attachment_read_failed")}</p>;
  return <div className="attachment-preview-scroll attachment-preview-text" tabIndex={0} aria-label={t("preview_attachment_label")}>
    <pre dir="auto">{result.text}</pre>
    {result.truncated && <p className="muted">{t("attachment_preview_truncated")}</p>}
  </div>;
}

function PreviewContent({ file, t }) {
  const [failed, setFailed] = useState(false);
  const kind = attachmentKind(file);
  const src = attachmentUrl(file, true);
  if (failed || !kind) return <div className="attachment-preview-empty"><Icon name="browse" />
    <p role="status">{t(failed ? "attachment_read_failed" : "attachment_preview_unavailable")}</p>
  </div>;
  if (kind === "video") return <video controls playsInline preload="metadata" src={src} aria-label={file.name} onError={() => setFailed(true)} />;
  if (kind === "audio") return <AttachmentAudioPreview src={src} t={t} onError={() => setFailed(true)} />;
  if (kind === "text") return <TextPreview src={src} t={t} />;
  return <Suspense fallback={<p role="status">{t("attachment_loading")}</p>}>
    {kind === "pdf" ? <PdfPreview src={src} t={t} /> : <ImagePreview src={src} name={file.name} t={t} onError={() => setFailed(true)} />}
  </Suspense>;
}

export function AttachmentList({ files, scope, t, quote, remove }) {
  const [active, setActive] = useState(null);
  const documents = files.filter((file) => file.kind !== "quote");
  const index = documents.findIndex((file) => file.id === active);
  const current = documents[index];
  if (!files.length) return null;
  const close = () => setActive(null);
  return <div className="attachments">
    {files.map((file) => file.kind === "quote" ? quote(file) : <div className="attachment attachment-file" key={file.id}>
      <button className="attachment-open" type="button" aria-haspopup="dialog"
        aria-label={t("preview_attachment", file.name)} onClick={() => setActive(file.id)}>
        <Thumbnail file={file} kind={attachmentKind(file)} scope={scope} />
        <span title={file.name}>{file.name}</span>
      </button>
      {remove && <IconButton icon="close" label={t("remove_attachment", file.name)} onClick={() => remove(file.id)} />}
    </div>)}
    <Surface>{current && <Modal title={current.name} close={close} t={t} wide
      className={"attachment-preview" + (attachmentKind(current) === "audio" ? " attachment-preview-audio" : "")}
      dismissOnBackdrop actions={ <a className="icon-button" href={attachmentUrl(current)} download
        aria-label={t("download_attachment")} title={t("download_attachment")}><Icon name="download" /></a> }>
      <div className="attachment-preview-stage" key={current.id}><PreviewContent file={current} t={t} /></div>
      {documents.length > 1 && <PreviewPager index={index} total={documents.length} t={t}
        select={(next) => setActive(documents[next].id)} />}
    </Modal>}</Surface>
  </div>;
}

export function PreviewPager({ index, total, select, t }) {
  return <nav className="attachment-preview-pager">
    <IconButton className="directional" icon="chevron_left" label={t("preview_previous")} disabled={index === 0}
      onClick={() => select(index - 1)} />
    <bdi aria-live="polite">{index + 1} / {total}</bdi>
    <IconButton className="directional" icon="chevron_right" label={t("preview_next")} disabled={index + 1 === total}
      onClick={() => select(index + 1)} />
  </nav>;
}
