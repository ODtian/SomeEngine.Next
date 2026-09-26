#ifndef VSM_PAGE_REQUEST_COMMON_SLANG
#define VSM_PAGE_REQUEST_COMMON_SLANG

#include "resource_effects.slang"

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
