import { useEffect, useRef, useState } from "react";
import { getDocument, GlobalWorkerOptions } from "pdfjs-dist";
import worker from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import { PreviewPager } from "./AttachmentPreview.jsx";

GlobalWorkerOptions.workerSrc = worker;
const resources = import.meta.env.DEV ? "/node_modules/pdfjs-dist/" : "/pdf-preview/";

export default function AttachmentPdfPreview({ src, t }) {
  const [document, setDocument] = useState(null);
  const [index, setIndex] = useState(0);
  const [failed, setFailed] = useState(false);
  const canvas = useRef(null);
  const render = useRef(null);

  useEffect(() => {
    let active = true;
    const loading = getDocument({
      url: src,
      isEvalSupported: false,
      useWasm: false,
      cMapUrl: resources + "cmaps/",
      cMapPacked: true,
      standardFontDataUrl: resources + "standard_fonts/",
    });
    loading.promise.then((doc) => { if (active) setDocument(doc); })
      .catch(() => { if (active) setFailed(true); });
    return () => {
      active = false;
      void loading.destroy().catch(() => {});
    };
  }, [src]);

  useEffect(() => {
    if (!document) return;
    let active = true;
    const previous = render.current;
    async function draw() {
      try {
        if (previous) await previous.promise.catch(() => {});
        const page = await document.getPage(index + 1);
        if (!active) return;
        const original = page.getViewport({ scale: 1 });
        const viewport = page.getViewport({ scale: Math.min(2.5, 1900 / Math.max(original.width, original.height)) });
        const target = canvas.current;
        target.width = Math.ceil(viewport.width);
        target.height = Math.ceil(viewport.height);
        render.current = page.render({ canvasContext: target.getContext("2d"), viewport });
        await render.current.promise;
      } catch {
        if (active) setFailed(true);
      }
    }
    void draw();
    return () => {
      active = false;
      render.current?.cancel();
    };
  }, [document, index]);

  if (failed) return <p role="status">{t("attachment_read_failed")}</p>;
  if (!document) return <p role="status">{t("attachment_loading")}</p>;
  return <>
    <div className="attachment-preview-scroll attachment-preview-pdf" tabIndex={0} aria-label={t("preview_attachment_label")}>
      <canvas ref={canvas} role="img" aria-label={t("preview_pdf_page", index + 1, document.numPages)} />
    </div>
    <PreviewPager index={index} total={document.numPages} select={setIndex} t={t} />
  </>;
}
