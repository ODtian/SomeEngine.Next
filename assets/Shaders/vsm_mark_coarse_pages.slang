#include "vsm_sampling.slang"

struct VirtualShadowCoarsePageDemandUniforms
{
    uint ViewCount;
    uint PageTableEntryCount;
    uint FrameIndex;
    uint Pad0;
};

ConstantBuffer<VirtualShadowCoarsePageDemandUniforms> CoarsePageDemand;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
RWStructuredBuffer<uint> RequestedShadowPages;

void PublishCoarsePage(
    VirtualShadowMapParameters shadowView,
    int2 coarseWorldPage)
{
    uint tableIndex = VirtualShadowCoarsePageTableIndex(
        shadowView,
        coarseWorldPage);
    if (tableIndex < CoarsePageDemand.PageTableEntryCount)
        RequestedShadowPages[tableIndex] = CoarsePageDemand.FrameIndex;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void MarkVirtualShadowCoarsePages(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint viewIndex = dispatchThreadId.x;
    if (viewIndex >= CoarsePageDemand.ViewCount)
        return;

    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    uint center = shadowView.VirtualResolution >> 1u;
    int2 centerPage = VirtualShadowCoarseWorldPage(
        shadowView,
        uint2(center, center));

    // Local-light maps need one lowest-mip representative. Directional clipmaps retain a
    // two-by-two central footprint so clipmap scrolling never leaves the camera-side seam
    // without coarse coverage.
    PublishCoarsePage(shadowView, centerPage);
    if (shadowView.LightKind != VSM_LIGHT_DIRECTIONAL)
        return;

    PublishCoarsePage(shadowView, centerPage + int2(-1, 0));
    PublishCoarsePage(shadowView, centerPage + int2(0, -1));
    PublishCoarsePage(shadowView, centerPage + int2(-1, -1));
}
