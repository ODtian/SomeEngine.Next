#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_resident_math.slang"
#include "resource_effects.slang"

struct VirtualShadowResidentUniforms
{
    uint ViewCount;
    uint EntriesPerView;
    uint EntryCount;
    uint Pad0;
};
ConstantBuffer<VirtualShadowResidentUniforms> ResidentUpdate;
StructuredBuffer<VirtualShadowMapParameters> ResidentShadowViews;
StructuredBuffer<uint4> CompletedPageTable;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> ResidentPages;

bool VirtualShadowPageHasCompletedDepth(VirtualShadowMapParameters view, uint mip, uint2 page, out uint tableIndex)
{
    tableIndex = VirtualShadowPageTableIndex(view, mip, page);
    uint4 entry = CompletedPageTable[tableIndex];
    uint physicalPage;
    return IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(view, mip, page)) &&
        (entry.w == view.CacheGeneration || entry.w == (view.CacheGeneration | VSM_PAGE_RECEIVER_DEPTH)) &&
        DecodeVirtualShadowPage(entry.x, physicalPage);
}

[shader("compute")]
[numthreads(64,1,1)]
void PropagateVirtualShadowResidentPages(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= ResidentUpdate.EntryCount) return;
    ResidentPages[index] = 0u.xx;
    uint viewIndex = index / ResidentUpdate.EntriesPerView;
    if (viewIndex >= ResidentUpdate.ViewCount) return;
    VirtualShadowMapParameters view = ResidentShadowViews[viewIndex];
    uint mip;
    uint2 page;
    if (!VirtualShadowDecodePageTableIndex(view, index, mip, page) || mip < VirtualShadowFirstMip(view)) return;
    uint sourceIndex;
    if (VirtualShadowPageHasCompletedDepth(view, mip, page, sourceIndex))
    {
        ResidentPages[index] = uint2(sourceIndex + 1u, 0u);
        return;
    }
    if (view.LightKind == VSM_LIGHT_DIRECTIONAL)
    {
        uint remaining = view.ClipmapLevelCount - uint(view.ClipmapLevel - view.FirstClipmapLevel);
        for (uint offset = 1u; offset < remaining && viewIndex + offset < ResidentUpdate.ViewCount; offset++)
        {
            VirtualShadowMapParameters coarse = ResidentShadowViews[viewIndex + offset];
            if (coarse.LightKind != VSM_LIGHT_DIRECTIONAL || coarse.LightIndex != view.LightIndex) break;
            int2 coarsePage = VirtualShadowCoarseClipmapPage(page, view, coarse, offset);
            uint dimension = VirtualShadowPagesPerRow(coarse);
            if (any(coarsePage < 0) || any(uint2(coarsePage) >= dimension)) continue;
            if (VirtualShadowPageHasCompletedDepth(coarse, 0u, uint2(coarsePage), sourceIndex))
            {
                ResidentPages[index] = uint2(sourceIndex + 1u, offset);
                return;
            }
        }
    }
    else
    {
        for (uint level = mip + 1u; level < view.MipLevelCount; level++)
        {
            if (VirtualShadowPageHasCompletedDepth(view, level, page >> (level - mip), sourceIndex))
            {
                ResidentPages[index] = uint2(sourceIndex + 1u, level - mip);
                return;
            }
        }
    }
}
