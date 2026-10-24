#include "comparison_common.slang"
RWStructuredBuffer<uint> Heads;
RWStructuredBuffer<uint> Counters;

[shader("compute")]
[numthreads(256, 1, 1)]
void ClearCounts(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x < Settings.Width * Settings.Height) Heads[threadId.x] = 0;
    if (threadId.x == 0) { Counters[0] = 0; Counters[1] = 0; }
}

[shader("compute")]
[numthreads(256, 1, 1)]
void ClearHeads(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x < Settings.Width * Settings.Height) Heads[threadId.x] = 0xffffffff;
    if (threadId.x == 0) Counters[0] = 0;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void FindMaximumCount(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= Settings.Width * Settings.Height) return;
    uint ignored;
    InterlockedMax(Counters[1], Heads[threadId.x], ignored);
}
