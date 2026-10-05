#ifndef CLUSTER_RASTER_TARGET_RESOURCES_SLANG
#define CLUSTER_RASTER_TARGET_RESOURCES_SLANG

#include "cluster_raster_target_view.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint> RasterTargetPageHierarchy;
StructuredBuffer<uint4> RasterTargetPageTable;
StructuredBuffer<uint2> RasterTargetReceiverMasks;

// Draw eligibility is not sample validity: newly dirty mapped pages have generation
// zero until complete depth work. Scrolling still requires matching world-page tags.
bool ClusterRasterTargetResolvePage(
    ClusterRasterTargetView target, uint2 localPage,
    out uint tableIndex, out uint page)
{
    tableIndex = ClusterRasterTargetTableIndex(target, localPage);
    uint4 entry = RasterTargetPageTable[tableIndex];
    int2 expectedWorldPage = ClusterRasterTargetWorldPage(target, localPage);
    if (entry.x == 0u || entry.x == 0xFFFFFFFFu ||
        (entry.w != 0u && entry.w != target.CacheGeneration) ||
        asint(entry.y) != expectedWorldPage.x || asint(entry.z) != expectedWorldPage.y)
    {
        page = 0xFFFFFFFFu;
        return false;
    }
    page = entry.x - 1u;
    return true;
}

#endif
