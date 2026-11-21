using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>A staged search workspace. No network or model dependencies; failed turns never commit it.</summary>
internal sealed class SearchTurn
{
    private readonly AgentSession session;
    private readonly PresetResearch? shared;
    private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);
    private List<string> selected = [];
    private string? selectedPage;

    public string Mode
    {
        get;
    }
    public string Target
    {
        get;
    }
    public JsonObject State
    {
        get;
    }
    public JsonNode? Answer
    {
        get; private set;
    }
    public bool Failed
    {
        get; private set;
    }
    public ResearchAssets Assets { get; }
    public bool Changed => Assets.Changed || changed;
    private bool changed;

    public SearchTurn(string mode, string target, Workspace workspace, AgentSession session, JsonObject? initialState = null,
        PresetResearch? shared = null)
    {
        Mode = mode;
        Target = target;
        State = (JsonObject)(initialState ?? workspace.State).DeepClone();
        this.session = session;
        this.shared = shared;
        foreach (var key in new[] { "pages", "search_cache" })
        {
            if (State[key] is not JsonObject)
            {
                State[key] = new JsonObject();
            }
        }
        Assets = new(workspace, State, shared);
        Assets.Migrate();
    }

    public JsonObject Register(JsonObject document, bool page)
    {
        var stored = (JsonObject)document.DeepClone();
        if (page) WebPageMarkdown.Normalize(stored, Target);
        return Register(Assets.Draft(stored, page));
    }

    private JsonObject Register(string url, int contentLimit = 12000)
    {
        var reference = JsonValues.NewId();
        documents[reference] = url;
        session.Assets?.GrantResearch(url);
        return Preview(reference, contentLimit);
    }

    private string Address(string reference) => documents.TryGetValue(reference, out var url)
        ? url
        : throw new ArgumentException("Unknown ref. Recall previous results again for this turn.");

    private JsonObject Get(string reference) => Assets.DocumentAt(Address(reference));

    private JsonObject Preview(string reference, int contentLimit = 12000)
    {
        var preview = PreviewDocument(Get(reference), contentLimit);
        preview["ref"] = reference;
        return preview;
    }

    private static JsonObject PreviewDocument(JsonObject document, int contentLimit)
    {
        var preview = new JsonObject();
        foreach (var key in new[] { "asset_url", "url", "resolved_url", "title", "snippet", "date", "source", "credibility", "completeness", "content" })
        {
            if (document[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            var limit = key == "content" ? contentLimit : key is "url" or "asset_url" or "resolved_url" ? text.Length : 1024;
            if (text.Length > limit && char.IsHighSurrogate(text[limit - 1])) limit--;
            preview[key] = text[..Math.Min(limit, text.Length)];
            if (key == "content" && text.Length > limit) preview["total_chars"] = text.Length;
        }
        return preview;
    }

    internal JsonNode? HistoryAnswer() => Answer is not JsonObject answer || answer.ContainsKey("error") ? Answer :
        answer["results"] is JsonArray results
            ? new JsonObject { ["query"] = answer.GetString("query"),
                ["results"] = new JsonArray(results.OfType<JsonObject>().Select(result => (JsonNode?)PreviewDocument(result, 512)).ToArray()) }
            : PreviewDocument(answer, 512);

    public async Task<JsonNode> Execute(string tool, JsonObject arguments, CancellationToken ct) => tool switch
    {
        "fail_request" => Fail(arguments),
        "add_result" => AddResult(arguments),
        "select_results" => SelectResults(arguments),
        "create_page" => CreatePage(arguments),
        "set_page" => SetPage(arguments),
        "submit_search_results" => await SubmitResults(arguments, ct),
        "submit_page" => await SubmitPage(arguments, ct),
        _ => throw new ArgumentException("Unknown search tool: " + tool)
    };

    private JsonObject Fail(JsonObject arguments)
    {
        Failed = true;
        Answer = JsonValues.Error(arguments.GetString("message"));
        return (JsonObject)Answer.DeepClone();
    }

    internal JsonObject Recall(JsonObject arguments)
    {
        var query = arguments.GetString("query");
        var url = arguments.GetString("url").TrimEnd('/');
        var state = shared?.Read() ?? State;
        var pages = state.GetObject("pages").Select(pair => pair.Value);
        var results = state.GetObject("search_cache").Select(pair => pair.Value).OfType<JsonArray>().SelectMany(array => array);
        var matches = pages.Concat(results).Select(node => JsonValues.GetText(node)).Where(Assets.Contains).Reverse().Where(asset =>
        {
            var document = Assets.DocumentAt(asset);
            return (url.Length == 0 || document.GetString("url").TrimEnd('/') == url) &&
                (query.Length == 0 || JsonValues.Serialize(document).Contains(query, StringComparison.OrdinalIgnoreCase));
        });
        var output = new JsonArray();
        foreach (var document in matches.Take(Math.Clamp(arguments.GetInt32("limit", 30), 1, 200)))
        {
            output.AddNode(Register(document, contentLimit: 512));
        }
        return new() { ["results"] = output };
    }

    private JsonObject AddResult(JsonObject arguments)
    {
        var document = arguments.GetString("ref").Length > 0 ? (JsonObject)Get(arguments.GetString("ref")).DeepClone() : new();
        CopyFields(arguments, document);
        if (document.GetString("title").Length == 0 || document.GetString("url").Length == 0)
        {
            throw new ArgumentException("A result needs a title and URL.");
        }
        var preview = Register(document, page: false);
        selected.Add(preview.GetString("ref"));
        return preview;
    }

    private JsonObject SelectResults(JsonObject arguments)
    {
        var references = arguments.GetArray("refs").Select(node => JsonValues.GetText(node)).ToList();
        foreach (var reference in references)
        {
            Get(reference);
        }
        selected = references;
        return new() { ["selected"] = selected.Count };
    }

    private static void CopyFields(JsonObject source, JsonObject target)
    {
        foreach (var field in source)
        {
            if (field.Key is not ("ref" or "asset_url"))
            {
                target[field.Key] = JsonValues.Clone(field.Value);
            }
        }
    }

    private JsonObject CreatePage(JsonObject arguments)
    {
        var document = (JsonObject)arguments.DeepClone();
        document.Remove("ref");
        if (document.GetString("url").Length == 0)
        {
            document["url"] = Target;
        }
        SearchAgent.ValidateUrl(document.GetString("url"));
        if (document.GetString("content").Length == 0)
        {
            throw new ArgumentException("A page needs content.");
        }
        return Register(document, page: true);
    }

    private JsonObject SetPage(JsonObject arguments)
    {
        Get(arguments.GetString("ref"));
        selectedPage = arguments.GetString("ref");
        return new() { ["selected"] = selectedPage };
    }

    internal JsonObject RetainCache(JsonObject args)
    {
        var key = args.GetString("key");
        var urls = args.GetArray("urls");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A cache key is required.");
        if (args.GetBoolean("page"))
        {
            SearchAgent.ValidateUrl(key);
            var address = JsonValues.GetText(urls.Single());
            var document = Assets.DocumentAt(address);
            if (document.GetString("content").Length == 0) throw new ArgumentException("A cached page needs content.");
            var retained = JsonValue.Create(Assets.Retain(document, page: true));
            shared?.Set("pages", key, retained);
            State.GetObject("pages")[key] = retained;
        }
        else
        {
            var stored = new JsonArray();
            foreach (var address in urls.Select(value => JsonValues.GetText(value)))
                stored.AddNode(JsonValue.Create(Assets.Retain(Assets.DocumentAt(address), page: false)));
            shared?.Set("search_cache", key, stored);
            State.GetObject("search_cache")[key] = stored;
        }
        changed = true;
        return new() { ["cached"] = true };
    }

    internal JsonArray SelectedUrls(JsonObject arguments)
    {
        var references = arguments["refs"] is JsonArray array ? array.Select(node => JsonValues.GetText(node)) : selected;
        var results = new JsonArray();
        foreach (var reference in references)
        {
            results.AddNode(JsonValue.Create(Address(reference)));
        }
        return results;
    }

    internal JsonObject CacheInput(JsonObject arguments, bool page) => page
        ? new() { ["url"] = Address(arguments.GetString("ref")) }
        : new() { ["query"] = arguments.GetString("query", Target), ["urls"] = SelectedUrls(arguments) };

    private async Task<JsonObject> SubmitResults(JsonObject arguments, CancellationToken ct)
    {
        if (Mode is not ("search" or "direct"))
        {
            throw new ArgumentException("A page request needs submit_page.");
        }
        var urls = SelectedUrls(arguments);
        var query = Mode == "direct" ? arguments.GetString("query") : Target;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A search query is required.");
        }
        await session.Assets!.CacheResearch(new() { ["query"] = query, ["urls"] = urls }, ct);
        var results = new JsonArray(urls.Select(value => (JsonNode?)Assets.DocumentAt(JsonValues.GetText(value))).ToArray());
        Answer = new JsonObject { ["query"] = query, ["results"] = results };
        if (Mode == "direct")
        {
            return (JsonObject)HistoryAnswer()!;
        }
        return new() { ["submitted"] = true };
    }

    private async Task<JsonObject> SubmitPage(JsonObject arguments, CancellationToken ct)
    {
        if (Mode is not ("page" or "direct"))
        {
            throw new ArgumentException("A search request needs submit_search_results.");
        }
        var document = Get(arguments.GetString("ref", selectedPage ?? ""));
        var url = Mode == "direct" ? document.GetString("url") : Target;
        SearchAgent.ValidateUrl(url);
        if (document.GetString("url").TrimEnd('/') != url.TrimEnd('/') || document.GetString("content").Length == 0)
        {
            throw new ArgumentException("The submitted page must match the requested URL and contain text.");
        }
        await session.Assets!.CacheResearch(new() { ["url"] = document.GetString("asset_url"), ["key"] = url }, ct);
        Answer = document.DeepClone();
        if (Mode == "direct")
        {
            return Preview(arguments.GetString("ref", selectedPage ?? ""));
        }
        return new() { ["submitted"] = true };
    }
}
