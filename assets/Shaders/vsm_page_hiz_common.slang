#include "resource_effects.slang"
#include "depth_page_hiz.slang"
#include "vsm_page_dispatch.slang"
#include "vsm_page_entry.slang"
#include "vsm_depth_cache.slang"

struct VirtualShadowPageHiZUniforms
{
    uint PageSize;
    uint PhysicalPagesPerRow;
    uint SourceMip;
    uint DestinationMip;
    uint StaticLayerOffset;
    uint3 Padding;
};

ConstantBuffer<VirtualShadowPageHiZUniforms> PageHiZ;
StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> ActivePhysicalPageCount;
StructuredBuffer<VirtualShadowPageMetadata> PageHiZMetadata;
#if VSM_PAGE_HIZ_INITIALIZE
Texture2DArray<uint> ShadowPhysicalAtlas;
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

bool NeedsVirtualShadowPageHiZUpdate(uint physicalPage)
{
    // Allocation keeps static depth only after its page identity and completion
    // match. Dynamic-only redraws leave both static depth and its HiZ unchanged.
    return PageHiZ.StaticLayerOffset != 0u ||
        (PageHiZMetadata[physicalPage].Flags & VSM_PAGE_STATIC_UNCACHED) != 0u;
}

void WriteVirtualShadowPageHiZ(RWTexture2DArray<float> output, uint3 address, float2 depth)
{
    if (PageHiZ.StaticLayerOffset == 0u) output[address] = depth.x;
    else
    {
        output[address] = depth.y;
        address.z += PageHiZ.StaticLayerOffset;
        output[address] = depth.x;
    }
}

#ifndef VSM_PAGE_HIZ_INITIALIZE
#define VSM_PAGE_HIZ_INITIALIZE 0
#endif
#if !VSM_PAGE_HIZ_INITIALIZE
float2 ReadVirtualShadowPageHiZ(uint3 address, uint mip)
{
    float combined = VirtualShadowPageHiZSource.Load(int4(address, mip));
    if (PageHiZ.StaticLayerOffset == 0u) return combined.xx;
    address.z += PageHiZ.StaticLayerOffset;
    return float2(VirtualShadowPageHiZSource.Load(int4(address, mip)), combined);
}
#endif

groupshared float2 PageHiZTile[64];

void StoreVirtualShadowPageHiZ(float2 depth, uint physicalPage, uint2 pixel, uint2 thread)
{
    uint baseSize = max(PageHiZ.PageSize / 2u, 1u);
    uint destinationSize = max(baseSize >> PageHiZ.DestinationMip, 1u);
    uint lane = thread.y * 8u + thread.x;
    PageHiZTile[lane] = depth;
    if (all(pixel < destinationSize))
        WriteVirtualShadowPageHiZ(VirtualShadowPageHiZDestination, CalculateDepthPageHiZPixel(physicalPage, pixel, destinationSize, PageHiZ.PhysicalPagesPerRow), depth);
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
                    WriteVirtualShadowPageHiZ(VirtualShadowPageHiZDestination1, CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow), depth);
                else if (step == 2u)
                    WriteVirtualShadowPageHiZ(VirtualShadowPageHiZDestination2, CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow), depth);
                else
                    WriteVirtualShadowPageHiZ(VirtualShadowPageHiZDestination3, CalculateDepthPageHiZPixel(physicalPage, outputPixel, destinationSize >> step, PageHiZ.PhysicalPagesPerRow), depth);
            }
        }
    }
}
