using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Minichat.Protocol;

namespace Minichat.Cli;

internal sealed class PluginScripts : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Runner>> isolates = new();

    public JsonObject Step(JsonObject request, CancellationToken token, Func<string, byte[]?>? read = null, Action<string, byte[]>? write = null)
    {
        var id = request.GetString("isolate");
        var isolate = request.GetString("action") == "load"
            ? isolates.GetOrAdd(id, _ => new(() => new Runner()))
            : isolates.GetValueOrDefault(id) ?? throw new InvalidOperationException("Plugin isolate is closed.");
        try { return isolate.Value.Step(request, token, read, write); }
        catch { Close(id); throw; }
    }

    public void Close(string id)
    {
        if (isolates.TryRemove(id, out var runner) && runner.IsValueCreated) runner.Value.Dispose();
    }

    public void Finish(string isolate, string run)
    {
        if (isolates.TryGetValue(isolate, out var entry) && entry.IsValueCreated) entry.Value.Finish(run);
    }

    public void Dispose()
    {
        foreach (var id in isolates.Keys) Close(id);
    }

    private sealed class Runner : IDisposable
    {
        private readonly V8ScriptEngine engine = new(V8ScriptEngineFlags.DisableGlobalMembers);
        private readonly object gate = new();
        private bool closed;
        private bool initialized;

        public Runner()
        {
            engine.MaxRuntimeHeapSize = new UIntPtr(48 * 1024 * 1024);
            engine.RuntimeHeapSizeSampleInterval = TimeSpan.FromMilliseconds(50);
            engine.MaxRuntimeStackUsage = new UIntPtr(512 * 1024);
        }

        public JsonObject Step(JsonObject args, CancellationToken token, Func<string, byte[]?>? read, Action<string, byte[]>? write)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closed, this);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var interrupt = deadline.Token.Register(engine.Interrupt);
                deadline.Token.ThrowIfCancellationRequested();
                if (args.GetString("action") == "load")
                {
                    if (!initialized)
                    {
                        engine.Execute(args.GetString("bootstrap"));
                        engine.Execute("plugin.js", args.GetString("source"));
                        initialized = true;
                    }
                    return new();
                }
                var run = Quoted(args.GetString("run"));
                if (args.GetString("action") == "start")
                {
                    engine.Execute("__minichat.start(" + run + "," + Quoted(args.GetString("payload")) + ")");
                }
                else if (args.GetString("action") is "pull" or "cancel")
                {
                    var action = args.GetString("action");
                    engine.Execute("__minichat." + action + "(" + run + ")");
                }
                else
                {
                    foreach (var reply in args.GetArray("replies"))
                    {
                        var json = JsonValues.Serialize(reply);
                        var binary = reply.GetString("transfer");
                        if (binary.Length == 0) engine.Execute("__minichat.reply(" + run + "," + Quoted(json) + ")");
                        else
                        {
                            var bytes = read?.Invoke(binary) ?? throw new IOException("Missing incoming binary chunk.");
                            var array = (ITypedArray<byte>)engine.Evaluate($"new Uint8Array({bytes.Length})");
                            array.WriteBytes(bytes, 0, (ulong)bytes.Length, 0);
                            var bridge = (IScriptObject)engine.Evaluate("__minichat");
                            bridge.InvokeMethod("reply", args.GetString("run"), json, array);
                        }
                    }
                }
                var result = JsonValues.ParseObject((string)engine.Evaluate("__minichat.poll(" + run + ")"));
                if (result.GetBoolean("binary"))
                {
                    var data = (ITypedArray<byte>)engine.Evaluate("__minichat.takeBytes(" + run + ")");
                    if (data.Length is 0 or > 65536 || write is null) throw new IOException("Invalid outgoing asset chunk.");
                    write(args.GetString("upload"), data.GetBytes());
                }
                return result;
            }
        }

        private static string Quoted(string text) => JsonValues.Serialize(JsonValue.Create(text)).Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

        public void Finish(string run)
        {
            lock (gate)
            {
                if (closed || !initialized) return;
                engine.Execute("__minichat.finish(" + Quoted(run) + ")");
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                engine.Dispose();
            }
        }
    }
}
