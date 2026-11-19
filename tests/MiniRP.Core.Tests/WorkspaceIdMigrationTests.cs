using System.Text.Json.Nodes;
using MiniRP.Core;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class WorkspaceIdMigrationTests
{
    [TestMethod]
    public async Task RenamesUppercaseUuidFilesToTheirCanonicalCasing()
    {
        var root = Path.Combine(Path.GetTempPath(), "minirp-id-case-" + Guid.NewGuid().ToString("N"));
        var preset = "a06bbc72-2d6f-4db4-a415-86c875ac2d43";
        var room = "b34c0d00-4fdc-41b5-870a-849036e75d9d";
        var oldPreset = preset.ToUpperInvariant();
        var oldRoom = room.ToUpperInvariant();
        AtomicFile.Write(Path.Combine(root, "presets", oldPreset + ".json"), """{"name":"Uppercase fixture"}""");
        AtomicFile.Write(Path.Combine(root, "conversations", oldPreset, oldRoom + ".json"),
            JsonValues.Serialize(new JsonObject { ["id"] = oldRoom, ["preset"] = oldPreset }));

        await using var engine = new Engine(root, seedDefaultPreset: false);
        Assert.AreEqual(preset, engine.Workspace.PresetId);
        Assert.AreEqual(preset + ".json", Path.GetFileName(Directory.GetFiles(Path.Combine(root, "presets")).Single()));
        Assert.AreEqual(room + ".json", Path.GetFileName(Directory.GetFiles(Path.Combine(root, "conversations", preset)).Single()));
    }

    [TestMethod]
    public async Task MigratesLegacyRecordsAndKnownPluginScopesWithoutChangingTheirContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "minirp-id-migration-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "conversations", "old-preset");
        var plugin = Path.Combine(root, "plugins", "data", "test.fixture");
        var preset = WorkspaceIds.Preset("old-preset");
        var one = WorkspaceIds.Room(preset, "room-one");
        var two = WorkspaceIds.Room(preset, "room-two");
        var firstChat = WorkspaceIds.Chat(one, "bridge");
        var secondChat = WorkspaceIds.Chat(two, "bridge");
        var originalTime = new DateTime(2024, 10, 1, 12, 30, 0, DateTimeKind.Utc);
        AtomicFile.Write(Path.Combine(root, "presets", "old-preset.json"), """{"name":"Fixture","chat_mode":"ensemble"}""");
        AtomicFile.Write(Path.Combine(legacy, "room-one.json"), """
            {"id":"room-one","preset":"old-preset","name":"Room one","ensemble":{"rooms":[{"id":"bridge","name":"Bridge",
             "blocks":[{"id":"block-1","chat":"room-one","conversation":"bridge","plugin_card":
               {"environment":{"_ensemble":true,"room":"bridge","chat":"room-one","preset":"old-preset","agent":"chat","participant":"ada","session":"chat-room-one-ada"}}}]}],
             "agents":[{"id":"ada","name":"Ada"}]}}
            """);
        File.SetLastWriteTimeUtc(Path.Combine(legacy, "room-one.json"), originalTime);
        AtomicFile.Write(Path.Combine(legacy, "room-two.json"), """
            {"id":"room-two","preset":"old-preset","name":"Room two","parent_chat":"room-one",
             "ensemble":{"chats":[{"id":"bridge","name":"Bridge"}],"agents":[{"id":"ada","name":"Ada"}]}}
            """);
        AtomicFile.Write(Path.Combine(root, "world_state.json"), """{"presets":{"old-preset":{"phase":"World state"}}}""");
        WriteDb(Path.Combine(root, "preset_sessions", "old-preset", "agent.sqlite"), "shared", "reference");
        WriteDb(Path.Combine(legacy, "room-one", "ensemble", "ada", "chat.sqlite"), "_context/db-watches",
            """{"_state/plugin/score/bridge":4}""");
        WriteDb(Path.Combine(plugin, "settings.sqlite"), "host/scope", """{"mode":"include","presets":["old-preset"]}""");
        WriteDb(Path.Combine(plugin, "preset", "old-preset.sqlite"), "paper", "preset");
        WriteDb(Path.Combine(plugin, "chat", "room-one", "bridge.sqlite"), "paper", "chat");
        WriteDb(Path.Combine(plugin, "room", "room-one.sqlite"), "paper", "room");
        WriteDb(Path.Combine(plugin, "session", "chat-room-one-ada.sqlite"), "paper", "agent");

        await using (var engine = new Engine(root, seedDefaultPreset: false))
        {
            Assert.AreEqual(preset, engine.Workspace.PresetId);
            var room = ReadObject(Path.Combine(root, "conversations", preset, one + ".json"));
            var branch = ReadObject(Path.Combine(root, "conversations", preset, two + ".json"));
            Assert.AreEqual(one, room.GetString("id"));
            Assert.AreEqual(one, branch.GetString("parent_chat"));
            Assert.AreEqual(firstChat, room.GetObject("ensemble").GetArray("chats")[0].GetString("id"));
            Assert.AreEqual(secondChat, branch.GetObject("ensemble").GetArray("chats")[0].GetString("id"));
            Assert.AreNotEqual(firstChat, secondChat);
            var card = room.GetObject("ensemble").GetArray("chats")[0].GetArray("blocks")[0]
                .GetObject("plugin_card").GetObject("environment");
            Assert.AreEqual(one, card.GetString("room"));
            Assert.AreEqual(firstChat, card.GetString("chat"));
            Assert.AreEqual(preset, card.GetString("preset"));
            Assert.AreEqual("chat-" + one + "-ada", card.GetString("session"));
            Assert.AreEqual(originalTime, File.GetLastWriteTimeUtc(Path.Combine(root, "conversations", preset, one + ".json")));
            Assert.AreEqual("reference", ReadDb(Path.Combine(root, "preset_sessions", preset, "agent.sqlite"), "shared"));
            var watches = JsonValues.ParseObject(ReadDb(Path.Combine(root, "conversations", preset, one, "ensemble", "ada", "chat.sqlite"),
                "_context/db-watches")!);
            Assert.AreEqual(4, watches.GetInt32("_state/plugin/score/" + firstChat));
            var storedScope = JsonValues.ParseObject(ReadDb(Path.Combine(plugin, "settings.sqlite"), "host/scope")!);
            Assert.AreEqual(preset, JsonValues.GetText(storedScope.GetArray("presets").Single()));
            Assert.AreEqual("preset", ReadDb(Path.Combine(plugin, "preset", preset + ".sqlite"), "paper"));
            Assert.AreEqual("chat", ReadDb(Path.Combine(plugin, "chat", one, firstChat + ".sqlite"), "paper"));
            Assert.AreEqual("room", ReadDb(Path.Combine(plugin, "room", one + ".sqlite"), "paper"));
            Assert.AreEqual("agent", ReadDb(Path.Combine(plugin, "session", "chat-" + one + "-ada.sqlite"), "paper"));
            Assert.AreEqual("World state", ReadObject(Path.Combine(root, "world_state.json")).GetObject("presets")[preset].GetString("phase"));
        }

        // Re-opening a workspace must not reassign IDs or migrate a completed layout again.
        await using var reopened = new Engine(root, seedDefaultPreset: false);
        Assert.AreEqual(preset, reopened.Workspace.PresetId);
        Assert.AreEqual(firstChat, ReadObject(Path.Combine(root, "conversations", preset, one + ".json"))
            .GetObject("ensemble").GetArray("chats")[0].GetString("id"));
        Assert.IsFalse(File.Exists(Path.Combine(legacy, "room-one.json")));
    }

    private static JsonObject ReadObject(string path) => JsonValues.ParseObject(File.ReadAllText(path));

    private static void WriteDb(string path, string key, string value)
    {
        using var db = new MemoryDb(path);
        db.Write(key, value, internalWrite: true);
    }

    private static string? ReadDb(string path, string key)
    {
        Assert.IsTrue(File.Exists(path), path);
        using var db = new MemoryDb(path);
        return db.Get(key);
    }
}
