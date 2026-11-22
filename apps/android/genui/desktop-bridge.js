let assets;
window.MiniRPSurface = {
    postMessage: (message) => window.parent.postMessage({ minirp: JSON.parse(message) }, location.origin),
    assetUrl: (url) => assets ? "/api/asset?" + new URLSearchParams({ ...assets, url, inline: "1" }) : "",
};
window.addEventListener("message", (event) => {
    if (event.origin !== location.origin || event.source !== window.parent || !event.data?.minirpRender) return;
    const { scope, state } = event.data.minirpRender;
    assets = state.previewSteps ? null : scope;
    window.renderOpenUi?.(state);
});
