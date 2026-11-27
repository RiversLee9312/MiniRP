const scrollers = new WeakMap();

export function observeTraceClipping(trace) {
  const scroll = trace.closest(".transcript, .dialog-content");
  if (!scroll) return;
  let clipper = scrollers.get(scroll);
  if (!clipper) {
    const visible = new Set();
    const observed = new Set();
    let frame = 0;
    function update() {
      frame = 0;
      const measurements = [];
      for (const card of visible) {
        const header = card.firstElementChild;
        const body = header.nextElementSibling;
        const headerBounds = header.getBoundingClientRect();
        if (!card.open && card.getBoundingClientRect().bottom <= headerBounds.bottom) continue;
        const overlap = Math.max(0, headerBounds.bottom - body.getBoundingClientRect().top);
        measurements.push([body, Math.ceil(overlap * devicePixelRatio) / devicePixelRatio + "px"]);
      }
      for (const [body, inset] of measurements) {
        if (body.style.getPropertyValue("--trace-overlap") !== inset)
          body.style.setProperty("--trace-overlap", inset);
      }
    }
    function schedule() {
      if (!frame) frame = requestAnimationFrame(update);
    }
    const intersection = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting && observed.has(entry.target)) visible.add(entry.target);
        else visible.delete(entry.target);
      }
      schedule();
    }, { root: scroll, rootMargin: "64px 0px" });
    const size = new ResizeObserver(schedule);
    size.observe(scroll);
    scroll.addEventListener("scroll", schedule, { passive: true });
    clipper = (card) => {
      const body = card.lastElementChild;
      observed.add(card);
      intersection.observe(card);
      size.observe(card);
      return () => {
        visible.delete(card);
        observed.delete(card);
        intersection.unobserve(card);
        size.unobserve(card);
        body.style.removeProperty("--trace-overlap");
        if (observed.size) return;
        intersection.disconnect();
        size.disconnect();
        scroll.removeEventListener("scroll", schedule);
        cancelAnimationFrame(frame);
        scrollers.delete(scroll);
      };
    };
    scrollers.set(scroll, clipper);
  }
  return clipper(trace);
}
