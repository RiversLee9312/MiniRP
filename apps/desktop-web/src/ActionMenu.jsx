import { useEffect, useId, useRef, useState } from "react";
import { Icon, IconButton } from "./ui.jsx";

function positionMenu(anchor, menu) {
  const margin = 8;
  const gap = 5;
  const bounds = anchor.getBoundingClientRect();
  const width = menu.offsetWidth;
  const rtl = getComputedStyle(anchor).direction === "rtl";
  const left = rtl ? bounds.left : bounds.right - width;
  const below = innerHeight - bounds.bottom - gap - margin;
  const above = bounds.top - gap - margin;
  const opensUp = menu.scrollHeight > below && above > below;
  const height = Math.min(menu.scrollHeight, Math.max(0, opensUp ? above : below));
  menu.style.left = Math.max(margin, Math.min(left, innerWidth - width - margin)) + "px";
  menu.style.top = (opensUp ? bounds.top - height - gap : bounds.bottom + gap) + "px";
  menu.style.maxHeight = height + "px";
  menu.dataset.origin = (opensUp ? "bottom" : "top") + (rtl ? " left" : " right");
}

export function ActionMenu({ label, groups, disabled, buttonRef }) {
  const id = useId();
  const localTrigger = useRef(null);
  const trigger = buttonRef || localTrigger;
  const menu = useRef(null);
  const [open, setOpen] = useState(false);
  const items = () => Array.from(menu.current.querySelectorAll('[role="menuitem"]:enabled'));

  function show(last = false) {
    const popup = menu.current;
    if (!popup.matches(":popover-open")) popup.showPopover();
    positionMenu(trigger.current, popup);
    const available = items();
    (last ? available.at(-1) : available[0])?.focus();
  }

  useEffect(() => {
    if (!open) return;
    const popup = menu.current;
    function place(event) {
      if ((event.target instanceof Node && popup.contains(event.target)) || !popup.matches(":popover-open")) return;
      positionMenu(trigger.current, popup);
    }
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [open]);

  function navigate(event) {
    const available = items();
    if (event.key === "Tab" || event.key === "Escape") {
      menu.current.hidePopover();
      trigger.current.focus({ preventScroll: true });
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
      }
      return;
    }
    if (!available.length || !["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const current = available.indexOf(document.activeElement);
    const next = event.key === "Home" ? 0 : event.key === "End" ? available.length - 1
      : (current + available.length + (event.key === "ArrowUp" ? -1 : 1)) % available.length;
    available[next].focus();
  }

  return <>
    <IconButton ref={trigger} icon="more_horiz" label={label} disabled={disabled}
      aria-haspopup="menu" aria-expanded={open} aria-controls={id}
      onClick={() => menu.current.matches(":popover-open") ? menu.current.hidePopover() : show()}
      onKeyDown={(event) => {
        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
          event.preventDefault();
          show(event.key === "ArrowUp");
        }
      }} />
    <div id={id} className="action-menu" role="menu" aria-label={label} ref={menu} popover="auto"
      onToggle={(event) => setOpen(event.currentTarget.matches(":popover-open"))} onKeyDown={navigate}>
      {groups.filter((group) => group.length).map((group) => <div className="action-menu-group" role="group" key={group[0].label}>
        {group.map(({ label, icon, select, next, danger, disabled }) => <button key={label}
          type="button" role="menuitem" tabIndex="-1" className={"action-menu-item" + (danger ? " destructive" : "")}
          disabled={disabled} onClick={() => {
            menu.current.hidePopover();
            select();
          }}>
          {icon === "arrow_back" ? <span className="directional"><Icon name={icon} /></span> : <Icon name={icon} />}
          <span>{label}</span>
          {next && <span className="directional action-menu-next"><Icon name="chevron_right" /></span>}
        </button>)}
      </div>)}
    </div>
  </>;
}
