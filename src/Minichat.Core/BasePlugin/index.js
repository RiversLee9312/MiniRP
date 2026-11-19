globalThis.handlers = {
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
