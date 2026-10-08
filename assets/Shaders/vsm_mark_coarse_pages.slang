#define VSM_WHOLE_PAGE_SAMPLING 1
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
#include "vsm_page_request_common.slang"

void PublishPage(
    VirtualShadowMapParameters view,
    uint mipLevel,
    uint2 localPage)
{
    uint tableIndex = VirtualShadowPageTableIndex(view, mipLevel, localPage);
    PublishVirtualShadowRequest(
        tableIndex,
        VSM_PAGE_REQUEST_COARSE,
        CoarsePageDemand.FrameIndex,
        CoarsePageDemand.PageTableEntryCount);
    if ((view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
        MarkVirtualShadowFullReceiverMask(view, mipLevel, localPage);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void MarkVirtualShadowCoarsePages(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint viewIndex = dispatchThreadId.x;
    if (viewIndex >= CoarsePageDemand.ViewCount)
        return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];

    if (view.LightKind == VSM_LIGHT_DIRECTIONAL)
    {
        if ((view.Flags & VSM_VIEW_COARSE_CLIP_LEVEL) == 0u)
            return;
        // Coarse coverage follows the current camera origin inside the snapped
        // projection. The four page addresses surround this projected origin.
        uint dimension = VirtualShadowMipPagesPerRow(view, 0u);
        float4 originClip = ProjectVirtualShadowClip(view, view.ClipmapWorldOrigin);
        float2 originUv = originClip.xy / originClip.w * float2(0.5, -0.5) + 0.5;
        float2 pageAddress = originUv * float(dimension);
        uint2 low = (uint2)floor(pageAddress - 0.5);
        uint2 high = (uint2)ceil(pageAddress - 0.5);
        PublishPage(view, 0u, uint2(low.x, low.y));
        PublishPage(view, 0u, uint2(low.x, high.y));
        PublishPage(view, 0u, uint2(high.x, low.y));
        PublishPage(view, 0u, uint2(high.x, high.y));
        return;
    }

    if ((view.Flags & (VSM_VIEW_COARSE_CLIP_LEVEL | VSM_VIEW_SINGLE_PAGE)) == 0u)
        return;
    uint lastMip = view.MipLevelCount - 1u;
    PublishPage(view, lastMip, 0u.xx);
}
