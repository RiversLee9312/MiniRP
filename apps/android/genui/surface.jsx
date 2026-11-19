import { ThemeProvider } from "@openuidev/intelligent-ui-library";
import { createRoot } from "react-dom/client";
import { Renderer } from "@openuidev/react-lang";
import { Component } from "react";
import "maplibre-gl/dist/maplibre-gl.css";
import "./surface.css";
import { library } from "./catalog.mjs";

const host = window.MiniRPSurface;
const container = document.getElementById("surface");
const root = createRoot(container);
let initialState;

function post(type, data = {}) {
    host?.postMessage(JSON.stringify({ type, ...data }));
}

class RenderBoundary extends Component {
    state = { crashed: false };
    static getDerivedStateFromError() { return { crashed: true }; }
    componentDidCatch() { post("error"); }
    render() { return this.state.crashed ? null : this.props.children; }
}

function action(event) {
    if (event.type === "open_url") post("link", { url: event.params?.url });
    if (event.type === "continue_conversation") {
        post("reply", {
            text: event.humanFriendlyMessage,
            form: event.formName ? event.formState?.[event.formName] : undefined,
            context: event.params?.context,
        });
    }
}

window.renderOpenUi = (state) => {
    initialState ??= state.initialState ?? {};
    const managedState = state.managedState && Object.fromEntries(
        Object.entries(state.managedState).map(([name, value]) => [name.startsWith("$") ? name : "$" + name, value]),
    );
    document.documentElement.lang = state.locale || "en";
    document.documentElement.dir = state.rtl ? "rtl" : "ltr";
    document.documentElement.classList.toggle("expanded", state.expanded);
    document.documentElement.style.colorScheme = state.dark ? "dark" : "light";
    root.render(
        <RenderBoundary>
            <ThemeProvider mode={state.dark ? "dark" : "light"} lightTheme={state.theme}>
                <Renderer
                    library={library}
                    response={state.program}
                    isStreaming={state.streaming}
                    key={state.managedState ? `managed-${state.managedRevision}` : "reply"}
                    initialState={managedState ?? initialState}
                    onParseResult={(result) => {
                        if (result?.root) post("rendered");
                        else if (!state.streaming) post("error");
                    }}
                    onAction={action}
                    onStateUpdate={(value) => post("state", { value })}
                    publishObservability={false}
                />
            </ThemeProvider>
        </RenderBoundary>,
    );
};

document.addEventListener("click", (event) => {
    const link = event.target.closest?.("a[href]");
    if (!event.isTrusted || !link) return;
    event.preventDefault();
    post("link", { url: link.href });
}, true);

let previousHeight = -1;
new ResizeObserver(() => {
    const height = Math.ceil(container.getBoundingClientRect().height);
    if (height !== previousHeight) {
        previousHeight = height;
        post("height", { height });
    }
}).observe(container);
post("ready");
