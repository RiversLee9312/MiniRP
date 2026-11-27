import { TransformComponent, TransformWrapper } from "react-zoom-pan-pinch";

export default function AttachmentImagePreview({ src, name, t, onError }) {
  return <TransformWrapper centerOnInit minScale={1} maxScale={6} limitToBounds>
    {({ zoomIn, zoomOut, resetTransform }) => <>
      <TransformComponent wrapperClass="attachment-image-viewport">
        <img src={src} alt={name} draggable={false} onError={onError} />
      </TransformComponent>
      <div className="attachment-zoom">
        <button type="button" className="text-button" onClick={() => zoomOut()} aria-label={t("preview_zoom_out")}>−</button>
        <button type="button" className="text-button" onClick={() => resetTransform()} aria-label={t("preview_zoom_reset")}>100%</button>
        <button type="button" className="text-button" onClick={() => zoomIn()} aria-label={t("preview_zoom_in")}>+</button>
      </div>
    </>}
  </TransformWrapper>;
}
