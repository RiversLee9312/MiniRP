import { useLayoutEffect, useRef, useState } from "react";
import "./TranscriptRuler.css";

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const scrollRange = (element) => Math.max(0, element.scrollHeight - element.clientHeight);
const tickInset = 12;
const tickGap = 16;
const tickOffset = (index) => tickInset + index * tickGap;
const tickTrackHeight = (count) => count ? tickOffset(count - 1) + tickInset : 0;

function rowOffset(element, row, top = element.getBoundingClientRect().top) {
  if (!row) return null;
  const chunk = row.parentElement;
  if (chunk.checkVisibility?.({ contentVisibilityAuto: true }) === false) {
    const bounds = chunk.getBoundingClientRect();
    const index = Array.prototype.indexOf.call(chunk.children, row);
    return bounds.top + index * bounds.height / chunk.childElementCount - top + element.scrollTop;
  }
  return row.getBoundingClientRect().top - top + element.scrollTop;
}

function currentTurn(element, turns, rowFor, range) {
  if (!turns.length || !range) return 0;
  if (range - element.scrollTop < 3) return turns.length - 1;
  const top = element.getBoundingClientRect().top;
  let low = 0;
  let high = turns.length;
  while (low < high) {
    const index = (low + high) >>> 1;
    const offset = rowOffset(element, rowFor(turns[index]), top);
    if (offset == null || offset > element.scrollTop + 20) high = index;
    else low = index + 1;
  }
  return Math.max(0, low - 1);
}

function visibleTicks(count, scrollTop, height) {
  const first = Math.max(0, Math.floor((scrollTop - tickInset) / tickGap) - 2);
  const limit = Math.min(count, Math.ceil((scrollTop + height - tickInset) / tickGap) + 3);
  return Array.from({ length: Math.max(0, limit - first) }, (_, index) => first + index);
}

function Preview({ turn, t }) {
  return <div className="transcript-ruler-card">
    {turn.prompt && <div>
      <strong>{t(turn.kind === "system" ? "system_message" : "transcript_user_message")}</strong>
      <span dir="auto">{turn.prompt}</span>
    </div>}
    {turn.kind === "interjection" ? <div className="transcript-ruler-interjection">
      {t("transcript_interjection")}
    </div> : (turn.answer || turn.card) && <div>
      <strong>{t("transcript_assistant_reply")}</strong>
      <span dir="auto">{turn.answer || (turn.card === true ? t("interactive_reply") : turn.card)}</span>
    </div>}
  </div>;
}

