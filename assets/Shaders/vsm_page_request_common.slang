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
RWStructuredBuffer<uint2> PageReceiverMasks;

void PublishVirtualShadowRequest(
    uint tableIndex,
    uint flags,
    uint frameIndex,
    uint pageTableEntryCount)
{
    if (tableIndex >= pageTableEntryCount)
        return;

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

void MarkVirtualShadowReceiverMask(
    uint tableIndex,
    uint2 virtualPixel,
    uint pageSize)
{
    uint maskShift = firstbithigh(pageSize) - 3u;
    uint2 pagePixel = virtualPixel & (pageSize - 1u);
    uint2 cell = min(pagePixel >> maskShift, 7u.xx);
    uint bit = cell.y * 8u + cell.x;
    uint4 matchingCells = WaveMatchingLanes(tableIndex) & WaveMatchingLanes(bit);
    if (WaveGetLaneIndex() != WaveMaskFirstLane(matchingCells))
        return;
    uint ignored;
    if (bit < 32u)
        InterlockedOr(PageReceiverMasks[tableIndex].x, 1u << bit, ignored);
    else
        InterlockedOr(PageReceiverMasks[tableIndex].y, 1u << (bit - 32u), ignored);
}

void MarkVirtualShadowFullReceiverMask(uint tableIndex)
{
    PageReceiverMasks[tableIndex] = 0xFFFFFFFFu.xx;
}

#endif
