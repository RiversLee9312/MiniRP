globalThis.handlers = {
    async announce(_event, api) {
        await api.actions.publish({
            url: "minirp://actions/example.minichat.stream/events/ready",
            assets: { sample: api.assets.url("sample") },
        });
    },

    produce() {
        let remaining = 1_000_003;
        return new ReadableStream({
            pull(controller) {
                if (remaining === 0) {
                    controller.close();
                    return;
                }
                const chunk = new Uint8Array(Math.min(remaining, 65536));
                chunk.fill(42);
                remaining -= chunk.length;
                controller.enqueue(chunk);
            },
        });
    },

    async consume(event, api) {
        const { body } = await api.assets.open(event.assets.sample);
        let bytes = 0;
        for await (const chunk of body) bytes += chunk.byteLength;
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
        let bytes = 0;
        for await (const chunk of body) bytes += chunk.byteLength;
        return { bytes };
    },

    async encodeAbout(_event, api) {
        const { text } = await api.text.read(api.assets.url("about"));
        return api.text.encode("\uFEFF" + text, { encoding: "utf-16le" });
    },

    aboutUrl(_event, api) {
        return api.assets.url("about-utf16");
    },
};
