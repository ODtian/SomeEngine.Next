#ifndef CLUSTER_RASTER_TARGET_RESOURCES_SLANG
#define CLUSTER_RASTER_TARGET_RESOURCES_SLANG

#include "cluster_raster_target_view.slang"
#include "vsm_page_entry.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint2> RasterTargetPageHierarchy;
StructuredBuffer<uint> RasterTargetPageTable;
Texture2D<uint> RasterTargetReceiverMasks;
#include "cluster_depth_cache.slang"
#include "vsm_receiver_mask_bits.slang"


uint2 ClusterTargetReceiverMaskOrigin(ClusterRasterTargetView target)
{
    return uint2(target.ReceiverMaskOrigin >> 16u, target.ReceiverMaskOrigin & 0xffffu);
}

uint2 LoadClusterTargetReceiverMask(ClusterRasterTargetView target, uint2 localPage)
{
    uint2 address = ClusterTargetReceiverMaskOrigin(target) + localPage * uint2(1u, 2u);
    return uint2(RasterTargetReceiverMasks.Load(int3(address, 0)),
        RasterTargetReceiverMasks.Load(int3(address + uint2(0, 1), 0)));
}

uint ClusterTargetDirtyPageWord(uint index, uint depthCacheClass)
{
    uint2 bits = RasterTargetPageHierarchy[index];
    return depthCacheClass < 2u ? bits[depthCacheClass] : bits.x | bits.y;
}

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
