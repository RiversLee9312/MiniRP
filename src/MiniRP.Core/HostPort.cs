using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MiniRP.Core;

/// <summary>The native core owns decisions and parsing; the platform owns TLS and browser I/O.</summary>
public sealed class HostPort(Action<JsonObject> emit, CancellationToken lifetime = default)
{
    internal CancellationToken Lifetime { get; } = lifetime;
    readonly ConcurrentDictionary<string, Channel<JsonObject>> pending = new();
    private readonly ConcurrentDictionary<string, byte[]> transfers = new();
    private static readonly byte[] Reserved = [];
    internal const int ChunkBytes = 64 * 1024;

    internal string ReserveBinary()
    {
        var id = JsonValues.NewId();
        if (!transfers.TryAdd(id, Reserved)) throw new InvalidOperationException("Duplicate binary transfer.");
        return id;
    }

    internal string StageBinary(byte[] bytes)
    {
        var id = ReserveBinary();
        WriteBinary(id, bytes);
        return id;
    }

    public void WriteBinary(string id, byte[] bytes)
    {
        if (bytes.Length is 0 or > ChunkBytes || !transfers.TryUpdate(id, bytes, Reserved))
            throw new InvalidOperationException("Invalid binary transfer.");
    }

    public byte[]? TakeBinary(string id) =>
        transfers.TryRemove(id, out var value) && value.Length > 0 ? value : null;
    public void Reply(JsonObject message)
    {
        if (pending.TryGetValue(message.GetString("id"), out var channel))
        {
            channel.Writer.TryWrite(message);
        }
    }
    public async IAsyncEnumerable<string> Http(JsonObject request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = JsonValues.NewId();
        var channel = Channel.CreateUnbounded<JsonObject>();
        pending[id] = channel;
        request["id"] = id;
        request["kind"] = "http_request";
        emit(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(request.GetInt32("timeout_ms", 240000), 1000, 600000)));
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            await foreach (var part in channel.Reader.ReadAllAsync(timeout.Token))
            {
                if (part.GetString("error").Length > 0)
                {
                    throw new IOException(part.GetString("error"));
                }

                var status = part.GetInt32("status", 200);
                if (status >= 400)
                {
                    throw new IOException($"HTTP {status}: {part.GetString("data")}");
                }

                if (part.GetString("data").Length > 0)
                {
                    yield return part.GetString("data");
                }
                if (part.GetString("data_base64").Length > 0)
                {
                    var bytes = Convert.FromBase64String(part.GetString("data_base64"));
                    var characters = new char[bytes.Length + 2];
                    var count = decoder.GetChars(bytes, characters, flush: part.GetBoolean("done"));
                    if (count > 0)
                    {
                        yield return new string(characters, 0, count);
                    }
                }

                if (part.GetBoolean("done"))
                {
                    yield break;
                }
            }
        }
        finally { pending.TryRemove(id, out _); emit(new() { ["kind"] = "cancel_io", ["id"] = id }); }
    }
    public async Task<JsonObject> Request(JsonObject request, CancellationToken ct)
    {
        var body = new StringBuilder();
        await foreach (var chunk in Http(request, ct))
        {
            body.Append(chunk);
        }

        return JsonValues.ParseObject(body.ToString());
    }
    public async Task<JsonObject> Browser(string job, string tool, JsonObject args, CancellationToken ct)
    {
        var id = JsonValues.NewId();
        var channel = Channel.CreateUnbounded<JsonObject>();
        pending[id] = channel;
        emit(new()
        {
            ["kind"] = "browser_request",
            ["id"] = id,
            ["job"] = job,
            ["tool"] = tool,
            ["arguments"] = args.DeepClone()
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var result = await channel.Reader.ReadAsync(timeout.Token);
            if (result.GetString("error").Length > 0)
            {
                throw new IOException(result.GetString("error"));
            }

            return result.GetObject("result");
        }
        finally { pending.TryRemove(id, out _); emit(new() { ["kind"] = "cancel_io", ["id"] = id }); }
    }

    internal async Task<JsonObject> CallHost(string kind, JsonObject request, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } duration) deadline!.CancelAfter(duration);
        var id = JsonValues.NewId();
        var channel = Channel.CreateUnbounded<JsonObject>();
        pending[id] = channel;
        request["id"] = id;
        request["kind"] = kind;
        emit(request);
        var received = false;
        try
        {
            var reply = await channel.Reader.ReadAsync(deadline?.Token ?? ct);
            received = true;
            if (reply.GetString("error") is { Length: > 0 } error) throw new IOException(error);
            var result = reply.GetObject("result");
            // The caller can embed this result directly in a reply to V8.
            reply.Remove("result");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new IOException($"Platform request {kind} timed out.");
        }
        finally
        {
            pending.TryRemove(id, out _);
            if (!received) emit(new() { ["kind"] = "cancel_io", ["id"] = id });
        }
    }
}
