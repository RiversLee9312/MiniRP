import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Icon, Modal } from "./ui.jsx";
import { advanceRoomProgress, retargetRoomProgress } from "./roomMotion.js";

const motionPreference = matchMedia("(prefers-reduced-motion: reduce)");

function LoadingCaption({ text, reducedMotion }) {
  const graphemes = useMemo(() => typeof Intl.Segmenter === "function"
    ? Array.from(new Intl.Segmenter(document.documentElement.lang || undefined, { granularity: "grapheme" }).segment(text), (item) => item.segment)
    : Array.from(text), [text]);
  const length = graphemes.length;
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (reducedMotion) return;
    const start = performance.now();
    const duration = Math.min(1280, Math.max(280, length * 31));
    const timer = setInterval(() => {
      const shown = Math.min(length, Math.ceil((performance.now() - start) * length / duration));
      setCount(shown);
      if (shown === length) clearInterval(timer);
    }, 28);
    return () => clearInterval(timer);
  }, [length, reducedMotion]);
  const shown = reducedMotion ? length : count;
  return <p className="loading-copy" dir="auto">
    <span className="loading-copy-spacer" aria-hidden="true">{text}</span>
    <span className="sr-only">{text}</span>
    <span className="loading-text" data-typing={shown < length} aria-hidden="true">{graphemes.slice(0, shown).join("")}</span>
  </p>;
}

function LoadingProgress({ progress, ready, total, declared, reducedMotion, t }) {
  const track = useRef(null);
  const target = useRef(null);
  useLayoutEffect(() => {
    target.current = retargetRoomProgress(target.current, progress, ready, total, performance.now(), declared);
    if (reducedMotion) {
      target.current.value = target.current.confirmed;
      track.current.style.setProperty("--progress", (target.current.value * 100) + "%");
    }
  }, [progress, ready, total, declared, reducedMotion]);
  useEffect(() => {
    if (reducedMotion) return;
    let previous = performance.now();
    let frame;
    function animate(now) {
      const element = track.current;
      if (!element) return;
      const current = target.current;
      current.value = advanceRoomProgress(current, now, Math.min(90, now - previous));
      element.style.setProperty("--progress", (current.value * 100) + "%");
      previous = now;
      frame = requestAnimationFrame(animate);
    }
    frame = requestAnimationFrame(animate);
    return () => cancelAnimationFrame(frame);
  }, [reducedMotion]);
  return <div className="loading-progress" ref={track}>
    <progress className="sr-only" max={1} value={progress} aria-label={t("running")} />
    <div className="progress-track" aria-hidden="true"><span /><span /></div>
  </div>;
}

export function RoomLoading({ loading, title, stop, t }) {
  const [index, setIndex] = useState(0);
  const [reducedMotion, setReducedMotion] = useState(motionPreference.matches);
  const { ready, total, messages, progress } = loading;
  useEffect(() => {
    const change = (event) => setReducedMotion(event.matches);
    motionPreference.addEventListener("change", change);
    return () => motionPreference.removeEventListener("change", change);
  }, []);
  useEffect(() => {
    if (messages.length < 2) return;
    const timer = setInterval(() => setIndex((current) => (current + 1) % messages.length), 3000);
    return () => clearInterval(timer);
  }, [messages.length]);

  return <Modal title={t("running")} close={stop} heading={false} className="room-loading-modal" t={t}>
    <section className="room-loading">
      <span className="loading-emblem" aria-hidden="true"><Icon name="auto_awesome" /></span>
      <div className="loading-heading">
        <h2 dir="auto">{title || t("running")}</h2>
        {title && <span>{t("running")}</span>}
      </div>
      {messages[index] && <LoadingCaption text={messages[index]} reducedMotion={reducedMotion} key={index} />}
      <LoadingProgress {...{ progress, ready, total, reducedMotion, t }} declared={loading.declared_progress} />
      <button className="text-button" type="button" onClick={stop}>{t("stop")}</button>
    </section>
  </Modal>;
}
