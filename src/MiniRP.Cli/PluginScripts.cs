using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal sealed class PluginScripts : IDisposable
{
    private readonly ConcurrentDictionary<string, Runner> isolates = new();

    public Func<CancellationToken, JsonObject> Prepare(JsonObject request, Func<string, byte[]?>? read = null,
        Action<string, byte[]>? write = null)
    {
        var id = request.GetString("isolate");
        var isolate = request.GetString("action") == "load"
            ? isolates.GetOrAdd(id, _ => new Runner())
            : isolates.GetValueOrDefault(id);
        return token =>
        {
            if (isolate is null) throw new InvalidOperationException("Plugin isolate is closed.");
            try { return isolate.Step(request, token, read, write); }
            catch
            {
                if (isolate.IsClosed && isolates.TryRemove(new KeyValuePair<string, Runner>(id, isolate))) isolate.Dispose();
                throw;
            }
        };
    }

    public JsonObject Step(JsonObject request, CancellationToken token, Func<string, byte[]?>? read = null,
        Action<string, byte[]>? write = null) => Prepare(request, read, write)(token);

    public IDisposable? Detach(string id)
    {
        if (!isolates.TryRemove(id, out var runner)) return null;
        runner.Retire();
        return runner;
    }

    public void Close(string id) => Detach(id)?.Dispose();

    public void Dispose()
    {
        foreach (var id in isolates.Keys) Close(id);
    }

    private sealed class Runner : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource retiring = new();
        private V8ScriptEngine? engine;
        private volatile bool retired;
        public bool IsClosed { get; private set; }

        public void Retire() => retired = true;

        public JsonObject Step(JsonObject args, CancellationToken token, Func<string, byte[]?>? read, Action<string, byte[]>? write)
        {
            lock (gate)
            {
                if (IsClosed || retired) throw new InvalidOperationException("Plugin isolate is closed.");
                token.ThrowIfCancellationRequested();
                try
                {
                    var loaded = engine is not null;
                    var flags = V8ScriptEngineFlags.DisableGlobalMembers;
                    if (args["modules"] is JsonObject)
                        flags |= V8ScriptEngineFlags.EnableTaskPromiseConversion;
                    var runtime = engine ??= new(flags)
                    {
                        MaxRuntimeHeapSize = new UIntPtr(48 * 1024 * 1024),
                        RuntimeHeapSizeSampleInterval = TimeSpan.FromMilliseconds(50),
                        MaxRuntimeStackUsage = new UIntPtr(512 * 1024),
                    };
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, retiring.Token);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    using var interrupt = cancellation.Token.Register(runtime.Interrupt);
                    using var watchdog = timeout.Token.Register(runtime.Interrupt);
                    cancellation.Token.ThrowIfCancellationRequested();
                    var result = Execute(runtime, loaded, args, read, write, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    timeout.Token.ThrowIfCancellationRequested();
                    return result;
                }
                catch { IsClosed = true; throw; }
            }
        }

        private static JsonObject Execute(V8ScriptEngine engine, bool loaded, JsonObject args, Func<string, byte[]?>? read,
            Action<string, byte[]>? write, CancellationToken token)
        {
            var action = args.GetString("action");
            if (action == "load")
            {
                if (!loaded)
                {
                    engine.Execute(args.GetString("bootstrap"));
                    if (args["modules"] is JsonObject modules) PluginModules.Load(engine, args.GetString("entry"), modules);
                    else engine.Execute("plugin.js", args.GetString("source"));
                }
                return new();
            }
            if (!loaded) throw new InvalidOperationException("Plugin isolate has not loaded.");
            var run = Quoted(args.GetString("run"));
            if (action == "finish")
            {
                engine.Execute("__minirp.finish(" + run + "," + (args.GetBoolean("abort") ? "true" : "false") + ")");
                return new();
            }
            if (action == "start")
            {
                engine.Execute("__minirp.start(" + run + "," + Quoted(args.GetString("payload")) + ")");
            }
            else if (action == "pull") engine.Execute("__minirp.pull(" + run + ")");
            else
            {
                foreach (var reply in args.GetArray("replies"))
                {
                    token.ThrowIfCancellationRequested();
                    var json = JsonValues.Serialize(reply);
                    var binary = reply.GetString("transfer");
                    if (binary.Length == 0) engine.Execute("__minirp.reply(" + run + "," + Quoted(json) + ")");
                    else
                    {
                        var bytes = read?.Invoke(binary) ?? throw new IOException("Missing incoming binary chunk.");
                        token.ThrowIfCancellationRequested();
                        var array = (ITypedArray<byte>)engine.Evaluate($"new Uint8Array({bytes.Length})");
                        array.WriteBytes(bytes, 0, (ulong)bytes.Length, 0);
                        var bridge = (IScriptObject)engine.Evaluate("__minirp");
                        bridge.InvokeMethod("reply", args.GetString("run"), json, array);
                    }
                }
            }
            var result = JsonValues.ParseObject((string)engine.Evaluate("__minirp.poll(" + run + ")"));
            if (result.GetBoolean("binary"))
            {
                var data = (ITypedArray<byte>)engine.Evaluate("__minirp.takeBytes(" + run + ")");
                if (data.Length is 0 or > 65536 || write is null) throw new IOException("Invalid outgoing asset chunk.");
                write(args.GetString("upload"), data.GetBytes());
            }
            return result;
        }

        private static string Quoted(string text) => JsonValues.Serialize(JsonValue.Create(text)).Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

        public void Dispose()
        {
            retiring.Cancel();
            lock (gate)
            {
                IsClosed = true;
                engine?.Dispose();
                retiring.Dispose();
            }
        }
    }
}
