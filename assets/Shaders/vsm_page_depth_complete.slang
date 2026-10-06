// This producer uses page definitions, not the material receiver-mask resource.
#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_depth_cache.slang"

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
    // Final static HiZ precedes this marker and remains whole-page proof.
    CompletedPageMetadata[page.x].y = page.w;
    CompletedPageMetadata[page.x].w = masked ? VSM_PAGE_MASKED | VSM_PAGE_DYNAMIC_UNCACHED : 0u;
}
