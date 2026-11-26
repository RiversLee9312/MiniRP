let value = "before";
let received = null;

globalThis.handlers = {
    grant(_event, api) {
        return api.assets.url("current");
    },
    async update(_event, api) {
        const url = api.assets.url("current");
        const metadata = await api.assets.describe(url);
        let reserved = false;
        try { await api.actions.publish({ url: metadata.changeEvent }); }
        catch { reserved = true; }
        value = value === "before" ? "after" : "before";
        const result = await api.assets.notifyChanged(url);
        return api.text.encode(JSON.stringify({ result, listenable: metadata.listenable, reserved }));
    },
    current(_event, api) {
        return api.text.encode(value);
    },
    async listen(event, api) {
        const { text } = await api.text.read(event.assets.asset);
        received = { name: event.name, url: event.url, source: event.source, reason: event.reason, text };
    },
    receipt(_event, api) {
        return api.text.encode(JSON.stringify({ received }));
    },
};
