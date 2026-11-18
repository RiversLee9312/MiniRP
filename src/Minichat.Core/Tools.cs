using System.Text.Json.Nodes;

namespace Minichat.Core;

public static class Tools
{
    public static JsonObject String(string description = "") => new() { ["type"] = "string", ["description"] = description };
    public static JsonObject Integer() => new() { ["type"] = "integer" };
    public static JsonObject Boolean() => new() { ["type"] = "boolean" };
    public static JsonObject StringArray() => new() { ["type"] = "array", ["items"] = String() };
    public static JsonObject Define(string name, string description, JsonObject? fields = null, params string[] required)
    {
        var r = new JsonArray();
        foreach (var key in required)
        {
            r.AddNode(key);
        }

        return new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = fields ?? new(), ["required"] = r } }
        };
    }
    public static JsonArray Memory() => new(
        Define("db_list", "List string keys. Returns cursor next_after.", new()
        {
            ["prefix"] = String(),
            ["after"] = String(),
            ["limit"] = Integer()
        }),
        Define("db_read", "Read a complete entry in pages; follow next_offset. Offsets are UTF-16 code units.", new()
        {
            ["key"] = String(),
            ["start"] = Integer(),
            ["length"] = Integer()
        }, "key"),
        Define("db_write", "Create or replace an entry. Existing entries require expected_revision from a read. Use memorable string keys.", new()
        {
            ["key"] = String(),
            ["content"] = String(),
            ["expected_revision"] = Integer()
        }, "key", "content"),
        Define("db_patch", "Precisely replace unique old_string; read first. Framework archives are read only.", new()
        {
            ["key"] = String(),
            ["old_string"] = String(),
            ["new_string"] = String(),
            ["expected_revision"] = Integer(),
            ["replace_all"] = Boolean()
        }, "key", "old_string", "new_string"),
        Define("db_delete", "Delete at expected_revision.", new()
        {
            ["key"] = String(),
            ["expected_revision"] = Integer()
        }, "key", "expected_revision"),
        Define("db_search", "Literal text search, FTS5 trigram or short-string scan. No embeddings. scope=all|keys|content.", new()
        {
            ["query"] = String(),
            ["prefix"] = String(),
            ["scope"] = String(),
            ["offset"] = Integer(),
            ["limit"] = Integer()
        }, "query"),
        Define("history_search", "Search your own earlier conversation, even after compaction. Use a short exact phrase or keyword. Returns message ids, roles and snippets; follow next_offset for more.", new()
        {
            ["query"] = String(),
            ["offset"] = Integer(),
            ["limit"] = Integer()
        }, "query"),
        Define("history_recall", "Read an original message by id from history_search. Follow next_offset for more text (UTF-16 offsets).", new()
        {
            ["id"] = String(),
            ["start"] = Integer(),
            ["length"] = Integer()
        }, "id"));
}
