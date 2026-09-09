struct VirtualShadowPageClearUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint AtlasSize;
    uint Pad0;
};

ConstantBuffer<VirtualShadowPageClearUniforms> PageClear;
StructuredBuffer<uint2> ActivePhysicalPages;
RWTexture2D<uint> ShadowPhysicalAtlas;

[shader("compute")]
[numthreads(8, 8, 1)]
void ClearVirtualShadowPages(
    uint3 groupId : SV_GroupID,
    uint3 groupThreadId : SV_GroupThreadID)
{
    uint physicalPage = ActivePhysicalPages[groupId.x].x;
    uint2 physicalPageCoord = uint2(
        physicalPage % PageClear.PhysicalPagesPerRow,
        physicalPage / PageClear.PhysicalPagesPerRow);
    uint2 basePixel = (uint2(groupId.y, groupId.z) * 8u + groupThreadId.xy) * 2u;
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
