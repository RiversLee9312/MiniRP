using System.Text.Json.Nodes;

namespace Minichat.Core;

internal sealed class WorkspaceCatalog : IDisposable
{
    private readonly MemoryDb db;
    private readonly Workspace source;
    private readonly object indexGate = new();

    public WorkspaceCatalog(Workspace workspace)
    {
        source = workspace;
        db = new(Path.Combine(workspace.Root, "catalog.sqlite"));
        db.Execute("CREATE TABLE IF NOT EXISTS presets(id TEXT PRIMARY KEY,name TEXT NOT NULL)");
        db.Execute("CREATE TABLE IF NOT EXISTS chats(preset TEXT NOT NULL,id TEXT NOT NULL,name TEXT NOT NULL,updated TEXT NOT NULL,PRIMARY KEY(preset,id))");
        db.Execute("CREATE INDEX IF NOT EXISTS chat_order ON chats(preset,updated DESC,id)");
        foreach (var preset in workspace.PresetEntries())
        {
            Preset(preset.GetString("id"), preset.GetString("name"));
        }
    }

    private void LoadLegacy(string preset)
    {
        lock (indexGate)
        {
            if (db.Get("indexed/" + preset) is not null)
            {
                return;
            }
            foreach (var chat in source.ChatEntries(preset))
            {
                db.Execute("INSERT OR IGNORE INTO chats VALUES(?,?,?,?)", preset, chat.GetString("id"), chat.GetString("name"), chat.GetString("updated_at"));
            }
            db.Write("indexed/" + preset, "1", internalWrite: true);
        }
    }

    public void Preset(string id, string name) => db.Execute("INSERT INTO presets VALUES(?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name", id, name);

    public void Chat(string preset, string id, string name, string updated) =>
        db.Execute("INSERT INTO chats VALUES(?,?,?,?) ON CONFLICT(preset,id) DO UPDATE SET name=excluded.name,updated=excluded.updated", preset, id, name, updated);

    public void RemoveChat(string preset, string id) => db.Execute("DELETE FROM chats WHERE preset=? AND id=?", preset, id);

    public void RemovePreset(string preset)
    {
        lock (indexGate)
        {
            db.Execute("DELETE FROM presets WHERE id=?", preset);
            db.Execute("DELETE FROM chats WHERE preset=?", preset);
        }
    }

    public JsonArray Page(string preset, int offset, int limit, PluginScope? scope = null)
    {
        if (preset.Length > 0)
        {
            LoadLegacy(Workspace.Safe(preset));
        }
        var rows = preset.Length > 0
            ? db.Query("SELECT id,name FROM chats WHERE preset=? ORDER BY updated DESC,id LIMIT ? OFFSET ?", preset, limit, offset)
            : scope is { Mode: "include" or "exclude" }
                ? db.Query("SELECT id,name FROM presets WHERE (id IN (SELECT value FROM json_each(?))) = ? ORDER BY name COLLATE NOCASE,id LIMIT ? OFFSET ?",
                    JsonValues.Serialize(scope.Describe()["presets"]), scope.Mode == "include" ? 1 : 0, limit, offset)
                : db.Query("SELECT id,name FROM presets ORDER BY name COLLATE NOCASE,id LIMIT ? OFFSET ?", limit, offset);
        var result = new JsonArray();
        foreach (var row in rows)
        {
            result.AddNode(new JsonObject { ["id"] = row["id"], ["name"] = row["name"] });
        }
        return result;
    }

    public void Dispose() => db.Dispose();
}
