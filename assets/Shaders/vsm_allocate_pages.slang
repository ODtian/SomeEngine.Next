#include "vsm_sampling.slang"

struct VirtualShadowPageAllocationUniforms
{
    uint PageTableEntryCount;
    uint MaxPhysicalPages;
    uint ClearTilesPerPageAxis;
    uint VirtualPagesPerView;
    uint ViewCount;
    uint TraversalGroupCount;
    uint MaxPagesToRasterPerFrame;
    uint PhysicalPageEvictionProbeCount;
};

ConstantBuffer<VirtualShadowPageAllocationUniforms> PageAllocation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<uint> RequestedShadowPages;
StructuredBuffer<uint> RequestedShadowPageList;
StructuredBuffer<uint> RequestedShadowPageCount;
RWStructuredBuffer<uint4> ShadowPageTable;
RWStructuredBuffer<uint> ShadowPageAllocator;
RWStructuredBuffer<uint> PhysicalPageOwners;
RWStructuredBuffer<uint> ShadowPagesToRaster;
RWStructuredBuffer<uint> ShadowRasterBudget;
RWStructuredBuffer<uint4> ShadowDirtyPageBounds;
RWStructuredBuffer<uint> ActivePhysicalPages;
RWStructuredBuffer<uint> ActivePhysicalPageCount;
RWStructuredBuffer<uint> ShadowRasterViewMask;
RWByteAddressBuffer ClearPageDispatchArgs;
RWByteAddressBuffer ShadowTraversalDispatchArgs;

int2 RequestedWorldPage(
    uint pageTableIndex,
    VirtualShadowMapParameters shadowView)
{
    uint pagesPerRow = VirtualShadowPagesPerRow(shadowView);
    uint localIndex = pageTableIndex - shadowView.PageTableOffset;
    uint2 slot = uint2(localIndex % pagesPerRow, localIndex / pagesPerRow);
    uint mask = pagesPerRow - 1u;
    uint2 originSlot = uint2(
        asuint(shadowView.VirtualPageOriginX) & mask,
        asuint(shadowView.VirtualPageOriginY) & mask);
    uint2 localPage = (slot - originSlot) & mask;
    return int2(
        shadowView.VirtualPageOriginX,
        shadowView.VirtualPageOriginY) + int2(localPage);
}

bool TryAllocateFreePhysicalPage(
    uint ownerPageTableIndex,
    out uint physicalPage)
{
    uint observed = ShadowPageAllocator[0];
    [loop]
    while (observed < PageAllocation.MaxPhysicalPages)
    {
        uint latest;
        InterlockedCompareExchange(
            ShadowPageAllocator[0],
            observed,
            observed + 1u,
            latest);
        if (latest == observed)
        {
            physicalPage = observed;
            PhysicalPageOwners[physicalPage] = ownerPageTableIndex;
            return true;
        }
        observed = latest;
    }
    physicalPage = 0u;
    return false;
}

