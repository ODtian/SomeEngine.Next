#define VSM_PAGE_HIZ_INITIALIZE 0
#include "vsm_page_hiz_common.slang"

[shader("compute")]
[numthreads(8, 8, 1)]
void ReduceVirtualShadowPageHiZ(
    uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint pageIndex = VirtualShadowDispatchPage(groupId);
    if (pageIndex >= ActivePhysicalPageCount[0]) return;
    uint physicalPage = ActivePhysicalPages[pageIndex].x;
    uint destinationSize = max(max(PageHiZ.PageSize / 2u, 1u) >> PageHiZ.DestinationMip, 1u);
    uint tiles = (destinationSize + 7u) / 8u;
    uint2 pixel = VirtualShadowDispatchTile(groupId, tiles) * 8u + groupThreadId.xy;
    uint sourceSize = max(max(PageHiZ.PageSize / 2u, 1u) >> PageHiZ.SourceMip, 1u);
    uint2 source = min(pixel * 2u, sourceSize - 1u);
    uint2 next = min(source + 1u, sourceSize - 1u);
    float d0 = VirtualShadowPageHiZSource.Load(int4(CalculateDepthPageHiZPixel(physicalPage, source, sourceSize, PageHiZ.PhysicalPagesPerRow), 0));
    float d1 = VirtualShadowPageHiZSource.Load(int4(CalculateDepthPageHiZPixel(physicalPage, uint2(next.x, source.y), sourceSize, PageHiZ.PhysicalPagesPerRow), 0));
    float d2 = VirtualShadowPageHiZSource.Load(int4(CalculateDepthPageHiZPixel(physicalPage, uint2(source.x, next.y), sourceSize, PageHiZ.PhysicalPagesPerRow), 0));
    float d3 = VirtualShadowPageHiZSource.Load(int4(CalculateDepthPageHiZPixel(physicalPage, next, sourceSize, PageHiZ.PhysicalPagesPerRow), 0));
    StoreVirtualShadowPageHiZ(max(max(d0, d1), max(d2, d3)), physicalPage, pixel, groupThreadId.xy);
}
