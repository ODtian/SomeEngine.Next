namespace SomeEngine.Render.Cluster.Tests;

internal sealed class TestClusterPageStorage : IClusterPageStorage
{
    private readonly Memory<byte> _memory;
    private readonly Dictionary<uint, int> _allocations = [];
    private int _disposed;

    internal TestClusterPageStorage(Memory<byte> memory)
    {
        if (memory.IsEmpty) throw new ArgumentException("Storage cannot be empty.", nameof(memory));
        _memory = memory;
    }

    public Memory<byte> Allocate(uint offset, int length)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked((ulong)offset + (uint)length) > (ulong)_memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!_allocations.TryAdd(offset, length))
            throw new InvalidOperationException($"Page offset {offset} is already allocated.");
        return _memory.Slice(checked((int)offset), length);
    }

    public void Stage(uint offset, int length)
    {
        ThrowIfDisposed();
        if (!_allocations.TryGetValue(offset, out int owned) || owned != length)
            throw new InvalidOperationException($"Page range at {offset} is not allocated.");
    }

    public void Publish() => ThrowIfDisposed();

    public void Release(uint offset, int length)
    {
        ThrowIfDisposed();
        if (!_allocations.Remove(offset, out int owned) || owned != length)
            throw new InvalidOperationException($"Page range at {offset} is not allocated.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _allocations.Clear();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class TestClusterBvhStorage : IClusterBvhStorage
{
    private readonly Memory<byte> _memory;
    private readonly Dictionary<ulong, int> _allocations = [];
    private int _disposed;

    internal TestClusterBvhStorage(Memory<byte> memory)
    {
        if (memory.IsEmpty) throw new ArgumentException("Storage cannot be empty.", nameof(memory));
        _memory = memory;
    }

    public Memory<byte> Allocate(ulong offset, int length)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked(offset + (uint)length) > (ulong)_memory.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!_allocations.TryAdd(offset, length))
            throw new InvalidOperationException($"BVH offset {offset} is already allocated.");
        return _memory.Slice(checked((int)offset), length);
    }

    public Memory<byte> GetRange(ulong offset, int length)
    {
        ThrowIfDisposed();
        foreach ((ulong start, int owned) in _allocations)
        {
            if (offset >= start && checked(offset + (uint)length) <= checked(start + (uint)owned))
                return _memory.Slice(checked((int)offset), length);
        }
        throw new InvalidOperationException($"BVH range at {offset} is not allocated.");
    }

    public void Stage(ulong offset, int length) => _ = GetRange(offset, length);

    public void Publish() => ThrowIfDisposed();

    public void Release(ulong offset, int length)
    {
        ThrowIfDisposed();
        if (!_allocations.Remove(offset, out int owned) || owned != length)
            throw new InvalidOperationException($"BVH range at {offset} is not allocated.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _allocations.Clear();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
