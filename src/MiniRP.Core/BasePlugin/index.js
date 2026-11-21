async function retainResearch(input, api) {
    if (input.url) {
        const key = input.key || (await api.research.metadata({ url: input.url })).url;
        return api.research.retain({ key, page: true, urls: [input.url] });
    }
    return api.research.retain({ key: input.query, page: false, urls: input.urls });
}

async function fetchResearch({ url, backend = "auto" }, api) {
    if (backend === "auto") backend = await api.research.backend();
    if (!["api", "local", "api_then_local"].includes(backend)) throw new TypeError("Unknown page fetch backend.");
    const normalize = document => api.research.normalize({
        url, document: { ...document, resolved_url: document.url || url, url },
    });
    let page;
    if (backend !== "local") {
        try {
            page = await normalize(await api.research.request({ operation: "fetch", input: { url } }));
            if (!page.content?.trim()) throw new Error("The page fetch API returned no content.");
            page.completeness = "unknown";
        } catch (error) {
            if (backend !== "api_then_local") throw error;
            page = undefined;
        }
    }
    page ??= await normalize(await api.research.local({ url }));
    if (!page.content?.trim()) throw new Error("The fetched page contains no readable content.");
    return api.research.register({ document: page, page: true });
}

globalThis.handlers = {
    presetCreated(event) {
        return {
            text: "Start the preset setup interview now. Reply in the user's language or the app language (" +
                String(event.locale || "en") + ") if the user hasn't written yet. In the first reply, tell the user " +
                "they can also send their own setting documents or a character card from SillyTavern or a similar app, " +
                "and you can automatically import that material into a MiniRP preset for them. Ask only the first question: " +
                "what kind of world, situation, or experience do they want to create? Offer a couple of short examples " +
                "only if useful. Wait for their answer. Keep the setup instructions private. " +
                "On later turns follow the preset interview instructions. Take one decision at a time and respect answers already given.",
        };
    },
    develop(event, api) {
        return api.development.run({ ...event.arguments, action: event.tool.slice("plugin_base_".length) });
    },
    searchAsset(event, api) {
        return event.tool === "plugin_base_raw_grep" ? api.assetView.rawGrep(event.arguments) : api.assetView.grep(event.arguments);
    },
    editAsset(event, api) {
        return event.tool === "plugin_base_bin_patch" ? api.assetEdit.binPatch(event.arguments) : api.assetEdit.textEdit(event.arguments);
    },
    async research(event, api) {
        const args = event.arguments;
        switch (event.tool) {
            case "real_search": {
                const count = Number.isInteger(args.limit) ? Math.max(1, Math.min(50, args.limit)) : 8;
                const response = await api.research.request({ operation: "search", input: { query: args.query, count } });
                const documents = (response.results || []).filter(document => typeof document?.url === "string" && document.url.length > 0);
                const results = await api.research.registerAll({ documents });
                return { results };
            }
            case "real_fetch": return fetchResearch(args, api);
            case "virtual_search": return api.research.recall(args);
            case "cache_search_results":
            case "cache_page":
                return retainResearch(await api.research.selection({ arguments: args, page: event.tool === "cache_page" }), api);
            default: throw new TypeError("Unknown research tool.");
        }
    },
    researchCache(event, api) {
        return retainResearch(event.input, api);
    },
    async viewAsset(event, api) {
        const { url, ...options } = event.arguments;
        const mode = event.tool.slice("plugin_base_view_".length);
        const asset = await api.assets.describe(url);
        switch (mode) {
            case "url_metadata": return asset;
            case "pdf_metadata": return { ...asset, metadata: await api.pdf.metadata(url) };
            case "image_metadata": return { ...asset, metadata: await api.image.metadata(url) };
            case "video_metadata": return { ...asset, metadata: await api.media.videoMetadata(url) };
            case "audio":
            case "video":
                if (!asset.mime.startsWith(mode + "/")) throw new TypeError("The Asset has a different media type.");
                return { ...asset, mode, ...await api.assetView.present(url) };
        }
        let sample;
        switch (mode) {
            case "image": sample = await api.image.preview(url, options); break;
            case "pdf_page": sample = await api.pdf.renderPage(url, options); break;
            case "video_frame": sample = await api.media.decodeFrame(url, options); break;
            case "video_audio_clip":
                if (!asset.mime.startsWith("video/")) throw new TypeError("Use a video Asset URL.");
                sample = await api.media.decodeAudio(url, options);
                break;
            default: throw new TypeError("Unknown Asset viewer.");
        }
        return { ...asset, mode, width: sample.width, height: sample.height,
            source_width: sample.source_width, source_height: sample.source_height,
            ...await api.assetView.present(url, sample) };
    },
};
