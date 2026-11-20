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
    async viewAsset(event, api) {
        const { url, ...options } = event.arguments;
        const mode = event.tool.slice("plugin_base_view_".length);
        const asset = await api.assets.describe(url);
        switch (mode) {
            case "url_metadata": return asset;
            case "text": return api.text.read(url, options);
            case "pdf_metadata": return { ...asset, metadata: await api.pdf.metadata(url) };
            case "image_metadata": return { ...asset, metadata: await api.image.metadata(url) };
            case "video_metadata": return { ...asset, metadata: await api.media.videoMetadata(url) };
            case "image":
            case "audio":
            case "video":
                if (!asset.mime.startsWith(mode + "/")) throw new TypeError("The Asset has a different media type.");
                return { ...asset, mode, ...await api.assetView.present(url) };
        }
        let sample;
        switch (mode) {
            case "pdf_page": sample = await api.pdf.renderPage(url, options); break;
            case "video_frame": sample = await api.media.decodeFrame(url, options); break;
            case "video_audio_clip":
                if (!asset.mime.startsWith("video/")) throw new TypeError("Use a video Asset URL.");
                sample = await api.media.decodeAudio(url, options);
                break;
            default: throw new TypeError("Unknown Asset viewer.");
        }
        return { ...asset, mode, ...await api.assetView.present(url, sample) };
    },
};
