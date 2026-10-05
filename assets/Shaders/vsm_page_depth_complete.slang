StructuredBuffer<uint4> ActivePhysicalPages;
StructuredBuffer<uint> CompletePageCount;
RWStructuredBuffer<uint4> CompletedShadowPageTable;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompleteVirtualShadowPageDepth(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= CompletePageCount[0]) return;
    uint4 page = ActivePhysicalPages[thread.x];
    uint4 entry = CompletedShadowPageTable[page.y];
    entry.w = page.w;
    CompletedShadowPageTable[page.y] = entry;
}
