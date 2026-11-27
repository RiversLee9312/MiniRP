using System.Globalization;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private const int TranscriptPageSize = 80;
    private readonly object transcriptGate = new();
    private readonly Dictionary<string, TranscriptSource> transcriptSources = new(StringComparer.Ordinal);
    private readonly Dictionary<JsonObject, WebDisplayBlock> webDisplay = new(ReferenceEqualityComparer.Instance);
    private Workspace? transcriptOwner;

    private sealed record TranscriptSource(Workspace Workspace, string Pane, string Conversation, JsonArray Blocks, string Generation,
        bool MessagesOnly, string PluginRun = "")
    {
        public int SubagentsIndexed { get; set; }
        public HashSet<string> Subagents { get; } = new(StringComparer.Ordinal);
    }
    private sealed record WebDisplayBlock(Workspace Workspace, JsonObject Block, string Id, string Conversation);

    private bool WebHistory => Volatile.Read(ref routing).Settings.GetBoolean("_web_transcripts");

    private void TrackWebDisplay(JsonObject block, string id, string conversation, Workspace workspace)
    {
        if (block["plugin_card"] is not JsonObject && block.GetString("tool").Length == 0 || id.Length == 0) return;
        lock (transcriptGate)
        {
            if (!ReferenceEquals(workspace, Workspace)) return;
            UseTranscriptOwner(workspace);
            webDisplay[block] = new(workspace, block, id, conversation);
        }
    }

    private void RecordWebDisplay(JsonObject item, Dictionary<string, JsonObject> blocks, string conversation, Workspace workspace)
    {
        if (WebHistory && (item.GetString("kind") == "open_ui_card" || ToolTrace.IsEvent(item)) &&
            blocks.TryGetValue(item.GetString("block"), out var block))
            TrackWebDisplay(block, item.GetString("block"), conversation, workspace);
    }

    private WebDisplayBlock[] TrackedWebCards(Workspace workspace) =>
        TrackedWebDisplay(workspace, block => block["plugin_card"] is JsonObject);

    private WebDisplayBlock[] TrackedWebToolTraces(Workspace workspace) =>
        TrackedWebDisplay(workspace, block => block.GetString("tool").Length > 0);

    private WebDisplayBlock[] TrackedWebDisplay(Workspace workspace, Func<JsonObject, bool> includes)
    {
        lock (transcriptGate)
            return webDisplay.Values.Where(block => ReferenceEquals(block.Workspace, workspace) &&
                (block.Conversation.Length == 0 || block.Conversation == activeEnsembleChat) && includes(block.Block)).ToArray();
    }

    private void ResetTranscriptProjection()
    {
        lock (transcriptGate) UseTranscriptOwner(null);
    }

    // Called with transcriptGate held.
    private void UseTranscriptOwner(Workspace? workspace)
    {
        if (ReferenceEquals(transcriptOwner, workspace)) return;
        transcriptOwner = workspace;
        transcriptSources.Clear();
        webDisplay.Clear();
    }

    private static string TranscriptPane(JsonObject block) => block.GetString("agent") switch
    {
        "env" or "EnvAgent" => "env",
        "preset" or "PresetAgent" or "plugin" or "PluginAgent" => "preset",
        _ => "chat"
    };

    private JsonArray ProjectRoomTranscript(PresetConversation conversation, JsonObject? history)
    {
        var blocks = Workspace.Chat.GetArray("blocks");
        if (history is null)
            return new JsonArray(blocks.OfType<JsonObject>().Where(block => !PresetConversation.IsPreset(block))
                .Concat(conversation.Blocks.OfType<JsonObject>()).Select(LoadBlock).ToArray());

        var result = new JsonArray();
        foreach (var (pane, source) in new[] { ("chat", blocks), ("env", blocks), ("preset", conversation.Blocks) })
            ProjectTranscript(pane, source, history, result);
        return result;
    }

    private JsonArray ProjectPresetTranscript(PresetConversation conversation, JsonObject? history)
    {
        if (history is null) return new JsonArray(conversation.Blocks.OfType<JsonObject>().Select(LoadBlock).ToArray());
        var result = new JsonArray();
        ProjectTranscript("preset", conversation.Blocks, history, result);
        return result;
    }

    private JsonObject ProjectEnsembleTranscript(JsonObject item, EnsembleConversation hub, string conversation)
    {
        lock (hub.SyncRoot)
        lock (presetTraceGate)
        {
            if (conversation.Length == 0)
            {
                item["blocks"] = new JsonArray();
                if (WebHistory)
                {
                    lock (transcriptGate) transcriptSources.Remove("ensemble");
                    item["history"] = new JsonObject { ["ensemble"] = null };
                }
                return item;
            }
            var blocks = hub.Blocks(conversation);
            var messagesOnly = hub.PrivateParticipant(conversation).Length == 0;
            if (!WebHistory)
                item["blocks"] = new JsonArray(blocks.OfType<JsonObject>()
                    .Where(block => !messagesOnly || EnsembleConversation.IsMessage(block)).Select(block => LoadBlock(block, conversation)).ToArray());
            else
            {
                var history = new JsonObject();
                var result = new JsonArray();
                ProjectTranscript("ensemble", blocks, history, result, conversation, preserveRunning: !messagesOnly, messagesOnly: messagesOnly);
                item["blocks"] = result;
                item["history"] = history;
            }
            return item;
        }
    }

    private void ProjectTranscript(string pane, JsonArray blocks, JsonObject history, JsonArray result, string conversation = "",
        bool preserveRunning = false, bool messagesOnly = false)
    {
        TranscriptSource source;
        lock (transcriptGate)
        {
            UseTranscriptOwner(Workspace);
            // Tail checkpoints keep saved cursors valid as new blocks append to the same array.
            var current = transcriptSources.GetValueOrDefault(pane);
            source = current is not null && ReferenceEquals(current.Workspace, Workspace) &&
                ReferenceEquals(current.Blocks, blocks) && current.Conversation == conversation && current.MessagesOnly == messagesOnly
                ? current : new(Workspace, pane, conversation, blocks, JsonValues.NewId(), messagesOnly);
            transcriptSources[pane] = source;
        }
        var (page, older) = ReadTranscript(source, blocks.Count, preserveRunning);
        foreach (var block in page) result.AddNode(block);
        history[pane] = older;
        if (history["_subagents"] is not JsonObject summaries) history["_subagents"] = summaries = new();
        summaries[pane] = IndexSubagents(source);
        if (history["_ruler"] is not JsonObject rulers) history["_ruler"] = rulers = new();
        rulers[pane] = TranscriptRulerPrefix(source, older);
    }

    private static bool InTranscript(TranscriptSource source, JsonObject block) =>
        (!source.MessagesOnly || EnsembleConversation.IsMessage(block)) &&
        (source.PluginRun.Length == 0 || PluginRunId(block) == source.PluginRun) &&
        (source.Pane is not ("chat" or "env") || TranscriptPane(block) == source.Pane);

    private static bool IsBrowserTrace(JsonObject block) =>
        block.GetString("agent") is "fetch" or "BrowserAgent" or "FetchAgent" ||
        block.GetString("agent") is "env" or "EnvAgent" or "preset" or "PresetAgent" or "plugin" or "PluginAgent" &&
        block.GetString("speaker") is "BrowserAgent" or "FetchAgent";

    private static JsonArray IndexSubagents(TranscriptSource source)
    {
        if (source.SubagentsIndexed > source.Blocks.Count)
        {
            source.SubagentsIndexed = 0;
            source.Subagents.Clear();
        }
        for (; source.SubagentsIndexed < source.Blocks.Count; source.SubagentsIndexed++)
        {
            if (source.Blocks[source.SubagentsIndexed] is not JsonObject block || !InTranscript(source, block)) continue;
            if (PluginRunId(block) is { Length: > 0 } run)
                source.Subagents.Add("plugin:" + block.GetString("plugin_agent", run));
            else if (IsBrowserTrace(block))
                source.Subagents.Add(block.GetString("parent_agent") is "search" or "SearchAgent" ? "search/browser" : "browser");
            else if (block.GetString("agent") is "search" or "SearchAgent") source.Subagents.Add("search");
        }
        return new JsonArray(source.Subagents.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    private (JsonObject[] Blocks, string? Before) ReadTranscript(TranscriptSource source, int end, bool preserveRunning = false,
        string? locale = null, int start = 0, bool bounded = true)
    {
        var page = new List<JsonObject>(TranscriptPageSize);
        string? older = null;
        for (var index = end - 1; index >= start; index--)
        {
            if (source.Blocks[index] is not JsonObject block || !InTranscript(source, block))
                continue;
            if (bounded && page.Count == TranscriptPageSize)
            {
                older = source.Generation + ":" + (index + 1).ToString(CultureInfo.InvariantCulture);
                break;
            }
            var loaded = LoadBlock(block, source.Conversation,
                source.Generation + "-" + index.ToString(CultureInfo.InvariantCulture), source.Workspace, locale);
            if (preserveRunning) loaded["running"] = block.GetBoolean("running");
            page.Add(loaded);
        }
        page.Reverse();
        return ([.. page], older);
    }

    private void TranscriptCheckpoint()
    {
        var reply = new JsonObject { ["kind"] = "transcript_checkpoint" };
        var workspace = Workspace;
        try
        {
            if (!WebHistory) return;
            lock (workspace.RoomGate)
            lock (presetTraceGate)
            {
                var history = new JsonObject();
                var panes = new JsonObject();
                void Project(string pane, JsonArray source, string conversation = "", bool messagesOnly = false)
                {
                    var blocks = new JsonArray();
                    ProjectTranscript(pane, source, history, blocks, conversation, preserveRunning: true, messagesOnly: messagesOnly);
                    panes[pane] = blocks;
                }
                if (workspace.ChatId.Length > 0)
                {
                    var blocks = workspace.Chat.GetArray("blocks");
                    Project("chat", blocks);
                    Project("env", blocks);
                }
                if (presetConversation is { } preset) Project("preset", preset.Blocks);
                var selected = activeEnsembleChat;
                if (selected.Length > 0 && ensemble is { } room)
                    Project("ensemble", room.Blocks(selected), selected, messagesOnly: room.PrivateParticipant(selected).Length == 0);
                reply["preset"] = workspace.PresetId;
                reply["chat"] = workspace.ChatId;
                reply["conversation"] = selected;
                reply["panes"] = panes;
                reply["history"] = history;
                if (ensemble is { } group)
                {
                    reply["activity"] = Volatile.Read(ref roomActivity)?.Snapshot() ?? new JsonArray();
                    reply["private_in_story"] = group.DirectMessagesInStory;
                    if (selected.Length > 0 && group.PrivateParticipant(selected) is { Length: > 0 } participant)
                        reply["private_participant"] = participant;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply["error"] = error.Message;
        }
        finally { events.Enqueue(reply); }
    }

    private void TranscriptPage(JsonObject command)
    {
        var reply = new JsonObject { ["kind"] = "transcript_page", ["request"] = command.GetString("request") };
        try
        {
            if (!WebHistory) throw new InvalidOperationException("Web transcript history is unavailable.");
            var preset = WorkspaceIds.Require(command.GetString("preset"));
            var room = command.GetString("room");
            var conversation = command.GetString("conversation");
            var agent = command.GetString("agent");
            var pane = agent == "chat" && conversation.Length != 0 ? "ensemble" : agent;
            if (agent is not ("chat" or "env" or "preset") || (agent != "chat" && conversation.Length != 0) ||
                (agent == "preset" ? room.Length != 0 : !WorkspaceIds.IsValid(room)))
                throw new ArgumentException("Choose a transcript to read.");

            var before = command.GetString("before");
            TranscriptSource? source;
            lock (transcriptGate) source = transcriptSources.GetValueOrDefault(pane);
            if (source is null || source.Workspace.PresetId != preset || source.Conversation != conversation ||
                (pane != "preset" && source.Workspace.ChatId != room) ||
                !before.StartsWith(source.Generation + ":", StringComparison.Ordinal) ||
                !int.TryParse(before.AsSpan(source.Generation.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end))
                throw new InvalidOperationException("The displayed transcript has changed.");

            lock (source.Workspace.RoomGate)
            lock (presetTraceGate)
            {
                bool Current()
                {
                    lock (transcriptGate)
                        return ReferenceEquals(source.Workspace, Workspace) &&
                            ReferenceEquals(source, transcriptSources.GetValueOrDefault(pane)) &&
                            (pane != "ensemble" || conversation == activeEnsembleChat);
                }
                if (!Current() || end < 1 || end > source.Blocks.Count)
                    throw new InvalidOperationException("The displayed transcript has changed.");
                var (page, older) = ReadTranscript(source, end, locale: command.GetString("locale"));
                if (!Current()) throw new InvalidOperationException("The displayed transcript has changed.");
                reply["blocks"] = new JsonArray(page);
                reply["before"] = older;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply["error"] = error.Message;
        }
        events.Enqueue(reply);
    }

    private static string PluginRunId(JsonObject block) => block.GetString("plugin_run", block.GetString("plugin_agent"));

    private void ReadPresetActivity(JsonObject command)
    {
        var reply = new JsonObject { ["kind"] = "preset_activity", ["request"] = command.GetString("request") };
        try
        {
            var preset = WorkspaceIds.Require(command.GetString("preset"));
            var run = command.GetString("run");
            lock (presetTraceGate)
            {
                var workspace = Workspace;
                if (!WebHistory || workspace.PresetId != preset || presetConversation is not { } owner || owner.PresetId != preset)
                    throw new InvalidOperationException("The displayed preset has changed.");
                var blocks = owner.Blocks;
                if (run.Length == 0)
                {
                    var items = blocks.OfType<JsonObject>().GroupBy(PluginRunId)
                        .Where(group => group.Key.Length != 0)
                        .Select(group =>
                        {
                            var status = group.FirstOrDefault(block => block.GetString("plugin_status").Length > 0);
                            var worker = group.First();
                            return new JsonObject
                            {
                                ["id"] = group.Key, ["worker"] = worker.GetString("plugin_agent", group.Key),
                                ["name"] = worker.GetString("plugin_agent_name"), ["status"] = status.GetString("plugin_status")
                            };
                        });
                    reply["items"] = new JsonArray(items.ToArray());
                }
                else
                {
                    var runBlocks = blocks.OfType<JsonObject>().Where(block => PluginRunId(block) == run).ToArray();
                    if (runBlocks.Length == 0)
                        throw new InvalidOperationException("The plugin subagent run is unavailable.");
                    var generation = "preset-run-" + preset + "-" + run;
                    var before = command.GetString("before");
                    var after = command.GetString("after");
                    var end = blocks.Count;
                    var start = 0;
                    int Cursor(string value) =>
                        value.StartsWith(generation + ":", StringComparison.Ordinal) &&
                        int.TryParse(value.AsSpan(generation.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                        index > 0 && index <= blocks.Count
                            ? index : throw new ArgumentException("The displayed run has changed.");
                    if (before.Length > 0 && after.Length > 0)
                        throw new ArgumentException("Choose one end of the displayed run.");
                    if (before.Length > 0) end = Cursor(before);
                    if (after.Length > 0 && after != "0") start = Cursor(after);
                    var status = runBlocks.FirstOrDefault(block => block.GetString("plugin_status").Length > 0);
                    var source = new TranscriptSource(workspace, "preset", "", blocks, generation, MessagesOnly: false, PluginRun: run);
                    var (page, older) = ReadTranscript(source, end, preserveRunning: status.GetString("plugin_status") == "running",
                        locale: command.GetString("locale"), start: start, bounded: after.Length == 0);
                    reply["blocks"] = new JsonArray(page);
                    reply["before"] = after.Length > 0 && start > 0 ? after : older;
                    if (before.Length == 0 && after.Length == 0) reply["ruler"] = TranscriptRulerPrefix(source, older);
                    reply["has_browser"] = runBlocks.Any(IsBrowserTrace);
                }
                if (!ReferenceEquals(workspace, Workspace))
                    throw new InvalidOperationException("The displayed preset has changed.");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply["error"] = error.Message;
        }
        events.Enqueue(reply);
    }

    private void ReadEnsembleArchive(JsonObject command)
    {
        var reply = new JsonObject { ["kind"] = "ensemble_archive", ["request"] = command.GetString("request") };
        try
        {
            var preset = WorkspaceIds.Require(command.GetString("preset"));
            var room = WorkspaceIds.Require(command.GetString("room"));
            var workspace = Workspace;
            var hub = ensemble;
            lock (workspace.RoomGate)
            {
                if (!ReferenceEquals(workspace, Workspace) || !ReferenceEquals(hub, ensemble) ||
                    workspace.PresetId != preset || workspace.ChatId != room || hub is null)
                    throw new InvalidOperationException("The room has changed.");
                var conversation = command.GetString("conversation");
                if (conversation.Length == 0)
                    reply["items"] = hub.ListArchived();
                else
                {
                    conversation = WorkspaceIds.Require(conversation);
                    var blocks = hub.ArchivedBlocks(conversation);
                    var generation = "archive-" + room + "-" + conversation;
                    var before = command.GetString("before");
                    var end = blocks.Count;
                    if (before.Length > 0 && (!before.StartsWith(generation + ":", StringComparison.Ordinal) ||
                        !int.TryParse(before.AsSpan(generation.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out end) ||
                        end < 1 || end > blocks.Count))
                        throw new ArgumentException("The archived transcript has changed.");
                    var source = new TranscriptSource(workspace, "ensemble", conversation, blocks, generation, MessagesOnly: true);
                    var (page, older) = ReadTranscript(source, end, locale: command.GetString("locale"));
                    reply["blocks"] = new JsonArray(page);
                    reply["before"] = older;
                    if (before.Length == 0) reply["ruler"] = TranscriptRulerPrefix(source, older);
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply["error"] = error.Message;
        }
        events.Enqueue(reply);
    }
}
