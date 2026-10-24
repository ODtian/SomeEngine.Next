using System.Runtime.Intrinsics.X86;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-006">RHI-LIFE-006</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum MapType : byte
{
    Read,
    Write,
    ReadWrite,
}

internal abstract class MappingLease
{
    private const int StateBitCount = 2;
    private const long StateMask = (1 << StateBitCount) - 1;
    private const long Inactive = 0;
    private const long Active = 1;
    private const long Releasing = 2;
    private const ulong MaximumSequence = (ulong)(long.MaxValue >> StateBitCount);

    private long _authority;
    private BufferRange _range;

    protected MappingLease(Buffer buffer) =>
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    internal Buffer Buffer { get; }

    internal ulong PrepareNextSequence()
    {
        long authority = Volatile.Read(ref _authority);
        if (GetState(authority) != Inactive)
            throw new InvalidOperationException("The Buffer already has an active mapping.");

        ulong current = GetSequence(authority);
        if (current == MaximumSequence)
        {
            throw new InvalidOperationException(
                "The Buffer mapping sequence domain is exhausted.");
        }
        return current + 1;
    }

    protected void Publish(ulong sequence, in BufferRange range)
    {
        _range = range;
        Volatile.Write(ref _authority, Encode(sequence, Active));
    }

    internal void EnsureActive(ulong sequence)
    {
        if (sequence == 0 || Volatile.Read(ref _authority) != Encode(sequence, Active))
            throw new InvalidOperationException("The buffer mapping is no longer active.");
    }

    internal BufferRange GetRange(ulong sequence)
    {
        EnsureActive(sequence);
        return _range;
    }

    internal void Flush(ulong sequence, in BufferRange range)
    {
        EnsureActive(sequence);
        ValidateContained(range);
        if (range.Size != 0)
            FlushCore(range);
    }

    internal void Invalidate(ulong sequence, in BufferRange range)
    {
        EnsureActive(sequence);
        ValidateContained(range);
        if (range.Size != 0)
            InvalidateCore(range);
    }

    internal void Dispose(ulong sequence)
    {
        if (sequence == 0)
            return;

        long active = Encode(sequence, Active);
        long releasing = Encode(sequence, Releasing);
        long inactive = Encode(sequence, Inactive);
        SpinWait spinner = default;

        while (true)
        {
            long authority = Volatile.Read(ref _authority);
            if (GetSequence(authority) != sequence || authority == inactive)
                return;
            if (authority == releasing)
            {
                spinner.SpinOnce();
                continue;
            }
            if (authority != active)
                return;
            if (Interlocked.CompareExchange(ref _authority, releasing, active) != active)
                continue;

            try
            {
                UnmapCore();
            }
            finally
            {
                Volatile.Write(ref _authority, inactive);
            }
            return;
        }
    }

    internal void DisposeCurrent()
    {
        long authority = Volatile.Read(ref _authority);
        if (GetState(authority) != Inactive)
            Dispose(GetSequence(authority));
    }

    private void ValidateContained(in BufferRange range)
    {
        ulong mappingEnd = checked(_range.Offset + _range.Size);
        if (range.Offset < _range.Offset ||
            range.Offset > mappingEnd ||
            range.Size > mappingEnd - range.Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
    }

    private static long Encode(ulong sequence, long state) =>
        (long)(sequence << StateBitCount) | state;

    private static ulong GetSequence(long authority) =>
        (ulong)authority >> StateBitCount;

    private static long GetState(long authority) => authority & StateMask;

    protected abstract void FlushCore(in BufferRange range);
    protected abstract void InvalidateCore(in BufferRange range);
    protected abstract void UnmapCore();
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed borrowed mapping scope; it never owns the Buffer. Every copy shares the same non-reusable mapping sequence.</para>
/// <para><b>After Dispose:</b> Bytes, Range, Flush, and Invalidate are invalid; a previously copied Span is contractually invalid even though the runtime cannot revoke it.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-006">RHI-LIFE-006</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public unsafe struct MappedBuffer : IDisposable
{
    private readonly MappingLease? _lease;
    private readonly Memory<byte> _memory;
    private readonly nint _pointer;
    private readonly int _length;
    private readonly ulong _sequence;

    internal MappedBuffer(MappingLease lease, Memory<byte> bytes, ulong sequence)
    {
        _lease = lease;
        _memory = bytes;
        _pointer = 0;
        _length = bytes.Length;
        _sequence = sequence;
    }

    internal MappedBuffer(MappingLease lease, nint pointer, int length, ulong sequence)
    {
        _lease = lease;
        _memory = default;
        _pointer = pointer;
        _length = length;
        _sequence = sequence;
    }

    public BufferRange Range
    {
        get
        {
            MappingLease lease = RequireLease();
            return lease.GetRange(_sequence);
        }
    }

    public Span<byte> Bytes
    {
        get
        {
            MappingLease lease = RequireLease();
            lease.EnsureActive(_sequence);
            return _pointer == 0 ? _memory.Span : new Span<byte>((void*)_pointer, _length);
        }
    }

    /// <summary>
    /// Reads into the full destination from an offset relative to this mapping. Upload memory
    /// uses streaming loads when supported; ordinary memory and overlapping copies use span copy.
    /// This does not wait for GPU work or invalidate non-coherent mappings.
    /// </summary>
    public void Read(Span<byte> destination, int offset = 0)
    {
        ReadOnlySpan<byte> source = Bytes.Slice(offset, destination.Length);
        if (_pointer == 0 || _lease!.Buffer.Info.MemoryType != MemoryType.Upload ||
            !Sse41.IsSupported || source.Length < 16 || source.Overlaps(destination))
        {
            source.CopyTo(destination);
            return;
        }

        fixed (byte* input = source)
        fixed (byte* output = destination)
        {
            // Streaming reads of write-combined memory need ordering with earlier writes.
            Sse2.MemoryFence();
            int position = 0;
            int alignment = (int)((nuint)input & 15);
            if (alignment != 0)
            {
                Sse2.Store(output, Sse2.LoadVector128(input));
                position = 16 - alignment;
            }
            for (; position <= source.Length - 64; position += 64)
            {
                // Read the vectors before stores can disturb the streaming read buffer.
                var first = Sse41.LoadAlignedVector128NonTemporal(input + position);
                var second = Sse41.LoadAlignedVector128NonTemporal(input + position + 16);
                var third = Sse41.LoadAlignedVector128NonTemporal(input + position + 32);
                var fourth = Sse41.LoadAlignedVector128NonTemporal(input + position + 48);
                Sse2.Store(output + position, first);
                Sse2.Store(output + position + 16, second);
                Sse2.Store(output + position + 32, third);
                Sse2.Store(output + position + 48, fourth);
            }
            for (; position <= source.Length - 16; position += 16)
                Sse2.Store(output + position, Sse41.LoadAlignedVector128NonTemporal(input + position));
            if (position != source.Length)
                Sse2.Store(output + source.Length - 16, Sse2.LoadVector128(input + source.Length - 16));
            Sse2.MemoryFence();
        }
    }

    public void Flush(in BufferRange range) => RequireLease().Flush(_sequence, range);
    public void Invalidate(in BufferRange range) => RequireLease().Invalidate(_sequence, range);
    public void Dispose() => _lease?.Dispose(_sequence);

    private readonly MappingLease RequireLease() => _lease
        ?? throw new InvalidOperationException("The default MappedBuffer is invalid.");
}
