using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MiniRP.Cli;

namespace MiniRP.Core.Tests;

[TestClass]
public sealed class ModelRecoveryTests
{
    [TestMethod]
    public async Task ModelRetriesWireFailuresAndReplaysOnlyTheUnfinishedResponse()
    {
        var requests = new List<JsonObject>();
        var events = new List<JsonObject>();
        var cancelled = new List<string>();
        var executions = 0;
        HostPort? port = null;
        port = new(item =>
        {
            if (item.GetString("kind") == "cancel_io") cancelled.Add(item.GetString("id"));
            if (item.GetString("kind") != "http_request") return;
            requests.Add(item);
            switch (requests.Count)
            {
                case 1:
                    port!.Reply(new() { ["id"] = item.GetString("id"), ["status"] = 503 });
                    break;
                case 2:
                    Frame(port!, item, Tool("committed"));
                    break;
                case 3:
                    var partial = Tool("unfinished");
                    partial["content"] = "Interrupted draft.";
                    Frame(port!, item, partial, false);
                    Disconnect(port!, item);
                    break;
                case 4:
                    Frame(port!, item, new() { ["content"] = "Recovered answer." });
                    break;
                default:
                    Assert.Fail("The runtime sent too many model requests.");
                    break;
            }
        });
        using var session = TestAgentSession.Open("chat", ":memory:", Model(port), events.Add,
            () => [Mutation(() => ++executions)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var answer = await session.Run("Fixture", "Start.", timeout.Token);

        Assert.AreEqual("Recovered answer.", answer.GetString("content"));
        Assert.AreEqual(1, executions);
        Assert.HasCount(4, requests.Select(item => item.GetString("id")).Distinct());
        CollectionAssert.AreEquivalent(requests.Select(item => item.GetString("id")).ToArray(), cancelled.ToArray());
        Assert.AreEqual(requests[0].GetString("body"), requests[1].GetString("body"));
        Assert.AreEqual(requests[2].GetString("body"), requests[3].GetString("body"));
        Assert.IsTrue(Messages(requests[3]).Any(item => item.GetString("tool_call_id") == "committed"));
        Assert.IsFalse(session.History.Any(item => item.GetString("content") == "Interrupted draft."));
        var discarded = events.Single(item => item.GetString("kind") == "turn_end" && item.GetBoolean("retrying"));
        Assert.IsTrue(discarded.GetBoolean("interrupted"));
        Assert.IsFalse(events.Any(item => item.GetString("kind") == "tool_start" && item.GetString("block") == "unfinished"));
    }

    [TestMethod]
    public async Task InterruptedPluginSessionReopensWithCompletedToolHistory()
    {
        var directory = Directory.CreateTempSubdirectory("minirp-model-resume-");
        var database = Path.Combine(directory.FullName, "worker.sqlite");
        var executions = 0;
        var attempts = 0;
        HostPort? port = null;
        port = new(item =>
        {
            if (item.GetString("kind") != "http_request") return;
            switch (++attempts)
            {
                case 1:
                    Frame(port!, item, Tool("committed"));
                    break;
                case 2 or 3 or 4:
                    Disconnect(port!, item);
                    break;
                case 5:
                    var history = Messages(item);
                    Assert.IsTrue(history.Any(message => message.GetString("content") == "First task."));
                    Assert.IsTrue(history.Any(message => message.GetString("tool_call_id") == "committed"));
                    Assert.IsTrue(history.Any(message => message.GetString("content") == "Continue this task."));
                    Frame(port!, item, new() { ["content"] = "Resumed." });
                    break;
                default:
                    Assert.Fail("Unexpected response retry.");
                    break;
            }
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using (var first = TestAgentSession.Open("plugin", database, Model(port), _ => { }, () => [Mutation(() => ++executions)]))
            {
                var interrupted = await Assert.ThrowsExactlyAsync<ModelEvents.Interrupted>(
                    () => first.Run("Fixture", "First task.", timeout.Token));
                Assert.IsFalse(interrupted.ResponseStarted);
            }
            using (var resumed = TestAgentSession.Open("plugin", database, Model(port), _ => { }, () => [Mutation(() => ++executions)]))
                Assert.AreEqual("Resumed.", (await resumed.Run("Fixture", "Continue this task.", timeout.Token)).GetString("content"));
            Assert.AreEqual(1, executions);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task StreamingHttpReportsReadIdleAsTransportFailure()
    {
        var events = new List<JsonObject>();
        var port = new HostPort(events.Add);
        var request = new JsonObject { ["stream"] = true, ["read_idle_timeout_ms"] = 1000 };
        async Task Read()
        {
            await foreach (var _ in port.Http(request, CancellationToken.None)) { }
        }

        await Assert.ThrowsExactlyAsync<HostPort.HttpTransportError>(Read);
        var issued = events.Single(item => item.GetString("kind") == "http_request");
        Assert.AreEqual(issued.GetString("id"), events.Single(item => item.GetString("kind") == "cancel_io").GetString("id"));
    }

    [TestMethod]
    public async Task UnfinishedSseEventAtEofCannotDispatchItsTool()
    {
        var attempts = 0;
        var executions = 0;
        HostPort? port = null;
        port = new(item =>
        {
            if (item.GetString("kind") != "http_request") return;
            if (++attempts > 1)
            {
                Frame(port!, item, new() { ["content"] = "Recovered." });
                return;
            }
            var truncated = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = Tool("unfinished") }) };
            port!.Reply(new() { ["id"] = item.GetString("id"), ["done"] = true,
                ["data"] = "data: " + JsonValues.Serialize(truncated) + "\n" });
        });
        using var session = TestAgentSession.Open("chat", ":memory:", Model(port), _ => { },
            () => [Mutation(() => ++executions)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var answer = await session.Run("Fixture", "Start.", timeout.Token);
        Assert.AreEqual("Recovered.", answer.GetString("content"));
        Assert.AreEqual(0, executions);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task CancellingTheModelRequestDoesNotStartAReplay()
    {
        using var stop = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issued = 0;
        var port = new HostPort(item =>
        {
            if (item.GetString("kind") != "http_request") return;
            Interlocked.Increment(ref issued);
            requested.TrySetResult();
        });
        using var session = TestAgentSession.Open("chat", ":memory:", Model(port), _ => { });
        var run = session.Run("Fixture", "Start.", stop.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.AreEqual(1, issued);
    }

    [TestMethod]
    public async Task DesktopTransportMarksAnActualEarlySocketEofAsRetryable()
    {
        var directory = Directory.CreateTempSubdirectory("minirp-socket-drop-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            const string data = "data: {\"fixture\":true}\n\n";
            var destination = (IPEndPoint)listener.LocalEndpoint;
            var fixture = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                await using var connection = client.GetStream();
                using var reader = new StreamReader(connection, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
                var headers = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n" +
                    "Content-Length: " + (data.Length + 128) + "\r\n\r\n";
                await connection.WriteAsync(Encoding.ASCII.GetBytes(headers + data), token);
                client.Client.Shutdown(SocketShutdown.Send);
            }, token);
            var replies = Channel.CreateUnbounded<JsonObject>();
            await using var browser = new BrowserHost(directory.FullName);
            await using var platform = new PlatformHost(item => replies.Writer.TryWrite(item), _ => null, (_, _) => { },
                browser, directory.FullName);
            Assert.IsTrue(platform.Handle(new() { ["kind"] = "http_request", ["id"] = "fixture", ["stream"] = true,
                ["url"] = "http://127.0.0.1:" + destination.Port + "/stream" }));
            using var received = new MemoryStream();
            JsonObject failed;
            while (true)
            {
                var item = await replies.Reader.ReadAsync(token);
                if (item.GetString("error").Length > 0) { failed = item; break; }
                Assert.IsFalse(item.GetBoolean("done"));
                received.Write(Convert.FromBase64String(item.GetString("data_base64")));
            }
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes(data), received.ToArray());
            Assert.AreEqual("fixture", failed.GetString("id"));
            Assert.IsTrue(failed.GetBoolean("network_error"));
            Assert.IsFalse(failed.GetBoolean("done"));
            await fixture;
        }
        finally
        {
            listener.Stop();
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PluginWorkerUsesTheSameDraftAndRunHistoryAfterFreshInput()
    {
        var directory = Directory.CreateTempSubdirectory("minirp-worker-resume-");
        try
        {
            var root = directory.FullName;
            var workspace = new Workspace(root);
            var preset = workspace.InitialPreset().Id;
            workspace.UsePreset(preset);
            var presetDatabase = SessionFiles.PresetDatabase(SessionFiles.PresetDirectory(root, preset));
            using (var presetSeed = new MemoryDb(presetDatabase))
                Assert.IsTrue(presetSeed.HasEntriesTable());
            var requests = Channel.CreateUnbounded<JsonObject>();
            var statuses = Channel.CreateUnbounded<JsonObject>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var token = timeout.Token;
            var port = new HostPort(item =>
            {
                if (item.GetString("kind") == "http_request") requests.Writer.TryWrite(item);
            }, token);
            await using var runtime = new PluginRuntime(root, port, _ => { },
                (_, _, _, _, _) => Task.FromResult<JsonNode?>(null));
            runtime.Load();
            var model = Model(port);
            var factory = new AgentSession.Factory(port, runtime, Path.Combine(root, "attachments"), () => "", () => workspace);
            var pages = new PageSource(port, new BrowserAgent(port, _ => throw new AssertFailedException("Unexpected browser job.")), () => new());
            var coordinator = new PluginAgentCoordinator(root, factory, runtime, () => model.SelectedProvider, (_, _) => pages,
                () => { }, _ => { }, token);
            var context = new JsonObject { ["agent"] = "preset", ["preset"] = preset };
            void Trace(JsonObject item)
            {
                if (item.GetString("kind") == "plugin_agent_status") statuses.Writer.TryWrite(item);
            }
            Task<JsonNode?> Execute(JsonObject command) => coordinator.Execute(context, command, Trace, null, token);

            var created = await Execute(new() { ["action"] = "subagent_spawn", ["name"] = "Fixture worker", ["message"] = "Record the fixture." });
            var id = created.GetString("id");
            Assert.AreEqual("running", (await statuses.Reader.ReadAsync(token)).GetString("status"));
            var request = await requests.Reader.ReadAsync(token);
            Frame(port, request, Tool("persisted-write", "db_write", new() { ["key"] = "recovery-fixture", ["content"] = "value" }));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                request = await requests.Reader.ReadAsync(token);
                Assert.IsTrue(Messages(request).Any(message => message.GetString("tool_call_id") == "persisted-write"));
                Disconnect(port, request);
            }
            var interrupted = await statuses.Reader.ReadAsync(token);
            Assert.AreEqual("interrupted", interrupted.GetString("status"));
            Assert.AreEqual(id, interrupted.GetString("plugin_agent"));
            using (var owner = MemoryDb.ReadOnly(presetDatabase))
            {
                var report = owner.Get("_context/inbox/plugin-agent-" + interrupted.GetString("plugin_run"));
                StringAssert.Contains(report, "subagent_send using the same worker ID " + id);
            }
            var workers = (JsonArray)(await Execute(new() { ["action"] = "subagent_list" }))!;
            Assert.AreEqual("interrupted", workers.Single().GetString("status"));
            await Execute(new() { ["action"] = "subagent_send", ["id"] = id, ["message"] = "Describe the saved fixture." });
            var restarted = await statuses.Reader.ReadAsync(token);
            Assert.AreEqual(id, restarted.GetString("plugin_agent"));
            Assert.AreEqual("running", restarted.GetString("status"));
            request = await requests.Reader.ReadAsync(token);
            var messages = Messages(request);
            Assert.IsTrue(messages.Any(message => message.GetString("content") == "Describe the saved fixture."));
            Assert.AreEqual(1, messages.Count(message => message.GetString("tool_call_id") == "persisted-write"));
            Frame(port, request, new() { ["content"] = "The fixture is saved." });
            Assert.AreEqual("finished", (await statuses.Reader.ReadAsync(token)).GetString("status"));
            await coordinator.Drain();
            using var db = MemoryDb.ReadOnly(Path.Combine(SessionFiles.PluginDirectory(root, preset), id, "session.sqlite"));
            var entry = db.Call("db_read", new() { ["key"] = "recovery-fixture" });
            Assert.AreEqual("value", entry.GetString("content"));
            Assert.AreEqual(1, entry.GetInt32("revision"));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static AgentSkill Mutation(Func<int> execute) => new("fixture", "Run one fixture mutation.",
        new JsonArray(Tools.Define("mutate_fixture", "Commit the fixture mutation.")),
        (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["saved"] = execute() }), Required: true);

    private static JsonObject Tool(string id, string name = "mutate_fixture", JsonObject? arguments = null) => new()
    {
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["index"] = 0, ["id"] = id,
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments is null ? "{}" : JsonValues.Serialize(arguments) }
        })
    };

    private static ModelClient Model(HostPort port) => new(port,
        () => new("fixture", "Fixture", "openai", "http://fixture.invalid", "", "fixture"));

    private static JsonArray Messages(JsonObject request) => JsonValues.ParseObject(request.GetString("body")).GetArray("messages");

    private static void Frame(HostPort port, JsonObject request, JsonObject delta, bool done = true)
    {
        var frame = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = delta }) };
        port.Reply(new() { ["id"] = request.GetString("id"), ["done"] = done, ["data"] = "data: " + JsonValues.Serialize(frame) + "\n\n" });
    }

    private static void Disconnect(HostPort port, JsonObject request) => port.Reply(new()
    {
        ["id"] = request.GetString("id"), ["error"] = "Fixture connection dropped.", ["network_error"] = true
    });
}
