import { flushSync } from "react-dom";

const selector = [
    "button", "a[href]", "input:not([type=hidden])", "select", "textarea",
    '[role="button"]', '[role="switch"]', '[role="tab"]', '[role="checkbox"]',
    '[role="radio"]', '[role="slider"]',
].join(",");

function labelOf(element) {
    const labelledBy = element.getAttribute("aria-labelledby")?.split(/\s+/)
        .map((id) => document.getElementById(id)?.textContent).filter(Boolean).join(" ");
    const labels = Array.from(element.labels ?? [], (label) => label.textContent).join(" ");
    return (element.getAttribute("aria-label") || labelledBy || labels || element.innerText ||
        element.getAttribute("placeholder") || element.title || "").replace(/\s+/g, " ").trim().slice(0, 100);
}

function controls(container) {
    return Array.from(container.querySelectorAll(selector)).filter((element) =>
        element.getClientRects().length && getComputedStyle(element).visibility === "visible" &&
        !element.closest('[aria-hidden="true"]'));
}

function disabled(element) {
    return element.matches(':disabled, [aria-disabled="true"], [data-disabled]');
}

function snapshot(container) {
    return {
        text: container.innerText.trim().slice(0, 1500),
        width: container.clientWidth,
        height: container.scrollHeight,
        horizontal_overflow: container.scrollWidth > container.clientWidth,
        controls: controls(container).slice(0, 24).map((element) => ({
            label: labelOf(element),
            kind: element.getAttribute("role") || element.getAttribute("type") || element.localName,
            disabled: disabled(element),
            value: "value" in element ? String(element.value).slice(0, 150) : element.getAttribute("aria-valuenow"),
            selected: element.getAttribute("aria-pressed") ?? element.getAttribute("aria-checked") ??
                element.getAttribute("aria-selected"),
        })),
    };
}

function interact(container, step) {
    const targets = controls(container).filter((element) => labelOf(element) === step.control);
    if (targets.length !== 1) throw new Error(targets.length ? "The label is ambiguous." : "Control not found.");
    const [element] = targets;
    if (disabled(element)) throw new Error("The control is disabled.");
    if (!Object.prototype.hasOwnProperty.call(step, "value")) {
        if (element.matches("a[href]")) return { type: "link", url: element.href };
        flushSync(() => element.click());
        return;
    }
    const nativeInput = element.matches('input:is([type="text"], [type="number"], [type="range"], :not([type])), textarea, select');
    if (!nativeInput) throw new Error("This control does not accept a direct value. Try clicking it.");
    const setValue = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), "value").set;
    flushSync(() => {
        setValue.call(element, step.value);
        element.dispatchEvent(new Event("input", { bubbles: true }));
        element.dispatchEvent(new Event("change", { bubbles: true }));
    });
}

function limited(value, size) {
    return JSON.stringify(value).length <= size ? value : { truncated: true };
}

export function createPreview(container, steps, complete) {
    let errors = [];
    let state = {};
    let finished = false;
    const actions = [];
    const recordAction = (event) => {
        if (actions.length < 12) actions.push(limited(event, 900));
    };

    function finish(parse, crash) {
        if (finished) return;
        finished = true;
        if (crash) errors.push({ source: "runtime", code: "render-error", message: crash.message });
        const rendered = !crash && container.childElementCount > 0 && Boolean(parse?.root || parse?.rootExpression);
        const unresolved = parse?.meta.unresolved ?? [];
        const incomplete = parse?.meta.incomplete ?? false;
        const initial = snapshot(container);
        const interactions = [];
        if (rendered && !errors.some((error) => error.severity !== "warning") && !incomplete && !unresolved.length) {
            for (const step of steps) {
                try {
                    const action = interact(container, step);
                    if (action) recordAction(action);
                    interactions.push({ control: step.control, ok: true });
                } catch (error) {
                    interactions.push({ control: step.control, ok: false, error: error.message });
                    break;
                }
            }
        }
        complete({
            ok: rendered && !incomplete && !unresolved.length &&
                !errors.some((error) => error.severity !== "warning") &&
                interactions.length === steps.length && interactions.every((step) => step.ok),
            rendered,
            incomplete,
            unresolved: unresolved.slice(0, 12),
            diagnostics: errors.slice(0, 10).map((error) => ({
                source: error.source,
                code: error.code,
                message: String(error.message).slice(0, 350),
                statement: error.statementId,
                component: error.component,
                path: error.path,
                severity: error.severity,
            })),
            ...(interactions.length ? { initial } : {}),
            view: interactions.length ? snapshot(container) : initial,
            steps: interactions,
            actions,
            state: limited(state, 3000),
        });
    }

    return {
        parsed: (result) => setTimeout(() => finish(result), 0),
        crashed: (error) => finish(null, error),
        errors: (items) => { errors = items; },
        state: (value) => { state = value; },
        action: recordAction,
    };
}
