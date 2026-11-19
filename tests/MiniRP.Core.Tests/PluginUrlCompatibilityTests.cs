using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MiniRP.Cli;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class PluginUrlCompatibilityTests
{
    [TestMethod]
    public async Task LegacySubscriberReceivesCanonicalEventAndCanReadItsOldAssetUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "minirp-urls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Plugin URL compatibility test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var signer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var publisher = Pack(root, signer, "publisher", """
                {"id":"publisher","name":"Publisher","apiVersion":2,"packageName":"test.rename.publisher","entry":"index.js",
                 "hooks":{"plugin.command":"minirp://actions/test.rename.publisher/hooks/send"},
                 "actions":{"handlers":{"send":"send"},"events":["ready"]},
                 "assets":[{"id":"item","mimeType":"text/plain","type":"file","path":"item.txt","accessibility":"public"}]}
                """, """
                globalThis.handlers = { send: (_, api) => api.actions.publish({
                    url: 'minirp://actions/test.rename.publisher/events/ready',
                    assets: {item: 'minichat://assets/test.rename.publisher/item'}
                }) };
                """);
            var listener = Pack(root, signer, "listener", """
                {"id":"listener","name":"Listener","apiVersion":2,"packageName":"test.rename.listener","entry":"index.js",
                 "permissions":["db"],"actions":{"handlers":{"receive":"receive"},"subscriptions":{
                 "minichat://actions/test.rename.publisher/events/ready":"minichat://actions/test.rename.listener/hooks/receive"}}}
                """, """
                globalThis.handlers = { async receive(event, api) {
                    const { bytes } = await api.assets.read(event.assets.item);
                    await api.db.put({ scope: 'preset', key: 'received', content: event.url + ' ' + bytes[0] });
                } };
                """);
            using var scripts = new PluginScripts();
            await using var engine = new Engine(Path.Combine(root, "workspace"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            foreach (var archive in new[] { publisher, listener })
            {
                engine.Dispatch(new() { ["op"] = "plugin_install", ["path"] = archive });
                await Until(engine, scripts, item => item.GetString("kind") == "plugin", timeout.Token);
            }
            engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "publisher" });
            var start = await Until(engine, scripts, item => item.GetString("kind") == "plugin_script" &&
                item.GetString("action") == "start" && JsonValues.ParseObject(item.GetString("payload")).GetString("handler") == "receive", timeout.Token);
            await Until(engine, scripts, item => item.GetString("kind") == "plugin_script" &&
                item.GetString("action") == "finish" && item.GetString("run") == start.GetString("run"), timeout.Token);

            var file = Path.Combine(engine.Workspace.Root, "plugins", "data", "test.rename.listener." + signer.GetCertHashString(HashAlgorithmName.SHA256),
                "preset", engine.Workspace.PresetId + ".sqlite");
            using var db = new MemoryDb(file);
            Assert.AreEqual("minichat://actions/test.rename.publisher/events/ready 65", db.Get("received"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void PreviousBuiltInAddressesResolveWithoutChangingSignedPackageNames()
    {
        Assert.AreEqual(PluginActions.Parse("minirp://actions/minirp.app/hooks/language"),
            PluginActions.Parse("minichat://actions/minichat.app/hooks/language"));
        Assert.AreEqual("example.minichat.stream", PluginActions.Parse("minirp://actions/example.minichat.stream/hooks/send").Package);
        Assert.IsTrue(PluginUrls.IsLegacyThumbnail("minirp://assets/minirp.legacy/attachment-thumbnail"));
        Assert.IsTrue(PluginUrls.IsLegacyThumbnail("minichat://assets/minichat.legacy/attachment-thumbnail"));
        Assert.IsFalse(PluginUrls.IsLegacyThumbnail("https://assets/minichat.legacy/attachment-thumbnail"));
    }

    private static string Pack(string root, X509Certificate2 signer, string id, string manifest, string source)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(directory, "index.js"), source);
        if (id == "publisher") File.WriteAllText(Path.Combine(directory, "item.txt"), "A");
        var archive = directory + ".plugin";
        PluginArchive.Pack(directory, archive, signer);
        return archive;
    }

    private static async Task<JsonObject> Until(Engine engine, PluginScripts scripts, Func<JsonObject, bool> condition, CancellationToken ct)
    {
        while (true)
        {
            JsonObject? found = null;
            foreach (var item in engine.Poll().OfType<JsonObject>())
            {
                switch (item.GetString("kind"))
                {
                    case "plugin_script":
                        var result = scripts.Step(item, ct, engine.Port.TakeBinary, engine.Port.WriteBinary);
                        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = item.GetString("id"), ["result"] = result });
                        break;
                    case "plugin_isolate_close": scripts.Close(item.GetString("isolate")); break;
                    case "plugin_error":
                    case "error": Assert.Fail(item.GetString("message")); break;
                }
                if (condition(item)) found = item;
            }
            if (found is not null) return found;
            await Task.Delay(8, ct);
        }
    }
}
