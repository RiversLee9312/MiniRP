using System.Text.Json.Nodes;

namespace Minichat.Core;

/// <summary>Real-web routing is independent of the search agent's candidate editing and submission.</summary>
public sealed class PageSource(HostPort port, FetchAgent browser, Func<JsonObject> settings)
{
    public Task<JsonObject> Search(string query, int limit, CancellationToken cancellationToken) => Api("search", new()
    {
        ["query"] = query,
        ["count"] = Math.Clamp(limit, 1, 50)
    }, cancellationToken);

    public async Task<JsonObject> Fetch(string url, string backend, CancellationToken cancellationToken)
    {
        SearchAgent.ValidateUrl(url);
        if (backend == "auto")
        {
            backend = settings().GetString("fetch_backend", "api_then_local");
        }
        if (backend is not ("api" or "local" or "api_then_local"))
        {
            throw new ArgumentException("Unknown page fetch backend.");
        }
        JsonObject result;
        if (backend == "local")
        {
            result = await browser.Fetch(url, cancellationToken);
        }
        else
        {
            try
            {
                result = await Api("fetch", new() { ["url"] = url }, cancellationToken);
                if (result.GetString("content").Length == 0)
                {
                    throw new IOException("The page fetch API returned no content.");
                }
                result["completeness"] = "unknown";
            }
            catch (Exception) when (backend == "api_then_local" && !cancellationToken.IsCancellationRequested)
            {
                result = await browser.Fetch(url, cancellationToken);
            }
        }
        result["resolved_url"] = result.GetString("url", url);
        result["url"] = url;
        return result;
    }

    private async Task<JsonObject> Api(string method, JsonObject body, CancellationToken cancellationToken)
    {
        var config = settings();
        var key = config.GetString("search_key");
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Configure a search API key or use cached/local pages.");
        }
        return await port.Request(new()
        {
            ["url"] = config.GetString("search_url", "https://api.deepseek.com").TrimEnd('/') + "/v1/tools/" + method,
            ["method"] = "POST",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + key, ["Content-Type"] = "application/json" },
            ["body"] = JsonValues.Serialize(body),
            ["stream"] = false
            ,
            ["timeout_ms"] = 30000
        }, cancellationToken);
    }
}
