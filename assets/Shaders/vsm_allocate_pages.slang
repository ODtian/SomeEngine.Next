#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "resource_effects.slang"
#include "vsm_page_lists.slang"

struct VirtualShadowPageAllocationUniforms
{
    uint PageTableEntryCount;
    uint PageTableEntriesPerView;
    uint MaxPhysicalPages;
    uint ViewCount;
    uint FrameIndex;
    uint Pad0;
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
    bool partialReceivers = sameTag && entry.w != view.CacheGeneration &&
        (entry.w & VSM_PAGE_RECEIVER_DEPTH) == 0u &&
        (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u;
    bool previousHiZ = false;
    if (sameTag)
    {
        if (!DecodeVirtualShadowPage(entry.x, atlasPage))
            return;
        // Metadata.y is the generation of completed whole-page HiZ, not allocation.
        // A retagged/reused or masked/incomplete page cannot acquire prior history.
        previousHiZ = PageMetadata[atlasPage].y == view.CacheGeneration &&
            (PageMetadata[atlasPage].w & VSM_PAGE_MASKED) == 0u &&
            (view.Flags & VSM_VIEW_PREVIOUS_PROJECTION_VALID) != 0u;
        previousLayer = previousHiZ ? atlasPage : 0xFFFFFFFFu;
        packedPage = entry.x;
        uint previousFlags = PageMetadata[atlasPage].z;
        PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
        PageMetadata[atlasPage].z = requestFlags;
        bool detailUpgrade =
            (requestFlags & VSM_PAGE_REQUEST_DETAIL) != 0u &&
            (previousFlags & VSM_PAGE_REQUEST_DETAIL) == 0u;
        // Clipmaps scroll by a quarter of the base page grid. Higher mips
        // move by a fraction of their page, so a matching coarse tag alone
        // cannot preserve the depth pixels when the projection changes.
        uint scrollingMip = firstbithigh(VirtualShadowPagesPerRow(view)) - 2u;
        bool coarseProjectionChanged = view.LightKind == VSM_LIGHT_DIRECTIONAL &&
            mipLevel > scrollingMip &&
            (view.Flags & VSM_VIEW_PREVIOUS_PROJECTION_VALID) == 0u;
        // A receiver-masked depth page is incomplete even if its projection and
        // casters did not change. Render it fully once before retaining it.
        if (entry.w == view.CacheGeneration && !detailUpgrade && !coarseProjectionChanged &&
            (PageMetadata[atlasPage].w & VSM_PAGE_MASKED) == 0u)
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
        0u);
    PageMetadata[atlasPage].x = PageAllocation.FrameIndex;
    if (!partialReceivers)
        PageReceiverMasks[tableIndex] = 0xFFFFFFFFu.xx;
    // Coarse/full receiver requests are whole-page work even on an invalidated page.
    partialReceivers = partialReceivers && any(PageReceiverMasks[tableIndex] != 0xFFFFFFFFu.xx);
    PageMetadata[atlasPage].y = 0u;
    PageMetadata[atlasPage].z = requestFlags;
    PageMetadata[atlasPage].w = (partialReceivers ? VSM_PAGE_MASKED : 0u) |
        (previousHiZ ? VSM_PAGE_PRIOR_HIZ : 0u);
    PublishDirtyPage(
        tableIndex,
        viewIndex,
        mipLevel,
        localPage,
        atlasPage,
        previousLayer);
}
