globalThis.handlers = {
    presetCreated(event) {
        return {
            text: "Start the preset setup interview now. Reply in the user's language or the app language (" +
                String(event.locale || "en") + ") if the user hasn't written yet. Ask only the first question: " +
                "what kind of world, situation, or experience do they want to create? Offer a couple of short examples " +
                "only if useful. Wait for their answer. Keep the setup instructions private. " +
                "On later turns follow the preset interview instructions. Take one decision at a time and respect answers already given.",
        };
    },
    develop(event, api) {
        return api.development.run({ ...event.arguments, action: event.tool.slice("plugin_base_".length) });
    },
};
