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

    private sealed record TranscriptSource(Workspace Workspace, string Pane, string Conversation, JsonArray Blocks, string Generation, bool MessagesOnly);
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
    }

    private (JsonObject[] Blocks, string? Before) ReadTranscript(TranscriptSource source, int end, bool preserveRunning = false,
        string? locale = null)
    {
        var page = new List<JsonObject>(TranscriptPageSize);
        string? older = null;
        for (var index = end - 1; index >= 0; index--)
        {
            if (source.Blocks[index] is not JsonObject block ||
                source.MessagesOnly && !EnsembleConversation.IsMessage(block) ||
                source.Pane is "chat" or "env" && TranscriptPane(block) != source.Pane)
                continue;
            if (page.Count == TranscriptPageSize)
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
}
