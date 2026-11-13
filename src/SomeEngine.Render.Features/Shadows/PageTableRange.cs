namespace SomeEngine.Render.Features.Shadows;

/// <summary>A light projection's contiguous page-table entries and reuse generation.</summary>
public readonly record struct PageTableRange(
    uint RangeId,
    uint Offset,
    uint EntryCount,
    uint RangeGeneration);
