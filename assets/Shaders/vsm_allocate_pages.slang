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
RWStructuredBuffer<uint> ShadowPageTable;
RWStructuredBuffer<uint> PhysicalPageOwners;
RWStructuredBuffer<VirtualShadowPageMetadata> PageMetadata;
#include "vsm_page_lists.slang"
RWStructuredBuffer<uint4> ActivePhysicalPages;
RWStructuredBuffer<uint> ActivePhysicalPageCount;
RWStructuredBuffer<uint4> UncachedPageRectBounds;
RWStructuredBuffer<uint2> RasterTargetPageHierarchy;
RWStructuredBuffer<uint> ShadowAllocationFailures;
RWTexture2D<uint> PageReceiverMasks;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip1;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip2;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip3;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip4;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip5;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip6;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWTexture2D<uint> PageReceiverMaskMip7;


void ReduceReceiverMask(RWTexture2D<uint> destination, inout uint mask, inout uint2 address)
{
    uint2 fraction = address & 1u;
    uint compact = ShrinkVirtualShadowReceiverMask(mask);
    mask = ((compact & 0xfu) | ((compact & 0xf0u) << 4u)) << (fraction.y * 16u + fraction.x * 4u);
    address >>= 1u;
    uint ignored;
    InterlockedOr(destination[address], mask, ignored);
}

void BuildReceiverMaskHierarchy(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage)
{
    if ((view.Flags & VSM_VIEW_USE_RECEIVER_MASK) == 0u) return;
    uint2 masks = LoadVirtualShadowReceiverMask(PageReceiverMasks, view, mipLevel, localPage);
    uint upper = ShrinkVirtualShadowReceiverMask(masks.x);
    uint lower = ShrinkVirtualShadowReceiverMask(masks.y);
    uint compact = upper | (lower << 8u);
    uint mask = (compact & 0xfu) | ((compact & 0xf0u) << 4u) |
        ((compact & 0xf00u) << 8u) | ((compact & 0xf000u) << 12u);
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    mask <<= (address.x & 1u) * 4u;
    address >>= 1u;
    uint pages = VirtualShadowPagesPerRow(view);
    uint ignored;
    if (pages >= 2u) InterlockedOr(PageReceiverMaskMip1[address], mask, ignored);
    if (pages >= 4u) ReduceReceiverMask(PageReceiverMaskMip2, mask, address);
    if (pages >= 8u) ReduceReceiverMask(PageReceiverMaskMip3, mask, address);
    if (pages >= 16u) ReduceReceiverMask(PageReceiverMaskMip4, mask, address);
    if (pages >= 32u) ReduceReceiverMask(PageReceiverMaskMip5, mask, address);
    if (pages >= 64u) ReduceReceiverMask(PageReceiverMaskMip6, mask, address);
    if (pages >= 128u) ReduceReceiverMask(PageReceiverMaskMip7, mask, address);
}

void FillReceiverMask(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    PageReceiverMasks[address] = 0xffffffffu;
    PageReceiverMasks[address + uint2(0, 1)] = 0xffffffffu;
}

