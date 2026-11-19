using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class WorkspaceTransferTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PresetAndSaveRestoreSignedPluginsAndConversationState()
    {
        var ct = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "minirp-transfer-" + WorkspaceIds.New());
        var source = new Workspace(Path.Combine(root, "source"), seedDefaultPreset: false);
        var target = new Workspace(Path.Combine(root, "target"), seedDefaultPreset: false);
        try
        {
            await using var sourcePlugins = Runtime(source);
            await using var targetPlugins = Runtime(target);
            var transfer = new WorkspaceTransfer(source, sourcePlugins);
            var restore = new WorkspaceTransfer(target, targetPlugins);
            var preset = WorkspaceIds.New();
            var room = WorkspaceIds.New();
            var ensemble = WorkspaceIds.New();
            var child = WorkspaceIds.New();
            var version = JsonValues.NewId();
            var attachment = JsonValues.NewId();
            var hiddenAttachment = JsonValues.NewId();
            var config = $"presets/{preset}.json";
            Write(source, config, new JsonObject { ["id"] = preset, ["name"] = "Portable fixture" }.ToJsonString());
            Write(source, $"conversations/{preset}/{room}.json", new JsonObject
            {
                ["id"] = room, ["name"] = "Regular room",
                ["blocks"] = new JsonArray(new JsonObject { ["version"] = version, ["attachments"] = Attachments(attachment) })
            }.ToJsonString());
            Write(source, $"conversations/{preset}/{ensemble}.json", new JsonObject
            {
                ["id"] = ensemble, ["name"] = "Ensemble",
                ["ensemble"] = new JsonObject { ["chats"] = new JsonArray(new JsonObject { ["id"] = child }) }
            }.ToJsonString());
            Write(source, $"versions/{version}/world.json", """{"phase":"remembered"}""");
            Write(source, $"attachments/{attachment}", "attachment bytes");
            Write(source, $"attachments/{hiddenAttachment}", "archived attachment");
            Write(source, $"preset_sessions/{preset}/blocks.json", """[{"content":"shared design"}]""");
            Write(source, $"preset_sessions/{preset}/plugin_agents/local/signer.pfx", "private signer");
            Write(source, "provider.json", "provider secret");
            var original = Pack(root, preset);
            var package = sourcePlugins.Install(original, inspect: false);
            var roomDb = $"conversations/{preset}/{room}/agents/chat.sqlite";
            var branchDb = $"versions/{version}/chat.sqlite";
            var ensembleDb = $"conversations/{preset}/{ensemble}/ensemble/actor/chat.sqlite";
            var scopedDb = $"plugins/data/{package.StorageKey}/chat/{ensemble}/{child}.sqlite";
            var presetDb = $"preset_sessions/{preset}/agent.sqlite";
            using var active = new MemoryDb(Path.Combine(source.Root, roomDb));
            active.Write("fixture", "active WAL");
            active.Write("_archive/history/one", new JsonObject
            {
                ["attachments"] = Attachments(hiddenAttachment)
            }.ToJsonString(), internalWrite: true);
            foreach (var file in new[] { branchDb, ensembleDb, scopedDb, presetDb })
            {
                using var db = new MemoryDb(Path.Combine(source.Root, file));
                db.Write("fixture", file);
            }
            var exportedPlugin = Path.Combine(root, "owned.plugin");
            await transfer.Export("plugin", "", "", package.Id, exportedPlugin, ct);
            CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(exportedPlugin));

            var roomArchive = Path.Combine(root, "single.minirp-room");
            await transfer.Export("room", preset, room, "", roomArchive, ct);
            using (var archive = ZipFile.OpenRead(roomArchive))
            {
                Assert.IsNotNull(archive.GetEntry(roomDb));
                Assert.IsNotNull(archive.GetEntry(branchDb));
                Assert.IsNotNull(archive.GetEntry($"attachments/{hiddenAttachment}"));
                Assert.IsNull(archive.GetEntry(ensembleDb));
                Assert.IsNull(archive.GetEntry(presetDb));
            }
            var ensembleArchive = Path.Combine(root, "ensemble.minirp-room");
            await transfer.Export("room", preset, ensemble, "", ensembleArchive, ct);
            using (var archive = ZipFile.OpenRead(ensembleArchive))
            {
                Assert.IsNotNull(archive.GetEntry(ensembleDb));
                Assert.IsNotNull(archive.GetEntry(scopedDb));
                Assert.IsNull(archive.GetEntry(roomDb));
            }

            var presetArchive = Path.Combine(root, "preset.minirp-preset");
            await transfer.Export("preset", preset, "", "", presetArchive, ct);
            Assert.AreEqual(preset, await restore.Import("preset", presetArchive, ct));
            Assert.AreEqual(0, target.Chats(preset).Count);
            var restored = targetPlugins.List().Single(item => item.GetString("id") == package.Id)!;
            Assert.AreEqual(preset, restored.GetString("presetId"));
            Assert.AreEqual(package.Signer, restored.GetString("signer"));
            CollectionAssert.AreEqual(File.ReadAllBytes(original),
                File.ReadAllBytes(Path.Combine(target.Root, "plugins", package.Id + ".plugin")));
            targetPlugins.RequireScope(package.Id, preset);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => targetPlugins.RequireScope(package.Id, WorkspaceIds.New()));

            var saveArchive = Path.Combine(root, "save.minirp-save");
            await transfer.Export("save", preset, "", "", saveArchive, ct);
            Assert.AreEqual(preset, await restore.Import("save", saveArchive, ct));
            Assert.AreEqual(2, target.Chats(preset).Count);
            Assert.AreEqual("attachment bytes", File.ReadAllText(Path.Combine(target.Root, "attachments", attachment)));
            Assert.AreEqual("archived attachment", File.ReadAllText(Path.Combine(target.Root, "attachments", hiddenAttachment)));
            Assert.AreEqual("""{"phase":"remembered"}""", File.ReadAllText(Path.Combine(target.Root, "versions", version, "world.json")));
            using (var db = MemoryDb.ReadOnly(Path.Combine(target.Root, roomDb)))
                Assert.AreEqual("active WAL", db.Get("fixture"));
            foreach (var file in new[] { branchDb, ensembleDb, scopedDb, presetDb })
            {
                using var db = MemoryDb.ReadOnly(Path.Combine(target.Root, file));
                Assert.AreEqual(file, db.Get("fixture"));
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(target.Root, "preset_sessions", preset, "plugin_agents")));
            Assert.IsFalse(File.Exists(Path.Combine(target.Root, "provider.json")));

            Write(target, config, new JsonObject { ["id"] = preset, ["name"] = "Local edit" }.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restore.Import("save", saveArchive, ct));
            target.UsePreset(preset);
            Assert.AreEqual("Local edit", target.Config.GetString("name"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidArchiveLeavesTheWorkspaceUntouched()
    {
        var ct = TestContext.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "minirp-transfer-" + WorkspaceIds.New());
        var workspace = new Workspace(root, seedDefaultPreset: false);
        try
        {
            await using var plugins = Runtime(workspace);
            var preset = WorkspaceIds.New();
            var archive = Path.Combine(root, "bad.minirp-preset");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var header = new StreamWriter(zip.CreateEntry("archive.json").Open()))
                    header.Write(new JsonObject { ["version"] = 1, ["kind"] = "preset", ["preset"] = preset }.ToJsonString());
                zip.CreateEntry($"presets/{preset}.json");
                zip.CreateEntry("../bad.json");
            }
            var transfer = new WorkspaceTransfer(workspace, plugins);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transfer.Import("save", archive, ct));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => transfer.Import("preset", archive, ct));
            Assert.AreEqual(0, workspace.Presets().Count);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(root, "trash")).Any());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static PluginRuntime Runtime(Workspace workspace)
    {
        var runtime = new PluginRuntime(workspace.Root, new HostPort(_ => { }), _ => { },
            (_, _, _, _, _) => throw new InvalidOperationException("Transfer needs no Harness calls."));
        runtime.Load();
        return runtime;
    }

    private static JsonArray Attachments(string id) => new(new JsonObject { ["id"] = id });

    private static void Write(Workspace workspace, string relative, string content)
    {
        var path = Path.Combine(workspace.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Pack(string root, string preset)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Transfer fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Path.Combine(root, "draft");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), new JsonObject
        {
            ["id"] = "owned", ["packageName"] = "test.transfer.owned", ["name"] = "Owned",
            ["apiVersion"] = 2, ["entry"] = "index.js", ["presetId"] = preset
        }.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "index.js"), "globalThis.handlers = {};");
        var archive = Path.Combine(root, "original.plugin");
        PluginArchive.Pack(directory, archive, signer);
        return archive;
    }
}
