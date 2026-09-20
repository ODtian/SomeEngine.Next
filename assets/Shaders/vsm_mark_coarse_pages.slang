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
        MarkVirtualShadowFullReceiverMask(tableIndex);
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
        int lastClipmapLevel =
            view.FirstClipmapLevel + int(max(view.ClipmapLevelCount, 1u)) - 1;
        if (view.ClipmapLevel == lastClipmapLevel)
        {
            uint lastMip = max(view.MipLevelCount, 1u) - 1u;
            PublishPage(view, lastMip, 0u.xx);
        }
        if ((view.Flags & VSM_VIEW_COARSE_CLIP_LEVEL) == 0u)
            return;
        // UE-style directional coarse coverage: selected clipmap levels contribute only
        // the center pages. Sampling continues into coarser clipmaps when this view
        // has no resident page.
        uint dimension = VirtualShadowMipPagesPerRow(view, 0u);
        uint2 low = max(dimension / 2u, 1u) - 1u;
        uint2 high = min(low + 1u, dimension - 1u);
        PublishPage(view, 0u, uint2(low.x, low.y));
        PublishPage(view, 0u, uint2(low.x, high.y));
        PublishPage(view, 0u, uint2(high.x, low.y));
        PublishPage(view, 0u, uint2(high.x, high.y));
        return;
    }

    uint lastMip = max(view.MipLevelCount, 1u) - 1u;
    PublishPage(view, lastMip, 0u.xx);
}
