using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class PluginAgentCoordinator(string root, HostPort port, PluginRuntime runtime,
    Func<Provider> provider, Action<JsonObject> emit, Action pluginsChanged, CancellationToken lifetime)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Preset, string Id), Worker> running = new();

    private sealed record Worker(CancellationTokenSource Cancellation, TurnInbox Inbox)
    {
        public Task Task { get; set; } = Task.CompletedTask;
    }

    public async Task<JsonNode?> Execute(JsonObject context, JsonObject args, CancellationToken ct)
    {
        var role = context.GetString("agent");
        var preset = WorkspaceIds.Require(context.GetString("preset"));
        if (role is not ("preset" or "plugin")) throw new UnauthorizedAccessException("This skill belongs to the preset development agents.");
        var action = args.GetString("action");
        if (action == "plugin_docs") return PluginDevelopmentDocs.Read(args);
        if (role == "plugin")
        {
            var id = Workspace.Safe(context.GetString("session"));
            lock (gate)
                if (!running.ContainsKey((preset, id))) throw new InvalidOperationException("This PluginAgent is no longer running.");
            return await Project(preset, id).Execute(action, args, ct);
        }

        switch (action)
        {
            case "plugin_spawn":
                lock (gate)
                {
                    var name = args.GetString("name").Trim();
                    if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
                        throw new ArgumentException("Provide a short project name.");
                    var id = JsonValues.NewId();
                    var task = Instruction(args);
                    Project(preset, id).Create(name);
                    var state = new JsonObject { ["id"] = id, ["name"] = name, ["status"] = "idle" };
                    Start(preset, id, state, task, ct);
                    return state;
                }
            case "plugin_list":
                lock (gate)
                {
                    var folder = PresetPath(preset);
                    return new JsonArray((Directory.Exists(folder) ? Directory.EnumerateDirectories(folder) : [])
                        .Where(directory => File.Exists(Path.Combine(directory, "worker.json")))
                        .Select(directory => (JsonNode?)Read(preset, Path.GetFileName(directory))).ToArray());
                }
            case "plugin_send":
                var target = Workspace.Safe(args.GetString("id"));
                var instruction = Instruction(args);
                lock (gate)
                {
                    var state = Read(preset, target);
                    if (state.GetBoolean("archived")) throw new InvalidOperationException("Resume this PluginAgent before sending a task.");
                    if (running.TryGetValue((preset, target), out var current))
                    {
                        if (!current.Inbox.TryPost("", "plugin", new() { ["text"] = instruction, ["role"] = "user" }))
                            throw new InvalidOperationException("The PluginAgent is finishing its turn. Wait for the result and send again.");
                    }
                    else Start(preset, target, state, instruction, ct);
                    return state;
                }
            case "plugin_wait":
                var ids = args.GetArray("ids").Select(item => Workspace.Safe(JsonValues.GetText(item))).Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Length == 0) throw new ArgumentException("Provide one or more PluginAgent IDs.");
                Task[] tasks;
                lock (gate)
                {
                    foreach (var id in ids) _ = Read(preset, id);
                    tasks = ids.Select(id => running.GetValueOrDefault((preset, id))?.Task).OfType<Task>().ToArray();
                }
                if (tasks.Length > 0)
                {
                    var finished = Task.WhenAll(tasks);
                    await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(args.GetInt32("seconds", 15), 1, 30)), ct));
                    ct.ThrowIfCancellationRequested();
                }
                lock (gate) return new JsonArray(ids.Select(id => (JsonNode?)Read(preset, id)).ToArray());
            case "plugin_archive":
            case "plugin_resume":
                var archive = action == "plugin_archive";
                var workerId = Workspace.Safe(args.GetString("id"));
                Worker? worker;
                lock (gate)
                {
                    var state = Read(preset, workerId);
                    state["archived"] = archive;
                    if (archive) state["status"] = "archived";
                    else if (state.GetString("status") == "archived") state["status"] = "idle";
                    Write(preset, workerId, state);
                    worker = running.GetValueOrDefault((preset, workerId));
                    if (archive) worker?.Cancellation.Cancel();
                }
                if (archive && worker is not null) await worker.Task.WaitAsync(ct);
                lock (gate) return Read(preset, workerId);
            default:
                throw new UnauthorizedAccessException("PresetAgent can coordinate PluginAgents and read the development reference.");
        }
    }

    private void Start(string preset, string id, JsonObject state, string instruction, CancellationToken parent)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, parent);
        var worker = new Worker(cancellation, new TurnInbox("", "plugin", _ => { }));
        state["status"] = "running";
        state["task"] = instruction;
        state.Remove("result");
        Write(preset, id, state);
        running.Add((preset, id), worker);
        var selected = provider();
        worker.Task = Task.Run(() => Run(preset, id, state.GetString("name"), instruction, selected, worker));
    }

    private async Task Run(string preset, string id, string name, string instruction, Provider model, Worker worker)
    {
        var status = "finished";
        string result;
        try
        {
            var context = new JsonObject { ["agent"] = "plugin", ["preset"] = preset, ["session"] = id };
            using var session = new AgentSession("PluginAgent", Path.Combine(WorkerPath(preset, id), "session.sqlite"),
                new ModelClient(port, () => model), item =>
                {
                    item["preset"] = preset;
                    item["plugin_agent"] = id;
                    if (item.GetString("block") is { Length: > 0 } block) item["block"] = id + "-" + block;
                    emit(item);
                });
            session.AdditionalSkills = () => runtime.Skills("plugin", context, builtInOnly: true);
            session.Inbox = worker.Inbox;
            var reply = await new PluginAgent(name, session).Run(instruction, worker.Cancellation.Token);
            result = reply.GetString("content");
        }
        catch (OperationCanceledException) { status = "cancelled"; result = "PluginAgent was cancelled."; }
        catch (Exception error) { status = "failed"; result = error.Message; }
        finally { worker.Inbox.Close(); }
        lock (gate)
        {
            running.Remove((preset, id));
            worker.Cancellation.Dispose();
            var state = Read(preset, id);
            state["status"] = state.GetBoolean("archived") ? "archived" : status;
            state["result"] = result;
            Write(preset, id, state);
            emit(new() { ["kind"] = "plugin_agent_status", ["agent"] = "PluginAgent", ["preset"] = preset, ["plugin_agent"] = id, ["status"] = state.GetString("status") });
        }
    }

    public async Task Drain()
    {
        Task[] tasks;
        lock (gate) tasks = running.Values.Select(worker => worker.Task).ToArray();
        await Task.WhenAll(tasks);
    }

    public async Task StopPreset(string preset)
    {
        Task[] tasks;
        lock (gate)
        {
            var workers = running.Where(item => item.Key.Preset == preset).Select(item => item.Value).ToArray();
            foreach (var worker in workers) worker.Cancellation.Cancel();
            tasks = workers.Select(worker => worker.Task).ToArray();
        }
        await Task.WhenAll(tasks);
    }

    private static string Instruction(JsonObject args) => args.GetString("message").Trim() is { Length: > 0 and <= 32000 } value
        ? value : throw new ArgumentException("Provide a task or follow-up message up to 32,000 characters.");

    private string PresetPath(string preset) => Path.Combine(root, "preset_sessions", preset, "plugin_agents");
    private string WorkerPath(string preset, string id) => Path.Combine(PresetPath(preset), id);
    private PluginDevelopmentProject Project(string preset, string id) => new(WorkerPath(preset, id), id, preset, runtime, pluginsChanged);
    private void Write(string preset, string id, JsonObject state) => AtomicFile.Write(Path.Combine(WorkerPath(preset, id), "worker.json"), JsonValues.Serialize(state));
    private JsonObject Read(string preset, string id)
    {
        var file = Path.Combine(WorkerPath(preset, id), "worker.json");
        if (!File.Exists(file)) throw new ArgumentException("There is no PluginAgent with that ID in this preset.");
        var state = JsonValues.ParseObject(File.ReadAllText(file));
        if (state.GetString("status") == "running" && !running.ContainsKey((preset, id))) state["status"] = "interrupted";
        return state;
    }
}
