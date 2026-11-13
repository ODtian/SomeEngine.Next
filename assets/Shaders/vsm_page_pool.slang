#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
__exported import resource_effects;

struct VirtualShadowPagePoolUniforms
{
    uint MaxPages;
    uint PageTableEntryCount;
    uint FrameIndex;
    uint PageListPhase;
    uint PageTableEntriesPerView;
    uint HierarchyWordsPerView;
    uint MipLevelCount;
    uint ViewCount;
};

ConstantBuffer<VirtualShadowPagePoolUniforms> PagePool;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
RWStructuredBuffer<uint> ShadowPageTable;
StructuredBuffer<uint> RequestedPageFlags;
StructuredBuffer<uint> RequestedPageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PageRequestDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PageAssignments;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<VirtualShadowPageMetadata> PageMetadata;
#include "vsm_page_lists.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> AllocatedPageHierarchy;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint4> AllocatedPageRectBounds;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

void PublishAllocatedPage(
    uint viewIndex,
    VirtualShadowMapParameters view,
    uint mipLevel,
    uint2 localPage)
{
    uint offset =
        viewIndex * PagePool.HierarchyWordsPerView +
        VirtualShadowPageHierarchyMipOffset(view, mipLevel);
    uint dimension = VirtualShadowMipPagesPerRow(view, mipLevel);
    uint2 page = localPage;
    uint ignored;
    uint rectIndex = viewIndex * PagePool.MipLevelCount + mipLevel;
    InterlockedMin(AllocatedPageRectBounds[rectIndex].x, localPage.x, ignored);
    InterlockedMin(AllocatedPageRectBounds[rectIndex].y, localPage.y, ignored);
    InterlockedMax(AllocatedPageRectBounds[rectIndex].z, localPage.x, ignored);
    InterlockedMax(AllocatedPageRectBounds[rectIndex].w, localPage.y, ignored);
    [loop]
    while (true)
    {
        uint wordsPerRow = (dimension + 31u) / 32u;
        uint index =
            offset +
            page.y * wordsPerRow +
            page.x / 32u;
        InterlockedOr(
            AllocatedPageHierarchy[index],
            1u << (page.x & 31u),
            ignored);
        offset += dimension * wordsPerRow;
        if (dimension == 1u)
            break;
        dimension >>= 1u;
        page >>= 1u;
    }
}

bool PublishOwnedPage(uint atlasPage, uint owner)
{
    if (owner >= PagePool.PageTableEntryCount)
        return false;
    uint4 range = ShadowRanges[PageMetadata[atlasPage].RangeId];
    if (range.z != PageMetadata[atlasPage].RangeGeneration || range.w >= PagePool.ViewCount)
    {
        ShadowPageTable[owner] = 0u;
        return false;
    }
    uint viewIndex = range.w;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint mipLevel;
    uint2 localPage;
    if (!VirtualShadowDecodePageTableIndex(view, owner, mipLevel, localPage) ||
        mipLevel >= PagePool.MipLevelCount)
    {
        ShadowPageTable[owner] = 0u;
        return false;
    }
    uint entry = ShadowPageTable[owner];
    int2 worldPage = VirtualShadowWorldPage(view, mipLevel, localPage);
    uint page;
    if ((entry & VSM_PAGE_RENDERING) == 0u ||
        !DecodeVirtualShadowPage(entry, view.AtlasPagesPerRow, page) || page != atlasPage) return false;
    if (PageMetadata[atlasPage].Generation != view.CacheGeneration || any(PageMetadata[atlasPage].WorldPage != worldPage))
    {
        ShadowPageTable[owner] = 0u;
        return false;
    }
    PublishAllocatedPage(viewIndex, view, mipLevel, localPage);
    return true;
}

void FinishShadowPageOrder(uint groupIndex)
{
    uint requestedCount = ShadowPageListCount(VSM_PAGE_LIST_REQUESTED, PagePool.MaxPages);
    uint availableCount = ShadowPageListCount(VSM_PAGE_LIST_AVAILABLE, PagePool.MaxPages);
    uint requestedStart = ShadowPageListStart(VSM_PAGE_LIST_REQUESTED, PagePool.MaxPages);
    uint availableStart = ShadowPageListStart(VSM_PAGE_LIST_AVAILABLE, PagePool.MaxPages);
    for (uint index = groupIndex; index < availableCount; index += 256u)
        ShadowPageLists[requestedStart + requestedCount + index] = ShadowPageLists[availableStart + index];
    AllMemoryBarrierWithGroupSync();
    if (groupIndex == 0u)
        ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_REQUESTED, PagePool.MaxPages)] = requestedCount + availableCount;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void BuildVirtualShadowPagePool(uint3 threadId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint index = threadId.x;
    uint capacity = PagePool.MaxPages;
    if (PagePool.PageListPhase == 0u)
    {
        if (index < capacity)
            ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_LRU, capacity) + index] =
                ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_REQUESTED, capacity) + index];
        if (index == 0u)
        {
            ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_REQUESTED, capacity)] = 0u;
            ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_AVAILABLE, capacity)] = 0u;
            ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_EMPTY, capacity)] = 0u;
            uint requestCount = min(RequestedPageCount[0], PagePool.PageTableEntryCount);
            PageRequestDispatchArgs.Store(0u, (requestCount + 63u) / 64u);
            PageRequestDispatchArgs.Store(4u, 1u);
            PageRequestDispatchArgs.Store(8u, 1u);
            PageRequestDispatchArgs.Store(12u, requestCount);
        }
        return;
    }

    if (PagePool.PageListPhase == 3u)
    {
        FinishShadowPageOrder(groupIndex);
        return;
    }
    if (index >= capacity)
        return;

    uint lruIndex = ShadowPageListStart(VSM_PAGE_LIST_LRU, capacity) + index;
    uint atlasPage = ShadowPageLists[lruIndex];
    uint owner = PageAssignments[atlasPage];
    if (owner == VSM_UNOWNED_PAGE || owner >= PagePool.PageTableEntryCount)
    {
        AppendShadowPage(VSM_PAGE_LIST_EMPTY, capacity, atlasPage);
        ShadowPageLists[lruIndex] = VSM_UNOWNED_PAGE;
        return;
    }
    if (!PublishOwnedPage(atlasPage, owner))
    {
        PageAssignments[atlasPage] = VSM_UNOWNED_PAGE;
        AppendShadowPage(VSM_PAGE_LIST_EMPTY, capacity, atlasPage);
        ShadowPageLists[lruIndex] = VSM_UNOWNED_PAGE;
        return;
    }
    if (RequestedPageFlags[owner] != 0u)
    {
        AppendShadowPage(VSM_PAGE_LIST_REQUESTED, capacity, atlasPage);
        ShadowPageLists[lruIndex] = VSM_UNOWNED_PAGE;
    }
    // Unrequested cached pages retain their previous order and invalidation data.
}
