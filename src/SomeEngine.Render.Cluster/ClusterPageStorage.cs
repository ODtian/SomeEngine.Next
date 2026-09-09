namespace SomeEngine.Render.Cluster;

/// <summary>
/// One immutable view of CPU staging bytes awaiting a RenderGraph copy.
/// </summary>
internal readonly record struct ClusterStagedUpload(
    ulong Offset,
    ReadOnlyMemory<byte> Bytes);

/// <summary>Supplies asynchronous CPU streaming destinations and tracks completed staging ranges.</summary>
internal interface IClusterPageStorage : IDisposable
{
    Memory<byte> Allocate(uint offset, int length);
    void Stage(uint offset, int length);
    ClusterStagedUpload[] CaptureStagedUploads();
    void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads);
    void Release(uint offset, int length);
}

/// <summary>Each in-flight page upload owns one sparse CPU staging array until publication.</summary>
internal sealed class SparseClusterPageStorage : IClusterPageStorage
{
    private readonly uint _capacity;
    private readonly Dictionary<uint, byte[]> _pages = [];
    private readonly HashSet<uint> _stagedOffsets = [];
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
            throw new InvalidOperationException($"Page-heap offset {offset} already owns staging storage.");
        return bytes;
    }

    public void Stage(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.TryGetValue(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Page-heap range at {offset} is not allocated.");
        _stagedOffsets.Add(offset);
    }

    public ClusterStagedUpload[] CaptureStagedUploads()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_stagedOffsets.Count == 0)
            return [];
        uint[] offsets = [.. _stagedOffsets];
        Array.Sort(offsets);
        var uploads = new ClusterStagedUpload[offsets.Length];
        for (int index = 0; index < offsets.Length; index++)
        {
            uint offset = offsets[index];
            uploads[index] = new ClusterStagedUpload(offset, _pages[offset]);
        }
        return uploads;
    }

    public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (ref readonly ClusterStagedUpload upload in uploads)
        {
            uint offset = checked((uint)upload.Offset);
            if (!_pages.TryGetValue(offset, out byte[]? bytes) ||
                bytes.Length != upload.Bytes.Length ||
                !_stagedOffsets.Contains(offset))
            {
                throw new InvalidOperationException(
                    $"Page-heap staging range [{upload.Offset}, {checked(upload.Offset + (ulong)upload.Bytes.Length)}) is stale.");
            }
        }
        foreach (ref readonly ClusterStagedUpload upload in uploads)
        {
            uint offset = checked((uint)upload.Offset);
            _stagedOffsets.Remove(offset);
            _pages.Remove(offset);
        }
    }

    public void Release(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.Remove(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Page-heap range [{offset}, {checked(offset + (uint)length)}) is not owned by this storage.");
        _stagedOffsets.Remove(offset);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pages.Clear();
            _stagedOffsets.Clear();
        }
    }
}

internal interface IClusterBvhStorage : IDisposable
{
    Memory<byte> Allocate(ulong offset, int length);
    Memory<byte> GetRange(ulong offset, int length);
    void Stage(ulong offset, int length);
    ClusterStagedUpload[] CaptureStagedUploads();
    void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads);
    void Release(ulong offset, int length);
}

internal sealed class SparseClusterBvhStorage : IClusterBvhStorage
{
    private readonly ulong _capacity;
    private readonly SortedDictionary<ulong, byte[]> _blocks = [];
    private readonly List<(ulong Offset, int Length)> _stagedRanges = [];
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

    public void Stage(ulong offset, int length)
    {
        _ = GetRange(offset, length);
        _stagedRanges.Add((offset, length));
    }

    public ClusterStagedUpload[] CaptureStagedUploads()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var uploads = new ClusterStagedUpload[_stagedRanges.Count];
        for (int index = 0; index < uploads.Length; index++)
        {
            (ulong offset, int length) = _stagedRanges[index];
            uploads[index] = new ClusterStagedUpload(offset, GetRange(offset, length));
        }
        return uploads;
    }

    public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (uploads.Length > _stagedRanges.Count)
            throw new InvalidOperationException("The BVH staging completion is ahead of pending uploads.");
        for (int index = 0; index < uploads.Length; index++)
        {
            (ulong offset, int length) = _stagedRanges[index];
            if (uploads[index].Offset != offset || uploads[index].Bytes.Length != length)
                throw new InvalidOperationException("The BVH staging completion is stale.");
        }
        _stagedRanges.RemoveRange(0, uploads.Length);
    }

    public void Release(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_blocks.Remove(offset, out byte[]? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Global BVH range at {offset} is not owned by this storage.");
        _stagedRanges.RemoveAll(range =>
            range.Offset >= offset &&
            checked(range.Offset + (uint)range.Length) <= checked(offset + (uint)length));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _blocks.Clear();
            _stagedRanges.Clear();
        }
    }
}
