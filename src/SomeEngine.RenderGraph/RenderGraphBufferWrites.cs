namespace SomeEngine.RenderGraph;

/// <summary>Writes directly into frame upload pages and commits only the buffer ranges.</summary>
public sealed class RenderGraphBufferWrites : IDisposable
{
    private readonly RenderGraphNextFrame _next;
    private readonly FrameBufferTransfers _transfers;
    private readonly ThreadLocal<List<FrameBufferTransfers.BufferUpload>> _writes =
        new(static () => [], trackAllValues: true);
    private bool _closed;

    internal RenderGraphBufferWrites(RenderGraphNextFrame next, FrameBufferTransfers transfers)
    {
        _next = next;
        _transfers = transfers;
    }

    /// <summary>Returns a mapped range; parallel callers may write disjoint ranges.</summary>
    public Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length)
    {
        _next.EnsureOpen();
        DevelopmentCheck.Require(!_closed, "The buffer writes must be open for writing.");
        _next.Graph.ValidateBufferUpload(buffer, offset, length);
        FrameBufferTransfers.BufferUpload write = _transfers.ReserveUpload(buffer, offset, length);
        _writes.Value!.Add(write);
        return FrameBufferTransfers.GetUploadSpan(write);
    }

    public void WriteBuffer(GraphBufferId buffer, ulong offset, scoped ReadOnlySpan<byte> data) =>
        data.CopyTo(GetWriteSpan(buffer, offset, data.Length));

    /// <summary>Commits the ranges after all writers have finished.</summary>
    public void Commit()
    {
        _next.EnsureOpen();
        DevelopmentCheck.Require(!_closed, "The buffer writes must be open when committed.");
        _transfers.AddUploads(_writes.Values.SelectMany(writes => writes));
        Dispose();
    }

    /// <summary>Discards the range records; the page is reclaimed with the frame resources.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writes.Dispose();
    }
}
