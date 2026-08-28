struct VirtualShadowPageClearUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint AtlasSize;
    uint Pad0;
};

ConstantBuffer<VirtualShadowPageClearUniforms> PageClear;
StructuredBuffer<uint> ActivePhysicalPages;
RWTexture2D<uint> ShadowPhysicalAtlas;

[shader("compute")]
[numthreads(8, 8, 1)]
void ClearVirtualShadowPages(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID)
{
    uint physicalPage = ActivePhysicalPages[groupId.x];
    uint2 physicalPageCoord = uint2(
        physicalPage % PageClear.PhysicalPagesPerRow,
        physicalPage / PageClear.PhysicalPagesPerRow);
    uint2 pagePixel = uint2(groupId.y, groupId.z) * 8u + groupThreadId.xy;
    if (any(pagePixel >= PageClear.PageSize))
        return;
    uint2 atlasPixel = physicalPageCoord * PageClear.PageSize + pagePixel;
    if (all(atlasPixel < PageClear.AtlasSize))
        ShadowPhysicalAtlas[atlasPixel] = 0u;
}
