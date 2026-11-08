#ifndef VSM_PAGE_REQUEST_COMMON_SLANG
#define VSM_PAGE_REQUEST_COMMON_SLANG

#include "resource_effects.slang"
#include "wave_aggregation.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> RequestedPageFlags;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedPageIndices;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> RequestedPageCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMasks;

void PublishVirtualShadowRequest(
    uint tableIndex,
    uint flags,
    uint frameIndex,
    uint pageTableEntryCount)
{
    if (tableIndex >= pageTableEntryCount)
        return;
    if ((flags & VSM_PAGE_REQUEST_SECONDARY) == 0u) flags |= VSM_PAGE_REQUEST_PRIMARY;

    // Receiver pixels in a wave frequently request the same page. Only one
    // lane needs to publish each identical request; other lanes still mark
    // their receiver cells after this function returns.
    uint4 matchingRequests = WaveMatchingLanes(tableIndex) & WaveMatchingLanes(flags);
    if (WaveGetLaneIndex() != WaveMaskFirstLane(matchingRequests))
        return;

    uint previous;
    InterlockedOr(RequestedPageFlags[tableIndex], flags, previous);
    if (previous != 0u) return;

    uint outputIndex;
    InterlockedAdd(RequestedPageCount[0], 1u, outputIndex);
    if (outputIndex < pageTableEntryCount)
        RequestedPageIndices[outputIndex] = tableIndex;
}


void MarkVirtualShadowReceiverCell(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage, uint2 cell)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u) + uint2(0u, cell.y >> 2u);
    uint bit = (cell.y & 3u) * 8u + cell.x;
    uint4 matching = WaveMatchingLanes(address.x) & WaveMatchingLanes(address.y) & WaveMatchingLanes(bit);
    if (WaveGetLaneIndex() != WaveMaskFirstLane(matching)) return;
    uint ignored;
    InterlockedOr(PageReceiverMasks[address], 1u << bit, ignored);
}

void MarkVirtualShadowFullReceiverMask(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    PageReceiverMasks[address] = 0xffffffffu;
    PageReceiverMasks[address + uint2(0, 1)] = 0xffffffffu;
}

void MarkVirtualShadowReceiverRectangle(VirtualShadowMapParameters view, uint mipLevel,
    uint2 localPage, uint2 firstPixel, uint2 lastPixel)
{
    uint2 pageOrigin = localPage * view.PageSize;
    uint shift = firstbithigh(view.PageSize) - 3u;
    uint2 firstCell = (max(firstPixel, pageOrigin) - pageOrigin) >> shift;
    uint2 lastCell = (min(lastPixel, pageOrigin + view.PageSize - 1u) - pageOrigin) >> shift;
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    for (uint word = firstCell.y >> 2u; word <= lastCell.y >> 2u; word++)
    {
        uint2 low = uint2(firstCell.x, max(firstCell.y, word * 4u) - word * 4u);
        uint2 high = uint2(lastCell.x, min(lastCell.y, word * 4u + 3u) - word * 4u);
        uint mask = VirtualShadowReceiverWordRectangle(low, high);
        uint2 texel = address + uint2(0u, word);
        uint4 matching = WaveMatchingLanes(texel.x) & WaveMatchingLanes(texel.y) & WaveMatchingLanes(mask);
        if (WaveGetLaneIndex() != WaveMaskFirstLane(matching)) continue;
        uint ignored;
        InterlockedOr(PageReceiverMasks[texel], mask, ignored);
    }
}

#endif
