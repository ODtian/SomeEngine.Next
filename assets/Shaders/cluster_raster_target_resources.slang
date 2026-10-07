#ifndef CLUSTER_RASTER_TARGET_RESOURCES_SLANG
#define CLUSTER_RASTER_TARGET_RESOURCES_SLANG

#include "cluster_raster_target_view.slang"
#include "vsm_page_entry.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint> RasterTargetPageHierarchy;
StructuredBuffer<uint> RasterTargetPageTable;
StructuredBuffer<uint2> RasterTargetReceiverMasks;

// Draw eligibility is not sample validity: newly dirty mapped pages have generation
// zero until complete depth work. Scrolling still requires matching world-page tags.
bool ClusterRasterTargetResolvePage(
    ClusterRasterTargetView target, uint2 localPage,
    out uint tableIndex, out uint page)
{
    tableIndex = ClusterRasterTargetTableIndex(target, localPage);
    uint entry = RasterTargetPageTable[tableIndex];
    if ((entry & VSM_PAGE_RENDERING) == 0u ||
        !DecodeVirtualShadowPage(entry, target.PhysicalPagesPerRow, page))
    {
        page = 0xffffffffu;
        return false;
    }
    return true;
}

#endif
