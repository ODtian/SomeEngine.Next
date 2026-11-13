using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Stores submitted light ranges and recycles whole ranges after their light leaves.</summary>
public sealed class VirtualShadowPageTableRanges
{
    private readonly Dictionary<VirtualShadowLightKey, PageTableRange> _ranges = [];
    private readonly LinkedList<PageTableRange> _freeRanges = [];
    private uint _nextRangeId = 1;

    /// <summary>The submitted page table's entry high-water mark, including reusable ranges.</summary>
    public uint PageTableEntryCount { get; private set; }
    public uint RangeCount => _nextRangeId;

    public bool TryGetRange(VirtualShadowLightKey light, out PageTableRange range) => _ranges.TryGetValue(light, out range);

    /// <summary>
    /// Writes one range for each light into the first <paramref name="lights"/>.Length elements
    /// of <paramref name="ranges"/> and returns the required page-table entry count for this frame.
    /// Inputs contain distinct light keys; each key keeps its entry count throughout its active life.
    /// The caller owns the frame spans and uses the returned length to size the graph buffer.
    /// This operation reads submitted storage; Apply adopts the spans after submission succeeds.
    /// </summary>
    public uint WriteRanges(ReadOnlySpan<VirtualShadowLightKey> lights, ReadOnlySpan<uint> entryCounts,
        Span<PageTableRange> ranges)
    {
        uint entryCount = PageTableEntryCount;
        uint nextRangeId = _nextRangeId;
        for (int index = 0; index < lights.Length; index++)
        {
            if (_ranges.TryGetValue(lights[index], out PageTableRange existing))
            {
                ranges[index] = existing;
                continue;
            }

            LinkedListNode<PageTableRange>? available = _freeRanges.First;
            while (available is not null && (available.Value.EntryCount < entryCounts[index]
                || ContainsRange(ranges[..index], available.Value.RangeId)))
                available = available.Next;
            if (available is not null)
                ranges[index] = available.Value;
            else
            {
                ranges[index] = new(nextRangeId++, entryCount, entryCounts[index], 1);
                entryCount += entryCounts[index];
            }
        }
        return entryCount;
    }

    /// <summary>
    /// Adopts the frame's ranges from VirtualShadowFeature.OnFrameSubmitted.
    /// The feature supplies the same light keys and range values used to record that frame.
    /// Each departed light advances its range generation before the range enters the free chain.
    /// </summary>
    public void Apply(ReadOnlySpan<VirtualShadowLightKey> lights, ReadOnlySpan<PageTableRange> ranges)
    {
        foreach ((VirtualShadowLightKey light, PageTableRange range) in _ranges)
            if (lights.IndexOf(light) < 0)
            {
                _ranges.Remove(light);
                _freeRanges.AddLast(range with { RangeGeneration = range.RangeGeneration + 1 });
            }

        for (int index = 0; index < lights.Length; index++)
        {
            PageTableRange range = ranges[index];
            if (!_ranges.TryAdd(lights[index], range)) continue;
            LinkedListNode<PageTableRange>? available = _freeRanges.First;
            while (available is not null && available.Value.RangeId != range.RangeId)
                available = available.Next;
            if (available is not null) _freeRanges.Remove(available);
            _nextRangeId = Math.Max(_nextRangeId, range.RangeId + 1);
            PageTableEntryCount = Math.Max(PageTableEntryCount, range.Offset + range.EntryCount);
        }
    }

    internal void WriteViewRanges(ReadOnlySpan<PageTableRange> ranges, ReadOnlySpan<VirtualShadowView> views,
        Span<VirtualShadowRange> destination)
    {
        destination.Clear();
        destination[0] = new(0, 0, 0, uint.MaxValue);
        foreach (PageTableRange range in _ranges.Values)
            destination[(int)range.RangeId] = new(range.Offset, 0, range.RangeGeneration, uint.MaxValue);
        foreach (PageTableRange range in _freeRanges)
            destination[(int)range.RangeId] = new(range.Offset, 0, range.RangeGeneration, uint.MaxValue);
        for (int index = 0; index < ranges.Length; index++)
        {
            PageTableRange range = ranges[index];
            destination[(int)range.RangeId] = new(range.Offset, views[index].PageTableEntryCount,
                range.RangeGeneration, (uint)index);
        }
    }

    private static bool ContainsRange(ReadOnlySpan<PageTableRange> ranges, uint rangeId)
    {
        foreach (PageTableRange range in ranges)
            if (range.RangeId == rangeId) return true;
        return false;
    }
}