bool TryEvictPhysicalPage(
    uint ownerPageTableIndex,
    out uint physicalPage)
{
    uint probeCount = min(
        PageAllocation.MaxPhysicalPages,
        PageAllocation.PhysicalPageEvictionProbeCount);
    [loop]
    for (uint attempt = 0u; attempt < probeCount; attempt++)
    {
        uint cursor;
        InterlockedAdd(ShadowPageAllocator[1], 1u, cursor);
        uint candidate = cursor % PageAllocation.MaxPhysicalPages;
        uint oldOwner = PhysicalPageOwners[candidate];
        if (oldOwner < PageAllocation.PageTableEntryCount &&
            RequestedShadowPages[oldOwner] != 0u)
        {
            continue;
        }

        // The old virtual mapping is locked before the physical owner changes. Therefore no
        // observable state can contain a new owner while the old virtual page still resolves to
        // this physical page. A failed owner claim restores the old mapping before retrying.
        uint expectedMapping = PackVirtualShadowPage(candidate + 1u);
        bool lockedOldMapping = false;
        if (oldOwner < PageAllocation.PageTableEntryCount)
        {
            uint observedMapping;
            InterlockedCompareExchange(
                ShadowPageTable[oldOwner].x,
                expectedMapping,
                VSM_PAGE_LOCKED,
                observedMapping);
            if (observedMapping != expectedMapping)
                continue;
            lockedOldMapping = true;
        }

        uint latestOwner;
        InterlockedCompareExchange(
            PhysicalPageOwners[candidate],
            oldOwner,
            ownerPageTableIndex,
            latestOwner);
        if (latestOwner != oldOwner)
        {
            if (lockedOldMapping)
            {
                uint ignoredRestore;
                InterlockedCompareExchange(
                    ShadowPageTable[oldOwner].x,
                    VSM_PAGE_LOCKED,
                    expectedMapping,
                    ignoredRestore);
            }
            continue;
        }

        if (lockedOldMapping)
        {
            uint ignoredClear;
            InterlockedCompareExchange(
                ShadowPageTable[oldOwner].x,
                VSM_PAGE_LOCKED,
                VSM_PAGE_UNMAPPED,
                ignoredClear);
        }
        physicalPage = candidate;
        return true;
    }
    physicalPage = 0u;
    return false;
}

bool TryReserveRasterBudget()
{
    uint observed = ShadowRasterBudget[0];
    while (observed < PageAllocation.MaxPagesToRasterPerFrame)
    {
        uint latest;
        InterlockedCompareExchange(
            ShadowRasterBudget[0],
            observed,
            observed + 1u,
            latest);
        if (latest == observed)
            return true;
        observed = latest;
    }
    return false;
}

void PublishDirtyPageBounds(uint viewIndex, uint2 localPage)
{
    // The buffer is zero-cleared. Inverted minima let all four fields use InterlockedMax while
    // preserving an empty value without a dedicated initialization pass.
    uint ignored;
    InterlockedMax(
        ShadowDirtyPageBounds[viewIndex].x,
        0xFFFFu - localPage.x,
        ignored);
    InterlockedMax(
        ShadowDirtyPageBounds[viewIndex].y,
        0xFFFFu - localPage.y,
        ignored);
    InterlockedMax(
        ShadowDirtyPageBounds[viewIndex].z,
        localPage.x + 1u,
        ignored);
    InterlockedMax(
        ShadowDirtyPageBounds[viewIndex].w,
        localPage.y + 1u,
        ignored);
}

