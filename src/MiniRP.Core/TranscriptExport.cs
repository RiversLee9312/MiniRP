using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class TranscriptExport
{
    private static readonly string[] Fields =
    [
        "id", "agent", "speaker", "participant", "sequence", "content", "interjection", "reasoning", "tool", "arguments", "result",
        "open_ui", "open_ui_title", "open_ui_state", "open_ui_revision", "open_ui_action",
        "plugin_agent", "plugin_agent_name", "plugin_run", "plugin_sequence", "plugin_status"
    ];

    internal static async Task Write(string destination, JsonObject header, IEnumerable<JsonObject> blocks, CancellationToken ct)
    {
        destination = Path.GetFullPath(destination);
        var temporary = destination + "." + JsonValues.NewId() + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            await using (var writer = new StreamWriter(stream) { NewLine = "\n" })
            {
                await writer.WriteLineAsync(JsonValues.Serialize(header).AsMemory(), ct);
                foreach (var block in blocks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Record(block) is { } line) await writer.WriteLineAsync(JsonValues.Serialize(line).AsMemory(), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private static JsonObject? Record(JsonObject block)
    {
        var kind = block.GetString("kind", block.GetString("role"));
        if (kind == "reply_end") return null;
        var type = block.GetString("plugin_status").Length > 0 ? "worker_status" :
            block.GetBoolean("compaction") || block.GetString("tool") == "compact_context" ? "compaction" :
            block.GetString("tool").Length > 0 ? "tool" :
            block.GetString("open_ui").Length > 0 ? "card" :
            block.GetString("content").Length > 0 || kind is "user" or "system" ? "message" :
            block.GetString("reasoning").Length > 0 ? "reasoning" : "";
        if (type.Length == 0) return null;
        var result = new JsonObject
        {
            ["type"] = type,
            ["role"] = kind is "user" or "system" ? kind : type == "tool" ? "tool" : "assistant",
        };
        foreach (var key in Fields)
            if (block[key] is { } value) result[key] = value.DeepClone();
        var attachments = block.GetArray("attachments").OfType<JsonObject>().Select(item => new JsonObject
        {
            ["name"] = item.GetString("name"),
            ["mime"] = item.GetString("mime"),
        }).ToArray();
        if (attachments.Length > 0) result["attachments"] = new JsonArray(attachments);
        return result;
    }
}
