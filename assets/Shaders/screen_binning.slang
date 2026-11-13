#ifndef SCREEN_BINNING_SLANG
#define SCREEN_BINNING_SLANG

__exported import resource_effects;

struct ScreenBinningBounds
{
    uint3 Minimum;
    uint3 Size;
};

interface IScreenBinningCoverage
{
    static ScreenBinningBounds GetBounds(uint itemIndex);
    static bool Intersects(uint itemIndex, uint3 cell);
};

interface IScreenBinningItem : IScreenBinningCoverage
{
    static uint GetCellIndex(uint itemIndex, uint3 cell);
    static uint GetItemIndex(uint itemIndex);
};

interface IScreenBinningOutput
{
    static void Write(uint itemIndex, uint3 cell);
};

struct ScreenBinningParameters
{
    uint ItemCount;
    uint ItemCapacity;
    uint CellCount;
    uint ClearThreadCount;
    uint MaxItemsPerCell;
    uint3 Padding;
};

ConstantBuffer<ScreenBinningParameters> ScreenBinningUniforms;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScreenBinningItemOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScreenBinningCellCounts;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScreenBinningCellOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> ScreenBinningCells;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScreenBinningIndices;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScreenBinningDiagnostics;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScreenBinningDispatch;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScreenBinningScanValues;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScreenBinningScanSums;

uint ScreenBinningThreadIndex(uint3 dispatchThreadId)
{
    return dispatchThreadId.x + dispatchThreadId.y * 65535u * 256u;
}

void ScreenBinningCountCoverage<TItem : IScreenBinningCoverage>(uint index)
{
    if (index <= ScreenBinningUniforms.ItemCapacity)
    {
        uint count = 0u;
        if (index < ScreenBinningUniforms.ItemCount)
        {
            ScreenBinningBounds bounds = TItem.GetBounds(index);
            count = bounds.Size.x * bounds.Size.y * bounds.Size.z;
        }
        ScreenBinningItemOffsets[index] = count;
    }
}

void ScreenBinningCount<TItem : IScreenBinningItem>(uint index)
{
    ScreenBinningCountCoverage<TItem>(index);
    if (index < ScreenBinningUniforms.CellCount)
        ScreenBinningCellCounts[index] = 0u;
    uint listCapacity, listStride;
    ScreenBinningIndices.GetDimensions(listCapacity, listStride);
    for (uint entry = index; entry < listCapacity; entry += ScreenBinningUniforms.ClearThreadCount)
        ScreenBinningIndices[entry] = 0xffffffffu;
    if (index < 5u)
        ScreenBinningDiagnostics[index] = 0u;
}

groupshared uint ScreenBinningPrefix[256];

