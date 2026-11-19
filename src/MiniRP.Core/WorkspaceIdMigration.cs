using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Upgrades host-owned identities before the catalog, plugins or sessions are opened.</summary>
internal sealed class WorkspaceIdMigration(string root)
{
    private const string Journal = "identity-migration.json";
    private const string BackupDirectory = "identity-backup";
    private static readonly string[] SessionRoles = ["chat", "env", "search", "fetch", "title"];
    private static readonly string[] DatabaseSuffixes = [".sqlite", ".sqlite-wal", ".sqlite-shm", ".sqlite-journal"];

    public static void Run(string root) => new WorkspaceIdMigration(Path.GetFullPath(root)).Run();

    private void Run()
    {
        var journalPath = At(Journal);
        var plan = ReadObject(journalPath);
        if (plan?.GetBoolean("complete") == true) return;
        if (plan is null)
        {
            if (File.Exists(journalPath)) throw new InvalidDataException("The identity migration journal cannot be read.");
            plan = Plan();
            AtomicFile.Write(journalPath, JsonValues.Serialize(plan));
        }
        var presets = plan.GetArray("presets").OfType<JsonObject>().ToArray();
        foreach (var preset in presets) MigratePreset(preset);
        MigrateWorld();
        MigratePlugins(presets);
        plan["complete"] = true;
        AtomicFile.Write(journalPath, JsonValues.Serialize(plan));
    }

    private JsonObject Plan()
    {
        var presets = FileIds(At("presets"))
            .Concat(DirectoryIds(At("conversations")))
            .Concat(DirectoryIds(At("preset_sessions")))
            .Distinct(StringComparer.Ordinal);
        var items = new JsonArray();
        foreach (var legacy in presets)
        {
            var preset = WorkspaceIds.Preset(legacy);
            var parent = At("conversations", legacy);
            var rooms = new JsonArray();
            foreach (var source in FileIds(parent).Concat(DirectoryIds(parent)).Distinct(StringComparer.Ordinal))
            {
                var room = WorkspaceIds.Room(preset, source);
                var record = ReadObject(Path.Combine(parent, source + ".json"));
                var ensemble = record?.GetObject("ensemble");
                var children = new JsonArray();
                foreach (var chat in EnsembleChats(ensemble))
                {
                    var old = chat.GetString("id");
                    if (FileId(old))
                        children.AddNode(new JsonObject { ["old"] = old, ["id"] = WorkspaceIds.Chat(room, old) });
                }
                rooms.AddNode(new JsonObject
                {
                    ["old"] = source, ["id"] = room, ["ensemble"] = ensemble is { Count: > 0 },
                    ["chats"] = children,
                    ["participants"] = JsonValues.Clone(ensemble?["agents"])
                });
            }
            items.AddNode(new JsonObject { ["old"] = legacy, ["id"] = preset, ["rooms"] = rooms });
        }
        return new JsonObject { ["presets"] = items, ["complete"] = false };
    }

    private void MigratePreset(JsonObject entry)
    {
        var old = Workspace.Safe(entry.GetString("old"));
        var preset = WorkspaceIds.Require(entry.GetString("id"));
        var legacyRecord = At("presets", old + ".json");
        var currentRecord = At("presets", preset + ".json");
        Move(legacyRecord, currentRecord);
        Rewrite(currentRecord, document =>
        {
            document["id"] = preset;
            foreach (var chat in EnsembleChats(document["ensemble"] as JsonObject))
            {
                var id = chat.GetString("id");
                if (FileId(id)) chat["id"] = WorkspaceIds.Chat(preset, id);
            }
        });

        var oldParent = At("conversations", old);
        var parent = At("conversations", preset);
        if (ChangesNameCase(oldParent, parent) && !HasExactEntry(parent))
            Move(oldParent, parent);
        foreach (var item in entry.GetArray("rooms").OfType<JsonObject>())
            MigrateRoom(oldParent, parent, preset, item);
        if (UnlinkedDirectory(oldParent) && !SamePath(oldParent, parent) && !Directory.EnumerateFileSystemEntries(oldParent).Any())
            Directory.Delete(oldParent);

        var presetSession = At("preset_sessions", preset);
        Move(At("preset_sessions", old), presetSession);
        var transcript = Path.Combine(presetSession, "blocks.json");
        RewriteNode(transcript, document =>
        {
            if (document is JsonArray blocks)
                RewriteBlocks(blocks, preset, "", null, null);
        });
    }

