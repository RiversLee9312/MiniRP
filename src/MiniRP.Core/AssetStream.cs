namespace MiniRP.Core;

// Adapt the authorized Asset stream to the framework's incremental text and binary readers.
internal sealed class AssetStream : Stream
{
    private readonly IAsyncEnumerator<byte[]> source;
    private ReadOnlyMemory<byte> pending;
    private long received;

    public AssetStream(PluginRuntime.AssetContent asset, CancellationToken ct)
    {
        if (asset.Length > MessageAttachments.MaxAssetBytes)
            throw new IOException("The Asset exceeds the supported size for this operation.");
        source = asset.Chunks(ct).GetAsyncEnumerator(ct);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var written = 0;
        while (!buffer.IsEmpty)
        {
            if (pending.IsEmpty)
            {
                if (!await source.MoveNextAsync()) break;
                pending = source.Current;
                received += pending.Length;
                if (received > MessageAttachments.MaxAssetBytes)
                    throw new IOException("The Asset exceeds the supported size for this operation.");
            }
            var count = Math.Min(buffer.Length, pending.Length);
            pending[..count].CopyTo(buffer);
            pending = pending[count..];
            buffer = buffer[count..];
            written += count;
        }
        return written;
    }

    public override ValueTask DisposeAsync() => source.DisposeAsync();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
