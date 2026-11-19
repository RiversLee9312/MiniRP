using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class SessionStateWatches(MemoryDb db, Func<string, JsonObject?> readProjection)
{
    private const string Index = "_context/db-watches";
    private readonly JsonObject watches = JsonValues.ParseObject(db.Get(Index) ?? "{}");

    private JsonObject? Current(string key)
    {
        if (key.StartsWith(PluginRuntime.StateProjectionPrefix, StringComparison.Ordinal))
            return readProjection(key);
        if (key.StartsWith('_') || string.IsNullOrWhiteSpace(key)) return null;
        var row = db.Query("SELECT revision FROM entries WHERE key=?", key).FirstOrDefault();
        return row is null ? null : new JsonObject { ["revision"] = int.Parse(row["revision"], System.Globalization.CultureInfo.InvariantCulture) };
    }

    public JsonObject Set(JsonObject args)
    {
        var key = args.GetString("key");
        var enabled = args.GetBoolean("enabled", true);
        if (enabled)
        {
            var current = Current(key) ?? throw new ArgumentException("Read an available DB entry before watching it.");
            if (watches.Count >= 32 && !watches.ContainsKey(key)) throw new InvalidOperationException("This session can watch up to 32 entries.");
            watches[key] = current.GetInt32("revision");
        }
        else watches.Remove(key);
        Save();
        return new() { ["key"] = key, ["watching"] = enabled };
    }

    public void AppendUpdates(JsonArray history)
    {
        foreach (var (key, _) in watches.ToArray())
        {
            var previous = watches.GetInt32(key);
            var now = Current(key);
            var revision = now?.GetInt32("revision") ?? -1;
            if (previous == revision) continue;
            watches[key] = revision;
            var status = revision < 0 ? "is no longer available" : $"changed to revision {revision}";
            history.AddNode(new JsonObject { ["role"] = "system",
                ["content"] = $"Watched DB entry {key} {status}." + (revision < 0 ? "" : " Use db_read to obtain the current value.") });
        }
    }

    public void Save()
    {
        if (watches.Count > 0 || db.Get(Index) is not null)
            db.Write(Index, JsonValues.Serialize(watches), internalWrite: true);
    }

    public static JsonObject ReadProjection(JsonObject result, string key, JsonObject args)
    {
        var text = JsonValues.Serialize(result["value"]);
        var start = Math.Clamp(args.GetInt32("start"), 0, text.Length);
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(text.Length, start + Math.Clamp(args.GetInt32("length", 12000), 1, 32768));
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1])) end++;
        return new JsonObject { ["key"] = key, ["read_only"] = true, ["revision"] = result.GetInt32("revision"),
            ["content"] = text[start..end], ["total_chars"] = text.Length,
            ["next_offset"] = end < text.Length ? JsonValue.Create(end) : null };
    }
}
