StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> CompletePageCount;
RWStructuredBuffer<uint4> CompletedShadowPageTable;
ByteAddressBuffer GeometryWorkStatus;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompleteVirtualShadowPageDepth(uint3 thread : SV_DispatchThreadID)
{
    // An incomplete caster stream must remain uncached, including pages with clear depth.
    if (GeometryWorkStatus.Load(0u) != 0u || thread.x >= CompletePageCount[0]) return;
    uint4 page = ActivePhysicalPages[thread.x];
    uint4 entry = CompletedShadowPageTable[page.y];
    entry.w = page.w;
    CompletedShadowPageTable[page.y] = entry;
}
