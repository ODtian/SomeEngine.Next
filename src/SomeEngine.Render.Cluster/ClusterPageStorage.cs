using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster;

/// <summary>
/// Supplies asynchronous CPU streaming destinations and publishes completed ranges into the final
/// page-heap Buffer only at a renderer-owned quiescent boundary.
/// </summary>
internal interface IClusterPageStorage : IDisposable
{
    Memory<byte> Allocate(uint offset, int length);
    void Stage(uint offset, int length);
    void Publish();
    void Release(uint offset, int length);
}

/// <summary>CPU-test final storage. Each live page owns exactly one sparse backing array.</summary>
internal sealed class SparseClusterPageStorage : IClusterPageStorage
{
    private readonly uint _capacity;
    private readonly Dictionary<uint, byte[]> _pages = [];
    private int _disposed;

    internal SparseClusterPageStorage(uint capacity)
        => _capacity = capacity;

    public Memory<byte> Allocate(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked((ulong)offset + (uint)length) > _capacity)
            throw new ArgumentOutOfRangeException(nameof(length));
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        if (!_pages.TryAdd(offset, bytes))
            throw new InvalidOperationException($"Page-heap offset {offset} already owns final storage.");
        return bytes;
    }

    public void Stage(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.TryGetValue(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Page-heap range at {offset} is not allocated.");
    }

    public void Publish()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Release(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.Remove(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Page-heap range [{offset}, {checked(offset + (uint)length)}) is not owned by this storage.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _pages.Clear();
    }
}

/// <summary>
/// CPU-owned streaming destinations with an explicit stack-bounded RHI mapping window. Async IO
/// never retains mapped native memory, and publication occurs only after prior GPU readers retire.
/// </summary>
internal sealed class MappedClusterPageStorage : IClusterPageStorage
{
    private readonly IGraphicsBackend _backend;
    private readonly Buffer _buffer;
    private readonly byte[] _memory;
    private readonly Dictionary<uint, int> _allocations = [];
    private readonly HashSet<uint> _stagedOffsets = [];
    private int _disposed;

    internal MappedClusterPageStorage(
        IGraphicsBackend backend,
        Buffer buffer,
        int capacity)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _memory = GC.AllocateUninitializedArray<byte>(capacity);
    }

    public Memory<byte> Allocate(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked((ulong)offset + (uint)length) > (ulong)_memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!_allocations.TryAdd(offset, length))
            throw new InvalidOperationException($"Page-heap offset {offset} is already live.");
        return _memory.AsMemory(checked((int)offset), length);
    }

    public void Stage(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_allocations.TryGetValue(offset, out int allocatedLength) || allocatedLength != length)
            throw new InvalidOperationException($"Page-heap range at {offset} is not allocated.");
        _stagedOffsets.Add(offset);
    }

    public void Publish()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_stagedOffsets.Count == 0)
            return;

        BufferRange range = new(0, checked((ulong)_memory.Length));
        using MappedBuffer mapping = _backend.Map(_buffer, MapType.Write, range);
        foreach (uint offset in _stagedOffsets)
        {
            int length = _allocations[offset];
            _memory.AsSpan(checked((int)offset), length)
                .CopyTo(mapping.Bytes.Slice(checked((int)offset), length));
        }
        _stagedOffsets.Clear();
    }

    public void Release(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_allocations.Remove(offset, out int allocatedLength) || allocatedLength != length)
            throw new InvalidOperationException($"Page-heap offset {offset} is not live.");
        _stagedOffsets.Remove(offset);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocations.Clear();
            _stagedOffsets.Clear();
        }
    }
}

internal interface IClusterBvhStorage : IDisposable
{
    Memory<byte> Allocate(ulong offset, int length);
    Memory<byte> GetRange(ulong offset, int length);
    void Stage(ulong offset, int length);
    void Publish();
    void Release(ulong offset, int length);
}

internal sealed class SparseClusterBvhStorage : IClusterBvhStorage
{
    private readonly ulong _capacity;
    private readonly SortedDictionary<ulong, byte[]> _blocks = [];
    private int _disposed;

    internal SparseClusterBvhStorage(ulong capacity) => _capacity = capacity;

    public Memory<byte> Allocate(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked(offset + (uint)length) > _capacity)
            throw new InvalidOperationException("Global Cluster BVH storage capacity is exhausted.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        if (!_blocks.TryAdd(offset, bytes))
            throw new InvalidOperationException($"Global BVH offset {offset} is already allocated.");
        return bytes;
    }

    public Memory<byte> GetRange(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach ((ulong start, byte[] bytes) in _blocks)
        {
            ulong end = checked(start + (uint)bytes.Length);
            if (offset >= start && checked(offset + (uint)length) <= end)
                return bytes.AsMemory(checked((int)(offset - start)), length);
        }
        throw new InvalidOperationException($"Global BVH range [{offset}, {checked(offset + (uint)length)}) is not allocated.");
    }

    public void Stage(ulong offset, int length) => _ = GetRange(offset, length);

    public void Publish()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Release(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_blocks.Remove(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Global BVH range at {offset} is not owned by this storage.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _blocks.Clear();
    }
}

internal sealed class MappedClusterBvhStorage : IClusterBvhStorage
{
    private readonly IGraphicsBackend _backend;
    private readonly Buffer _buffer;
    private readonly byte[] _memory;
    private readonly Dictionary<ulong, int> _allocations = [];
    private readonly List<(ulong Offset, int Length)> _stagedRanges = [];
    private int _disposed;

    internal MappedClusterBvhStorage(
        IGraphicsBackend backend,
        Buffer buffer,
        int capacity)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _memory = GC.AllocateUninitializedArray<byte>(capacity);
    }

    public Memory<byte> Allocate(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked(offset + (uint)length) > (ulong)_memory.Length)
            throw new InvalidOperationException("Global Cluster BVH storage capacity is exhausted.");
        if (!_allocations.TryAdd(offset, length))
            throw new InvalidOperationException($"Global BVH offset {offset} is already allocated.");
        return _memory.AsMemory(checked((int)offset), length);
    }

    public Memory<byte> GetRange(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach ((ulong start, int allocationLength) in _allocations)
        {
            ulong end = checked(start + (uint)allocationLength);
            if (offset >= start && checked(offset + (uint)length) <= end)
                return _memory.AsMemory(checked((int)offset), length);
        }
        throw new InvalidOperationException($"Global BVH range [{offset}, {checked(offset + (uint)length)}) is not allocated.");
    }

    public void Stage(ulong offset, int length)
    {
        _ = GetRange(offset, length);
        _stagedRanges.Add((offset, length));
    }

    public void Publish()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_stagedRanges.Count == 0)
            return;

        BufferRange range = new(0, checked((ulong)_memory.Length));
        using MappedBuffer mapping = _backend.Map(_buffer, MapType.Write, range);
        foreach ((ulong offset, int length) in _stagedRanges)
        {
            _memory.AsSpan(checked((int)offset), length)
                .CopyTo(mapping.Bytes.Slice(checked((int)offset), length));
        }
        _stagedRanges.Clear();
    }

    public void Release(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_allocations.Remove(offset, out int ownedLength) || ownedLength != length)
            throw new InvalidOperationException($"Global BVH range at {offset} is not owned by this storage.");
        _stagedRanges.RemoveAll(range =>
            range.Offset >= offset &&
            checked(range.Offset + (uint)range.Length) <= checked(offset + (uint)length));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocations.Clear();
            _stagedRanges.Clear();
        }
    }
}
