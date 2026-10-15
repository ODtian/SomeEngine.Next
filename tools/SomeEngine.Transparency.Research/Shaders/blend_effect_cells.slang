#define BLEND_CELLS 1
#include "blend_effect_common.slang"
RWStructuredBuffer<uint> CellSlices;

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildBlendCells(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= Settings.ItemCount) return;
    float2 depth = Bounds[index].DepthRange;
    CellSlices[index] = DepthSlice(depth.x * .99999) | (DepthSlice(depth.y * 1.00001) << 8);
}