bool VirtualShadowPageMetadataMatches(uint entry, VirtualShadowMapParameters view, int2 worldPage)
{
    uint page;
    if ((entry & VSM_PAGE_RENDERING) == 0u || !DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, page)) return false;
    return PageMetadata[page].Generation == view.CacheGeneration && all(PageMetadata[page].WorldPage == worldPage);
}

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
        uint expected = ShadowPageTable[oldOwner];
        uint oldPage;
        if ((expected & VSM_PAGE_RENDERING) != 0u &&
            DecodeVirtualShadowPage(expected, ShadowViews[PageMetadata[atlasPage].ViewIndex].PhysicalPagesPerRow, oldPage) && oldPage == atlasPage)
        {
            uint ignored;
            InterlockedCompareExchange(ShadowPageTable[oldOwner], expected, VSM_PAGE_UNMAPPED, ignored);
        }
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
        uint flags = PageMetadata[atlasPage].Flags;
        if ((flags & VSM_PAGE_DYNAMIC_UNCACHED) != 0u)
            InterlockedOr(RasterTargetPageHierarchy[wordIndex].x, 1u << (hierarchyPage.x & 31u), ignored);
        if ((flags & VSM_PAGE_STATIC_UNCACHED) != 0u)
            InterlockedOr(RasterTargetPageHierarchy[wordIndex].y, 1u << (hierarchyPage.x & 31u), ignored);
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

    uint viewIndex = VirtualShadowViewFromPageTableIndex(ShadowViews, PageAllocation.ViewCount, tableIndex);
    if (viewIndex >= PageAllocation.ViewCount)
        return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint mipLevel;
    uint2 localPage;
    if (!VirtualShadowDecodePageTableIndex(view, tableIndex, mipLevel, localPage))
        return;
    int2 expectedWorldPage = VirtualShadowWorldPage(view, mipLevel, localPage);
    BuildReceiverMaskHierarchy(view, mipLevel, localPage);

    uint entry = ShadowPageTable[tableIndex];
    if (entry != VSM_PAGE_UNMAPPED && entry != VSM_PAGE_LOCKED)
    {
        entry = (entry & ~(VSM_PAGE_PRIMARY | VSM_PAGE_SECONDARY)) |
            ((requestFlags & VSM_PAGE_REQUEST_PRIMARY) != 0u ? VSM_PAGE_PRIMARY : 0u) |
            ((requestFlags & VSM_PAGE_REQUEST_SECONDARY) != 0u ? VSM_PAGE_SECONDARY : 0u);
        ShadowPageTable[tableIndex] = entry;
    }
    bool sameTag = entry != VSM_PAGE_UNMAPPED &&
        entry != VSM_PAGE_LOCKED &&
        VirtualShadowPageMetadataMatches(entry, view, expectedWorldPage);
    uint atlasPage = 0u;
    uint packedPage = VSM_PAGE_UNMAPPED;
    uint previousLayer = 0xFFFFFFFFu;
    bool partialReceivers = (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u &&
        any(LoadVirtualShadowReceiverMask(PageReceiverMasks, view, mipLevel, localPage) != 0xffffffffu.xx);
    bool staticUncached = true;
    bool dynamicUncached = true;
    bool keepDeferred = false;
    bool previousHiZ = false;
    if (sameTag)
    {
        if (!DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, atlasPage))
            return;
        // Metadata.y identifies completed static depth and both conservative HiZ depths.
        previousHiZ = PageMetadata[atlasPage].StaticDepthGeneration == view.CacheGeneration &&
            (view.Flags & VSM_VIEW_PREVIOUS_PROJECTION_VALID) != 0u;
        previousLayer = previousHiZ ? atlasPage : 0xFFFFFFFFu;
        packedPage = entry;
        uint previousFlags = PageMetadata[atlasPage].RequestFlags;
        PageMetadata[atlasPage].LastRequestedFrame = PageAllocation.FrameIndex;
        PageMetadata[atlasPage].RequestFlags = requestFlags;
        bool detailUpgrade =
            (requestFlags & VSM_PAGE_REQUEST_DETAIL) != 0u &&
            (previousFlags & VSM_PAGE_REQUEST_DETAIL) == 0u;
        bool primaryUpgrade = (requestFlags & VSM_PAGE_REQUEST_PRIMARY) != 0u &&
            (previousFlags & VSM_PAGE_REQUEST_PRIMARY) == 0u;
        uint dirtyFlags = PageMetadata[atlasPage].Flags;
        staticUncached = PageMetadata[atlasPage].StaticDepthGeneration != view.CacheGeneration ||
            (dirtyFlags & VSM_PAGE_INVALIDATE_STATIC) != 0u || detailUpgrade || primaryUpgrade;
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
        dynamicUncached = staticUncached || !VirtualShadowPageHasOwnDepth(entry) ||
            ((dirtyFlags & VSM_PAGE_INVALIDATE_DYNAMIC) != 0u && !retainDynamicCoarse) || receiverRefresh;
        if (!staticUncached && !dynamicUncached) return;

    }
    else if ((entry & VSM_PAGE_RENDERING) != 0u && entry != VSM_PAGE_LOCKED)
    {
        if (!DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, atlasPage))
            return;
        packedPage = entry;
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
        packedPage = PackVirtualShadowPage(atlasPage + 1u, view.PhysicalPagesPerRow);
    }

    ShadowPageTable[tableIndex] = (packedPage & 0x000fffffu) | VSM_PAGE_RENDERING |
        ((requestFlags & VSM_PAGE_REQUEST_PRIMARY) != 0u ? VSM_PAGE_PRIMARY : 0u) |
        ((requestFlags & VSM_PAGE_REQUEST_SECONDARY) != 0u ? VSM_PAGE_SECONDARY : 0u);
    PageMetadata[atlasPage].WorldPage = expectedWorldPage;
    PageMetadata[atlasPage].Generation = view.CacheGeneration;
    PageMetadata[atlasPage].ViewIndex = viewIndex;
    PageMetadata[atlasPage].LastRequestedFrame = PageAllocation.FrameIndex;
    if (!partialReceivers)
        FillReceiverMask(view, mipLevel, localPage);
    // Coarse/full receiver requests are whole-page work even on an invalidated page.
    partialReceivers = partialReceivers && any(LoadVirtualShadowReceiverMask(PageReceiverMasks, view, mipLevel, localPage) != 0xffffffffu.xx);
    if (staticUncached) PageMetadata[atlasPage].StaticDepthGeneration = 0u;
    PageMetadata[atlasPage].RequestFlags = requestFlags;
    PageMetadata[atlasPage].Flags = (partialReceivers ? VSM_PAGE_MASKED : 0u) |
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
