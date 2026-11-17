using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Minichat.Core;

internal static class ModelEvents
{
    public static async IAsyncEnumerable<JsonObject> Read(HostPort port, JsonObject request, bool ndjson, [EnumeratorCancellation] CancellationToken cancellationToken)
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
                yield return JsonValues.ParseObject(item.Data);
            }
        }
        if (!received && stream.Captured.Length > 0)
        {
            yield return JsonValues.ParseObject(stream.Captured.ToString());
        }
    }

    private sealed class ChunkStream(IAsyncEnumerator<string> chunks) : Stream
    {
        private ReadOnlyMemory<byte> remaining;
        public StringBuilder Captured { get; } = new();
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
