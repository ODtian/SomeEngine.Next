namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum MapType : byte
{
    Read,
    Write,
    ReadWrite,
}

internal abstract class MappingLease
{
    private int _released;

    protected MappingLease(Buffer buffer, MapType type, in BufferRange range, ulong sequence)
    {
        Buffer = buffer;
        Type = type;
        Range = range;
        Sequence = sequence;
    }

    internal Buffer Buffer { get; }
    internal MapType Type { get; }
    internal BufferRange Range { get; }
    internal ulong Sequence { get; }
    internal bool IsActive => Volatile.Read(ref _released) == 0;

    internal void EnsureActive()
    {
        if (!IsActive)
            throw new InvalidOperationException("The buffer mapping is no longer active.");
    }

    internal void Flush(in BufferRange range)
    {
        EnsureActive();
        ValidateContained(range);
        if (range.Size != 0)
            FlushCore(range);
    }

    internal void Invalidate(in BufferRange range)
    {
        EnsureActive();
        ValidateContained(range);
        if (range.Size != 0)
            InvalidateCore(range);
    }

    internal void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            UnmapCore();
    }

    private void ValidateContained(in BufferRange range)
    {
        ulong mappingEnd = checked(Range.Offset + Range.Size);
        if (range.Offset < Range.Offset ||
            range.Offset > mappingEnd ||
            range.Size > mappingEnd - range.Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
    }

    protected abstract void FlushCore(in BufferRange range);
    protected abstract void InvalidateCore(in BufferRange range);
    protected abstract void UnmapCore();
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed borrowed mapping scope; it never owns the Buffer. Every copy shares the same non-reusable mapping sequence.</para>
/// <para><b>After Dispose:</b> Bytes, Range, Flush, and Invalidate are invalid; a previously copied Span is contractually invalid even though the runtime cannot revoke it.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public unsafe ref struct MappedBuffer
{
    private readonly MappingLease? _lease;
    private readonly Span<byte> _bytes;

    internal MappedBuffer(MappingLease lease, Span<byte> bytes)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _bytes = bytes;
    }

    internal MappedBuffer(MappingLease lease, nint pointer, int length)
        : this(lease, new Span<byte>((void*)pointer, length))
    {
    }

    public BufferRange Range
    {
        get
        {
            MappingLease lease = RequireLease();
            lease.EnsureActive();
            return lease.Range;
        }
    }

    public Span<byte> Bytes
    {
        get
        {
            MappingLease lease = RequireLease();
            lease.EnsureActive();
            return _bytes;
        }
    }

    public void Flush(in BufferRange range) => RequireLease().Flush(range);
    public void Invalidate(in BufferRange range) => RequireLease().Invalidate(range);
    public void Dispose() => _lease?.Dispose();

    private readonly MappingLease RequireLease() => _lease
        ?? throw new InvalidOperationException("The default MappedBuffer is invalid.");
}