    private void MigrateRoom(string oldParent, string parent, string preset, JsonObject entry)
    {
        var old = Workspace.Safe(entry.GetString("old"));
        var room = WorkspaceIds.Require(entry.GetString("id"));
        var record = Path.Combine(parent, room + ".json");
        var directory = Path.Combine(parent, room);
        Move(Path.Combine(oldParent, old), directory);
        Move(Path.Combine(oldParent, old + ".json"), record);
        Rewrite(record, document =>
        {
            // A Python import should still preserve the source JavaScript-independent record.
            if (document["chat"] is JsonObject && document["blocks"] is not JsonArray)
                BackupOriginal(record, Path.Combine(directory, "migration", "python-v1.json"));
            document["id"] = room;
            if (document["preset"] is not JsonObject) document["preset"] = preset;
            if (document.ContainsKey("preset_id")) document["preset_id"] = preset;
            RewriteId(document, "parent_chat", id => WorkspaceIds.Room(preset, id));
            RewriteId(document, "parent_room", id => WorkspaceIds.Room(preset, id));
            var ensemble = document["ensemble"] as JsonObject;
            RewriteBlocks(document.GetArray("blocks"), preset, room, ensemble, null);
            foreach (var chat in EnsembleChats(ensemble))
            {
                var id = chat.GetString("id");
                if (!FileId(id)) continue;
                var chatId = WorkspaceIds.Chat(room, id);
                chat["id"] = chatId;
                RewriteBlocks(chat.GetArray("blocks"), preset, room, ensemble, chatId);
            }
            if (ensemble is not null && ensemble["chats"] is null && ensemble["rooms"] is JsonArray oldChats)
            {
                ensemble.Remove("rooms");
                ensemble["chats"] = oldChats;
            }
        });
        if (!entry.GetBoolean("ensemble")) return;
        foreach (var file in Files(Path.Combine(directory, "agents"), "*.sqlite"))
            RewriteWatches(file, room);
        foreach (var participant in Directories(Path.Combine(directory, "ensemble")))
            foreach (var file in Files(participant, "*.sqlite")) RewriteWatches(file, room);
    }

    private static IEnumerable<JsonObject> EnsembleChats(JsonObject? ensemble) =>
        (ensemble?["chats"] as JsonArray ?? ensemble?["rooms"] as JsonArray ?? new()).OfType<JsonObject>();

    private static void RewriteBlocks(JsonArray blocks, string preset, string room, JsonObject? ensemble, string? chat)
    {
        foreach (var block in blocks.OfType<JsonObject>())
        {
            RewriteId(block, "preset", WorkspaceIds.Preset);
            RewriteId(block, "chat", id => WorkspaceIds.Room(preset, id));
            if (room.Length > 0) RewriteId(block, "conversation", id => WorkspaceIds.Chat(room, id));
            if (block["plugin_card"]?["environment"] is not JsonObject environment) continue;
            RewriteId(environment, "preset", WorkspaceIds.Preset);
            var oldRoom = environment.GetString("room");
            var oldChat = environment.GetString("chat");
            if (room.Length == 0)
            {
                RewriteId(environment, "room", id => WorkspaceIds.Room(preset, id));
                RewriteId(environment, "chat", id => WorkspaceIds.Room(preset, id));
                RewriteId(environment, "session", id => RewritePresetSession(id, preset));
                continue;
            }
            if (oldRoom.Length > 0 || oldChat.Length > 0) environment["room"] = room;
            if (ensemble is null)
            {
                if (oldChat.Length > 0) environment["chat"] = room;
            }
            else
            {
                // In the experimental reversed layout, a nested Chat was called a Room.
                var child = FileId(oldChat) && WorkspaceIds.Room(preset, oldChat) == room ? oldRoom : oldChat;
                var candidate = FileId(child) ? WorkspaceIds.Chat(room, child) : null;
                var exists = candidate is not null && EnsembleChats(ensemble).Any(c =>
                    FileId(c.GetString("id")) && WorkspaceIds.Chat(room, c.GetString("id")) == candidate);
                environment["chat"] = exists ? candidate : chat;
            }
            var session = environment.GetString("session");
            var role = environment.GetString("agent");
            if (session.Length > 0 && role == "preset") environment["session"] = "preset-" + preset;
            else if (session.Length > 0 && role is "chat" or "env" or "search" or "title")
            {
                var participant = environment.GetString("participant");
                environment["session"] = role + "-" + room + (participant.Length > 0 ? "-" + participant : "");
            }
        }
    }

    private static string RewritePresetSession(string id, string preset) =>
        id.StartsWith("preset-", StringComparison.Ordinal) ? "preset-" + preset : id;

