import { countBytes, makeSample } from "./lib/bytes.js";

globalThis.handlers = {
    async announce(_event, api) {
        await api.actions.publish({
            url: "minirp://actions/example.minichat.stream/events/ready",
            assets: { sample: api.assets.url("sample") },
        });
    },

    produce() {
        return makeSample();
    },

    async consume(event, api) {
        const { body } = await api.assets.open(event.assets.sample);
        const bytes = await countBytes(body);
        if (api.environment.chat) {
            const key = "stream-example/last-size";
            const entry = await api.db.get({ scope: "chat", key });
            await api.db.put({
                scope: "chat",
                key,
                content: String(bytes),
                expected_revision: entry.missing ? 0 : entry.revision,
            });
        }
    },

    sampleUrl(_event, api) {
        return api.assets.url("sample");
    },

    async measure(event, api) {
        const { body } = await api.assets.open(event.arguments.url);
        return { bytes: await countBytes(body) };
    },

    async encodeAbout(_event, api) {
        const { text } = await api.text.read(api.assets.url("about"));
        return api.text.encode("\uFEFF" + text, { encoding: "utf-16le" });
    },

    aboutUrl(_event, api) {
        return api.assets.url("about-utf16");
    },
};
