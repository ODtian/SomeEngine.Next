using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Render.Cluster;

/// <summary>Coalescing byte ranges used by Cluster page and BVH buffers.</summary>
internal sealed class ClusterBufferAllocator
{
    private readonly List<FreeRange> _free = [];
    private readonly uint _alignment;

    internal ClusterBufferAllocator(ulong capacity, uint alignment)
    {
        Capacity = capacity;
        _alignment = alignment;
        _free.Add(new(0, capacity));
    }

    internal ulong Capacity { get; }
    internal int FreeRangeCount => _free.Count;
    internal ulong FreeBytes
    {
        get
        {
            ulong total = 0;
            foreach (FreeRange range in _free) total += range.Size;
            return total;
        }
    }

    internal ulong LargestFreeRange
    {
        get
        {
            ulong largest = 0;
            foreach (FreeRange range in _free) largest = Math.Max(largest, range.Size);
            return largest;
        }
    }

    internal ulong AllocationSize(ulong size) => checked(size + _alignment - 1) & ~((ulong)_alignment - 1);
    internal void ReserveFrees(int count) => _free.EnsureCapacity(checked(_free.Count + count));

    internal bool TryAllocate(ulong size, out ulong offset)
    {
        ulong aligned = AllocationSize(size);
        for (int index = 0; index < _free.Count; index++)
        {
            FreeRange range = _free[index];
            if (range.Size < aligned) continue;
            offset = range.Offset;
            if (range.Size == aligned) _free.RemoveAt(index);
            else _free[index] = new(range.Offset + aligned, range.Size - aligned);
            return true;
        }
        offset = 0;
        return false;
    }

    [Conditional("DEBUG")]
    internal void ValidateFree(ulong offset, ulong size)
    {
        DevelopmentCheck.Require((offset & (_alignment - 1)) == 0, "The released Cluster range must be aligned.");
        DevelopmentCheck.Require(size != 0, "The released Cluster range must contain bytes.");
        ulong end = checked(offset + AllocationSize(size));
        DevelopmentCheck.Require(end <= Capacity, "The released Cluster range must fit its buffer.");
        foreach (FreeRange range in _free)
            DevelopmentCheck.Require(offset >= range.Offset + range.Size || range.Offset >= end,
                "The released Cluster range overlaps free memory.");
    }

    internal void Free(ulong offset, ulong size)
    {
        ValidateFree(offset, size);
        var range = new FreeRange(offset, AllocationSize(size));
        int index = 0;
        while (index < _free.Count && _free[index].Offset < offset) index++;
        _free.Insert(index, range);
        if (index > 0 && _free[index - 1].Offset + _free[index - 1].Size == range.Offset)
        {
            FreeRange previous = _free[index - 1];
            range = new(previous.Offset, previous.Size + range.Size);
            _free[--index] = range;
            _free.RemoveAt(index + 1);
        }
        if (index + 1 < _free.Count && range.Offset + range.Size == _free[index + 1].Offset)
        {
            _free[index] = new(range.Offset, range.Size + _free[index + 1].Size);
            _free.RemoveAt(index + 1);
        }
    }

    internal void Reset()
    {
        _free.Clear();
        _free.Add(new(0, Capacity));
    }

    private readonly record struct FreeRange(ulong Offset, ulong Size);
}