export function TranscriptRuler({ scroller, controls, rowPrefix, turns, hasOlder, loadOlder, t }) {
  const track = useRef(null);
  const viewport = useRef(null);
  const pointer = useRef(null);
  const pointerFrame = useRef(0);
  const navigationFrame = useRef(0);
  const dragging = useRef(null);
  const selected = useRef(null);
  const requestMeasure = useRef(null);
  const turnsRef = useRef(turns);
  const [position, setPosition] = useState({ visible: false, current: 0, height: 0, railStart: 0, railTop: 0 });
  const [hover, setHover] = useState(null);
  const rowFor = (turn) => document.getElementById(rowPrefix + turn.id);

  function pointerIndex(clientY, draggingOutside = false) {
    const rail = viewport.current;
    const count = turnsRef.current.length;
    if (!rail || !count) return null;
    const relativeY = clientY - rail.getBoundingClientRect().top;
    const offset = relativeY + rail.scrollTop;
    if (!draggingOutside && (relativeY < 0 || relativeY >= rail.clientHeight || offset >= tickTrackHeight(count))) return null;
    return clamp(Math.round((offset - tickInset) / tickGap), 0, count - 1);
  }

  useLayoutEffect(() => {
    turnsRef.current = turns;
    requestMeasure.current?.();
  }, [turns]);

  useLayoutEffect(() => {
    const element = scroller.current;
    const rail = viewport.current;
    const ruler = track.current;
    if (!element || !rail || !ruler) return;
    let frame = 0;
    const measure = () => {
      frame = 0;
      const currentTurns = turnsRef.current;
      const range = scrollRange(element);
      const visible = range > 2 && currentTurns.length > 0;
      const chosen = selected.current;
      const chosenIndex = chosen && Math.abs(element.scrollTop - chosen.offset) < 3
        ? (currentTurns[chosen.index]?.id === chosen.id ? chosen.index : currentTurns.findIndex((turn) => turn.id === chosen.id)) : -1;
      if (chosenIndex < 0) selected.current = null;
      const current = visible ? (chosenIndex < 0 ? currentTurn(element, currentTurns, rowFor, range) : chosenIndex) : 0;
      const height = rail.clientHeight;
      const railStart = rail.offsetTop;
      const railTop = rail.scrollTop;
      setPosition((last) => last.visible === visible && last.current === current && last.height === height &&
        last.railStart === railStart && last.railTop === railTop ? last : { visible, current, height, railStart, railTop });
      setHover(visible && pointer.current?.preview ? pointerIndex(pointer.current.y) : null);
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
    requestMeasure.current = schedule;
    const wheel = (event) => {
      if (event.ctrlKey || !event.deltaY || rail.scrollHeight > rail.clientHeight + 1) return;
      const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? element.clientHeight : 1;
      event.preventDefault();
      element.scrollTop += event.deltaY * unit;
    };
    const observer = new ResizeObserver(schedule);
    observer.observe(element);
    observer.observe(rail);
    if (element.firstElementChild) observer.observe(element.firstElementChild);
    element.addEventListener("scroll", schedule, { passive: true });
    rail.addEventListener("scroll", schedule, { passive: true });
    ruler.addEventListener("wheel", wheel, { passive: false });
    measure();
    return () => {
      requestMeasure.current = null;
      cancelAnimationFrame(frame);
      cancelAnimationFrame(pointerFrame.current);
      cancelAnimationFrame(navigationFrame.current);
      observer.disconnect();
      element.removeEventListener("scroll", schedule);
      rail.removeEventListener("scroll", schedule);
      ruler.removeEventListener("wheel", wheel);
    };
  }, [scroller, rowPrefix]);

  function revealTurn(index) {
    const rail = viewport.current;
    if (!rail) return;
    const offset = tickOffset(index);
    if (offset < rail.scrollTop + tickInset || offset > rail.scrollTop + rail.clientHeight - tickInset)
      rail.scrollTop = offset - rail.clientHeight / 2;
  }

  useLayoutEffect(() => {
    if (position.visible && !pointer.current) revealTurn(position.current);
  }, [position.visible, position.current, position.height, turns.length]);

  function goTo(index) {
    const element = scroller.current;
    const turn = turnsRef.current[index];
    const row = turn && rowFor(turn);
    if (!element || !row) return;
    cancelAnimationFrame(navigationFrame.current);
    element.scrollTop = rowOffset(element, row) - 14;
    const target = { id: turn.id, index, offset: element.scrollTop };
    selected.current = target;
    requestMeasure.current?.();
    // Once the chosen chunk enters the viewport its reserved size is replaced
    // by actual row heights. Refine the landing if the reader hasn't moved.
    navigationFrame.current = requestAnimationFrame(() => {
      if (Math.abs(element.scrollTop - target.offset) > 2 || selected.current !== target) return;
      element.scrollTop = rowOffset(element, row) - 14;
      target.offset = element.scrollTop;
      requestMeasure.current?.();
    });
  }

  function clearPreview() {
    pointer.current = null;
    setHover(null);
  }

  function movePointer(event) {
    if (event.pointerType === "touch") return;
    pointer.current = { y: event.clientY, preview: true };
    if (pointerFrame.current) return;
    pointerFrame.current = requestAnimationFrame(() => {
      pointerFrame.current = 0;
      if (!pointer.current) return;
      const index = pointerIndex(pointer.current.y, dragging.current !== null);
      if (index === null) {
        clearPreview();
        return;
      }
      if (dragging.current !== null && selected.current?.id !== turnsRef.current[index]?.id) goTo(index);
      if (pointer.current.preview) setHover(index);
    });
  }

  function releasePointer(event) {
    if (dragging.current !== event.pointerId) return;
    if (event.type === "pointerup") goTo(pointerIndex(event.clientY, true));
    dragging.current = null;
    const bounds = track.current.getBoundingClientRect();
    if (event.pointerType === "touch" || event.type === "pointercancel" ||
      event.clientX < bounds.left || event.clientX > bounds.right || pointerIndex(event.clientY) === null)
      clearPreview();
  }

  function navigate(event) {
    const element = scroller.current;
    const page = element.clientHeight * .9;
    if (event.key === "Escape") {
      clearPreview();
      return;
    }
    if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      const offset = event.key === "ArrowUp" ? -1 : 1;
      const index = clamp((selected.current?.index ?? position.current) + offset, 0, turns.length - 1);
      goTo(index);
      revealTurn(index);
    } else {
      selected.current = null;
      if (event.key === "Home") {
        if (element.scrollTop === 0 && hasOlder) void loadOlder?.();
        element.scrollTop = 0;
      } else if (event.key === "End") element.scrollTop = scrollRange(element);
      else if (event.key === "PageUp") element.scrollTop -= page;
      else if (event.key === "PageDown") element.scrollTop += page;
      else return;
      requestMeasure.current?.();
    }
    clearPreview();
    event.preventDefault();
  }

  const count = turns.length;
  const current = clamp(position.current, 0, Math.max(0, count - 1));
  const hovered = hover !== null && hover < count ? hover : null;
  const hoveredTurn = hovered === null ? null : turns[hovered];
  const trackHeight = tickTrackHeight(count);
  return <div className="transcript-ruler" ref={track} hidden={!position.visible} role="scrollbar" tabIndex={0}
    style={{ "--ruler-track-height": `${trackHeight}px` }}
    aria-label={t("transcript_position")} aria-controls={controls} aria-orientation="vertical"
    aria-valuemin={1} aria-valuemax={Math.max(1, count)} aria-valuenow={current + 1} onKeyDown={navigate}
    data-more-above={position.railTop > 1} data-more-below={trackHeight - position.height - position.railTop > 1}
    onFocus={(event) => { if (event.target === event.currentTarget) revealTurn(current); }}>
    <div className="transcript-ruler-viewport" ref={viewport} tabIndex={-1}>
      <div className="transcript-ruler-ticks" aria-hidden="true" style={{ blockSize: trackHeight }}
        onPointerMove={movePointer} onPointerLeave={() => { if (dragging.current === null) clearPreview(); }}
        onPointerDown={(event) => {
          if (event.pointerType === "touch" || !event.isPrimary || event.button !== 0) return;
          event.preventDefault();
          event.currentTarget.setPointerCapture(event.pointerId);
          dragging.current = event.pointerId;
          movePointer(event);
        }} onPointerUp={releasePointer} onPointerCancel={releasePointer} onLostPointerCapture={releasePointer}
        onClick={(event) => { if (event.nativeEvent.pointerType === "touch") goTo(pointerIndex(event.clientY)); }}>
        {visibleTicks(count, position.railTop, position.height).map((index) => <span key={turns[index].id}
          data-turn={turns[index].id} data-current={index === current} data-hover={index === hovered}
          style={{ "--tick-position": `${tickOffset(index)}px`,
            "--proximity": Math.max(0, 1 - Math.abs(index - (hovered ?? -8)) / 4) }} />)}
      </div>
    </div>
    {hoveredTurn && <div className="transcript-ruler-preview" aria-hidden="true"
      style={{ "--preview-position": `${position.railStart + tickOffset(hovered) - position.railTop}px` }}>
      <Preview turn={hoveredTurn} t={t} />
    </div>}
  </div>;
}
