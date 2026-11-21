using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class PluginRuntime
{
    internal async Task<JsonNode?> CacheResearch(JsonObject environment, AgentAssets.ResearchScope research,
        JsonObject input, CancellationToken ct)
    {
        var package = Volatile.Read(ref packages).Single(item => item.BuiltIn && item.Id == "base");
        var handler = package.Bind("minirp://actions/minirp.base/hooks/research-cache");
        return await Invoke(new(package, handler, new() { ["name"] = "research.cache", ["input"] = input }, environment,
            Research: research), ct);
    }

    private static async Task<JsonNode?> ResearchApi(AgentAssets.ResearchScope research, string method,
        JsonObject args, CancellationToken ct)
    {
        var turn = research.Turn;
        return method switch
        {
            "request" => await research.Source.Request(args.GetString("operation"), args.GetObject("input"), ct),
            "local" => await research.Source.FetchLocal(args.GetString("url"), ct),
            "backend" => JsonValue.Create(research.Source.Backend),
            "normalize" => Normalize(args),
            "register" => turn.Register(args.GetObject("document"), args.GetBoolean("page")),
            "registerAll" => new JsonArray(args.GetArray("documents").OfType<JsonObject>()
                .Select(item => (JsonNode?)turn.Register(item, page: false)).ToArray()),
            "recall" => turn.Recall(args),
            "selection" => turn.CacheInput(args.GetObject("arguments"), args.GetBoolean("page")),
            "metadata" => new JsonObject { ["url"] = turn.Assets.DocumentAt(args.GetString("url")).GetString("url") },
            "retain" => turn.RetainCache(args),
            _ => throw new ArgumentException("Unknown research capability.")
        };
    }

    private static JsonObject Normalize(JsonObject args)
    {
        var document = (JsonObject)args.GetObject("document").DeepClone();
        var url = args.GetString("url");
        SearchAgent.ValidateUrl(url);
        WebPageMarkdown.Normalize(document, url);
        return document;
    }
}