uint ResolvePhysicalPage(
    uint pageTableIndex,
    int2 expectedWorldPage,
    uint cacheGeneration,
    out bool newlyAllocated)
{
    newlyAllocated = false;
    uint4 observedEntry = ShadowPageTable[pageTableIndex];
    [loop]
    for (uint attempt = 0u; attempt < 64u; attempt++)
    {
        uint observed = observedEntry.x;
        if (observed != VSM_PAGE_UNMAPPED && observed != VSM_PAGE_LOCKED &&
            IsVirtualShadowPageTag(observedEntry, expectedWorldPage))
        {
            return observed;
        }
        if (observed == VSM_PAGE_LOCKED)
        {
            observedEntry = ShadowPageTable[pageTableIndex];
            continue;
        }

        uint latest;
        InterlockedCompareExchange(
            ShadowPageTable[pageTableIndex].x,
            observed,
            VSM_PAGE_LOCKED,
            latest);
        if (latest != observed)
        {
            observedEntry = ShadowPageTable[pageTableIndex];
            continue;
        }

        uint physicalPage;
        bool acquired = observed != VSM_PAGE_UNMAPPED;
        if (acquired)
        {
            physicalPage = observed - 1u;
            PhysicalPageOwners[physicalPage] = pageTableIndex;
        }
        else
        {
            acquired = TryAllocateFreePhysicalPage(pageTableIndex, physicalPage) ||
                TryEvictPhysicalPage(pageTableIndex, physicalPage);
        }

        if (!acquired)
        {
            ShadowPageTable[pageTableIndex].x = VSM_PAGE_UNMAPPED;
            return VSM_PAGE_UNMAPPED;
        }

        ShadowPageTable[pageTableIndex].y = asuint(expectedWorldPage.x);
        ShadowPageTable[pageTableIndex].z = asuint(expectedWorldPage.y);
        ShadowPageTable[pageTableIndex].w = cacheGeneration;
        uint published = PackVirtualShadowPage(physicalPage + 1u);
        ShadowPageTable[pageTableIndex].x = published;
        newlyAllocated = true;
        return published;
    }
    return VSM_PAGE_UNMAPPED;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void AllocateVirtualShadowPages(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint requestIndex = dispatchThreadId.x;
    uint requestCount = min(
        RequestedShadowPageCount[0],
        PageAllocation.PageTableEntryCount);
    if (requestIndex >= requestCount)
        return;
    uint pageTableIndex = RequestedShadowPageList[requestIndex];
    if (pageTableIndex >= PageAllocation.PageTableEntryCount)
        return;

    uint viewIndex = PageAllocation.VirtualPagesPerView > 0u
        ? pageTableIndex / PageAllocation.VirtualPagesPerView
        : PageAllocation.ViewCount;
    if (viewIndex >= PageAllocation.ViewCount)
        return;
    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    int2 expectedWorldPage = RequestedWorldPage(pageTableIndex, shadowView);
    uint4 currentEntry = ShadowPageTable[pageTableIndex];
    bool currentMapping =
        currentEntry.x != VSM_PAGE_UNMAPPED &&
        currentEntry.x != VSM_PAGE_LOCKED &&
        IsVirtualShadowPageTag(currentEntry, expectedWorldPage);
    bool needsRaster =
        !currentMapping ||
        currentEntry.w != shadowView.CacheGeneration;
    if (!needsRaster || !TryReserveRasterBudget())
        return;

    bool newlyAllocated;
    uint pageEntry = ResolvePhysicalPage(
        pageTableIndex,
        expectedWorldPage,
        shadowView.CacheGeneration,
        newlyAllocated);
    if (pageEntry == VSM_PAGE_UNMAPPED)
        return;
    ShadowPageTable[pageTableIndex].w = shadowView.CacheGeneration;
    ShadowPagesToRaster[pageTableIndex] = 1u;

    uint activeIndex;
    InterlockedAdd(ActivePhysicalPageCount[0], 1u, activeIndex);
    if (activeIndex >= PageAllocation.MaxPhysicalPages)
        return;
    uint physicalPage = pageEntry - 1u;
    ActivePhysicalPages[activeIndex] = physicalPage;
    uint2 localPage = uint2(
        expectedWorldPage - int2(
            shadowView.VirtualPageOriginX,
            shadowView.VirtualPageOriginY));
    PublishDirtyPageBounds(viewIndex, localPage);

    if (viewIndex < PageAllocation.ViewCount && viewIndex < 32u)
    {
        uint ignoredMask;
        InterlockedOr(ShadowRasterViewMask[0], 1u << viewIndex, ignoredMask);
        uint ignoredGroups;
        uint traversalGroupsX = min(
            PageAllocation.TraversalGroupCount,
            65535u);
        uint traversalGroupsY = max(
            1u,
            ((PageAllocation.TraversalGroupCount - 1u) / 65535u) + 1u);
        ShadowTraversalDispatchArgs.InterlockedMax(
            0u,
            traversalGroupsX,
            ignoredGroups);
        ShadowTraversalDispatchArgs.InterlockedMax(
            4u,
            traversalGroupsY,
            ignoredGroups);
        ShadowTraversalDispatchArgs.Store(8u, 1u);
    }

    uint ignored;
    ClearPageDispatchArgs.InterlockedMax(0u, activeIndex + 1u, ignored);
    ClearPageDispatchArgs.Store(4u, PageAllocation.ClearTilesPerPageAxis);
    ClearPageDispatchArgs.Store(8u, PageAllocation.ClearTilesPerPageAxis);
}
