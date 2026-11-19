using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>One-way import of the Python storage format. Original files are kept as migration backups.</summary>
internal static class LegacyImporter
{
    public static void ImportChat(JsonObject chat)
    {
        if (chat["messages"] is not JsonArray)
        {
            var messages = new JsonArray();
            foreach (var message in chat.GetObject("chat").GetArray("history"))
            {
                if (message.GetString("role") is "user" or "assistant")
                {
                    messages.AddNode(new JsonObject { ["role"] = message.GetString("role"), ["agent"] = "chat", ["content"] = message.GetString("content") });
                }
            }
            chat["messages"] = messages;
        }
        if (chat["blocks"] is JsonArray)
        {
            return;
        }

        var blocks = new JsonArray();
        foreach (var trace in chat.GetArray("trace"))
        {
            if (trace is not JsonObject) continue;
            var kind = trace.GetString("kind");
            var block = new JsonObject { ["id"] = JsonValues.NewId(), ["agent"] = trace.GetString("agent"), ["running"] = false };
            if (kind == "user" || trace.GetString("role") == "user") block["kind"] = "user";
            if (kind == "thought")
            {
                block["reasoning"] = trace.GetString("text");
            }
            else if (kind == "tool") { block["tool"] = trace.GetString("title"); block["result"] = trace.GetString("text"); }
            else
            {
                block["content"] = trace.GetString("text");
            }

            blocks.AddNode(block);
        }
        if (blocks.Count == 0)
        {
            foreach (var message in chat.GetArray("messages").OfType<JsonObject>())
            {
                var block = (JsonObject)message!.DeepClone();
                block["id"] = JsonValues.NewId();
                if (block.GetString("role") == "user")
                {
                    block["kind"] = "user";
                }

                blocks.AddNode(block);
            }
        }
        chat["blocks"] = blocks;
    }

    public static JsonObject? World(string root, string preset)
    {
        var path = Path.Combine(root, "world_state.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonValues.ParseObject(File.ReadAllText(path));
        var state = document.GetObject("presets")[preset] as JsonObject;
        if (state is null && preset == "ufo-2026" && document.ContainsKey("phase"))
        {
            state = document;
        }

        if (state is null)
        {
            return null;
        }

        state = (JsonObject)state.DeepClone();
        var cache = new JsonObject();
        foreach (var pair in state.GetObject("search_cache"))
        {
            if (pair.Value is JsonObject record)
            {
                cache[record.GetString("query", pair.Key)] = JsonValues.Clone(record["results"]) ?? new JsonArray();
            }
            else if (pair.Value is JsonArray results)
            {
                cache[pair.Key] = results.DeepClone();
            }
        }
        state["search_cache"] = cache;
        return state;
    }
}
