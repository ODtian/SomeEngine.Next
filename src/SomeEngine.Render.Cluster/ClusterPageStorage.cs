using System.Buffers;
using System.Runtime.InteropServices;

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

/// <summary>Holds CPU page bytes until their upload has been submitted.</summary>
internal sealed class SparseClusterPageStorage : IClusterPageStorage
{
    private readonly uint _capacity;
    private readonly Dictionary<uint, ClusterPageBytes> _pages = [];
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
        var bytes = new ClusterPageBytes(length);
        try
        {
            if (!_pages.TryAdd(offset, bytes))
                throw new InvalidOperationException($"Page-heap offset {offset} already owns staging storage.");
            return bytes.Memory;
        }
        catch
        {
            bytes.Release();
            throw;
        }
    }

    public void Stage(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.TryGetValue(offset, out ClusterPageBytes? bytes) || bytes.Length != length)
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
            uploads[index] = new ClusterStagedUpload(offset, _pages[offset].Memory);
        }
        return uploads;
    }

    public void CompleteStagedUploads(ReadOnlySpan<ClusterStagedUpload> uploads)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (ref readonly ClusterStagedUpload upload in uploads)
        {
            uint offset = checked((uint)upload.Offset);
            if (!_pages.TryGetValue(offset, out ClusterPageBytes? bytes) ||
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
            _pages.Remove(offset, out ClusterPageBytes? bytes);
            bytes!.Release();
        }
    }

    public void Release(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_pages.TryGetValue(offset, out ClusterPageBytes? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Page-heap range [{offset}, {checked(offset + (uint)length)}) is not owned by this storage.");
        _pages.Remove(offset);
        _stagedOffsets.Remove(offset);
        bytes.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (ClusterPageBytes bytes in _pages.Values) bytes.Release();
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
    private readonly SortedDictionary<ulong, ClusterPageBytes> _blocks = [];
    private readonly List<(ulong Offset, int Length)> _stagedRanges = [];
    private int _disposed;

    internal SparseClusterBvhStorage(ulong capacity) => _capacity = capacity;

    public Memory<byte> Allocate(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (checked(offset + (uint)length) > _capacity)
            throw new InvalidOperationException("Global Cluster BVH storage capacity is exhausted.");
        var bytes = new ClusterPageBytes(length);
        try
        {
            if (!_blocks.TryAdd(offset, bytes))
                throw new InvalidOperationException($"Global BVH offset {offset} is already allocated.");
            return bytes.Memory;
        }
        catch
        {
            bytes.Release();
            throw;
        }
    }

    public Memory<byte> GetRange(ulong offset, int length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach ((ulong start, ClusterPageBytes bytes) in _blocks)
        {
            ulong end = checked(start + (uint)bytes.Length);
            if (offset >= start && checked(offset + (uint)length) <= end)
                return bytes.Memory.Slice(checked((int)(offset - start)), length);
        }
        throw new InvalidOperationException($"Global BVH range [{offset}, {checked(offset + (uint)length)}) is not allocated.");
    }

    public void Stage(ulong offset, int length)
    {
        _ = GetRange(offset, length);
        // Registration and page changes can touch the same mesh before one
        // upload. A containing range already carries its updated bytes.
        foreach ((ulong start, int byteCount) in _stagedRanges)
            if (offset >= start && offset + (uint)length <= start + (uint)byteCount)
                return;
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
        if (!_blocks.TryGetValue(offset, out ClusterPageBytes? bytes) || bytes.Length != length)
            throw new InvalidOperationException($"Global BVH range at {offset} is not owned by this storage.");
        _blocks.Remove(offset);
        _stagedRanges.RemoveAll(range =>
            range.Offset >= offset &&
            checked(range.Offset + (uint)range.Length) <= checked(offset + (uint)length));
        bytes.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (ClusterPageBytes bytes in _blocks.Values) bytes.Release();
            _blocks.Clear();
            _stagedRanges.Clear();
        }
    }
}

/// <summary>Page bytes whose address remains valid through asynchronous reads and CPU uploads.</summary>
internal sealed unsafe class ClusterPageBytes : MemoryManager<byte>
{
    private nint _address;
    internal int Length { get; }

    internal ClusterPageBytes(int length)
    {
        Length = length;
        _address = (nint)NativeMemory.Alloc(checked((nuint)length));
        if (_address == 0) throw new OutOfMemoryException();
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_address == 0, this);
        return new Span<byte>((void*)_address, Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_address == 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)Length);
        return new MemoryHandle((byte*)_address + elementIndex);
    }

    public override void Unpin() { }

    internal void Release() => ((IDisposable)this).Dispose();

    protected override void Dispose(bool disposing)
        => NativeMemory.Free((void*)Interlocked.Exchange(ref _address, 0));
}