[shader("compute")]
[numthreads(256, 1, 1)]
void ScreenBinningScan(uint3 dispatchId : SV_DispatchThreadID,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint count, stride;
    ScreenBinningScanValues.GetDimensions(count, stride);
    uint index = ScreenBinningThreadIndex(dispatchId);
    uint value = index < count ? ScreenBinningScanValues[index] : 0u;
    ScreenBinningPrefix[lane] = value;
    GroupMemoryBarrierWithGroupSync();
    for (uint distance = 1u; distance < 256u; distance <<= 1u)
    {
        uint previous = lane >= distance ? ScreenBinningPrefix[lane - distance] : 0u;
        GroupMemoryBarrierWithGroupSync();
        ScreenBinningPrefix[lane] += previous;
        GroupMemoryBarrierWithGroupSync();
    }
    if (index < count)
        ScreenBinningScanValues[index] = ScreenBinningPrefix[lane] - value;
    if (lane == 255u)
        ScreenBinningScanSums[groupId.x + groupId.y * 65535u] = ScreenBinningPrefix[lane];
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ScreenBinningAddGroupPrefixes(uint3 dispatchId : SV_DispatchThreadID)
{
    uint count, stride;
    ScreenBinningScanValues.GetDimensions(count, stride);
    uint index = ScreenBinningThreadIndex(dispatchId);
    if (index < count)
        ScreenBinningScanValues[index] += ScreenBinningScanSums[index / 256u];
}

uint ScreenBinningWriteExpansionDispatch()
{
    uint count = ScreenBinningItemOffsets[ScreenBinningUniforms.ItemCapacity];
    uint groups = (count + 255u) / 256u;
    ScreenBinningDispatch[0] = min(groups, 65535u);
    ScreenBinningDispatch[1] = max((groups + 65534u) / 65535u, 1u);
    ScreenBinningDispatch[2] = 1u;
    return count;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void ScreenBinningFinishExpansion()
{
    ScreenBinningWriteExpansionDispatch();
}

[shader("compute")]
[numthreads(1, 1, 1)]
void ScreenBinningFinishItems()
{
    ScreenBinningDiagnostics[2] = ScreenBinningWriteExpansionDispatch();
}

bool ScreenBinningFindItem<TItem : IScreenBinningCoverage>(uint expandedIndex,
    out uint itemIndex, out uint3 cell)
{
    itemIndex = 0u;
    cell = 0u;
    if (expandedIndex >= ScreenBinningItemOffsets[ScreenBinningUniforms.ItemCapacity])
        return false;
    uint end = ScreenBinningUniforms.ItemCount;
    while (itemIndex < end)
    {
        uint middle = itemIndex + (end - itemIndex) / 2u;
        if (ScreenBinningItemOffsets[middle + 1u] <= expandedIndex)
            itemIndex = middle + 1u;
        else
            end = middle;
    }
    ScreenBinningBounds bounds = TItem.GetBounds(itemIndex);
    uint position = expandedIndex - ScreenBinningItemOffsets[itemIndex];
    cell = bounds.Minimum + uint3(position % bounds.Size.x,
        (position / bounds.Size.x) % bounds.Size.y,
        position / (bounds.Size.x * bounds.Size.y));
    return true;
}

void ScreenBinningExpand<TItem : IScreenBinningCoverage, TOutput : IScreenBinningOutput>(uint expandedIndex)
{
    uint itemIndex;
    uint3 cell;
    if (ScreenBinningFindItem<TItem>(expandedIndex, itemIndex, cell) && TItem.Intersects(itemIndex, cell))
        TOutput.Write(itemIndex, cell);
}

struct ScreenBinningCellCountOutput<TItem : IScreenBinningItem> : IScreenBinningOutput
{
    static void Write(uint itemIndex, uint3 cell)
    {
        InterlockedAdd(ScreenBinningCellCounts[TItem.GetCellIndex(itemIndex, cell)], 1u);
    }
};

void ScreenBinningExpand<TItem : IScreenBinningItem>(uint expandedIndex)
{
    ScreenBinningExpand<TItem, ScreenBinningCellCountOutput<TItem>>(expandedIndex);
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ScreenBinningScanCells(uint3 dispatchId : SV_DispatchThreadID)
{
    uint cellIndex = ScreenBinningThreadIndex(dispatchId);
    if (cellIndex <= ScreenBinningUniforms.CellCount)
        ScreenBinningCellOffsets[cellIndex] = cellIndex == ScreenBinningUniforms.CellCount ? 0u :
            min(ScreenBinningCellCounts[cellIndex], ScreenBinningUniforms.MaxItemsPerCell);
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ScreenBinningFinishCells(uint3 dispatchId : SV_DispatchThreadID)
{
    uint cellIndex = ScreenBinningThreadIndex(dispatchId);
    uint listCapacity, listStride;
    ScreenBinningIndices.GetDimensions(listCapacity, listStride);
    if (cellIndex < ScreenBinningUniforms.CellCount)
    {
        uint offset = ScreenBinningCellOffsets[cellIndex];
        uint count = ScreenBinningCellOffsets[cellIndex + 1u] - offset;
        bool capacityOverflow = count != 0u && offset + count > listCapacity;
        ScreenBinningCells[cellIndex] = uint2(offset, capacityOverflow ? 0u : count);
        uint end = offset + count;
        if (end <= listCapacity)
        {
            if (cellIndex + 1u == ScreenBinningUniforms.CellCount)
                ScreenBinningDiagnostics[4] = end;
            else if (ScreenBinningCellOffsets[cellIndex + 2u] > listCapacity)
                ScreenBinningDiagnostics[4] = end;
        }
        if (capacityOverflow || ScreenBinningCellCounts[cellIndex] > ScreenBinningUniforms.MaxItemsPerCell)
            InterlockedAdd(ScreenBinningDiagnostics[0], 1u);
    }
    if (cellIndex == 0u)
    {
        ScreenBinningDiagnostics[1] = ScreenBinningCellOffsets[ScreenBinningUniforms.CellCount];
        ScreenBinningDiagnostics[3] = listCapacity;
    }
}

void ScreenBinningFill<TItem : IScreenBinningItem>(uint expandedIndex)
{
    uint itemIndex;
    uint3 cell;
    if (!ScreenBinningFindItem<TItem>(expandedIndex, itemIndex, cell) || !TItem.Intersects(itemIndex, cell))
        return;
    uint2 range = ScreenBinningCells[TItem.GetCellIndex(itemIndex, cell)];
    uint value = TItem.GetItemIndex(itemIndex);
    for (uint ordinal = 0u; ordinal < range.y && value != 0xffffffffu; ordinal++)
    {
        uint previous;
        InterlockedMin(ScreenBinningIndices[range.x + ordinal], value, previous);
        value = max(value, previous);
    }
}

// Consumers retaining every item can reuse the completed cell counts as a reverse write counter.
void ScreenBinningFillUnordered<TItem : IScreenBinningItem>(uint expandedIndex)
{
    uint itemIndex;
    uint3 cell;
    if (!ScreenBinningFindItem<TItem>(expandedIndex, itemIndex, cell) || !TItem.Intersects(itemIndex, cell))
        return;
    uint cellIndex = TItem.GetCellIndex(itemIndex, cell);
    uint2 range = ScreenBinningCells[cellIndex];
    if (range.y == 0u) return;
    uint remaining;
    InterlockedAdd(ScreenBinningCellCounts[cellIndex], 0xffffffffu, remaining);
    ScreenBinningIndices[range.x + remaining - 1u] = TItem.GetItemIndex(itemIndex);
}

#endif
