using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private static bool IsTranscriptExport(JsonObject command) =>
        command.GetString("op") == "archive_export" && command.GetString("type") == "transcript";

    private void RequireIdleTranscript()
    {
        if (Volatile.Read(ref inbox) is not null)
            throw new InvalidOperationException("Wait for the current turn to finish before exporting a transcript.");
    }

    private async Task ExportTranscript(JsonObject command, CancellationToken ct)
    {
        RequireIdleTranscript();
        RequirePreset(command);
        EnsurePresetSelected();
        var role = command.GetString("agent", "chat");
        var header = new JsonObject
        {
            ["type"] = "transcript", ["version"] = 1, ["scope"] = role, ["preset"] = Workspace.PresetId,
        };
        IEnumerable<JsonObject> blocks;
        if (role is "preset" or "plugin")
        {
            EnsurePresetSession();
            var run = command.GetString("run");
            if (role == "plugin" && run.Length == 0) throw new ArgumentException("Choose a plugin subagent run.");
            lock (presetTraceGate)
            {
                var source = presetConversation!.Blocks.OfType<JsonObject>();
                if (role == "plugin")
                {
                    source = source.Where(block => PluginRunId(block) == run);
                    header["run"] = run;
                }
                var snapshot = source.Select(block => (JsonObject)block.DeepClone()).ToArray();
                if (role == "plugin" && snapshot.Length == 0)
                    throw new InvalidOperationException("The plugin subagent run is unavailable.");
                if (snapshot.Any(block => block.GetString("plugin_status") == "running"))
                    throw new InvalidOperationException("Wait for the plugin subagent to finish before exporting this transcript.");
                header["title"] = role == "preset" ? Workspace.Config.GetString("name") :
                    snapshot.Select(block => block.GetString("plugin_agent_name")).FirstOrDefault(name => name.Length > 0) ?? "";
                blocks = snapshot;
            }
        }
        else if (role is "chat" or "env")
        {
            RequireActiveChat(command);
            header["room"] = Workspace.ChatId;
            header["title"] = Workspace.Chat.GetString("name");
            if (role == "chat" && ensemble is { } room)
            {
                var conversation = command.GetString("conversation");
                if (conversation.Length == 0) throw new InvalidOperationException("Open a chat in the room first.");
                header["chat"] = conversation;
                lock (room.SyncRoot)
                {
                    header["title"] = room.Name(conversation);
                    blocks = room.Blocks(conversation).OfType<JsonObject>().Select(block => (JsonObject)block.DeepClone()).ToArray();
                }
            }
            else
            {
                if (role == "chat") header["chat"] = Workspace.ChatId;
                bool Matches(JsonObject block) => role == "env"
                    ? block.GetString("agent") is "env" or "EnvAgent"
                    : ToolTrace.Role(block.GetString("agent")) is "" or "chat" or "search" or "fetch";
                lock (Workspace.RoomGate)
                {
                    var history = Workspace.Chat.GetArray("blocks").OfType<JsonObject>().Where(Matches);
                    var source = history.Any() ? history : Workspace.Chat.GetArray("messages").OfType<JsonObject>().Where(Matches);
                    blocks = source.Select(block => (JsonObject)block.DeepClone()).ToArray();
                }
            }
        }
        else throw new ArgumentException("Choose a chat, world, preset, or plugin subagent transcript.");

        await TranscriptExport.Write(command.GetString("path"), header, blocks, ct);
    }
}
