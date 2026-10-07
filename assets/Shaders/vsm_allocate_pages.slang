#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_depth_cache.slang"
#include "resource_effects.slang"

struct VirtualShadowPageAllocationUniforms
{
    uint PageTableEntryCount;
    uint PageTableEntriesPerView;
    uint MaxPhysicalPages;
    uint ViewCount;
    uint FrameIndex;
    int DeferredInvalidationBudget;
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
#include "vsm_page_lists.slang"
RWStructuredBuffer<uint4> ActivePhysicalPages;
RWStructuredBuffer<uint> ActivePhysicalPageCount;
RWStructuredBuffer<uint4> UncachedPageRectBounds;
RWStructuredBuffer<uint> RasterTargetPageHierarchy;
RWStructuredBuffer<uint> ShadowAllocationFailures;
RWStructuredBuffer<uint2> PageReceiverMasks;

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
    uint index;
    if (!ReservePages(ShadowPageLists,
        ShadowPageListCountIndex(VSM_PAGE_LIST_AVAILABLE, PageAllocation.MaxPhysicalPages), index))
        return false;
    atlasPage = ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_AVAILABLE, PageAllocation.MaxPhysicalPages) + index];
    ReleaseOldMapping(atlasPage);
    PhysicalPageOwners[atlasPage] = newTableIndex;
    AppendShadowPage(VSM_PAGE_LIST_REQUESTED, PageAllocation.MaxPhysicalPages, atlasPage);
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
            ShadowViews[viewIndex].CacheGeneration);
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
    bool partialReceivers = (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u &&
        any(PageReceiverMasks[tableIndex] != 0xFFFFFFFFu.xx);
    bool staticUncached = true;
    bool dynamicUncached = true;
    bool keepDeferred = false;
    bool previousHiZ = false;
    if (sameTag)
    {
        if (!DecodeVirtualShadowPage(entry.x, atlasPage))
            return;
        // Metadata.y identifies completed static depth and both conservative HiZ depths.
        previousHiZ = PageMetadata[atlasPage].y == view.CacheGeneration &&
            (view.Flags & VSM_VIEW_PREVIOUS_PROJECTION_VALID) != 0u;
        previousLayer = previousHiZ ? atlasPage : 0xFFFFFFFFu;
        packedPage = entry.x;
        uint previousFlags = PageMetadata[atlasPage].z;
        PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
        PageMetadata[atlasPage].z = requestFlags;
        bool detailUpgrade =
            (requestFlags & VSM_PAGE_REQUEST_DETAIL) != 0u &&
            (previousFlags & VSM_PAGE_REQUEST_DETAIL) == 0u;
        uint dirtyFlags = PageMetadata[atlasPage].w;
        staticUncached = PageMetadata[atlasPage].y != view.CacheGeneration ||
            (dirtyFlags & VSM_PAGE_INVALIDATE_STATIC) != 0u || detailUpgrade;
        bool retainDynamicCoarse = (view.Flags & VSM_VIEW_CACHED_DISTANT_LIGHT) != 0u ||
            (view.Flags & VSM_VIEW_FORCE_CACHE_DYNAMIC_COARSE) != 0u &&
            (requestFlags & VSM_PAGE_REQUEST_DETAIL) == 0u && !staticUncached;
        if ((dirtyFlags & VSM_PAGE_DEFERRED_INVALIDATION) != 0u)
        {
            bool consume = false;
            if ((view.Flags & VSM_VIEW_SINGLE_PAGE) == 0u && !retainDynamicCoarse)
            {
                consume = PageAllocation.DeferredInvalidationBudget < 0;
                if (PageAllocation.DeferredInvalidationBudget > 0)
                {
                    uint count = WaveActiveCountBits(true);
                    uint first = 0u;
                    if (WaveIsFirstLane()) InterlockedAdd(ShadowAllocationFailures[2], count, first);
                    consume = WaveReadLaneFirst(first) + WavePrefixCountBits(true) < uint(PageAllocation.DeferredInvalidationBudget);
                }
            }
            staticUncached |= consume;
            keepDeferred = !consume;
        }
        bool receiverRefresh = (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u && !retainDynamicCoarse;
        dynamicUncached = staticUncached || entry.w != view.CacheGeneration ||
            ((dirtyFlags & VSM_PAGE_INVALIDATE_DYNAMIC) != 0u && !retainDynamicCoarse) || receiverRefresh;
        if (!staticUncached && !dynamicUncached) return;

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
        0u);
    PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
    if (!partialReceivers)
        PageReceiverMasks[tableIndex] = 0xFFFFFFFFu.xx;
    // Coarse/full receiver requests are whole-page work even on an invalidated page.
    partialReceivers = partialReceivers && any(PageReceiverMasks[tableIndex] != 0xFFFFFFFFu.xx);
    if (staticUncached) PageMetadata[atlasPage].y = 0u;
    PageMetadata[atlasPage].z = requestFlags;
    PageMetadata[atlasPage].w = (partialReceivers ? VSM_PAGE_MASKED : 0u) |
        (previousHiZ && !staticUncached ? VSM_PAGE_PRIOR_HIZ : 0u) |
        (staticUncached ? VSM_PAGE_STATIC_UNCACHED : 0u) |
        (dynamicUncached ? VSM_PAGE_DYNAMIC_UNCACHED : 0u) |
        (keepDeferred ? VSM_PAGE_DEFERRED_INVALIDATION : 0u);
    PublishDirtyPage(
        tableIndex,
        viewIndex,
        mipLevel,
        localPage,
        atlasPage,
        previousLayer);
}
