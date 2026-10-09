#include "vsm_page_entry.slang"
#include "vsm_page_dispatch.slang"
#include "vsm_depth_cache.slang"
struct VirtualShadowPageMergeUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint AtlasSize;
    uint Pad0;
};
ConstantBuffer<VirtualShadowPageMergeUniforms> PageMerge;
StructuredBuffer<uint4> MergePhysicalPages;
StructuredBuffer<uint> MergePhysicalPageCount;
StructuredBuffer<VirtualShadowPageMetadata> MergePageMetadata;
RWTexture2DArray<uint> ShadowMergedAtlas;
[shader("compute")]
[numthreads(8,8,1)]
void MergeVirtualShadowStaticDepth(uint3 groupId : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint tiles = (max(PageMerge.PageSize / 2u, 1u) + 7u) / 8u;
    uint pageIndex = VirtualShadowDispatchPage(groupId, tiles);
    if (pageIndex >= MergePhysicalPageCount[0]) return;
    uint page = MergePhysicalPages[pageIndex].x;
    if ((MergePageMetadata[page].Flags & VSM_PAGE_STATIC_UNCACHED) == 0u) return;
    uint2 origin = uint2(page % PageMerge.PhysicalPagesPerRow, page / PageMerge.PhysicalPagesPerRow) * PageMerge.PageSize;
    uint2 first = (VirtualShadowDispatchTile(groupId, tiles) * 8u + thread.xy) * 2u;
    for (uint y = 0u; y < 2u; y++)
    for (uint x = 0u; x < 2u; x++)
    {
        uint2 local = first + uint2(x, y);
        if (any(local >= PageMerge.PageSize)) continue;
        uint2 pixel = origin + local;
        ShadowMergedAtlas[int3(pixel, 0)] = MergeVirtualShadowDepth(ShadowMergedAtlas[int3(pixel, 0)], ShadowMergedAtlas[int3(pixel, 1)]);
    }
}