    private void MigrateWorld() => Rewrite(At("world_state.json"), document =>
    {
        var states = document.GetObject("presets");
        foreach (var (id, state) in states.ToArray())
        {
            if (!FileId(id)) continue;
            var preset = WorkspaceIds.Preset(id);
            if (id != preset && !states.ContainsKey(preset))
            {
                states.Remove(id);
                states[preset] = state;
            }
        }
        if (document.ContainsKey("phase") && !states.ContainsKey(WorkspaceIds.Preset("ufo-2026")))
        {
            var original = (JsonObject)document.DeepClone();
            original.Remove("presets");
            states[WorkspaceIds.Preset("ufo-2026")] = original;
        }
        if (states.Count > 0 && document["presets"] is null) document["presets"] = states;
    });

    private void MigratePlugins(JsonObject[] presets)
    {
        var rooms = presets.SelectMany(preset => preset.GetArray("rooms").OfType<JsonObject>()).ToArray();
        var byLegacyRoom = rooms.GroupBy(room => room.GetString("old"), StringComparer.Ordinal)
            .Where(group => group.Select(room => room.GetString("id")).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var presetIds = Identifiers(presets);
        var roomIds = Identifiers(byLegacyRoom.Values);
        var sessionIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousSessions = new HashSet<string>(StringComparer.Ordinal);
        void Session(string old, string id)
        {
            if (!sessionIds.TryAdd(old, id) && sessionIds[old] != id) ambiguousSessions.Add(old);
        }
        foreach (var (old, id) in presetIds) Session("preset-" + old, "preset-" + id);
        foreach (var (old, room) in byLegacyRoom)
        {
            var id = room.GetString("id");
            var suffixes = room.GetArray("participants").Select(actor => actor.GetString("id"))
                .Where(FileId).Select(participant => "-" + participant).Prepend("");
            foreach (var suffix in suffixes)
                foreach (var role in SessionRoles)
                    Session(role + "-" + old + suffix, role + "-" + id + suffix);
        }
        foreach (var key in ambiguousSessions) sessionIds.Remove(key);
        var children = byLegacyRoom.ToDictionary(pair => pair.Key,
            pair => Identifiers(pair.Value.GetArray("chats").OfType<JsonObject>()), StringComparer.Ordinal);

        foreach (var area in new[] { "data", "structured" })
        {
            foreach (var plugin in Directories(At("plugins", area)))
            {
                var settings = Path.Combine(plugin, "settings.sqlite");
                if (area == "data" && File.Exists(settings))
                {
                    VerifyPath(settings);
                    RewriteScope(settings);
                }
                MigrateDatabases(Path.Combine(plugin, "preset"), Path.Combine(plugin, "preset"), presetIds);
                MigrateDatabases(Path.Combine(plugin, "session"), Path.Combine(plugin, "session"), sessionIds);
                MigrateDatabases(Path.Combine(plugin, "room"), Path.Combine(plugin, "room"), roomIds);
                var chatScope = Path.Combine(plugin, "chat");
                MigrateDatabases(chatScope, chatScope, roomIds);
                foreach (var directory in Directories(chatScope).ToArray())
                {
                    var old = Path.GetFileName(directory);
                    if (children.TryGetValue(old, out var chats))
                        MigrateDatabases(directory, Path.Combine(chatScope, roomIds[old]), chats);
                }
            }
        }
    }

    private static Dictionary<string, string> Identifiers(IEnumerable<JsonObject> entries) =>
        entries.GroupBy(entry => entry.GetString("old"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().GetString("id"), StringComparer.Ordinal);

    private void MigrateDatabases(string directory, string target, Dictionary<string, string> ids)
    {
        var stems = Files(directory, "*.sqlite*").Select(Path.GetFileName)
            .OfType<string>().Select(name => name.Split(".sqlite", 2)[0]).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var stem in stems)
        {
            if (!ids.TryGetValue(stem, out var id)) continue;
            var source = Path.Combine(directory, stem);
            var output = Path.Combine(target, id);
            var caseOnly = ChangesNameCase(source, output);
            if (SamePath(source, output) && !caseOnly) continue;
            if (caseOnly ? HasExactEntry(source + ".sqlite") && HasExactEntry(output + ".sqlite") :
                File.Exists(source + ".sqlite") && File.Exists(output + ".sqlite")) continue;
            foreach (var suffix in DatabaseSuffixes) Move(source + suffix, output + suffix);
        }
    }

    private static void RewriteScope(string path)
    {
        try
        {
            using var db = new MemoryDb(path);
            var raw = db.Get("host/scope");
            if (raw is null) return;
            var scope = JsonValues.ParseObject(raw);
            if (scope["presets"] is not JsonArray ids) return;
            var canonical = new JsonArray(ids.Select(id => JsonValues.GetText(id))
                .Select(id => FileId(id) ? WorkspaceIds.Preset(id) : id).Distinct(StringComparer.Ordinal)
                .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            if (JsonNode.DeepEquals(ids, canonical)) return;
            scope["presets"] = canonical;
            db.Write("host/scope", JsonValues.Serialize(scope), internalWrite: true);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
    }

    private static void RewriteWatches(string path, string room)
    {
        const string key = "_context/db-watches";
        try
        {
            using var db = new MemoryDb(path);
            if (db.Get(key) is not { } raw) return;
            var watches = JsonValues.ParseObject(raw);
            foreach (var (entry, revision) in watches.ToArray())
            {
                var parts = entry.Split('/');
                if (parts is not ["_state", _, _, var legacy] || !FileId(legacy)) continue;
                var chat = WorkspaceIds.Chat(room, legacy);
                if (chat == legacy) continue;
                watches.Remove(entry);
                var target = string.Join('/', parts.Take(3)) + "/" + chat;
                if (!watches.ContainsKey(target)) watches[target] = revision;
            }
            var canonical = JsonValues.Serialize(watches);
            if (raw != canonical) db.Write(key, canonical, internalWrite: true);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
    }

    private static void RewriteId(JsonObject document, string property, Func<string, string> convert)
    {
        if (document[property] is JsonValue value && value.TryGetValue<string>(out var id) && FileId(id))
            document[property] = convert(id);
    }

    private JsonObject? ReadObject(string path) => ReadNode(path) as JsonObject;

    private JsonNode? ReadNode(string path)
    {
        if (!File.Exists(path)) return null;
        VerifyPath(path);
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private void Rewrite(string path, Action<JsonObject> update) =>
        RewriteNode(path, node => { if (node is JsonObject document) update(document); });

    private void RewriteNode(string path, Action<JsonNode> update)
    {
        if (ReadNode(path) is not { } before) return;
        var after = before.DeepClone();
        update(after);
        if (JsonNode.DeepEquals(before, after)) return;
        BackupOriginal(path, At(BackupDirectory, Path.GetRelativePath(root, path)));
        var timestamp = File.GetLastWriteTimeUtc(path);
        AtomicFile.Write(path, JsonValues.Serialize(after));
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    private void BackupOriginal(string source, string backup)
    {
        VerifyPath(source);
        VerifyPath(backup);
        if (File.Exists(backup)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(source, backup);
    }

    private void Move(string source, string target)
    {
        var caseOnly = ChangesNameCase(source, target);
        if (caseOnly ? !HasExactEntry(source) :
            SamePath(source, target) || (!File.Exists(source) && !Directory.Exists(source))) return;
        VerifyPath(source);
        VerifyPath(target);
        if (caseOnly ? HasExactEntry(target) : File.Exists(target) || Directory.Exists(target))
            target = ConflictPath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private string ConflictPath(string source)
    {
        var path = At(BackupDirectory, "conflicts", Path.GetRelativePath(root, source));
        if (File.Exists(path) || Directory.Exists(path)) path += "." + JsonValues.NewId();
        VerifyPath(path);
        return path;
    }

    private string At(params string[] segments) => Path.Combine([root, ..segments]);

    private void VerifyPath(string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new IOException("Identity migration path escapes the workspace.");
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Identity migration cannot move or rewrite a linked path.");
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool ChangesNameCase(string left, string right) =>
        OperatingSystem.IsWindows() && SamePath(left, right) && Path.GetFileName(left) != Path.GetFileName(right);

    private static bool HasExactEntry(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        return UnlinkedDirectory(parent) && Directory.EnumerateFileSystemEntries(parent, name)
            .Any(entry => Path.GetFileName(entry) == name);
    }

    private static bool UnlinkedDirectory(string directory) =>
        Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;

    private static IEnumerable<string> Files(string directory, string pattern) =>
        UnlinkedDirectory(directory) ? Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
            { AttributesToSkip = FileAttributes.ReparsePoint }) : [];

    private static IEnumerable<string> Directories(string directory) =>
        UnlinkedDirectory(directory) ? Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            { AttributesToSkip = FileAttributes.ReparsePoint }) : [];

    private static IEnumerable<string> FileIds(string directory) =>
        Files(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(FileId);

    private static IEnumerable<string> DirectoryIds(string directory) =>
        Directories(directory).Select(Path.GetFileName).OfType<string>().Where(FileId);

    private static bool FileId(string id) =>
        id.Length != 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
