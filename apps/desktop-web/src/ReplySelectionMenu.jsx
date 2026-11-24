import { useEffect, useRef } from "react";
import { Icon } from "./ui.jsx";

function selectedReply(transcript) {
  const selection = document.getSelection();
  if (selection?.rangeCount !== 1 || selection.isCollapsed) return null;
  const range = selection.getRangeAt(0);
  function reply(node) {
    return (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement)?.closest(".assistant-reply");
  }
  const start = reply(range.startContainer);
  const text = selection.toString();
  if (!start || start !== reply(range.endContainer) || !transcript.contains(start) || !text.trim()) return null;
  return { text, range };
}

export function ReplySelectionMenu({ transcript, onQuote, t }) {
  const popup = useRef(null);
  const selected = useRef("");
  useEffect(() => {
    const scroller = transcript.current;
    const menu = popup.current;

    function dismiss() {
      if (menu.matches(":popover-open")) menu.hidePopover();
    }

    function show(event) {
      if (event.type === "pointerup" && (event.button !== 0 || event.pointerType === "touch")) return;
      const pick = selectedReply(scroller);
      if (!pick) return;
      const bounds = pick.range.getBoundingClientRect();
      const viewport = scroller.getBoundingClientRect();
      if (bounds.bottom < viewport.top || bounds.top > viewport.bottom) return;
      if (event.type === "contextmenu") {
        if ((event.clientX || event.clientY) && !Array.from(pick.range.getClientRects()).some((part) =>
          event.clientX >= part.left && event.clientX <= part.right && event.clientY >= part.top && event.clientY <= part.bottom))
          return;
        event.preventDefault();
      }

      selected.current = pick.text;
      if (!menu.matches(":popover-open")) menu.showPopover();
      const margin = 8;
      const above = Math.max(bounds.top, viewport.top) - menu.offsetHeight - margin;
      const below = Math.min(bounds.bottom, viewport.bottom) + margin;
      const left = bounds.left + (bounds.width - menu.offsetWidth) / 2;
      menu.style.left = Math.max(margin, Math.min(left, innerWidth - menu.offsetWidth - margin)) + "px";
      menu.style.top = Math.max(margin, Math.min(above >= margin ? above : below, innerHeight - menu.offsetHeight - margin)) + "px";
      if (event.type === "contextmenu" && !event.clientX && !event.clientY) menu.querySelector("button").focus();
    }

    function selectionChanged() {
      if (!menu.matches(":popover-open") || menu.contains(document.activeElement)) return;
      if (selectedReply(scroller)?.text !== selected.current) dismiss();
    }

    scroller.addEventListener("pointerup", show);
    scroller.addEventListener("contextmenu", show);
    scroller.addEventListener("scroll", dismiss);
    window.addEventListener("resize", dismiss);
    document.addEventListener("selectionchange", selectionChanged);
    return () => {
      scroller.removeEventListener("pointerup", show);
      scroller.removeEventListener("contextmenu", show);
      scroller.removeEventListener("scroll", dismiss);
      window.removeEventListener("resize", dismiss);
      document.removeEventListener("selectionchange", selectionChanged);
    };
  }, [transcript]);

  function act(action) {
    const text = selected.current;
    popup.current.hidePopover();
    action(text);
  }

  return <div ref={popup} className="action-menu reply-selection-menu" popover="auto" role="menu" aria-label={t(onQuote ? "quote_reply" : "copy")}
    onPointerDown={(event) => event.preventDefault()} onKeyDown={(event) => {
      if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
      event.preventDefault();
      const items = Array.from(event.currentTarget.querySelectorAll("button"));
      (event.key === "Home" ? items[0] : event.key === "End" ? items.at(-1)
        : items.find((item) => item !== document.activeElement))?.focus();
    }}>
    {onQuote && <button type="button" role="menuitem" className="action-menu-item" onClick={() => act(onQuote)}>
      <Icon name="format_quote" />{t("quote_reply")}
    </button>}
    <button type="button" role="menuitem" className="action-menu-item" onClick={() => act((text) => navigator.clipboard.writeText(text))}>
      <Icon name="content_copy" />{t("copy")}
    </button>
  </div>;
}
