namespace SomeEngine.Render.Cluster;

internal sealed class PageHeap
{
    public const uint CapacityBytes = 64 * 1024 * 1024;
    public const uint Alignment = 512;
    private readonly ClusterBufferAllocator _ranges;

    public PageHeap(uint capacityBytes = CapacityBytes)
    {
        if (capacityBytes == 0 || capacityBytes % Alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Page heap capacity must be positive and 512-byte aligned.");
        _ranges = new(capacityBytes, Alignment);
    }

    public uint FreeBytes => checked((uint)_ranges.FreeBytes);
    public uint UsedBytes => Capacity - FreeBytes;
    public uint Capacity => checked((uint)_ranges.Capacity);
    public int FreeBlockCount => _ranges.FreeRangeCount;

    public ClusterHeapSnapshot GetStatistics()
    {
        var statistics = _ranges.GetStatistics();
        uint free = checked((uint)statistics.FreeBytes);
        return new(Capacity, Capacity - free, free,
            checked((uint)statistics.LargestFreeRange), statistics.FreeRangeCount);
    }

    public void ReserveFrees(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _ranges.ReserveFrees(count);
    }

    public void ValidateFrees(ReadOnlySpan<PageRetirement> retirements)
    {
        for (int index = 0; index < retirements.Length; index++)
        {
            PageRetirement retirement = retirements[index];
            _ranges.ValidateFree(retirement.Offset, retirement.Size);
            ulong end = checked((ulong)retirement.Offset + AllocationSize(retirement.Size));
            for (int previous = 0; previous < index; previous++)
            {
                PageRetirement other = retirements[previous];
                ulong otherEnd = checked((ulong)other.Offset + AllocationSize(other.Size));
                if (retirement.Offset < otherEnd && other.Offset < end)
                    throw new InvalidOperationException("The publication contains overlapping page retirements.");
            }
        }
    }

    public static uint AllocationSize(uint size)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        return checked((uint)(((ulong)size + Alignment - 1) / Alignment * Alignment));
    }

    public bool CanFit(uint size) => size != 0 && AllocationSize(size) <= Capacity;

    public bool TryAlloc(uint size, out uint offset)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        bool allocated = _ranges.TryAllocate(size, out ulong address);
        offset = checked((uint)address);
        return allocated;
    }

    public void Free(uint offset, uint size) => _ranges.Free(offset, size);
    public uint Largest() => checked((uint)_ranges.LargestFreeRange);
    public void Reset() => _ranges.Reset();
}
