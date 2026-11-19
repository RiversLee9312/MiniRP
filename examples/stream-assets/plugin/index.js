globalThis.handlers = {
    async announce(_event, api) {
        await api.actions.publish({
            url: "minichat://actions/example.minichat.stream/events/ready",
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
};
