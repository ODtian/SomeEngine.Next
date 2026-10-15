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
    uint lower = DepthSlice(depth.x * .99999), upper = DepthSlice(depth.y * 1.00001);
#ifdef BLEND_DEPTH_INTERVALS
    CellSlices[index] = lower | (upper << 8);
#else
    CellSlices[index] = (0xffffffffu << lower) & (0xffffffffu >> (31 - upper));
#endif
}
