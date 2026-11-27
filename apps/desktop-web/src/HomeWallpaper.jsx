import { useEffect, useLayoutEffect, useRef, useState } from "react";

const linearRgb = Array.from({ length: 256 }, (_, channel) => {
  const s = channel / 255;
  return s <= .04045 ? s / 12.92 : ((s + .055) / 1.055) ** 2.4;
});

function paintContrast(context, pane, media, heading) {
  const viewport = media.getBoundingClientRect();
  const sourceWidth = media.naturalWidth ?? media.videoWidth ?? media.width;
  const sourceHeight = media.naturalHeight ?? media.videoHeight ?? media.height;
  if (!viewport.width || !viewport.height || !sourceWidth || !sourceHeight) return;
  const scale = Math.min(1, 192 / Math.max(viewport.width, viewport.height));
  const width = Math.max(1, Math.round(viewport.width * scale));
  const height = Math.max(1, Math.round(viewport.height * scale));
  const canvas = context.canvas;
  if (canvas.width !== width) canvas.width = width;
  if (canvas.height !== height) canvas.height = height;
  const fit = Math.max(viewport.width / sourceWidth, viewport.height / sourceHeight);
  const cropWidth = viewport.width / fit;
  const cropHeight = viewport.height / fit;
  context.fillStyle = getComputedStyle(pane).backgroundColor;
  context.fillRect(0, 0, width, height);
  context.drawImage(media, (sourceWidth - cropWidth) / 2, (sourceHeight - cropHeight) / 2,
    cropWidth, cropHeight, 0, 0, width, height);
  const pixels = context.getImageData(0, 0, width, height).data;
  const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
  const xScale = width / viewport.width;
  const yScale = height / viewport.height;
  const range = document.createRange();
  let blackFailure = 0;
  let whiteFailure = 0;
  let advantage = 0;
  let sampled = false;
  for (const headingText of heading.querySelectorAll(".preset-home-caption, .home-preset-name")) {
    range.selectNodeContents(headingText);
    for (const rect of range.getClientRects()) {
      if (!rect.width || !rect.height || rect.bottom <= viewport.top || rect.top >= viewport.bottom ||
          rect.right <= viewport.left || rect.left >= viewport.right) continue;
      sampled = true;
      const left = clamp(Math.floor((rect.left - viewport.left) * xScale), 0, width - 1);
      const right = clamp(Math.ceil((rect.right - viewport.left) * xScale), left + 1, width);
      const top = clamp(Math.floor((rect.top - viewport.top) * yScale), 0, height - 1);
      const bottom = clamp(Math.ceil((rect.bottom - viewport.top) * yScale), top + 1, height);
      let blackMisses = 0;
      let whiteMisses = 0;
      for (let y = top; y < bottom; y++) {
        for (let x = left; x < right; x++) {
          const pixel = (y * width + x) * 4;
          const level = .05 + .2126 * linearRgb[pixels[pixel]] +
            .7152 * linearRgb[pixels[pixel + 1]] + .0722 * linearRgb[pixels[pixel + 2]];
          const black = level / .05;
          const white = 1.05 / level;
          if (black < 3) blackMisses++;
          if (white < 3) whiteMisses++;
          advantage += black - white;
        }
      }
      const area = (right - left) * (bottom - top);
      blackFailure = Math.max(blackFailure, blackMisses / area);
      whiteFailure = Math.max(whiteFailure, whiteMisses / area);
    }
  }
  if (!sampled) return;
  // Protect the least readable line first, then use average contrast to break a tie.
  const black = blackFailure < whiteFailure || blackFailure === whiteFailure && advantage >= 0;
  const ink = black ? "black" : "white";
  const halo = Math.min(blackFailure, whiteFailure) > .10
    ? `0 0 24px rgb(${black ? "255 255 255" : "0 0 0"} / 80%)` : "none";
  heading.style.setProperty("--home-title-ink", ink);
  heading.style.setProperty("--home-title-halo", halo);
}

