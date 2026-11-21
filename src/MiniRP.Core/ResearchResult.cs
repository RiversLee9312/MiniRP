using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ResearchResult
{
    public static JsonObject PublicPage(JsonNode response, string url)
    {
        var page = response is JsonObject document && !document.ContainsKey("error")
            ? (JsonObject)document.DeepClone() : Public(response);
        if (page.ContainsKey("error")) return page;
        WebPageMarkdown.Normalize(page, url);
        if (string.IsNullOrWhiteSpace(page.GetString("content"))) return JsonValues.Error("The page has no readable Markdown content.");
        return Document(page, page: true);
    }

    public static JsonObject Public(JsonNode response)
    {
        if (response is not JsonObject result || result.ContainsKey("error"))
        {
            return JsonValues.Error("Research returned no usable results.");
        }
        if (result["results"] is JsonArray results)
        {
            return new() { ["results"] = new JsonArray(results.OfType<JsonObject>().Select(item => Document(item, page: false)).ToArray()) };
        }
        return Document(result, page: true);
    }

    private static JsonObject Document(JsonObject input, bool page)
    {
        var document = new JsonObject();
        foreach (var key in page ? new[] { "title", "url", "content", "date", "asset_url" } : ["title", "url", "snippet", "date", "asset_url"])
        {
            if (input[key] is JsonValue text && text.TryGetValue<string>(out var value))
            {
                document[key] = value;
            }
        }
        return document;
    }
}
