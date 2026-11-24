import { ThemeProvider } from "@openuidev/intelligent-ui-library";
import { defaultDarkTheme, defaultLightTheme } from "@openuidev/react-components";
import { createRoot } from "react-dom/client";
import { Renderer } from "@openuidev/react-lang";
import { Component } from "react";
import "maplibre-gl/dist/maplibre-gl.css";
import "./surface.css";
import { AssetMedia, library, originalAssetUrl, toolLibrary } from "./catalog.mjs";
import { createPreview } from "./preview.mjs";

const host = window.MiniRPSurface;
const container = document.getElementById("surface");
const root = createRoot(container);
const desktop = document.documentElement.dataset.host === "desktop";
const toolPresentation = document.documentElement.dataset.surface === "tool-presentation";
let initialState;
let preview;

function desktopTypography(theme) {
    const families = {
        '"Inter", sans-serif': "var(--typeface)",
        '"SFMono-Regular", Menlo, monospace': "var(--typeface-mono)",
    };
    const typography = { intelligentFont: "var(--typeface)", intelligentMono: "var(--typeface-mono)" };
    // OpenUI's type tokens include complete font shorthands. Keep its weights, sizes and line heights.
    for (const [name, value] of Object.entries(theme)) {
        if (typeof value !== "string") continue;
        for (const [family, replacement] of Object.entries(families)) {
            if (value.endsWith(family)) typography[name] = value.slice(0, -family.length) + replacement;
        }
    }
    return typography;
}

const desktopLight = desktop && desktopTypography(defaultLightTheme);
const desktopDark = desktop && desktopTypography(defaultDarkTheme);

function post(type, data = {}) {
    host?.postMessage(JSON.stringify({ type, ...data }));
}

class RenderBoundary extends Component {
    state = { crashed: false };
    static getDerivedStateFromError() { return { crashed: true }; }
    componentDidCatch(error) {
        post("error");
        preview?.crashed(error);
    }
    render() { return this.state.crashed ? null : this.props.children; }
}

function action(event) {
    if (event.type === "open_url") sendAction("link", { url: event.params?.url });
    if (event.type === "continue_conversation") {
        sendAction("reply", {
            text: event.humanFriendlyMessage,
            form: event.formName ? event.formState?.[event.formName] : undefined,
            context: event.params?.context,
        });
    }
}

function sendAction(type, data) {
    if (toolPresentation) return;
    if (preview) preview.action({ type, ...data });
    else post(type, data);
}

window.renderOpenUi = (state) => {
    initialState ??= state.initialState ?? {};
    if (state.previewSteps) {
        preview = createPreview(container, state.previewSteps, (result) => post("preview_result", { result }));
    }
    const managedState = state.managedState && Object.fromEntries(
        Object.entries(state.managedState).map(([name, value]) => [name.startsWith("$") ? name : "$" + name, value]),
    );
    document.documentElement.lang = state.locale || "en";
    document.documentElement.dir = state.rtl ? "rtl" : "ltr";
    document.documentElement.classList.toggle("expanded", state.expanded);
    document.documentElement.style.colorScheme = state.dark ? "dark" : "light";
    if (desktop) {
        document.documentElement.style.setProperty("--selection-background", state.theme?.selectionBackground || "");
        document.documentElement.style.setProperty("--selection-foreground", state.theme?.selectionForeground || "");
    }
    const theme = desktop ? { ...(state.dark ? desktopDark : desktopLight), ...state.theme } : state.theme;
    root.render(
        <RenderBoundary>
            <ThemeProvider mode={state.dark ? "dark" : "light"} lightTheme={theme}>
                <AssetMedia.Provider value={!state.streaming}>
                <Renderer
                    library={toolPresentation ? toolLibrary : library}
                    response={state.program}
                    isStreaming={state.streaming}
                    key={state.managedState ? `managed-${state.managedRevision}` : "reply"}
                    initialState={managedState ?? initialState}
                    onParseResult={(result) => {
                        if (result?.root) post("rendered");
                        else if (!state.streaming) post("error");
                        preview?.parsed(result);
                    }}
                    onError={toolPresentation ? (errors) => {
                        if (errors.some((error) => error.severity !== "warning")) post("error");
                    } : preview?.errors}
                    onAction={toolPresentation ? undefined : action}
                    onStateUpdate={toolPresentation ? undefined : preview?.state ?? ((value) => post("state", { value }))}
                    publishObservability={false}
                />
                </AssetMedia.Provider>
            </ThemeProvider>
        </RenderBoundary>,
    );
};

document.addEventListener("click", (event) => {
    const link = event.target.closest?.("a[href]");
    if (!event.isTrusted || !link) return;
    event.preventDefault();
    sendAction("link", { url: originalAssetUrl(link.href) });
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
