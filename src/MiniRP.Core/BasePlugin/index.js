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
    async recommend(event, api) {
        if (!await api.settings.get({ key: "topics" })) {
            await api.ui.setHomeSuggestions({ request: event.request, items: [] });
            return;
        }
        const locale = String(event.locale || "en").slice(0, 35);
        const result = await api.harness.generate({
            system: "Suggest five distinct, concrete and useful conversation starters. Vary the subjects. Use the language of the supplied locale. Return only a JSON array of short strings that the user could send as a message. No Markdown.",
            text: "Locale: " + locale,
        });
        let items;
        try { items = JSON.parse(result.text); }
        catch { items = result.text.split("\n").map(item => item.replace(/^\s*[-*\d.)]+\s*/, "").trim()); }
        if (!Array.isArray(items)) items = [];
        items = [...new Set(items.filter(item => typeof item === "string").map(item => item.trim()))]
            .filter(item => item && item.length <= 150 && !item.startsWith("```")).slice(0, 5);
        await api.ui.setHomeSuggestions({ request: event.request, items });
    },
};
