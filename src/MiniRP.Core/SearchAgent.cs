using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed class SearchAgent(Workspace world, Func<AgentSession> getSession, PageSource web,
    string identity = "", Func<string>? roomReference = null, bool persistResults = true)
{
    public SearchAgent(Workspace world, AgentSession session, PageSource web, string identity = "", Func<string>? roomReference = null)
        : this(world, () => session, web, identity, roomReference) { }

    private static readonly HashSet<string> TerminalTools = ["submit_search_results", "submit_page", "fail_request"];
    private const string SystemPrompt = """
        You are SearchAgent. Each turn is one search or page request.
        Choose any number of real searches/fetches, including none, or recall completed virtual results/pages.
        Freely mix candidates, precisely edit them, and cache completed work.
        Submit through submit_search_results/submit_page/fail_request; prose is not a submission.
        Refs last one request only. Result count is entirely your decision; legacy limits are advisory.
        Keep workflow and editing annotations out of document titles, snippets and bodies. Submit the document as the reader should see it.
        """;
    private readonly SearchWorkflow workflow = new(world);

    internal async Task<string> RunDirect(AgentSession chat, string system, string input, JsonArray? attachments,
        CancellationToken token, string inputRole, bool privateInput)
    {
        var turn = new SearchTurn("direct", "", world, chat);
        var response = await InResearch(chat, turn, web, () => chat.Run(system + "\n" + SearchWorkflow.WorldInstructions + "\n" + workflow.Policies +
            "\n" + PresetDesign.SearchGuidance(world.Mode) + "\n" + world.AgentPrompt("search"),
            input, new AgentSkill("direct-research", "Find, edit and cache search results and pages.", SearchTools.Create(direct: true),
                (tool, arguments, ct) => workflow.Execute(turn, tool, arguments, ct), Required: true),
            token, attachments: attachments, inputRole: inputRole, privateInput: privateInput));
        token.ThrowIfCancellationRequested();
        var answer = response.GetString("content");
        if (persistResults && !turn.Failed && turn.Changed && !string.IsNullOrWhiteSpace(answer))
        {
            turn.Commit(world);
        }
        return answer;
    }

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
        var session = getSession();
        var turn = new SearchTurn(mode, target, world, session);

        var request = "Request " + mode + ": " + target;
        await InResearch(session, turn, web, () => session.Run(
            SystemPrompt + "\n" + SearchWorkflow.WorldInstructions + "\n" + world.WorldText + "\n" + workflow.Policies +
            "\n" + PresetDesign.SearchGuidance(world.Mode) + "\n" + world.AgentPrompt("search") + "\n" + identity,
            request,
            new AgentSkill("search-workflow", "Find, recall, edit and submit search results or pages.", SearchTools.Create(),
                (tool, arguments, token) => workflow.Execute(turn, tool, arguments, token), Required: true),
            cancellationToken,
            () => turn.Answer is not null,
            terminalTools: TerminalTools,
            historyInput: request,
            historySubmission: () => session.ToolText(turn.HistoryAnswer()),
            reference: "Recent conversation reference:\n" + (roomReference?.Invoke() ?? world.RecentConversation(2400))));
        cancellationToken.ThrowIfCancellationRequested();
        if (persistResults && !turn.Failed)
        {
            turn.Commit(world);
        }
        return turn.Answer!;
    }

    internal static async Task<JsonObject> InResearch(AgentSession agent, SearchTurn turn, PageSource web, Func<Task<JsonObject>> run)
    {
        var assets = agent.Assets ?? throw new InvalidOperationException("Research needs a session Asset registry.");
        var previous = assets.Research;
        assets.Research = new(turn, web);
        try { return await run(); }
        finally { assets.Research = previous; }
    }
}
