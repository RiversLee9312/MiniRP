using System.Text.Json.Nodes;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed partial class WebSession
{
    private int transcriptUpdates;
    private bool transcriptCheckpointPending;

    private void RequestTranscriptCheckpoint()
    {
        if (transcriptCheckpointPending || ++transcriptUpdates < 256) return;
        transcriptCheckpointPending = true;
        Send(new() { ["op"] = "_transcript_checkpoint" });
    }

    private void RetainTranscriptCheckpoint(JsonObject checkpoint)
    {
        transcriptCheckpointPending = false;
        transcriptUpdates = 0;
        if (checkpoint["panes"] is not JsonObject panes || checkpoint["history"] is not JsonObject history) return;
        var preset = checkpoint.GetString("preset");
        var room = checkpoint.GetString("chat");
        var conversation = checkpoint.GetString("conversation");
        var loaded = journal.FindLastIndex(item => item.GetString("kind") == "loaded" &&
            item.GetString("preset") == preset && item["chat"].GetString("id") == room);
        var presetOpened = journal.FindLastIndex(item => item.GetString("kind") == "preset_opened" && item.GetString("preset") == preset);
        var ensembleLoaded = journal.FindLastIndex(item => item.GetString("kind") == "ensemble_chat_loaded" &&
            item.GetString("chat") == room && item.GetString("conversation") == conversation);

        var replaced = new HashSet<string>(StringComparer.Ordinal);
        JsonArray Replace(JsonObject item, params string[] names)
        {
            var updatedHistory = new JsonObject();
            var blocks = new JsonArray();
            foreach (var pane in names)
            {
                if (panes[pane] is not JsonArray source) continue;
                var entries = source.ToArray();
                source.Clear();
                foreach (var block in entries) blocks.AddNode(block);
                updatedHistory[pane] = JsonValues.Clone(history[pane]);
                replaced.Add(pane);
            }
            item["history"] = updatedHistory;
            return blocks;
        }

        if (loaded >= 0 && panes["chat"] is JsonArray && panes["env"] is JsonArray)
        {
            var original = journal[loaded];
            var updated = CloneExcept(original, "chat", "history");
            var chat = CloneExcept(original.GetObject("chat"), "blocks");
            chat["blocks"] = presetOpened > loaded
                ? Replace(updated, "chat", "env")
                : Replace(updated, "chat", "env", "preset");
            if (presetOpened > loaded) updated.GetObject("history")["preset"] = null;
            updated["chat"] = chat;
            journal[loaded] = updated;
        }
        if (presetOpened >= 0 && panes["preset"] is JsonArray)
        {
            var updated = CloneExcept(journal[presetOpened], "blocks", "history");
            updated["blocks"] = Replace(updated, "preset");
            journal[presetOpened] = updated;
        }
        if (ensembleLoaded >= 0 && panes["ensemble"] is JsonArray)
        {
            var updated = CloneExcept(journal[ensembleLoaded], "blocks", "history");
            updated["blocks"] = Replace(updated, "ensemble");
            journal[ensembleLoaded] = updated;
        }
        journal.RemoveAll(item =>
        {
            if (!IsTrace(item) && item.GetString("kind") != "ensemble_message") return false;
            if (item.GetString("preset") is { Length: > 0 } owner && owner != preset) return false;
            if (item.GetString("chat") is { Length: > 0 } target && target != room) return false;
            if (item.GetString("conversation") is { Length: > 0 } selected)
                return selected == conversation && replaced.Contains("ensemble");
            return replaced.Contains(item.GetString("agent") switch
            {
                "env" or "EnvAgent" => "env",
                "preset" or "PresetAgent" or "plugin" or "PluginAgent" => "preset",
                _ => "chat"
            });
        });
    }

    private static JsonObject CloneExcept(JsonObject item, params string[] excluded) => new(
        item.Where(field => !excluded.Contains(field.Key))
            .Select(field => new KeyValuePair<string, JsonNode?>(field.Key, JsonValues.Clone(field.Value))));
}
