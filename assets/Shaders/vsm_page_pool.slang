#include "vsm_sampling.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

struct VirtualShadowPagePoolUniforms
{
    uint MaxPhysicalPages;
    uint PageTableEntryCount;
    uint FrameIndex;
    uint EvictionBucketCount;
    uint PageTableEntriesPerView;
    uint HierarchyWordsPerView;
    uint MipLevelCount;
    uint ViewCount;
};

ConstantBuffer<VirtualShadowPagePoolUniforms> PagePool;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<uint4> ShadowPageTable;
StructuredBuffer<uint> RequestedShadowPages;
StructuredBuffer<uint> RequestedPageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PageRequestDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PhysicalPageOwners;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint4> PageMetadata;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> FreePages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> FreePageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> EvictionPages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> EvictionBucketCounts;
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

void PublishOwnedPage(uint atlasPage, uint owner)
{
    if (owner >= PagePool.PageTableEntryCount)
        return;
    uint viewIndex = owner / PagePool.PageTableEntriesPerView;
    if (viewIndex >= PagePool.ViewCount)
        return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint mipLevel;
    uint2 localPage;
    if (!VirtualShadowDecodePageTableIndex(view, owner, mipLevel, localPage) ||
        mipLevel >= PagePool.MipLevelCount)
    {
        return;
    }
    uint4 entry = ShadowPageTable[owner];
    int2 worldPage = VirtualShadowWorldPage(view, mipLevel, localPage);
    if (entry.x != PackVirtualShadowPage(atlasPage + 1u) ||
        entry.w != view.CacheGeneration ||
        !IsVirtualShadowPageTag(entry, worldPage))
    {
        return;
    }
    PublishAllocatedPage(viewIndex, view, mipLevel, localPage);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildVirtualShadowPagePool(uint3 threadId : SV_DispatchThreadID)
{
    uint atlasPage = threadId.x;
    if (atlasPage == 0u)
    {
        uint requestCount = min(RequestedPageCount[0], PagePool.PageTableEntryCount);
        PageRequestDispatchArgs.Store(0u, (requestCount + 63u) / 64u);
        PageRequestDispatchArgs.Store(4u, 1u);
        PageRequestDispatchArgs.Store(8u, 1u);
        PageRequestDispatchArgs.Store(12u, requestCount);
    }
    if (atlasPage >= PagePool.MaxPhysicalPages)
        return;
    uint owner = PhysicalPageOwners[atlasPage];
    if (owner == VSM_UNOWNED_PAGE || owner >= PagePool.PageTableEntryCount)
    {
        uint index = 0u;
        uint count = WaveActiveCountBits(true);
        if (WaveIsFirstLane())
            InterlockedAdd(FreePageCount[0], count, index);
        index = WaveReadLaneFirst(index) + WavePrefixCountBits(true);
        if (index < PagePool.MaxPhysicalPages)
            FreePages[index] = atlasPage;
        return;
    }

    PublishOwnedPage(atlasPage, owner);

    bool requested = RequestedShadowPages[owner] == PagePool.FrameIndex;
    if (requested)
        return;

    uint lastUsed = PageMetadata[atlasPage].x;
    uint age = PagePool.FrameIndex - lastUsed;
    uint bucket = min(PagePool.EvictionBucketCount - 1u, age >> 2u);
    uint4 matching = WaveMatchingLanes(bucket);
    uint leader = WaveMaskFirstLane(matching);
    uint local = 0u;
    if (WaveGetLaneIndex() == leader)
        InterlockedAdd(EvictionBucketCounts[bucket], WaveMaskBitCount(matching), local);
    local = WaveReadLaneAt(local, leader) + WaveMaskPrefix(matching, WaveGetLaneIndex());
    if (local < PagePool.MaxPhysicalPages)
        EvictionPages[bucket * PagePool.MaxPhysicalPages + local] = atlasPage;
}
