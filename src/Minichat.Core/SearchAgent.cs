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
        """;
    private readonly SearchWorkflow workflow = new(world, web);

    internal async Task<string> RunDirect(AgentSession chat, string system, string input, JsonArray? attachments, CancellationToken token)
    {
        var turn = new SearchTurn("direct", "", world.State, chat);
        var response = await chat.Run(system + "\n" + SearchWorkflow.WorldInstructions + "\n" + workflow.Policies,
            input, SearchTools.Create(direct: true), (tool, arguments, ct) => workflow.Execute(turn, tool, arguments, ct),
            token, attachments: attachments);
        token.ThrowIfCancellationRequested();
        var answer = response.GetString("content");
        if (!turn.Failed && turn.Changed && !string.IsNullOrWhiteSpace(answer))
        {
            world.Commit(turn.State);
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
        var turn = new SearchTurn(mode, target, world.State, session);

        var request = "Request " + mode + ": " + target;
        await session.Run(
            SystemPrompt + "\n" + SearchWorkflow.WorldInstructions + "\n" + world.WorldText + "\n" + workflow.Policies,
            "Main conversation reference:\n" + session.ToolText(JsonValue.Create(world.Conversation)) + "\n" + request,
            SearchTools.Create(),
            (tool, arguments, token) => workflow.Execute(turn, tool, arguments, token),
            cancellationToken,
            () => turn.Answer is not null,
            terminalTools: TerminalTools,
            historyInput: request,
            historySubmission: () => session.ToolText(turn.Answer));
        cancellationToken.ThrowIfCancellationRequested();
        if (!turn.Failed)
        {
            world.Commit(turn.State);
        }
        return turn.Answer!;
    }
}
