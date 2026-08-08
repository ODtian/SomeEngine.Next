namespace SomeEngine.Render.Cluster;

internal sealed class PageHeap
{
    public const uint CapacityBytes = 64 * 1024 * 1024;
    private readonly List<FreeBlock> _freeBlocks = [];
    private readonly uint _capacityBytes;

    public PageHeap(uint capacityBytes = CapacityBytes)
    {
        if (capacityBytes == 0 || (capacityBytes & 15) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Page heap capacity must be positive and 16-byte aligned.");

        _capacityBytes = capacityBytes;
        _freeBlocks.Add(new FreeBlock(0, capacityBytes));
    }

    public uint FreeBytes
    {
        get
        {
            uint total = 0;
            for (int i = 0; i < _freeBlocks.Count; i++)
                total += _freeBlocks[i].Size;

            return total;
        }
    }

    public uint UsedBytes => _capacityBytes - FreeBytes;
    public uint Capacity => _capacityBytes;
    public int FreeBlockCount => _freeBlocks.Count;

    public void ReserveFrees(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        _freeBlocks.EnsureCapacity(checked(_freeBlocks.Count + count));
    }

    public void ValidateFrees(ReadOnlySpan<PageRetirement> retirements)
    {
        for (int index = 0; index < retirements.Length; index++)
        {
            PageRetirement retirement = retirements[index];
            ValidateFreeRange(retirement.Offset, retirement.Size, out ulong end);
            foreach (FreeBlock free in _freeBlocks)
            {
                ulong freeEnd = (ulong)free.Offset + free.Size;
                if (retirement.Offset < freeEnd && free.Offset < end)
                    throw new InvalidOperationException("The released page range overlaps free heap memory.");
            }

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
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        return Align(size);
    }

    public bool CanFit(uint size)
        => size != 0 && AllocationSize(size) <= _capacityBytes;

    public bool TryAlloc(uint size, out uint offset)
    {
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
        uint alignedSize = AllocationSize(size);
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            FreeBlock block = _freeBlocks[i];
            if (block.Size < alignedSize)
                continue;

            offset = block.Offset;
            if (block.Size == alignedSize)
            {
                _freeBlocks.RemoveAt(i);
            }
            else
            {
                _freeBlocks[i] = new FreeBlock(
                    block.Offset + alignedSize,
                    block.Size - alignedSize);
            }

            return true;
        }

        offset = 0;
        return false;
    }

    public void Free(uint offset, uint size)
    {
        ValidateFreeRange(offset, size, out ulong end);
        uint alignedSize = AllocationSize(size);

        var block = new FreeBlock(offset, alignedSize);
        int index = 0;
        while (index < _freeBlocks.Count && _freeBlocks[index].Offset < block.Offset)
            index++;

        if (index > 0)
        {
            FreeBlock previous = _freeBlocks[index - 1];
            if ((ulong)previous.Offset + previous.Size > block.Offset)
                throw new InvalidOperationException("The released page range overlaps free heap memory.");
        }
        if (index < _freeBlocks.Count && end > _freeBlocks[index].Offset)
            throw new InvalidOperationException("The released page range overlaps free heap memory.");

        _freeBlocks.Insert(index, block);
        MergeAt(index);
    }

    private void ValidateFreeRange(uint offset, uint size, out ulong end)
    {
        if ((offset & 15) != 0)
            throw new ArgumentException("Page heap offsets must be 16-byte aligned.", nameof(offset));
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        uint alignedSize = AllocationSize(size);
        end = checked((ulong)offset + alignedSize);
        if (end > _capacityBytes)
            throw new ArgumentOutOfRangeException(nameof(size), "The released page range is outside the heap.");
    }

    public uint Largest()
    {
        uint largest = 0;
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            if (_freeBlocks[i].Size > largest)
                largest = _freeBlocks[i].Size;
        }

        return largest;
    }

    public void Reset()
    {
        _freeBlocks.Clear();
        _freeBlocks.Add(new FreeBlock(0, _capacityBytes));
    }

    private static uint Align(uint size)
    {
        ulong aligned = ((ulong)size + 15) & ~15ul;
        if (aligned > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size), "Page allocation size exceeds the 32-bit heap address space.");
        return (uint)aligned;
    }

    private void MergeAt(int index)
    {
        if (index > 0)
        {
            FreeBlock prev = _freeBlocks[index - 1];
            FreeBlock cur = _freeBlocks[index];
            if (prev.Offset + prev.Size == cur.Offset)
            {
                _freeBlocks[index - 1] = new FreeBlock(prev.Offset, prev.Size + cur.Size);
                _freeBlocks.RemoveAt(index);
                index--;
            }
        }

        if (index + 1 < _freeBlocks.Count)
        {
            FreeBlock cur = _freeBlocks[index];
            FreeBlock next = _freeBlocks[index + 1];
            if (cur.Offset + cur.Size == next.Offset)
            {
                _freeBlocks[index] = new FreeBlock(cur.Offset, cur.Size + next.Size);
                _freeBlocks.RemoveAt(index + 1);
            }
        }
    }

    private readonly record struct FreeBlock(uint Offset, uint Size);
}


