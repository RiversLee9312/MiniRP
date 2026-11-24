using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class ModelEvents
{
    internal sealed class Interrupted(IOException cause, bool responseStarted) : IOException(
        responseStarted ? "The model connection dropped before the response finished." :
            "The model could not be reached after retrying.", cause)
    {
        public bool ResponseStarted { get; } = responseStarted;
    }

    public static async IAsyncEnumerable<JsonObject> Read(HostPort port, JsonObject request, bool ndjson, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = false;
            IOException failure;
            await using (var frames = ReadFrames(port, (JsonObject)request.DeepClone(), ndjson, cancellationToken)
                .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    try
                    {
                        if (!await frames.MoveNextAsync()) yield break;
                    }
                    catch (IOException error) when (!cancellationToken.IsCancellationRequested &&
                        error is HostPort.HttpTransportError or HostPort.HttpError { Status: 408 or 429 or 500 or 502 or 503 or 504 })
                    {
                        failure = error;
                        break;
                    }
                    received = true;
                    yield return frames.Current;
                }
            }
            if (received || attempt == 2) throw new Interrupted(failure, received);
            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 350 : 1000), cancellationToken);
        }
    }

    private static async IAsyncEnumerable<JsonObject> ReadFrames(HostPort port, JsonObject request, bool ndjson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new ChunkStream(port.Http(request, cancellationToken).GetAsyncEnumerator(cancellationToken));
        if (ndjson)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return JsonValues.ParseObject(line);
                }
            }
            yield break;
        }

        var received = false;
        // Reuse the BCL's handling of multiline data, CR/LF variants, BOMs and byte boundaries.
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            received = true;
            if (item.Data == "[DONE]")
            {
                yield break;
            }

            if (item.Data.Length > 0)
            {
                if (stream.Ended && UnfinishedSseEvent(stream.Captured)) throw TruncatedStream();
                yield return JsonValues.ParseObject(item.Data);
            }
        }
        if (UnfinishedSseEvent(stream.Captured)) throw TruncatedStream();
        if (!received && stream.Captured.Length > 0)
        {
            yield return JsonValues.ParseObject(stream.Captured.ToString());
        }
    }

    private static HostPort.HttpTransportError TruncatedStream() =>
        new("The model connection ended within a streamed event.");

    private static bool UnfinishedSseEvent(StringBuilder captured)
    {
        using var lines = new StringReader(captured.ToString());
        var data = false;
        while (lines.ReadLine() is { } line)
        {
            var field = line.AsSpan().TrimStart('\uFEFF');
            if (field.IsEmpty) data = false;
            else if (field.SequenceEqual("data") || field.StartsWith("data:", StringComparison.Ordinal))
                data = true;
        }
        return data;
    }

    private sealed class ChunkStream(IAsyncEnumerator<string> chunks) : Stream
    {
        private ReadOnlyMemory<byte> remaining;
        public StringBuilder Captured { get; } = new();
        public bool Ended { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await chunks.MoveNextAsync())
                {
                    Ended = true;
                    return 0;
                }

                Captured.Append(chunks.Current);
                remaining = Encoding.UTF8.GetBytes(chunks.Current);
            }
            var count = Math.Min(buffer.Length, remaining.Length);
            remaining[..count].CopyTo(buffer);
            remaining = remaining[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
        public override void Flush()
        {
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync()
        {
            await chunks.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
