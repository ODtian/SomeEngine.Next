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
    if (shadowView.LightKind != VSM_LIGHT_DIRECTIONAL)
    {
        PublishCoarsePage(shadowView, int2(0));
        return;
    }

    // Directional resolution comes from larger clipmaps, not a second mip in each view.
    // Keep the pages surrounding the camera's projected position available at each level.
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(shadowView, shadowView.ClipmapWorldOrigin, ndc, uv))
        return;
    uint dimension = VirtualShadowPagesPerRow(shadowView);
    float2 pagePosition = uv * float(dimension) - 0.5;
    int2 firstPage = max(int2(floor(pagePosition)), int2(0));
    int2 lastPage = min(int2(ceil(pagePosition)), int2(dimension - 1u));
    for (int y = firstPage.y; y <= lastPage.y; y++)
        for (int x = firstPage.x; x <= lastPage.x; x++)
        {
            uint tableIndex = VirtualShadowPageTableIndex(shadowView, uint2(x, y));
            RequestedShadowPages[tableIndex] = CoarsePageDemand.FrameIndex;
        }
}
