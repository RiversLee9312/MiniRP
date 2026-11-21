using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Platform I/O for the base research skill. Provider credentials stay in the host.</summary>
public sealed class PageSource(HostPort port, FetchAgent browser, Func<JsonObject> settings)
{
    internal string Backend => settings().GetString("fetch_backend", "api_then_local");
    internal Task<JsonObject> FetchLocal(string url, CancellationToken ct)
    {
        SearchAgent.ValidateUrl(url);
        ct.ThrowIfCancellationRequested();
        return browser.Fetch(url, ct);
    }

    internal Task<JsonObject> Request(string method, JsonObject body, CancellationToken cancellationToken)
    {
        var config = settings();
        if (method == "search") return Search(body, config, cancellationToken);
        if (method != "fetch") throw new ArgumentException("Unknown web request.");
        SearchAgent.ValidateUrl(body.GetString("url"));
        var key = config.GetString("search_key");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure a search API key or use cached/local pages.");
        return ProviderRequest(method, body, config, key, cancellationToken);
    }

    private Task<JsonObject> ProviderRequest(string method, JsonObject body, JsonObject config, string key, CancellationToken ct) =>
        port.Request(new()
        {
            ["url"] = config.GetString("search_url", "https://api.deepseek.com").TrimEnd('/') + "/v1/tools/" + method,
            ["method"] = "POST",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + key, ["Content-Type"] = "application/json" },
            ["body"] = JsonValues.Serialize(body),
            ["stream"] = false,
            ["timeout_ms"] = 30000
        }, ct);

    private async Task<JsonObject> Search(JsonObject body, JsonObject config, CancellationToken ct)
    {
        var query = body.GetString("query").Trim();
        if (query.Length == 0) throw new ArgumentException("Provide a search query.");
        var count = Math.Clamp(body.GetInt32("count", 8), 1, 50);
        var escaped = Uri.EscapeDataString(query);
        var key = config.GetString("search_key");
        var received = false;
        Exception? lastFailure = null;
        async Task<JsonObject?> TrySearch(Func<Task<JsonObject>> request, string snippetField = "snippet")
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await request();
                if (response["results"] is not JsonArray documents)
                    throw new IOException("The search service did not return search results.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var results = new JsonArray(documents.OfType<JsonObject>()
                    .Where(item => Uri.TryCreate(item.GetString("url"), UriKind.Absolute, out var url) &&
                        url.Scheme is "https" or "http" && seen.Add(url.AbsoluteUri))
                    .Take(count)
                    .Select(item =>
                    {
                        var result = (JsonObject)item.DeepClone();
                        if (snippetField != "snippet") result.Remove(snippetField);
                        result["snippet"] = item.GetString(snippetField);
                        return result;
                    }).ToArray());
                received |= documents.Count == 0 || results.Count > 0;
                return results.Count > 0 ? new JsonObject { ["results"] = results } : null;
            }
            catch (Exception failure) when (!ct.IsCancellationRequested &&
                failure is IOException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                lastFailure = failure;
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(key) &&
            await TrySearch(() => ProviderRequest("search", body, config, key, ct)) is { } paid)
            return paid;
        if (await TrySearch(() => port.Request(new()
        {
            ["url"] = "https://api.mwmbl.org/api/v2/search/?q=" + escaped,
            ["method"] = "GET",
            ["headers"] = new JsonObject { ["Accept"] = "application/json" },
            ["stream"] = false,
            ["timeout_ms"] = 30000
        }, ct), "content") is { } free)
            return free;

        string[] engines = config.GetString("search_region").Equals("CN", StringComparison.OrdinalIgnoreCase)
            ? ["https://www.baidu.com/s?wd=", "https://cn.bing.com/search?q="]
            : ["https://www.google.com/search?q=", "https://duckduckgo.com/?q=", "https://www.bing.com/search?q="];
        foreach (var engine in engines)
        {
            if (await TrySearch(() => browser.Search(engine + escaped, count, ct)) is { } local)
                return local;
        }
        if (received) return new() { ["results"] = new JsonArray() };
        throw new IOException("Search APIs and browser search engines were unavailable.", lastFailure);
    }
}
