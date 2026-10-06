#define VSM_PAGE_HIZ_INITIALIZE 1
#include "vsm_page_hiz_common.slang"

[shader("compute")]
[numthreads(8, 8, 1)]
void InitializeVirtualShadowPageHiZ(
    uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint pageIndex = VirtualShadowDispatchPage(groupId);
    if (pageIndex >= ActivePhysicalPageCount[0]) return;
    uint physicalPage = ActivePhysicalPages[pageIndex].x;
    uint tiles = (max(PageHiZ.PageSize / 2u, 1u) + 7u) / 8u;
    uint2 pixel = VirtualShadowDispatchTile(groupId, tiles) * 8u + groupThreadId.xy;
    uint2 atlasPage = uint2(physicalPage % PageHiZ.PhysicalPagesPerRow,
        physicalPage / PageHiZ.PhysicalPagesPerRow) * PageHiZ.PageSize;
    uint2 source = min(pixel * 2u, PageHiZ.PageSize - 1u);
    uint2 next = min(source + 1u, PageHiZ.PageSize - 1u);
    float d0 = 1.0 - asfloat(ShadowPhysicalAtlas.Load(int3(atlasPage + source, 0)));
    float d1 = 1.0 - asfloat(ShadowPhysicalAtlas.Load(int3(atlasPage + uint2(next.x, source.y), 0)));
    float d2 = 1.0 - asfloat(ShadowPhysicalAtlas.Load(int3(atlasPage + uint2(source.x, next.y), 0)));
    float d3 = 1.0 - asfloat(ShadowPhysicalAtlas.Load(int3(atlasPage + next, 0)));
    StoreVirtualShadowPageHiZ(max(max(d0, d1), max(d2, d3)), physicalPage, pixel, groupThreadId.xy);
}
