namespace SomeEngine.Assets.Schema;

public sealed partial class Mesh
{
    /// <summary>Creates mesh content with the caller's vertex encoding and completed page layout.</summary>
    public static Mesh Create(string name, string vertexEncoding, Memory<byte> payload, ulong bvhOffset,
        Bounds? bounds = null)
    {
        var fields = new MeshData
        {
            Name = name, VertexEncoding = vertexEncoding, Payload = payload, BvhOffset = bvhOffset,
            Bounds = bounds, PayloadKey = MeshData.PayloadChunkKey, PayloadLength = checked((ulong)payload.Length),
        };
        MeshData.PopulatePayloadIntegrity(fields, payload.Span);
        return FromFields(fields);
    }

    public int PageCount => PageDigests?.Count ?? 0;
    public int GetPageByteLength(int page) => checked((int)PageDigests![page].Length);
    public int BvhByteLength => checked((int)BvhLength);

    public ValueTask ReadPageAsync(int page, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRetainPayloadSource(out Pipeline.MeshPayloadSource? source))
            return ReadFilePageAsync(source!, page, destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = PageDigests![page];
        if (destination.Length != descriptor.Length)
            throw new ArgumentException("The destination must match the page byte length.", nameof(destination));
        Payload!.Value.Slice(checked((int)descriptor.Offset), destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReadBvhAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (TryRetainPayloadSource(out Pipeline.MeshPayloadSource? source))
            return ReadFileBvhAsync(source!, destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length != BvhByteLength)
            throw new ArgumentException("The destination must match the BVH byte length.", nameof(destination));
        Payload!.Value.Slice(checked((int)BvhOffset), destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ReadFilePageAsync(Pipeline.MeshPayloadSource source, int page,
        Memory<byte> destination, CancellationToken cancellationToken)
    {
        using (source) await source.ReadPageIntoAsync(page, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadFileBvhAsync(Pipeline.MeshPayloadSource source,
        Memory<byte> destination, CancellationToken cancellationToken)
    {
        using (source) await source.ReadBvhIntoAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
