// This producer uses page definitions, not the material receiver-mask resource.
#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_depth_cache.slang"

StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> CompletePageCount;
RWStructuredBuffer<uint> CompletedShadowPageTable;
ByteAddressBuffer GeometryWorkStatus;
RWStructuredBuffer<VirtualShadowPageMetadata> CompletedPageMetadata;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompleteVirtualShadowPageDepth(uint3 thread : SV_DispatchThreadID)
{
    // A submitted raster pass does not prove caster/material/surface acceptance.
    // Failure leaves both sampling and next-frame HiZ without completion evidence.
    if (GeometryWorkStatus.Load(0u) != 0u || thread.x >= CompletePageCount[0]) return;
    uint4 page = ActivePhysicalPages[thread.x];
    uint entry = CompletedShadowPageTable[page.y];
    uint flags = CompletedPageMetadata[page.x].Flags;
    bool masked = (flags & VSM_PAGE_MASKED) != 0u;
    entry = (entry & (0x000fffffu | VSM_PAGE_PRIMARY | VSM_PAGE_SECONDARY)) | VSM_PAGE_RENDERING | VSM_PAGE_ANY_DEPTH |
        (masked ? VSM_PAGE_PARTIAL_DEPTH : 0u);
    CompletedShadowPageTable[page.y] = entry;
    // Final static and combined HiZ precede this marker and retain far uncovered pixels.
    CompletedPageMetadata[page.x].StaticDepthGeneration = page.w;
    CompletedPageMetadata[page.x].Flags = (masked ? VSM_PAGE_MASKED | VSM_PAGE_DYNAMIC_UNCACHED : 0u) |
        (flags & VSM_PAGE_DEFERRED_INVALIDATION);
}
