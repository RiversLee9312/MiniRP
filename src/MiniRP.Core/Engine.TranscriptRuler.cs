using System.Globalization;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private static JsonArray TranscriptRulerPrefix(TranscriptSource source, string? before)
    {
        var rows = new JsonArray();
        if (before is null) return rows;
        var end = int.Parse(before.AsSpan(source.Generation.Length + 1), CultureInfo.InvariantCulture);
        for (var index = 0; index < end; index++)
        {
            if (source.Blocks[index] is not JsonObject block || !InTranscript(source, block)) continue;
            var kind = block.GetString("kind", block.GetString("role"));
            var user = kind is "user" or "system";
            var content = block.GetString("content");
            var attachment = user ? block.GetArray("attachments").FirstOrDefault() : null;
            var card = block.GetString("open_ui").Length != 0;
            if (kind != "reply_end" && (user
                ? content.Length == 0 && attachment is null
                : block.GetString("tool").Length != 0 || block.GetBoolean("compaction") ||
                    block.GetString("plugin_status").Length != 0 || string.IsNullOrWhiteSpace(content) && !card))
                continue;
            var id = block.GetString("id");
            var row = new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(id) ? source.Generation + "-" + index.ToString(CultureInfo.InvariantCulture) : id
            };
            foreach (var key in new[] { "agent", "parent_agent", "plugin_run", "plugin_agent", "speaker" })
                CopyText(row, key, block.GetString(key));
            CopyText(row, "kind", kind);
            if (kind == "reply_end")
            {
                CopyText(row, "reply", RulerExcerpt(block.GetString("reply")));
                CopyText(row, "first_block", block.GetString("first_block"));
            }
            else
            {
                CopyText(row, "content", RulerExcerpt(content));
                if (user && block.GetBoolean("interjection")) row["interjection"] = true;
                if (user && block.GetString("version").Length != 0) row["version"] = true;
                if (attachment is not null)
                {
                    var excerpt = new JsonObject();
                    CopyText(excerpt, "kind", attachment.GetString("kind"));
                    CopyText(excerpt, "name", RulerExcerpt(attachment.GetString("name")));
                    CopyText(excerpt, "text", RulerExcerpt(attachment.GetString("text")));
                    row["attachments"] = new JsonArray(excerpt);
                }
                if (card)
                {
                    row["open_ui"] = true;
                    CopyText(row, "open_ui_title", RulerExcerpt(block.GetString("open_ui_title")));
                }
            }
            rows.AddNode(row);
        }
        return rows;
    }

    private static string RulerExcerpt(string text)
    {
        text = text.Trim();
        if (text.Length <= 320) return text;
        return text[..(char.IsHighSurrogate(text[319]) ? 319 : 320)];
    }

    private static void CopyText(JsonObject target, string key, string value)
    {
        if (value.Length != 0) target[key] = value;
    }
}
