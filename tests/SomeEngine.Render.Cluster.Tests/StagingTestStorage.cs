namespace SomeEngine.Render.Cluster.Tests;

internal sealed class TestClusterPageStorage : IClusterPageStorage
{
    private readonly Memory<byte> _memory;
    private readonly Dictionary<uint, int> _allocations = [];
    private readonly HashSet<uint> _staged = [];
    private int _disposed;

    internal TestClusterPageStorage(Memory<byte> memory)
    {
        if (memory.IsEmpty) throw new ArgumentException("Storage cannot be empty.", nameof(memory));
        _memory = memory;
    }

    internal int AllocationCount => _allocations.Count;

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
        _staged.Add(offset);
    }

    public ClusterStagedUpload[] CaptureStagedUploads()
    {
        ThrowIfDisposed();
        uint[] offsets = [.. _staged];
        Array.Sort(offsets);
        return offsets.Select(offset => new ClusterStagedUpload(
            offset,
            _memory.Slice(checked((int)offset), _allocations[offset]))).ToArray();
    }

    public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
    {
        ThrowIfDisposed();
        foreach (ref readonly ClusterStagedUpload upload in uploads)
        {
            uint offset = checked((uint)upload.Offset);
            if (!_allocations.TryGetValue(offset, out int length) ||
                length != upload.Bytes.Length ||
                !_staged.Contains(offset))
                throw new InvalidOperationException("The page staging completion is stale.");
        }
        foreach (ref readonly ClusterStagedUpload upload in uploads)
        {
            uint offset = checked((uint)upload.Offset);
            _staged.Remove(offset);
            _allocations.Remove(offset);
        }
    }

    public void Release(uint offset, int length)
    {
        ThrowIfDisposed();
        if (!_allocations.Remove(offset, out int owned) || owned != length)
            throw new InvalidOperationException($"Page range at {offset} is not allocated.");
        _staged.Remove(offset);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocations.Clear();
            _staged.Clear();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal sealed class TestClusterBvhStorage : IClusterBvhStorage
{
    private readonly Memory<byte> _memory;
    private readonly Dictionary<ulong, int> _allocations = [];
    private readonly List<(ulong Offset, int Length)> _staged = [];
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

    public void Stage(ulong offset, int length)
    {
        _ = GetRange(offset, length);
        _staged.Add((offset, length));
    }

    public ClusterStagedUpload[] CaptureStagedUploads()
    {
        ThrowIfDisposed();
        return _staged.Select(range => new ClusterStagedUpload(
            range.Offset,
            GetRange(range.Offset, range.Length))).ToArray();
    }

    public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
    {
        ThrowIfDisposed();
        if (uploads.Length > _staged.Count)
            throw new InvalidOperationException("The BVH staging completion is stale.");
        _staged.RemoveRange(0, uploads.Length);
    }

    public void Release(ulong offset, int length)
    {
        ThrowIfDisposed();
        if (!_allocations.Remove(offset, out int owned) || owned != length)
            throw new InvalidOperationException($"BVH range at {offset} is not allocated.");
        _staged.RemoveAll(range => range.Offset >= offset &&
            range.Offset + (uint)range.Length <= offset + (uint)length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _allocations.Clear();
            _staged.Clear();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