export function useWallpaperContrast(heading, wallpaper, theme, caption, title) {
  useLayoutEffect(() => {
    if (!heading.current || !wallpaper) return;
    const node = heading.current;
    const pane = node.closest(".main");
    const scroll = node.closest(".preset-home");
    const context = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
    if (!context) return;
    let frame = 0;
    let timer = 0;
    let disposed = false;
    const sample = () => {
      frame = 0;
      const media = pane.querySelector(".workspace-background");
      if (!media) return;
      try { paintContrast(context, pane, media, node); }
      catch { /* Keep the current text color until a valid frame is available. */ }
      if (media instanceof HTMLVideoElement && !media.paused && !timer) {
        timer = setTimeout(() => { timer = 0; schedule(); }, 83);
      }
    };
    const schedule = () => {
      if (!disposed && !frame) frame = requestAnimationFrame(sample);
    };
    const resize = new ResizeObserver(schedule);
    resize.observe(pane);
    resize.observe(node);
    const children = new MutationObserver(schedule);
    children.observe(pane, { childList: true });
    children.observe(node, { childList: true, subtree: true });
    const events = ["load", "loadeddata", "playing", "wallpaper-frame"];
    for (const event of events) pane.addEventListener(event, schedule, true);
    scroll.addEventListener("scroll", schedule, { passive: true });
    scroll.addEventListener("animationend", schedule);
    document.fonts.ready.then(schedule);
    schedule();
    return () => {
      disposed = true;
      cancelAnimationFrame(frame);
      clearTimeout(timer);
      resize.disconnect();
      children.disconnect();
      for (const event of events) pane.removeEventListener(event, schedule, true);
      scroll.removeEventListener("scroll", schedule);
      scroll.removeEventListener("animationend", schedule);
    };
  }, [heading, wallpaper, theme, caption, title]);
}

function AnimatedWallpaper({ animation, onReady, onFailure }) {
  const canvas = useRef(null);
  useEffect(() => {
    const target = canvas.current;
    const context = target.getContext("2d");
    let stopped = false;
    let timer = 0;
    let index = 0;
    let ready = false;
    async function draw() {
      try {
        const { image } = await animation.decode({ frameIndex: index });
        let delay;
        try {
          if (stopped) return;
          if (target.width !== image.displayWidth) target.width = image.displayWidth;
          if (target.height !== image.displayHeight) target.height = image.displayHeight;
          context.clearRect(0, 0, target.width, target.height);
          context.drawImage(image, 0, 0);
          delay = Math.max(20, (image.duration || 100000) / 1000);
        } finally { image.close(); }
        if (!ready) {
          ready = true;
          onReady(target);
        }
        target.dispatchEvent(new Event("wallpaper-frame", { bubbles: true }));
        index = (index + 1) % animation.tracks.selectedTrack.frameCount;
        timer = setTimeout(draw, delay);
      } catch {
        if (!stopped) onFailure();
      }
    }
    draw();
    return () => { stopped = true; clearTimeout(timer); };
  }, [animation]);
  return <canvas ref={canvas} className="workspace-background" aria-hidden="true" />;
}

export function WorkspaceBackdrop({ src, onReady, readAnimation }) {
  const [video, setVideo] = useState(false);
  const [decoded, setDecoded] = useState(null);
  const useDecoder = readAnimation && typeof ImageDecoder === "function";
  useEffect(() => {
    if (!useDecoder) {
      setDecoded(null);
      return;
    }
    const request = new AbortController();
    let url;
    let animation;
    async function prepare() {
      const response = await fetch(src, { signal: request.signal });
      if (!response.ok) throw new Error("Wallpaper could not be loaded");
      if (response.headers.get("content-type")?.startsWith("video/")) {
        response.body?.cancel().catch(() => {});
        if (!request.signal.aborted) setDecoded({ src, url: src, video: true });
        return;
      }
      const file = await response.blob();
      if (request.signal.aborted) return;
      url = URL.createObjectURL(file);
      try {
        if (["image/gif", "image/png", "image/webp", "image/apng"].includes(file.type) &&
            await ImageDecoder.isTypeSupported(file.type) && !request.signal.aborted) {
          animation = new ImageDecoder({ data: file.stream(), type: file.type, preferAnimation: true });
          await animation.tracks.ready;
          if (animation.tracks.selectedTrack?.animated) {
            await animation.completed;
          } else {
            animation.close();
            animation = undefined;
          }
        }
      } catch { animation?.close(); animation = undefined; }
      if (!request.signal.aborted) setDecoded({ src, url, animation });
    }
    prepare().catch(() => {
      if (!request.signal.aborted) setDecoded({ src, url: src });
    });
    return () => {
      request.abort();
      animation?.close();
      if (url) URL.revokeObjectURL(url);
    };
  }, [src, useDecoder]);
  const resource = useDecoder ? decoded : null;
  if (useDecoder && resource?.src !== src) return null;
  if (resource?.animation) return <AnimatedWallpaper animation={resource.animation} onReady={onReady}
    onFailure={() => setDecoded({ src, url: resource.url })} />;
  return video || resource?.video
    ? <video className="workspace-background" aria-hidden="true" src={resource?.url ?? src} autoPlay muted loop playsInline
        onLoadedData={(event) => onReady(event.currentTarget)} />
    : <img className="workspace-background" aria-hidden="true" alt="" src={resource?.url ?? src}
        onLoad={(event) => onReady(event.currentTarget)} onError={() => setVideo(true)} />;
}
