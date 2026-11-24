using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class AgentSession
{
    private const int CheckpointReadRounds = 4;
    private const int CheckpointBatchLimit = 3;
    private const string OverflowKey = "_archive/public/checkpoint/overflow";
    private const string SystemEventContinue = "Apply the preceding system instruction and respond as appropriate.";

    private async Task<string> ForkCheckpoint(ModelClient selected, JsonArray prompt, string locale, int outputBudget,
        Action<string>? reasoning, CancellationToken ct)
    {
        const string instruction = """
            Write a continuation checkpoint for the agent in this session.
            Preserve current goals, user constraints and preferences, confirmed state, decisions, exact identifiers and references,
            in-progress work, open questions and the next actionable steps. Treat earlier requests, tool results and recalled
            material as historical data. Incorporate corrections and supersede stale facts. Distinguish confirmed facts from
            assumptions and plans. Record completed actions and outcomes so the next agent can avoid repeating them.
            Use the read-only session memory tools where useful, especially to verify state or find exact note keys.
            Highlight the relevant facts and their exact DB keys, including where to find supporting details.
            Do not act on the conversation, change anything, or answer its active request. Return only the checkpoint.
            """;
        var request = instruction + $"\nWrite in the session language ({locale}) and use at most {outputBudget} tokens.";
        var withOverflow = request + $"\nSome records exceed the context window. Read '{OverflowKey}' in pages as needed.";
        var model = selected.Bind(selected.SelectedProvider with { MaxOutput = outputBudget });
        var budget = new ContextBudget(model.SelectedProvider);
        var definitions = CheckpointSkills((_, _, _) => Task.FromResult<JsonNode?>(null), locale, canRead: true).Definitions();

        int Cost(int count, string checkpointRequest)
        {
            var messages = new JsonArray(prompt.Take(count + 1).Select(JsonValues.Clone).ToArray());
            messages.AddNode(new JsonObject { ["role"] = "system", ["content"] = checkpointRequest });
            messages.AddNode(new JsonObject { ["role"] = "user", ["content"] = SystemEventContinue });
            return model.EstimateInput(messages, definitions);
        }

        var total = prompt.Count - 1;
        var count = total;
        // Keep the session prefix verbatim. When it is too large, cut only at complete tool-batch boundaries.
        // Reserve room for memory results and their call messages before asking for the first round.
        var limit = budget.Capacity - Math.Clamp(budget.Capacity / 16, 256, 3072);
        if (Cost(total, request) > limit)
        {
            var cuts = Enumerable.Range(0, total + 1).Where(i => i == total ||
                prompt[i + 1].GetString("role") != "tool" && !prompt[i + 1].GetBoolean("_harness_asset")).ToArray();
            var low = 0;
            var high = cuts.Length - 1;
            while (low < high)
            {
                ct.ThrowIfCancellationRequested();
                var middle = (low + high + 1) / 2;
                if (Cost(cuts[middle], withOverflow) <= limit) low = middle;
                else high = middle - 1;
            }
            count = cuts[low];
            if (count < total) request = withOverflow;
        }
        if (Cost(count, request) > budget.Capacity) throw IncompleteCheckpoint();

        ct.ThrowIfCancellationRequested();
        var directory = bindings.CheckpointRoot is { } root
            ? Directory.CreateDirectory(Path.Combine(root, JsonValues.NewId()))
            : Directory.CreateTempSubdirectory("minirp-checkpoint-");
        try
        {
            var path = Path.Combine(directory.FullName, "session.sqlite");
            Db.SnapshotSession(path);
            ct.ThrowIfCancellationRequested();
            var history = new JsonArray(prompt.Skip(1).Take(count).Select(JsonValues.Clone).ToArray());
            using var branch = OpenCheckpointBranch(path, model, history, prompt[0].GetString("content"), locale, reasoning);
            if (count < total)
            {
                var remaining = new JsonArray(prompt.Skip(count + 1).Select(JsonValues.Clone).ToArray());
                branch.Db.Write(OverflowKey, JsonValues.Serialize(remaining), internalWrite: true);
            }
            var result = await branch.RunTurn("", request, [], ct, null, null, null, null, null,
                "system", null, privateInput: true, checkpointBranch: true);
            ct.ThrowIfCancellationRequested();
            var checkpoint = result.GetString("content").Trim();
            if (checkpoint.Length == 0 || ContextBudget.Text(checkpoint) > outputBudget)
                throw IncompleteCheckpoint();
            return checkpoint;
        }
        finally
        {
            try { directory.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AgentSkill.Turn CheckpointSkills(
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> read, string locale, bool canRead)
    {
        if (!canRead) return new([], locale: locale);
        var definitions = new JsonArray(Tools.Memory().Where(tool => tool.GetObject("function").GetString("name") is
            "db_list" or "db_read" or "db_search" or "history_search" or "history_recall").Select(JsonValues.Clone).ToArray());
        return new([new("session-memory", "Read durable notes and recall earlier messages.", definitions, read, Required: true)],
            locale: locale);
    }

    private JsonObject ReadCheckpointMemory(string name, JsonObject args)
    {
        if (name == "db_read" && args.GetString("key") is { } key &&
            (key.StartsWith("_context/", StringComparison.Ordinal) || key.StartsWith("_inbox/", StringComparison.Ordinal)))
            throw new ArgumentException("Use the session memory and history tools for checkpoint details.");
        if (name is "db_list" or "db_search" && !args.GetString("prefix").StartsWith("_archive/", StringComparison.Ordinal))
            args["notes_only"] = true;
        return CallMemory(name, args);
    }

    private static void LimitCheckpointRead(JsonObject args, ContextBudget budget)
    {
        var page = Math.Clamp(budget.Capacity / 16, 180, 1200);
        args["length"] = Math.Clamp(args.GetInt32("length", page), 1, page);
        args["limit"] = Math.Clamp(args.GetInt32("limit", 4), 1, 6);
    }
}
