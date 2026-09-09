#include "vsm_sampling.slang"

struct VirtualShadowPageAllocationUniforms
{
    uint PageTableEntryCount;
    uint MaxPhysicalPages;
    uint FinePageTableEntryCount;
    uint VirtualPagesPerView;
    uint CoarsePagesPerView;
    uint ViewCount;
    uint FrameIndex;
    uint EvictionBucketCount;
};

ConstantBuffer<VirtualShadowPageAllocationUniforms> PageAllocation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<uint> RequestedShadowPages;
RWStructuredBuffer<uint4> ShadowPageTable;
RWStructuredBuffer<uint> PhysicalPageOwners;
RWStructuredBuffer<uint4> PageMetadata;
RWStructuredBuffer<uint> FreePages;
RWStructuredBuffer<uint> FreePageCount;
RWStructuredBuffer<uint> EvictionPages;
RWStructuredBuffer<uint> EvictionBucketCounts;
RWStructuredBuffer<uint2> ActivePhysicalPages;
RWStructuredBuffer<uint> ActivePhysicalPageCount;
RWStructuredBuffer<uint> ShadowAllocationFailures;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;
// One bounded counter reservation per active wave. Each lane receives a distinct
// stack index; a partly depleted stack serves its prefix before trying eviction.
bool ReservePages(RWStructuredBuffer<uint> counts, uint counterIndex, out uint index)
{
    uint requested = WaveActiveCountBits(true);
    uint taken = 0u;
    uint baseIndex = 0u;
    if (WaveIsFirstLane())
    {
        uint observed;
        InterlockedAdd(counts[counterIndex], 0u, observed);
        while (observed > 0u)
        {
            uint latest;
            taken = min(observed, requested);
            InterlockedCompareExchange(counts[counterIndex], observed, observed - taken, latest);
            if (latest == observed)
            {
                baseIndex = observed - taken;
                break;
            }
            observed = latest;
            taken = 0u;
        }
    }
    taken = WaveReadLaneFirst(taken);
    index = WaveReadLaneFirst(baseIndex) + WavePrefixCountBits(true);
    return WavePrefixCountBits(true) < taken;
}

bool PopEvictionPage(out uint page)
{
    for (int bucket = int(PageAllocation.EvictionBucketCount) - 1; bucket >= 0; bucket--)
    {
        uint local;
        if (ReservePages(EvictionBucketCounts, uint(bucket), local))
        {
            page = EvictionPages[uint(bucket) * PageAllocation.MaxPhysicalPages + local];
            return true;
        }
    }
    page = 0u;
    return false;
}

void ReleaseOldMapping(uint atlasPage)
{
    uint oldOwner = PhysicalPageOwners[atlasPage];
    if (oldOwner < PageAllocation.PageTableEntryCount)
    {
        uint expected = PackVirtualShadowPage(atlasPage + 1u);
        uint ignored;
        InterlockedCompareExchange(
            ShadowPageTable[oldOwner].x,
            expected,
            VSM_PAGE_UNMAPPED,
            ignored);
    }
    PhysicalPageOwners[atlasPage] = VSM_UNOWNED_PAGE;
}

bool AcquireAtlasPage(uint newTableIndex, out uint atlasPage)
{
    uint freeIndex;
    if (ReservePages(FreePageCount, 0u, freeIndex))
    {
        atlasPage = FreePages[freeIndex];
        PhysicalPageOwners[atlasPage] = newTableIndex;
        return true;
    }
    if (!PopEvictionPage(atlasPage))
        return false;
    ReleaseOldMapping(atlasPage);
    PhysicalPageOwners[atlasPage] = newTableIndex;
    return true;
}

uint ResolveViewIndex(uint tableIndex)
{
    if (tableIndex < PageAllocation.FinePageTableEntryCount)
        return tableIndex / PageAllocation.VirtualPagesPerView;
    uint coarseIndex = tableIndex - PageAllocation.FinePageTableEntryCount;
    return coarseIndex / PageAllocation.CoarsePagesPerView;
}

bool IsCoarseTableIndex(uint tableIndex)
{
    return tableIndex >= PageAllocation.FinePageTableEntryCount;
}

