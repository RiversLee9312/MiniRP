using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>A staged search workspace. No network or model dependencies; failed turns never commit it.</summary>
internal sealed class SearchTurn
{
    private readonly AgentSession session;
    private readonly Dictionary<string, JsonObject> documents = new(StringComparer.Ordinal);
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
    public bool Changed { get; private set; }

    public SearchTurn(string mode, string target, JsonObject state, AgentSession session)
    {
        Mode = mode;
        Target = target;
        State = (JsonObject)state.DeepClone();
        this.session = session;
        foreach (var key in new[] { "pages", "search_cache" })
        {
            if (State[key] is not JsonObject)
            {
                State[key] = new JsonObject();
            }
        }
    }

    public JsonObject Register(JsonObject document)
    {
        var reference = JsonValues.NewId();
        documents[reference] = (JsonObject)document.DeepClone();
        return Preview(reference);
    }

    private JsonObject Get(string reference) => documents.TryGetValue(reference, out var document)
        ? document
        : throw new ArgumentException("Unknown ref. Recall previous results again for this turn.");

    private JsonObject Preview(string reference)
    {
        var document = (JsonObject)Get(reference).DeepClone();
        document["ref"] = reference;
        var content = document.GetString("content");
        if (content.Length > 22000)
        {
            document["content_key"] = session.Archive(content, "page");
            document["total_chars"] = content.Length;
            document["content"] = content[..12000];
        }
        return document;
    }

    public JsonNode Execute(string tool, JsonObject arguments) => tool switch
    {
        "fail_request" => Fail(arguments),
        "virtual_search" => Recall(arguments),
        "add_result" => AddResult(arguments),
        "select_results" => SelectResults(arguments),
        "edit_result" => EditResult(arguments),
        "create_page" => CreatePage(arguments),
        "edit_page" => EditPage(arguments),
        "set_page" => SetPage(arguments),
        "cache_page" => CachePage(arguments),
        "cache_search_results" => CacheResults(arguments),
        "submit_search_results" => SubmitResults(arguments),
        "submit_page" => SubmitPage(arguments),
        _ => throw new ArgumentException("Unknown search tool: " + tool)
    };

    private JsonObject Fail(JsonObject arguments)
    {
        Failed = true;
        Answer = JsonValues.Error(arguments.GetString("message"));
        return (JsonObject)Answer.DeepClone();
    }

    private JsonObject Recall(JsonObject arguments)
    {
        var query = arguments.GetString("query");
        var url = arguments.GetString("url").TrimEnd('/');
        var pages = State.GetObject("pages").Select(pair => pair.Value).OfType<JsonObject>();
        var results = State.GetObject("search_cache").Select(pair => pair.Value).OfType<JsonArray>().SelectMany(array => array).OfType<JsonObject>();
        var matches = pages.Concat(results).Reverse().Where(document =>
            (url.Length == 0 || document.GetString("url").TrimEnd('/') == url) &&
            (query.Length == 0 || JsonValues.Serialize(document).Contains(query, StringComparison.OrdinalIgnoreCase)));
        var output = new JsonArray();
        foreach (var document in matches.Take(Math.Clamp(arguments.GetInt32("limit", 30), 1, 200)))
        {
            output.AddNode(Register(document));
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
        var preview = Register(document);
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

    private JsonObject EditResult(JsonObject arguments)
    {
        CopyFields(arguments, Get(arguments.GetString("ref")));
        return Preview(arguments.GetString("ref"));
    }

    private static void CopyFields(JsonObject source, JsonObject target)
    {
        foreach (var field in source)
        {
            if (field.Key != "ref")
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
        return Register(document);
    }

    private JsonObject EditPage(JsonObject arguments)
    {
        var document = Get(arguments.GetString("ref"));
        var body = document.GetString("content");
        var old = arguments.GetString("old_string");
        var unique = body.IndexOf(old, StringComparison.Ordinal) == body.LastIndexOf(old, StringComparison.Ordinal);
        if (old.Length == 0 || !body.Contains(old, StringComparison.Ordinal) || (!unique && !arguments.GetBoolean("replace_all")))
        {
            throw new ArgumentException("old_string must exist and uniquely identify the edit.");
        }
        document["content"] = body.Replace(old, arguments.GetString("new_string"), StringComparison.Ordinal);
        return Preview(arguments.GetString("ref"));
    }

    private JsonObject SetPage(JsonObject arguments)
    {
        Get(arguments.GetString("ref"));
        selectedPage = arguments.GetString("ref");
        return new() { ["selected"] = selectedPage };
    }

    private JsonObject CachePage(JsonObject arguments)
    {
        var document = Get(arguments.GetString("ref"));
        if (document.GetString("content").Length == 0)
        {
            throw new ArgumentException("A cached page needs content.");
        }
        State.GetObject("pages")[document.GetString("url")] = document.DeepClone();
        Changed = true;
        return new() { ["cached"] = true };
    }

    private JsonArray Results(JsonObject arguments)
    {
        var references = arguments["refs"] is JsonArray array ? array.Select(node => JsonValues.GetText(node)) : selected;
        var results = new JsonArray();
        foreach (var reference in references)
        {
            results.AddNode(Get(reference).DeepClone());
        }
        return results;
    }

    private JsonObject CacheResults(JsonObject arguments)
    {
        var query = arguments.GetString("query", Target);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A cache query is required.");
        }
        State.GetObject("search_cache")[query] = Results(arguments);
        Changed = true;
        return new() { ["cached"] = true };
    }

    private JsonObject SubmitResults(JsonObject arguments)
    {
        if (Mode is not ("search" or "direct"))
        {
            throw new ArgumentException("A page request needs submit_page.");
        }
        var results = Results(arguments);
        var query = Mode == "direct" ? arguments.GetString("query") : Target;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A search query is required.");
        }
        State.GetObject("search_cache")[query] = results.DeepClone();
        Changed = true;
        Answer = new JsonObject { ["query"] = query, ["results"] = results };
        if (Mode == "direct")
        {
            return (JsonObject)Answer.DeepClone();
        }
        return new() { ["submitted"] = true };
    }

    private JsonObject SubmitPage(JsonObject arguments)
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
        State.GetObject("pages")[url] = document.DeepClone();
        Changed = true;
        Answer = document.DeepClone();
        if (Mode == "direct")
        {
            return Preview(arguments.GetString("ref", selectedPage ?? ""));
        }
        return new() { ["submitted"] = true };
    }
}
