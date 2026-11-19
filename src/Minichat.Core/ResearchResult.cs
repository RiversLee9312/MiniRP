using System.Text.Json.Nodes;

namespace Minichat.Core;

internal static class ResearchResult
{
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
        foreach (var key in page ? new[] { "title", "url", "content", "date" } : ["title", "url", "snippet", "date"])
        {
            if (input[key] is JsonValue text && text.TryGetValue<string>(out var value))
            {
                document[key] = value;
            }
        }
        return document;
    }
}