int2 ResolveExpectedFineWorldPage(
    uint tableIndex,
    VirtualShadowMapParameters view)
{
    uint dimension = VirtualShadowPagesPerRow(view);
    uint mask = dimension - 1u;
    uint localIndex = tableIndex - view.PageTableOffset;
    uint2 slot = uint2(localIndex % dimension, localIndex / dimension);
    uint2 originSlot = uint2(
        asuint(view.VirtualPageOriginX) & mask,
        asuint(view.VirtualPageOriginY) & mask);
    uint2 localPage = (slot - originSlot) & mask;
    return int2(view.VirtualPageOriginX, view.VirtualPageOriginY) + int2(localPage);
}

int2 ResolveExpectedCoarseWorldPage(
    uint tableIndex,
    VirtualShadowMapParameters view)
{
    uint dimension = VirtualShadowCoarsePagesPerRow(view);
    uint mask = dimension - 1u;
    uint localIndex = tableIndex - view.CoarsePageTableOffset;
    uint2 slot = uint2(localIndex % dimension, localIndex / dimension);
    int scale = int(1u << view.CoarsePageShift);
    int2 origin = int2(
        VirtualShadowFloorDiv(view.VirtualPageOriginX, scale),
        VirtualShadowFloorDiv(view.VirtualPageOriginY, scale));
    uint2 originSlot = uint2(asuint(origin.x) & mask, asuint(origin.y) & mask);
    uint2 localPage = (slot - originSlot) & mask;
    return origin + int2(localPage);
}

void PublishDirtyPage(
    uint tableIndex,
    uint viewIndex,
    uint atlasPage)
{
    uint activeIndex = 0u;
    uint waveCount = WaveActiveCountBits(true);
    if (WaveIsFirstLane())
        InterlockedAdd(ActivePhysicalPageCount[0], waveCount, activeIndex);
    activeIndex = WaveReadLaneFirst(activeIndex) + WavePrefixCountBits(true);
    if (activeIndex < PageAllocation.MaxPhysicalPages)
    {
        ActivePhysicalPages[activeIndex] = uint2(atlasPage, tableIndex);

    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void AllocateVirtualShadowPages(uint3 threadId : SV_DispatchThreadID)
{
    uint tableIndex = threadId.x;
    if (tableIndex >= PageAllocation.PageTableEntryCount ||
        RequestedShadowPages[tableIndex] != PageAllocation.FrameIndex)
        return;

    uint viewIndex = ResolveViewIndex(tableIndex);
    if (viewIndex >= PageAllocation.ViewCount)
        return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    bool coarse = IsCoarseTableIndex(tableIndex);
    int2 expectedWorldPage = coarse
        ? ResolveExpectedCoarseWorldPage(tableIndex, view)
        : ResolveExpectedFineWorldPage(tableIndex, view);

    uint4 entry = ShadowPageTable[tableIndex];
    bool sameTag = entry.x != VSM_PAGE_UNMAPPED &&
        entry.x != VSM_PAGE_LOCKED &&
        IsVirtualShadowPageTag(entry, expectedWorldPage);
    uint atlasPage = 0u;
    uint packedPage = VSM_PAGE_UNMAPPED;
    if (sameTag)
    {
        if (!DecodeVirtualShadowPage(entry.x, atlasPage))
            return;
        packedPage = entry.x;
        PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
        if (entry.w == view.CacheGeneration)
            return;
    }
    else if (entry.x != VSM_PAGE_UNMAPPED && entry.x != VSM_PAGE_LOCKED)
    {
        if (!DecodeVirtualShadowPage(entry.x, atlasPage))
            return;
        packedPage = entry.x;
        PhysicalPageOwners[atlasPage] = tableIndex;
    }
    else
    {
        if (!AcquireAtlasPage(tableIndex, atlasPage))
        {
            uint failureIndex = coarse ? 0u : 1u;
            uint ignored;
            InterlockedAdd(ShadowAllocationFailures[failureIndex], 1u, ignored);
            return;
        }
        packedPage = PackVirtualShadowPage(atlasPage + 1u);
    }

    ShadowPageTable[tableIndex] = uint4(
        packedPage,
        asuint(expectedWorldPage.x),
        asuint(expectedWorldPage.y),
        view.CacheGeneration);
    PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
    PageMetadata[atlasPage].y = view.CacheGeneration;
    PublishDirtyPage(
        tableIndex,
        viewIndex,
        atlasPage);
}
