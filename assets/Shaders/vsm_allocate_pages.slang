#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowPageAllocationUniforms
{
    uint PageTableEntryCount;
    uint PageTableEntriesPerView;
    uint MaxPhysicalPages;
    uint ViewCount;
    uint FrameIndex;
    uint EvictionBucketCount;
    uint HierarchyWordsPerView;
    uint MipLevelCount;
};

ConstantBuffer<VirtualShadowPageAllocationUniforms> PageAllocation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<uint> RequestedPageIndices;
StructuredBuffer<uint> RequestedPageCount;
StructuredBuffer<uint> RequestedPageFlags;
RWStructuredBuffer<uint4> ShadowPageTable;
RWStructuredBuffer<uint> PhysicalPageOwners;
RWStructuredBuffer<uint4> PageMetadata;
RWStructuredBuffer<uint> FreePages;
RWStructuredBuffer<uint> FreePageCount;
RWStructuredBuffer<uint> EvictionPages;
RWStructuredBuffer<uint> EvictionBucketCounts;
RWStructuredBuffer<uint4> ActivePhysicalPages;
RWStructuredBuffer<uint> ActivePhysicalPageCount;
RWStructuredBuffer<uint4> UncachedPageRectBounds;
RWStructuredBuffer<uint> RasterTargetPageHierarchy;
RWStructuredBuffer<uint> ShadowAllocationFailures;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

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

void PublishDirtyPage(
    uint tableIndex,
    uint viewIndex,
    uint mipLevel,
    uint2 localPage,
    uint atlasPage,
    uint previousLayer)
{
    uint activeIndex = 0u;
    uint waveCount = WaveActiveCountBits(true);
    if (WaveIsFirstLane())
        InterlockedAdd(ActivePhysicalPageCount[0], waveCount, activeIndex);
    activeIndex = WaveReadLaneFirst(activeIndex) + WavePrefixCountBits(true);
    if (activeIndex < PageAllocation.MaxPhysicalPages)
        ActivePhysicalPages[activeIndex] = uint4(
            atlasPage,
            tableIndex,
            previousLayer,
            0u);
    uint ignored;
    uint rectIndex = viewIndex * PageAllocation.MipLevelCount + mipLevel;
    InterlockedMin(
        UncachedPageRectBounds[rectIndex].x,
        localPage.x,
        ignored);
    InterlockedMin(
        UncachedPageRectBounds[rectIndex].y,
        localPage.y,
        ignored);
    InterlockedMax(
        UncachedPageRectBounds[rectIndex].z,
        localPage.x,
        ignored);
    InterlockedMax(
        UncachedPageRectBounds[rectIndex].w,
        localPage.y,
        ignored);
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint hierarchyOffset =
        viewIndex * PageAllocation.HierarchyWordsPerView +
        VirtualShadowPageHierarchyMipOffset(view, mipLevel);
    uint dimension = VirtualShadowMipPagesPerRow(view, mipLevel);
    uint2 hierarchyPage = localPage;
    [loop]
    while (true)
    {
        uint wordsPerRow = (dimension + 31u) / 32u;
        uint wordIndex =
            hierarchyOffset +
            hierarchyPage.y * wordsPerRow +
            hierarchyPage.x / 32u;
        InterlockedOr(
            RasterTargetPageHierarchy[wordIndex],
            1u << (hierarchyPage.x & 31u),
            ignored);
        hierarchyOffset += dimension * wordsPerRow;
        if (dimension == 1u)
            break;
        dimension >>= 1u;
        hierarchyPage >>= 1u;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void AllocateVirtualShadowPages(uint3 threadId : SV_DispatchThreadID)
{
    uint requestIndex = threadId.x;
    uint requestCount = min(RequestedPageCount[0], PageAllocation.PageTableEntryCount);
    if (requestIndex >= requestCount)
        return;
    uint tableIndex = RequestedPageIndices[requestIndex];
    if (tableIndex >= PageAllocation.PageTableEntryCount)
        return;
    uint requestFlags = RequestedPageFlags[tableIndex];

    uint viewIndex = tableIndex / PageAllocation.PageTableEntriesPerView;
    if (viewIndex >= PageAllocation.ViewCount)
        return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint mipLevel;
    uint2 localPage;
    if (!VirtualShadowDecodePageTableIndex(view, tableIndex, mipLevel, localPage))
        return;
    int2 expectedWorldPage = VirtualShadowWorldPage(view, mipLevel, localPage);

    uint4 entry = ShadowPageTable[tableIndex];
    bool sameTag = entry.x != VSM_PAGE_UNMAPPED &&
        entry.x != VSM_PAGE_LOCKED &&
        IsVirtualShadowPageTag(entry, expectedWorldPage);
    uint atlasPage = 0u;
    uint packedPage = VSM_PAGE_UNMAPPED;
    uint previousLayer = 0xFFFFFFFFu;
    if (sameTag)
    {
        if (!DecodeVirtualShadowPage(entry.x, atlasPage))
            return;
        previousLayer = atlasPage;
        packedPage = entry.x;
        uint previousFlags = PageMetadata[atlasPage].z;
        PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
        PageMetadata[atlasPage].z = requestFlags;
        bool detailUpgrade =
            (requestFlags & VSM_PAGE_REQUEST_DETAIL) != 0u &&
            (previousFlags & VSM_PAGE_REQUEST_DETAIL) == 0u;
        if (entry.w == view.CacheGeneration && !detailUpgrade)
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
            uint ignored;
            InterlockedAdd(ShadowAllocationFailures[0], 1u, ignored);
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
    PageMetadata[atlasPage].z = requestFlags;
    PageMetadata[atlasPage].w = 0u;
    PublishDirtyPage(
        tableIndex,
        viewIndex,
        mipLevel,
        localPage,
        atlasPage,
        previousLayer);
}
