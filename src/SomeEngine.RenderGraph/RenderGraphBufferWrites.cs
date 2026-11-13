namespace SomeEngine.RenderGraph;

/// <summary>Writes directly into frame upload pages and commits only the buffer ranges.</summary>
public sealed class RenderGraphBufferWrites : IDisposable
{
    private readonly RenderGraphNextFrame _next;
    private readonly FrameBufferTransfers _transfers;
    private readonly List<BufferWriter> _writers = [];
    private bool _closed;

    internal RenderGraphBufferWrites(RenderGraphNextFrame next, FrameBufferTransfers transfers)
    {
        _next = next;
        _transfers = transfers;
    }

    /// <summary>Creates a writer for one thread's buffer ranges.</summary>
    public BufferWriter CreateWriter()
    {
        _next.EnsureOpen();
        DevelopmentCheck.Require(!_closed, "The buffer writes must be open for writing.");
        var writer = new BufferWriter(this);
        lock (_writers) _writers.Add(writer);
        return writer;
    }

    /// <summary>Commits the ranges after all writers have finished.</summary>
    public void Commit()
    {
        _next.EnsureOpen();
        DevelopmentCheck.Require(!_closed, "The buffer writes must be open when committed.");
        var writes = new List<FrameBufferTransfers.BufferUpload>();
        foreach (BufferWriter writer in _writers) writes.AddRange(writer.Writes);
        _transfers.AddUploads(writes);
        Dispose();
    }

    /// <summary>Discards the range records; the page is reclaimed with the frame resources.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writers.Clear();
    }

    public sealed class BufferWriter
    {
        private readonly RenderGraphBufferWrites _group;
        internal readonly List<FrameBufferTransfers.BufferUpload> Writes = [];

        internal BufferWriter(RenderGraphBufferWrites group) => _group = group;

        /// <summary>Returns a mapped range belonging to this writer.</summary>
        public Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length)
        {
            _group._next.EnsureOpen();
            DevelopmentCheck.Require(!_group._closed, "The buffer writes must be open for writing.");
            _group._next.Graph.ValidateBufferUpload(buffer, offset, length);
            FrameBufferTransfers.BufferUpload write = _group._transfers.ReserveUpload(buffer, offset, length);
            Writes.Add(write);
            return FrameBufferTransfers.GetUploadSpan(write);
        }

        public void WriteBuffer(GraphBufferId buffer, ulong offset, scoped ReadOnlySpan<byte> data) =>
            data.CopyTo(GetWriteSpan(buffer, offset, data.Length));
    }
}
