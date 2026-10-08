#ifndef VSM_PAGE_REQUEST_COMMON_SLANG
#define VSM_PAGE_REQUEST_COMMON_SLANG

#include "resource_effects.slang"
#include "wave_aggregation.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> RequestedShadowPages;
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
    InterlockedExchange(RequestedShadowPages[tableIndex], frameIndex, previous);
    if (previous == frameIndex)
    {
        uint ignored;
        InterlockedOr(RequestedPageFlags[tableIndex], flags, ignored);
        return;
    }

    RequestedPageFlags[tableIndex] = flags;
    uint outputIndex;
    InterlockedAdd(RequestedPageCount[0], 1u, outputIndex);
    if (outputIndex < pageTableEntryCount)
        RequestedPageIndices[outputIndex] = tableIndex;
}


void MarkVirtualShadowReceiverCell(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage, uint2 cell)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u + (cell >> 2u);
    uint bit = (cell.y & 3u) * 4u + (cell.x & 3u);
    uint4 matching = WaveMatchingLanes(address.x) & WaveMatchingLanes(address.y) & WaveMatchingLanes(bit);
    if (WaveGetLaneIndex() != WaveMaskFirstLane(matching)) return;
    uint ignored;
    InterlockedOr(PageReceiverMasks[address], 1u << bit, ignored);
}

void MarkVirtualShadowFullReceiverMask(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    PageReceiverMasks[address] = 0xffffu;
    PageReceiverMasks[address + uint2(1, 0)] = 0xffffu;
    PageReceiverMasks[address + uint2(0, 1)] = 0xffffu;
    PageReceiverMasks[address + 1u.xx] = 0xffffu;
}

void MarkVirtualShadowReceiverRectangle(VirtualShadowMapParameters view, uint mipLevel,
    uint2 localPage, uint2 firstPixel, uint2 lastPixel)
{
    uint2 pageOrigin = localPage * view.PageSize;
    uint shift = firstbithigh(view.PageSize) - 3u;
    uint2 firstCell = (max(firstPixel, pageOrigin) - pageOrigin) >> shift;
    uint2 lastCell = (min(lastPixel, pageOrigin + view.PageSize - 1u) - pageOrigin) >> shift;
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    for (uint y = firstCell.y >> 2u; y <= lastCell.y >> 2u; y++)
    for (uint x = firstCell.x >> 2u; x <= lastCell.x >> 2u; x++)
    {
        uint2 low = max(firstCell, uint2(x, y) * 4u) - uint2(x, y) * 4u;
        uint2 high = min(lastCell, uint2(x, y) * 4u + 3u) - uint2(x, y) * 4u;
        uint columns = (0xfu << low.x) & (0xfu >> (3u - high.x));
        uint rows = (0xffffu << (low.y * 4u)) & (0xffffu >> ((3u - high.y) * 4u));
        uint mask = (columns * 0x1111u) & rows;
        uint2 texel = address + uint2(x, y);
        uint4 matching = WaveMatchingLanes(texel.x) & WaveMatchingLanes(texel.y) & WaveMatchingLanes(mask);
        if (WaveGetLaneIndex() != WaveMaskFirstLane(matching)) continue;
        uint ignored;
        InterlockedOr(PageReceiverMasks[texel], mask, ignored);
    }
}

#endif
