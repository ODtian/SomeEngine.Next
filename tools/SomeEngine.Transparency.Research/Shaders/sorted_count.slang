#include "comparison_vertex.slang"
RWStructuredBuffer<uint> Heads;
RWStructuredBuffer<uint> Counters;

[shader("fragment")]
void CountFragments(VertexOutput input)
{
    uint2 pixel = uint2(input.Position.xy);
    uint ignored;
    InterlockedAdd(Heads[pixel.y * Settings.Width + pixel.x], 1, ignored);
    InterlockedAdd(Counters[0], 1, ignored);
}
