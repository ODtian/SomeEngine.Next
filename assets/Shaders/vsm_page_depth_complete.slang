#include "vsm_sampling.slang"

StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> CompletePageCount;
RWStructuredBuffer<uint4> CompletedShadowPageTable;
ByteAddressBuffer GeometryWorkStatus;
RWStructuredBuffer<uint4> CompletedPageMetadata;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompleteVirtualShadowPageDepth(uint3 thread : SV_DispatchThreadID)
{
    // A submitted raster pass does not prove caster/material/surface acceptance.
    // Failure leaves both sampling and next-frame HiZ without completion evidence.
    if (GeometryWorkStatus.Load(0u) != 0u || thread.x >= CompletePageCount[0]) return;
    uint4 page = ActivePhysicalPages[thread.x];
    uint4 entry = CompletedShadowPageTable[page.y];
    uint flags = CompletedPageMetadata[page.x].w;
    bool masked = (flags & VSM_PAGE_MASKED) != 0u;
    entry.w = page.w | (masked ? VSM_PAGE_RECEIVER_DEPTH : 0u);
    CompletedShadowPageTable[page.y] = entry;
    // Final HiZ precedes this marker. Only whole-page depth persists as prior proof.
    CompletedPageMetadata[page.x].y = masked ? 0u : page.w;
    CompletedPageMetadata[page.x].w = masked ? VSM_PAGE_MASKED : 0u;
}
