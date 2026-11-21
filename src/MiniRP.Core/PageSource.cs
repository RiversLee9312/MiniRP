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
        var url = body.GetString("url");
        SearchAgent.ValidateUrl(url);
        var service = config.GetString("fetch_provider", "deepseek");
        var key = ProviderKey(config, service);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure a page fetch API key or use the local browser.");
        return FetchApi(service, url, config, key, cancellationToken);
    }

    private static string ProviderKey(JsonObject config, string service)
    {
        var key = config.GetObject("research_keys").GetString(service).Trim();
        return key.Length == 0 && service == "deepseek" ? config.GetString("search_key").Trim() : key;
    }

    private Task<JsonObject> Post(string url, string header, string key, JsonObject body, CancellationToken ct) =>
        port.Request(new()
        {
            ["url"] = url,
            ["method"] = "POST",
            ["headers"] = new JsonObject { [header] = key, ["Content-Type"] = "application/json" },
            ["body"] = JsonValues.Serialize(body),
            ["stream"] = false,
            ["timeout_ms"] = 30000
        }, ct);

    private Task<JsonObject> DeepSeek(string method, JsonObject body, JsonObject config, string key, CancellationToken ct) =>
        Post(config.GetString("search_url", "https://api.deepseek.com").TrimEnd('/') + "/v1/tools/" + method,
            "Authorization", "Bearer " + key, body, ct);

    private async Task<JsonObject> SearchApi(string service, string query, int count, JsonObject config, string key, CancellationToken ct)
    {
        if (service == "deepseek")
            return await DeepSeek("search", new() { ["query"] = query, ["count"] = count }, config, key, ct);
        var response = service switch
        {
            "tavily" => await Post("https://api.tavily.com/search", "Authorization", "Bearer " + key,
                new() { ["query"] = query, ["max_results"] = Math.Min(count, 20), ["search_depth"] = "basic" }, ct),
            "exa" => await Post("https://api.exa.ai/search", "x-api-key", key,
                new() { ["query"] = query, ["numResults"] = count, ["type"] = "auto",
                    ["contents"] = new JsonObject { ["highlights"] = new JsonObject { ["maxCharacters"] = 1000 } } }, ct),
            "brave" => await port.Request(new()
            {
                ["url"] = "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query) +
                    "&count=" + Math.Min(count, 20) + "&text_decorations=false",
                ["method"] = "GET",
                ["headers"] = new JsonObject { ["X-Subscription-Token"] = key, ["Accept"] = "application/json" },
                ["stream"] = false, ["timeout_ms"] = 30000
            }, ct),
            _ => throw new InvalidOperationException("Unknown web search provider.")
        };
        var envelope = service == "brave" ? response.GetObject("web") : response;
        if (envelope["results"] is not JsonArray documents)
            throw new IOException("The search API did not return search results.");
        return new() { ["results"] = new JsonArray(documents.OfType<JsonObject>().Select(item => new JsonObject
        {
            ["url"] = item.GetString("url"),
            ["title"] = item.GetString("title"),
            ["snippet"] = service switch
            {
                "tavily" => item.GetString("content"),
                "exa" => string.Join("\n", item.GetArray("highlights").Select(value => JsonValues.GetText(value))),
                _ => item.GetString("description")
            }
        }).ToArray()) };
    }

    private async Task<JsonObject> FetchApi(string service, string url, JsonObject config, string key, CancellationToken ct)
    {
        if (service == "deepseek") return await DeepSeek("fetch", new() { ["url"] = url }, config, key, ct);
        var response = service switch
        {
            "tavily" => await Post("https://api.tavily.com/extract", "Authorization", "Bearer " + key,
                new() { ["urls"] = new JsonArray(url), ["format"] = "markdown" }, ct),
            "exa" => await Post("https://api.exa.ai/contents", "x-api-key", key,
                new() { ["urls"] = new JsonArray(url), ["text"] = true }, ct),
            _ => throw new InvalidOperationException("Unknown page fetch provider.")
        };
        var document = response.GetArray("results").FirstOrDefault();
        var content = document.GetString(service == "tavily" ? "raw_content" : "text");
        if (string.IsNullOrWhiteSpace(content)) throw new IOException("The page fetch API returned no readable content.");
        return new() { ["url"] = document.GetString("url", url), ["title"] = document.GetString("title"), ["content"] = content };
    }

    private async Task<JsonObject> Search(JsonObject body, JsonObject config, CancellationToken ct)
    {
        var query = body.GetString("query").Trim();
        if (query.Length == 0) throw new ArgumentException("Provide a search query.");
        var count = Math.Clamp(body.GetInt32("count", 8), 1, 50);
        var escaped = Uri.EscapeDataString(query);
        var service = config.GetString("search_provider", "deepseek");
        var key = ProviderKey(config, service);
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
            await TrySearch(() => SearchApi(service, query, count, config, key, ct)) is { } paid)
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
