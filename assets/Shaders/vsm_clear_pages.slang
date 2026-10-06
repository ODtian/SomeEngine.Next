#include "vsm_page_dispatch.slang"

struct VirtualShadowPageClearUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint AtlasSize;
    uint Pad0;
};

ConstantBuffer<VirtualShadowPageClearUniforms> PageClear;
StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> ActivePhysicalPageCount;
RWTexture2D<uint> ShadowPhysicalAtlas;

[shader("compute")]
[numthreads(8, 8, 1)]
void ClearVirtualShadowPages(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID)
{
    uint tiles = (max(PageClear.PageSize / 2u, 1u) + 7u) / 8u;
    uint pageIndex = VirtualShadowDispatchPage(groupId, tiles);
    if (pageIndex >= ActivePhysicalPageCount[0]) return;
    uint physicalPage = ActivePhysicalPages[pageIndex].x;
    uint2 physicalPageCoord = uint2(
        physicalPage % PageClear.PhysicalPagesPerRow,
        physicalPage / PageClear.PhysicalPagesPerRow);
    uint2 basePixel = (VirtualShadowDispatchTile(groupId, tiles) * 8u + groupThreadId.xy) * 2u;
    [unroll]
    for (uint y = 0u; y < 2u; y++)
    [unroll]
    for (uint x = 0u; x < 2u; x++)
    {
        uint2 pagePixel = basePixel + uint2(x, y);
        uint2 atlasPixel = physicalPageCoord * PageClear.PageSize + pagePixel;
        if (all(pagePixel < PageClear.PageSize) && all(atlasPixel < PageClear.AtlasSize))
            ShadowPhysicalAtlas[atlasPixel] = 0u;
    }
}
