using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MiniRP.Cli;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class PluginLifecycleTests
{
    [TestMethod]
    public async Task CancellingARealV8PullAndTimingOutCleanupRebuildOnlyTheAffectedIsolate()
    {
        await using var fixture = new Fixture();
        await fixture.Install(fixture.Package("first", """
            let counter = 0;
            globalThis.handlers = {
              count() { return Uint8Array.of(++counter); },
              busy() {
                return new ReadableStream({type:"bytes", pull(controller) {
                  if (counter > 0) for (;;) {}
                  controller.enqueue(Uint8Array.of(++counter));
                }});
              },
              cleanup() {
                return new ReadableStream({type:"bytes", pull(controller) {
                  controller.enqueue(Uint8Array.of(++counter));
                }, cancel() { for (;;) {} }});
              }
            };
            """, assets: ["count", "busy", "cleanup"]));
        await fixture.Install(fixture.Package("second", """
            let count = 0;
            globalThis.handlers = { count() { return Uint8Array.of(++count); } };
            """, assets: ["count"]));
        Assert.AreEqual((byte)1, await fixture.Read("second", "count"));

        var busy = await fixture.Open("first", "busy");
        Assert.AreEqual((byte)1, await fixture.Chunk(busy));
        var start = await fixture.Next(item => IsScript("start")(item) &&
            JsonValues.ParseObject(item.GetString("payload")).GetString("handler") == "busy");
        bool BusyPull(JsonObject item) => IsScript("pull")(item) && item.GetString("run") == start.GetString("run");
        await fixture.Next(BusyPull);
        var interrupted = fixture.Pull(busy);
        var step = await fixture.Next(BusyPull);
        var close = fixture.Close(busy);
        await fixture.Next(item => item.GetString("kind") == "cancel_io" && item.GetString("id") == step.GetString("id"));
        Assert.AreEqual("error", (await interrupted).GetString("kind"));
        Assert.AreEqual("asset_result", (await close).GetString("kind"));

        var badCleanup = await fixture.Open("first", "cleanup");
        Assert.AreEqual((byte)1, await fixture.Chunk(badCleanup), "A cancelled V8 step retires its isolate.");
        var cleanup = fixture.Close(badCleanup);
        var finish = await fixture.Next(item => IsScript("finish")(item) && item.GetBoolean("abort"));
        Assert.AreNotEqual(step.GetString("isolate"), finish.GetString("isolate"));
        Assert.AreEqual((byte)2, await fixture.Read("second", "count"));
        await fixture.Next(item => item.GetString("kind") == "plugin_isolate_close" && item.GetString("isolate") == finish.GetString("isolate"));
        Assert.AreEqual("asset_result", (await cleanup).GetString("kind"));
        Assert.AreEqual((byte)1, await fixture.Read("first", "count"));
        Assert.AreEqual((byte)2, await fixture.Read("first", "count"), "Normal cleanup keeps the replacement isolate alive.");
    }

    [TestMethod]
    public async Task UpdatingRejectsAnAlreadyQueuedWorkspaceWriteAndRevokesOpenAssets()
    {
        var releasePicker = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture { Picker = (_, ct) => releasePicker.Task.WaitAsync(ct) };
        var engine = fixture.Engine;
        var preset = engine.Workspace.PresetId;
        engine.Dispatch(new() { ["op"] = "system_message", ["text"] = "fixture" });
        var loaded = await fixture.Next(item => item.GetString("kind") == "loaded");
        var chat = loaded.GetObject("chat").GetString("id");
        await fixture.Install(fixture.Package("update", """
            globalThis.handlers = { async run(_, api) {
              await api.picker.file();
              await api.harness.renameChat({name:"Retired plugin"});
            } };
            """, hooks: """{"plugin.command":"run"}""", permissions: ["picker", "harness.write"], file: "old"));
        await fixture.Call(new() { ["op"] = "plugin_scope", ["id"] = "update",
            ["scope"] = new JsonObject { ["mode"] = "include", ["presets"] = new JsonArray(preset) } });
        var oldAsset = await fixture.Open("update", "file");
        Assert.AreEqual((byte)'o', await fixture.Chunk(oldAsset, oneByte: false));
        engine.Dispatch(new() { ["op"] = "plugin_command", ["id"] = "update" });
        await fixture.Next(item => item.GetString("kind") == "plugin_picker");
        var oldRun = (await fixture.Next(IsScript("start"))).GetString("run");

        engine.Dispatch(new() { ["op"] = "configure", ["provider"] = new JsonObject
            { ["url"] = "http://fixture.invalid/v1", ["model"] = "fixture" } });
        engine.Dispatch(new() { ["op"] = "message", ["chat"] = chat, ["preset"] = preset, ["text"] = "Hold the workspace worker" });
        var request = await fixture.Next(item => item.GetString("kind") == "http_request");
        var update = fixture.Install(fixture.Package("update", """
            globalThis.handlers = { async ready(_, api) {
              await api.harness.renameChat({name:"Current plugin"});
            } };
            """, hooks: """{"plugin.enabled":"ready"}""", permissions: ["harness.write"], file: "new"));
        releasePicker.SetResult(new());
        bool ResumesOldRun(JsonObject item) => IsScript("resume")(item) && item.GetString("run") == oldRun;
        await fixture.Next(ResumesOldRun);
        var queued = (await fixture.Next(ResumesOldRun)).GetArray("replies").Single();
        Assert.IsTrue(queued.GetObject("result").GetBoolean("queued"), queued?.ToJsonString());
        engine.Dispatch(new() { ["op"] = "io_reply", ["id"] = request.GetString("id"), ["done"] = true,
            ["data"] = "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n" });
        await update;
        await fixture.Next(item => item.GetString("kind") == "error" &&
            item.GetString("message").Contains("earlier plugin installation", StringComparison.Ordinal));
        await fixture.Next(item => item.GetString("kind") == "title" && item.GetString("name") == "Current plugin");
        Assert.AreEqual("error", (await fixture.Pull(oldAsset)).GetString("kind"));
        var fresh = await fixture.Open("update", "file");
        Assert.AreEqual((byte)'n', await fixture.Chunk(fresh, oneByte: false));
    }

    [TestMethod]
    public async Task FullHookQueueRejectsStateWriteBeforeCommitAndRetryDeliversTheRevision()
    {
        var releasePicker = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture
        {
            Picker = (item, ct) => item.GetObject("arguments").GetBoolean("hold")
                ? releasePicker.Task.WaitAsync(ct) : Task.FromResult(new JsonObject()),
        };
        await fixture.Install(fixture.Package("queue", """
            globalThis.handlers = {
              command(event, api) {
                return event.hold || event.last ? api.picker.file({hold:event.hold === true}) : null;
              },
              async write(_, api) {
                const result = await api.state.set({key:"counter",expected_revision:0,value:{value:1}});
                return Uint8Array.of(result.revision);
              },
              async read(_, api) { return Uint8Array.of((await api.state.get({key:"counter"})).revision); },
              changed() {}
            };
            """, hooks: """{"plugin.command":"command"}""", permissions: ["picker"], assets: ["read", "write"],
            state: """
            [{"key":"counter","scope":"preset","description":"Counter","initial":{"value":0},
              "onChange":"minirp://actions/test.lifecycle.queue/hooks/changed"}]
            """));
        void Command(JsonObject args) => fixture.Engine.Dispatch(new() { ["op"] = "plugin_command",
            ["id"] = "queue", ["arguments"] = args });
        Command(new() { ["hold"] = true });
        await fixture.Next(item => item.GetString("kind") == "plugin_picker");
        for (var index = 0; index < 128; index++) Command(new() { ["last"] = index == 127 });
        await fixture.Call(new() { ["op"] = "plugin_settings" });
        var writer = await fixture.Open("queue", "write");
        var rejected = await fixture.Pull(writer);
        StringAssert.Contains(rejected.GetString("message"), "state was not changed");
        Assert.AreEqual((byte)0, await fixture.Read("queue", "read"));

        releasePicker.SetResult(new());
        await fixture.Next(item => item.GetString("kind") == "plugin_picker" && !item.GetObject("arguments").GetBoolean("hold"));
        Assert.AreEqual((byte)1, await fixture.Read("queue", "write"));
        await fixture.Next(item => IsScript("start")(item) &&
            JsonValues.ParseObject(item.GetString("payload")).GetObject("event").GetInt32("revision") == 1);
    }

    [TestMethod]
    public async Task ExpiredAbandonedAssetsReleaseTheirSlots()
    {
        var clock = new AssetClock();
        await using var fixture = new Fixture(clock);
        await fixture.Install(fixture.Package("expiry", "", file: "contents"));
        const string url = "minirp://assets/test.lifecycle.expiry/file";
        var handles = new List<string>();
        for (var index = 0; index < 16; index++) handles.Add(await fixture.Open("expiry", "file"));
        var rejected = await fixture.Call(new() { ["op"] = "asset_open", ["url"] = url });
        StringAssert.Contains(rejected.GetString("message"), "Too many open asset streams");
        clock.Expire();

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var opened = await fixture.Call(new() { ["op"] = "asset_open", ["url"] = url });
            if (opened.GetString("kind") == "asset_result") break;
            Assert.IsLessThan(TimeSpan.FromSeconds(5), deadline.Elapsed, opened.GetString("message"));
        }
        foreach (var handle in handles)
            Assert.AreEqual("error", (await fixture.Pull(handle)).GetString("kind"));
    }

    private sealed class AssetClock : TimeProvider
    {
        private readonly ConcurrentQueue<Timer> timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, Timeout.InfiniteTimeSpan, period);
            timers.Enqueue(timer);
            return timer;
        }

        public void Expire()
        {
            while (timers.TryDequeue(out var timer)) timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private static Func<JsonObject, bool> IsScript(string action) =>
        item => item.GetString("kind") == "plugin_script" && item.GetString("action") == action;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "minirp-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource stopping = new();
        private readonly Channel<JsonObject> events = Channel.CreateUnbounded<JsonObject>();
        private readonly List<JsonObject> backlog = [];
        private readonly BrowserHost browser;
        private readonly PlatformHost platform;
        private readonly Task pump;
        private readonly X509Certificate2 certificate;
        public Engine Engine { get; }
        public Func<JsonObject, CancellationToken, Task<JsonObject>>? Picker { init => platform.Picker = value; }

        public Fixture(TimeProvider? clock = null)
        {
            Engine = new(Path.Combine(root, "workspace"), seedDefaultPreset: true, clock ?? TimeProvider.System);
            browser = new(root);
            platform = new(Engine.Dispatch, Engine.Port.TakeBinary, Engine.Port.WriteBinary, browser, root);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Plugin lifecycle test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            pump = Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    foreach (var item in Engine.Poll().OfType<JsonObject>())
                    {
                        if (item.GetString("kind") != "http_request") platform.Handle(item);
                        events.Writer.TryWrite(item);
                    }
                    await Task.Delay(5, stopping.Token);
                }
            });
        }

        public string Package(string id, string code, string hooks = "{}", string[]? permissions = null,
            string[]? assets = null, string? file = null, string state = "[]")
        {
            var package = "test.lifecycle." + id;
            string Handler(string name) => $"minirp://actions/{package}/hooks/{name}";
            var handlers = new JsonObject();
            var hookMap = JsonValues.ParseObject(hooks);
            foreach (var hook in hookMap.ToArray())
            {
                var name = JsonValues.GetText(hook.Value);
                handlers[name] = name;
                hookMap[hook.Key] = Handler(name);
            }
            var declarations = new JsonArray();
            foreach (var name in assets ?? [])
            {
                handlers[name] = name;
                declarations.Add(new JsonObject { ["id"] = name, ["type"] = "dynamic", ["mimeType"] = "application/octet-stream",
                    ["accessibility"] = "public", ["handler"] = Handler(name) });
            }
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (file is not null)
            {
                File.WriteAllText(Path.Combine(directory, "file.txt"), file);
                declarations.Add(new JsonObject { ["id"] = "file", ["type"] = "file", ["mimeType"] = "text/plain",
                    ["accessibility"] = "public", ["path"] = "file.txt" });
            }
            var entries = JsonNode.Parse(state)!.AsArray();
            if (entries.Count > 0) handlers["changed"] = "changed";
            var manifest = new JsonObject { ["id"] = id, ["packageName"] = package, ["name"] = id, ["apiVersion"] = 2,
                ["entry"] = "index.js", ["permissions"] = new JsonArray((permissions ?? []).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["actions"] = new JsonObject { ["handlers"] = handlers }, ["hooks"] = hookMap, ["assets"] = declarations, ["stateEntries"] = entries };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonValues.Serialize(manifest));
            File.WriteAllText(Path.Combine(directory, "index.js"), code);
            var archive = directory + ".plugin";
            PluginArchive.Pack(directory, archive, certificate);
            return archive;
        }

        public async Task Install(string path)
        {
            var reply = await Call(new() { ["op"] = "plugin_install", ["path"] = path });
            Assert.AreEqual("plugin", reply.GetString("kind"), reply.GetString("message"));
        }

        public async Task<string> Open(string package, string asset)
        {
            var result = await Call(new() { ["op"] = "asset_open", ["url"] = $"minirp://assets/test.lifecycle.{package}/{asset}" });
            Assert.AreEqual("asset_result", result.GetString("kind"), result.GetString("message"));
            return result.GetObject("result").GetString("handle");
        }

        public Task<JsonObject> Pull(string handle) => Call(new() { ["op"] = "asset_pull", ["handle"] = handle });
        public Task<JsonObject> Close(string handle) => Call(new() { ["op"] = "asset_close", ["handle"] = handle });

        public async Task<byte> Chunk(string handle, bool oneByte = true)
        {
            var reply = await Pull(handle);
            Assert.AreEqual("asset_result", reply.GetString("kind"), reply.GetString("message"));
            var chunk = Engine.Port.TakeBinary(reply.GetObject("result").GetString("transfer"))!;
            return oneByte ? chunk.Single() : chunk[0];
        }

        public async Task<byte> Read(string package, string asset)
        {
            var handle = await Open(package, asset);
            var result = await Chunk(handle);
            Assert.IsTrue((await Pull(handle)).GetObject("result").GetBoolean("done"));
            return result;
        }

        public Task<JsonObject> Call(JsonObject request)
        {
            var id = Guid.NewGuid().ToString("N");
            request["request"] = id;
            Engine.Dispatch(request);
            return Next(item => item.GetString("request") == id);
        }

        public async Task<JsonObject> Next(Func<JsonObject, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // Each waiter sees events that arrived before or during the asynchronous action.
            while (true)
            {
                lock (backlog)
                {
                    var index = backlog.FindIndex(item => match(item));
                    if (index >= 0)
                    {
                        var item = backlog[index];
                        backlog.RemoveAt(index);
                        return item;
                    }
                    while (events.Reader.TryRead(out var item)) backlog.Add(item);
                }
                try { await Task.Delay(5, timeout.Token); }
                catch (OperationCanceledException)
                {
                    lock (backlog)
                        throw new TimeoutException("Expected host event was absent. Pending events:\n" +
                            string.Join('\n', backlog.TakeLast(15).Select(item =>
                            {
                                var diagnostic = JsonValues.Serialize(item);
                                return diagnostic[..Math.Min(500, diagnostic.Length)];
                            })));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            try { await pump; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            await platform.DisposeAsync();
            await Engine.DisposeAsync();
            await browser.DisposeAsync();
            stopping.Dispose();
            certificate.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
