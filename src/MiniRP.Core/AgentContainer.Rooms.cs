using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed partial class AgentContainer
{
    internal sealed class Rooms(HostPort port, string root)
    {
        private readonly SemaphoreSlim gate = new(1);
        private Selection? selected;
        private RoomVm? current;
        private bool warm;
        internal CancellationToken Lifetime => port.Lifetime;

        internal sealed record Selection(string Preset, string Room, string Database);

        internal void EnableWarmStart()
        {
            warm = true;
            QueueSelection(Volatile.Read(ref selected));
        }

        internal void Select(Selection? selection)
        {
            if (selection == Volatile.Read(ref selected)) return;
            Volatile.Write(ref selected, selection);
            if (Volatile.Read(ref current) is { } vm && vm.Target != selection) vm.Interrupt();
            QueueSelection(selection);
        }

        private void QueueSelection(Selection? selection) => _ = Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(Lifetime);
                try
                {
                    if (selection != Volatile.Read(ref selected)) return;
                    if (current?.Target != selection) await StopCurrent();
                    if (warm && selection is not null) await Open(selection, Lifetime);
                }
                finally { gate.Release(); }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Tool calls retry and report startup failures when the runtime is needed.
                System.Diagnostics.Trace.WriteLine(error.Message);
            }
        });

        internal async Task<JsonObject> Use(string preset, string room, Func<RoomVm, Task<JsonObject>> action, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                var selection = Volatile.Read(ref selected);
                if (selection is null || selection.Preset != preset || selection.Room != room)
                    throw new InvalidOperationException("The container's room is closed.");
                return await action(await Open(selection, ct));
            }
            finally { gate.Release(); }
        }

        internal async Task Flush(string preset, string room, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (current is { Alive: true } vm && (preset.Length == 0 || vm.Target.Preset == preset) &&
                    (room.Length == 0 || vm.Target.Room == room)) await vm.Save(ct);
            }
            finally { gate.Release(); }
        }

        internal async Task Shutdown()
        {
            await gate.WaitAsync();
            try { await StopCurrent(save: false); }
            finally { gate.Release(); }
        }

        private async Task<RoomVm> Open(Selection selection, CancellationToken ct)
        {
            if (current is { Alive: true, Retiring: false } vm && vm.Target == selection) return vm;
            await StopCurrent();
            var opened = new RoomVm(port, root, selection);
            try { await opened.Start(ct); }
            catch { await opened.Stop(save: false); throw; }
            return current = opened;
        }

        private async Task StopCurrent(bool save = true)
        {
            if (current is not { } vm) return;
            current = null;
            await vm.Stop(save);
        }
    }

    internal sealed class RoomVm(HostPort port, string root, Rooms.Selection selection)
    {
        private readonly string id = JsonValues.NewId();
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(port.Lifetime);
        private int retiring;
        private Task<JsonObject>? runner;
        private JsonObject? checkpoint;
        private string image = "";
        private string Mailbox => Path.Combine(DirectoryPath, "mailbox.raw");
        private string Disk => Path.Combine(DirectoryPath, "workspace.raw");
        internal string DirectoryPath => Path.Combine(root, "container-runs", id);
        internal Rooms.Selection Target => selection;
        internal bool Alive => !lifetime.IsCancellationRequested && runner is { IsCompleted: false };
        internal bool Retiring => Volatile.Read(ref retiring) != 0;
        internal void Interrupt() => Interlocked.Exchange(ref retiring, 1);

        internal async Task Start(CancellationToken ct)
        {
            var runtime = await port.CallHost("container_runtime", new(), ct, TimeSpan.FromSeconds(15));
            image = runtime.GetString("image");
            if (!IsHash(image) || !runtime.GetBoolean("room_vm")) throw new IOException("Install a compatible MiniRP container image.");
            using (var db = MemoryDb.OpenScratch(selection.Database))
                checkpoint = db.Get(StateKey) is { } saved ? JsonValues.ParseObject(saved) : null;
            Directory.CreateDirectory(DirectoryPath);
            ActiveRuns.TryAdd(DirectoryPath, 0);
            await Restore(root, selection.Preset, Disk, checkpoint, ct);
            using (var mailbox = File.Create(Mailbox)) mailbox.SetLength(HeaderSize + MessageAttachments.MaxAssetBytes + 4096);
            runner = port.CallHost("container_run", new() { ["run"] = id, ["image"] = image,
                ["arguments"] = QemuArguments(runtime.GetString("machine") == "microvm", checkpoint is null) }, lifetime.Token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(40));
            var ready = await WaitFor(reply => reply.ContainsKey("ready"), limit.Token);
            if (!ready.GetBoolean("ready")) throw new IOException(ready.GetString("error", "The container did not boot."));
        }

        internal FileStream ReadOutput()
        {
            var stream = OpenMailbox(FileAccess.Read);
            stream.Position = HeaderSize;
            return stream;
        }

        internal async Task<JsonObject> Command(JsonObject request, PluginRuntime.AssetContent? source, CancellationToken ct)
        {
            if (source is not null)
            {
                await using var stream = OpenMailbox(FileAccess.Write);
                stream.Position = HeaderSize;
                await using var input = new AssetStream(source, ct);
                await input.CopyToAsync(stream, ct);
                request["size"] = stream.Position - HeaderSize;
            }
            ct.ThrowIfCancellationRequested();
            var reply = await Exchange(request, request.GetInt32("seconds", 60) + 45,
                request.GetString("op") == "shell" ? () => Retiring || ct.IsCancellationRequested : null);
            if (reply.GetBoolean("frozen"))
            {
                try
                {
                    await SaveDisk(lifetime.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    var details = reply.GetString("error");
                    reply["error"] = (details.Length > 0 ? details + " " : "") + "The workspace checkpoint failed: " + error.Message;
                }
                try { await Resume(); }
                catch (IOException error)
                {
                    var details = reply.GetString("error");
                    reply["error"] = (details.Length > 0 ? details + " " : "") + error.Message;
                }
            }
            return reply;
        }

        internal async Task Save(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await Exchange(new() { ["op"] = "checkpoint" }, 30);
            if (!reply.GetBoolean("frozen")) throw new IOException(reply.GetString("error", "Could not freeze the workspace."));
            try { await SaveDisk(lifetime.Token); }
            finally { await Resume(); }
        }

        private async Task SaveDisk(CancellationToken ct)
        {
            var chunks = await Checkpoint(root, selection.Preset, Disk, checkpoint, ct);
            if (checkpoint?["chunks"] is JsonArray previous && JsonNode.DeepEquals(previous, chunks)) return;
            var next = new JsonObject { ["version"] = 1, ["image"] = image, ["chunks"] = chunks };
            if (!File.Exists(selection.Database)) return;
            using var db = MemoryDb.OpenScratch(selection.Database);
            db.Write(StateKey, JsonValues.Serialize(next), internalWrite: true);
            checkpoint = next;
        }

        private async Task Resume()
        {
            var reply = await Exchange(new() { ["op"] = "resume" }, 10);
            if (reply.GetBoolean("frozen") || reply.GetString("error").Length > 0)
            {
                lifetime.Cancel();
                throw new IOException("Could not resume the workspace after its checkpoint.");
            }
        }

        internal async Task Stop(bool save)
        {
            try
            {
                if (save && Alive)
                {
                    if (File.Exists(selection.Database)) await Save(lifetime.Token);
                    await Send(new() { ["op"] = "shutdown", ["id"] = JsonValues.NewId() }, lifetime.Token);
                    if (runner is not null) await runner.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            { System.Diagnostics.Trace.WriteLine(error.Message); }
            finally
            {
                lifetime.Cancel();
                if (runner is not null)
                {
                    try { await runner; }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { System.Diagnostics.Trace.WriteLine(error.Message); }
                }
                lifetime.Dispose();
                ActiveRuns.TryRemove(DirectoryPath, out _);
                try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private async Task<JsonObject> Exchange(JsonObject request, int seconds, Func<bool>? interrupted = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var requestId = JsonValues.NewId();
            request["id"] = requestId;
            try
            {
                await Send(request, timeout.Token);
                return await WaitFor(reply => reply.GetString("id") == requestId && !reply.ContainsKey("op"), timeout.Token,
                    interrupted, requestId);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                lifetime.Cancel();
                throw new IOException("The container did not respond in time.");
            }
            catch (IOException) { lifetime.Cancel(); throw; }
        }

        private async Task Send(JsonObject request, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonValues.Serialize(request));
            if (bytes.Length >= HeaderSize) throw new ArgumentException("The guest request is too large.");
            await using var stream = OpenMailbox(FileAccess.Write);
            // Put op first, then publish the full header in one write after the payload.
            Array.Resize(ref bytes, HeaderSize);
            await stream.WriteAsync(bytes, ct);
        }

        private async Task<JsonObject> WaitFor(Func<JsonObject, bool> match, CancellationToken ct,
            Func<bool>? interrupted = null, string requestId = "")
        {
            await using var stream = OpenMailbox(FileAccess.Read);
            var bytes = new byte[HeaderSize];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (interrupted?.Invoke() == true)
                {
                    await using var control = OpenMailbox(FileAccess.Write);
                    control.Position = HeaderSize + MessageAttachments.MaxAssetBytes;
                    await control.WriteAsync(Encoding.ASCII.GetBytes(requestId), ct);
                    interrupted = null;
                }
                stream.Position = 0;
                await stream.ReadExactlyAsync(bytes, ct);
                var length = Array.IndexOf(bytes, (byte)0);
                if (length != 0)
                {
                    try
                    {
                        if (JsonNode.Parse(bytes.AsSpan(0, length < 0 ? HeaderSize : length)) is JsonObject reply && match(reply)) return reply;
                    }
                    catch (JsonException) { }
                }
                if (runner is { IsCompleted: true })
                {
                    await runner;
                    throw new IOException("The container stopped before acknowledging the request.");
                }
                await Task.Delay(20, ct);
            }
        }

        private FileStream OpenMailbox(FileAccess access) => new(Mailbox, FileMode.Open, access, FileShare.ReadWrite, 1, useAsync: true);
    }
}
