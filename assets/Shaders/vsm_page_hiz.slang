#define VSM_PAGE_HIZ_INITIALIZE 1
#include "vsm_page_hiz_common.slang"

[shader("compute")]
[numthreads(8, 8, 1)]
void InitializeVirtualShadowPageHiZ(
    uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    uint tiles = (max(PageHiZ.PageSize / 2u, 1u) + 7u) / 8u;
    uint pageIndex = VirtualShadowDispatchPage(groupId, tiles);
    if (pageIndex >= ActivePhysicalPageCount[0]) return;
    uint physicalPage = ActivePhysicalPages[pageIndex].x;
    uint2 pixel = VirtualShadowDispatchTile(groupId, tiles) * 8u + groupThreadId.xy;
    uint2 atlasPage = uint2(physicalPage % PageHiZ.PhysicalPagesPerRow,
        physicalPage / PageHiZ.PhysicalPagesPerRow) * PageHiZ.PageSize;
    uint2 source = min(pixel * 2u, PageHiZ.PageSize - 1u);
    uint2 next = min(source + 1u, PageHiZ.PageSize - 1u);
    uint4 staticDepth = uint4(
        ShadowPhysicalAtlas.Load(int4(atlasPage + source, 1, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + uint2(next.x, source.y), 1, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + uint2(source.x, next.y), 1, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + next, 1, 0)));
    uint4 dynamicDepth = 0u.xxxx;
    if (PageHiZ.StaticLayerOffset != 0u)
    {
        dynamicDepth = uint4(
        ShadowPhysicalAtlas.Load(int4(atlasPage + source, 0, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + uint2(next.x, source.y), 0, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + uint2(source.x, next.y), 0, 0)),
        ShadowPhysicalAtlas.Load(int4(atlasPage + next, 0, 0)));
    }
    float4 staticForwardDepth = 1.0 - asfloat(staticDepth);
    float4 combinedForwardDepth = 1.0 - asfloat(max(staticDepth, dynamicDepth));
    float2 depth = float2(
        max(max(staticForwardDepth.x, staticForwardDepth.y), max(staticForwardDepth.z, staticForwardDepth.w)),
        max(max(combinedForwardDepth.x, combinedForwardDepth.y), max(combinedForwardDepth.z, combinedForwardDepth.w)));
    StoreVirtualShadowPageHiZ(depth, physicalPage, pixel, groupThreadId.xy);
}
