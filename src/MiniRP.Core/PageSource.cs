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
        if (method is not ("search" or "fetch")) throw new ArgumentException("Unknown web request.");
        if (method == "fetch") SearchAgent.ValidateUrl(body.GetString("url"));
        var config = settings();
        var key = config.GetString("search_key");
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Configure a search API key or use cached/local pages.");
        }
        return port.Request(new()
        {
            ["url"] = config.GetString("search_url", "https://api.deepseek.com").TrimEnd('/') + "/v1/tools/" + method,
            ["method"] = "POST",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + key, ["Content-Type"] = "application/json" },
            ["body"] = JsonValues.Serialize(body),
            ["stream"] = false,
            ["timeout_ms"] = 30000
        }, cancellationToken);
    }
}
