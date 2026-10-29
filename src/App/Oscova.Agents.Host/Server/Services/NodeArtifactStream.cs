using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Nodes;

namespace Oscova.Agents.Host.Server.Services;

/// <summary>Serves remote artifact ranges without retaining a whole file or a server-side copy.</summary>
internal sealed class NodeArtifactStream(NodeFileChunk metadata,
    Func<long, int, CancellationToken, Task<NodeFileChunk>> read) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => metadata.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = (int)Math.Min(Math.Min(buffer.Length, ProjectCapability.FileChunkBytes), Length - _position);
        if (count <= 0) { return 0; }

        var chunk = await read(_position, count, cancellationToken).ConfigureAwait(false);
        if (chunk.Length != Length || chunk.LastWriteTicks != metadata.LastWriteTicks || chunk.Bytes.Length != count)
        {
            throw new IOException("The file changed during transfer. Open it again to download the current version.");
        }

        chunk.Bytes.CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > Length) { throw new IOException("The requested position is outside the file."); }

        return _position = position;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
