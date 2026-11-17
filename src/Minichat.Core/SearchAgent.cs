using System.Text.Json.Nodes;

namespace Minichat.Core;

public sealed class SearchAgent(Workspace world, AgentSession session, PageSource web)
{
    private static readonly HashSet<string> TerminalTools = ["submit_search_results", "submit_page", "fail_request"];
    private const string SystemPrompt = """
        You are SearchAgent. Each turn is one search or page request.
        Choose any number of real searches/fetches, including none, or recall completed virtual results/pages.
        Freely mix candidates, precisely edit them, and cache completed work.
        Submit through submit_search_results/submit_page/fail_request; prose is not a submission.
        Refs last one request only. Result count is entirely your decision; legacy limits are advisory.
        Follow current world rules/canon. Preserve unrelated real-world content. Cached material may be stale.
        API completeness unknown is not complete; explicitly request backend=local for truncated pages.
        Full Markdown is retained in DB; use db_read with content_key to read beyond a preview.
        """;

    public static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("An HTTP(S) URL is required.");
        }
    }

    public async Task<JsonNode> Run(string mode, string target, CancellationToken cancellationToken)
    {
        if (mode == "page")
        {
            ValidateUrl(target);
        }
        var turn = new SearchTurn(mode, target, world.State, session);

        async Task<JsonNode?> Execute(string tool, JsonObject arguments, CancellationToken token)
        {
            switch (tool)
            {
                case "inspect_world":
                    return JsonValues.ParseObject(world.WorldText);
                case "real_search":
                    var response = await web.Search(arguments.GetString("query"), arguments.GetInt32("limit", 8), token);
                    var candidates = new JsonArray();
                    foreach (var document in response.GetArray("results").OfType<JsonObject>())
                    {
                        if (document.GetString("url").Length > 0)
                        {
                            candidates.AddNode(turn.Register(document));
                        }
                    }
                    return new JsonObject { ["results"] = candidates };
                case "real_fetch":
                    var page = await web.Fetch(arguments.GetString("url"), arguments.GetString("backend", "auto"), token);
                    return turn.Register(page);
                default:
                    return turn.Execute(tool, arguments);
            }
        }

        await session.Run(
            SystemPrompt + "\n" + world.WorldText + "\nEditing policies:\n" + JsonValues.Serialize(new JsonObject
            {
                ["search_editor"] = JsonValues.Clone(world.Config["search_editor"]),
                ["page_editor"] = JsonValues.Clone(world.Config["page_editor"]),
                ["page_generator"] = JsonValues.Clone(world.Config["page_generator"])
            }),
            "Main conversation reference:\n" + session.ToolText(JsonValue.Create(world.Conversation)) + "\nRequest " + mode + ": " + target,
            SearchTools.Create(),
            Execute,
            cancellationToken,
            () => turn.Answer is not null,
            terminalTools: TerminalTools);
        cancellationToken.ThrowIfCancellationRequested();
        if (!turn.Failed)
        {
            world.Commit(turn.State);
        }
        return turn.Answer!;
    }
}
