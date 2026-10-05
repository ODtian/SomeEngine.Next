#include "resource_effects.slang"
#include "depth_page_hiz.slang"

struct VirtualShadowPageHiZUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint SourceMip;
    uint DestinationMip;
};

ConstantBuffer<VirtualShadowPageHiZUniforms> PageHiZ;
StructuredBuffer<uint4> ActivePhysicalPages;
#if VSM_PAGE_HIZ_INITIALIZE
Texture2D<uint> ShadowPhysicalAtlas;
#else
Texture2DArray<float> VirtualShadowPageHiZSource;
#endif
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> VirtualShadowPageHiZDestination;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> VirtualShadowPageHiZDestination1;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> VirtualShadowPageHiZDestination2;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> VirtualShadowPageHiZDestination3;

groupshared float PageHiZTile[64];

void StoreVirtualShadowPageHiZ(float depth, uint physicalPage, uint2 pixel, uint2 thread)
{
    uint baseSize = max(PageHiZ.PageSize / 2u, 1u);
    uint destinationSize = max(baseSize >> PageHiZ.DestinationMip, 1u);
    uint lane = thread.y * 8u + thread.x;
    PageHiZTile[lane] = depth;
    if (all(pixel < destinationSize))
        VirtualShadowPageHiZDestination[CalculateDepthPageHiZPixel(physicalPage, pixel, destinationSize, PageHiZ.PhysicalPagesPerRow)] = depth;
    // One tile produces four mips; dispatch boundaries synchronize tiles.
    [unroll]
    for (uint step = 1u; step <= 3u; step++)
    {
        GroupMemoryBarrierWithGroupSync();
        uint scale = 1u << step;
        uint half = scale >> 1u;
        if (all((thread & (scale - 1u)) == 0u.xx))
            depth = max(max(PageHiZTile[lane], PageHiZTile[lane + half]),
                max(PageHiZTile[lane + half * 8u], PageHiZTile[lane + half * 9u]));
        // Finish all reads before overwriting data another wave may still use.
        GroupMemoryBarrierWithGroupSync();
        if (all((thread & (scale - 1u)) == 0u.xx))
        {
            PageHiZTile[lane] = depth;
            uint2 outputPixel = pixel >> step;
            if (destinationSize >= scale && all(outputPixel < (destinationSize >> step)))
            {
                if (step == 1u)
                    VirtualShadowPageHiZDestination1[CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow)] = depth;
                else if (step == 2u)
                    VirtualShadowPageHiZDestination2[CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow)] = depth;
                else
                    VirtualShadowPageHiZDestination3[CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow)] = depth;
            }
        }
    }
}
