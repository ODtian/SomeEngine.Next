#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"

struct VirtualShadowFilterRequests
{
    uint ViewCount;
    uint PageTableEntryCount;
    uint FrameIndex;
    uint EntriesPerView;
};
ConstantBuffer<VirtualShadowFilterRequests> FilterRequests;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
#include "vsm_page_request_common.slang"

[shader("compute")]
[numthreads(64,1,1)]
void PropagateVirtualShadowFilterRequests(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= FilterRequests.PageTableEntryCount || RequestedPageFlags[index] == 0u) return;
    uint viewIndex = VirtualShadowViewFromPageTableIndex(ShadowViews, FilterRequests.ViewCount, index);
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    if ((view.Flags & VSM_VIEW_PREFILTERED_DISTANT) == 0u) return;
    uint mip;
    uint2 page;
    if (!VirtualShadowDecodePageTableIndex(view, index, mip, page) || mip != 0u) return;
    uint2 mask = (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u ? LoadVirtualShadowReceiverMask(PageReceiverMasks, view, mip, page) : 0xffffffffu.xx;
    int quarterCells = int(VirtualShadowPagesPerRow(view) * 2u);
    int2 sourceCorner = int2(view.ClipmapCornerRelativeOffsetX, view.ClipmapCornerRelativeOffsetY);
    uint remaining = view.ClipmapLevelCount - uint(view.ClipmapLevel - view.FirstClipmapLevel);
    for (uint offset = 1u; offset < remaining; offset++)
    {
        VirtualShadowMapParameters coarse = ShadowViews[viewIndex + offset];
        int2 coarseCorner = int2(coarse.ClipmapCornerRelativeOffsetX, coarse.ClipmapCornerRelativeOffsetY);
        int2 cellBias = ((quarterCells * coarseCorner) << offset) - quarterCells * sourceCorner;
        for (uint bit = 0u; bit < 64u; bit++)
        {
            if (!VirtualShadowReceiverMaskCell(mask, uint2(bit & 7u, bit >> 3u))) continue;
            int2 cell = (int2(page * 8u + uint2(bit & 7u, bit >> 3u)) + cellBias) >> offset;
            if (any(cell < 0) || any(cell >= int(VirtualShadowPagesPerRow(coarse) * 8u))) continue;
            uint2 coarsePage = uint2(cell) >> 3u;
            uint destination = VirtualShadowPageTableIndex(coarse, 0u, coarsePage);
            PublishVirtualShadowRequest(destination, VSM_PAGE_REQUEST_DETAIL | VSM_PAGE_REQUEST_SECONDARY,
                FilterRequests.FrameIndex, FilterRequests.PageTableEntryCount);
            MarkVirtualShadowReceiverCell(coarse, 0u, coarsePage, uint2(cell) & 7u);
        }
    }
}
