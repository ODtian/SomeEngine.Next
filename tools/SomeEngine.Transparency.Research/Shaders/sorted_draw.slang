#include "comparison_vertex.slang"
RWStructuredBuffer<uint> Heads;
RWStructuredBuffer<uint> Counters;
RWStructuredBuffer<Fragment> Fragments;

[shader("fragment")]
void StoreFragments(VertexOutput input)
{
    ClipOpaque(input);
    uint2 pixel = uint2(input.Position.xy);
    uint index, previous;
    InterlockedAdd(Counters[0], 1, index);
    InterlockedExchange(Heads[pixel.y * Settings.Width + pixel.x], index, previous);
    Fragment fragment;
    fragment.Depth = input.Position.z;
    fragment.TriangleId = input.TriangleId;
    fragment.ColorIndex = input.ColorIndex;
    fragment.NextIndex = previous;
    Fragments[index] = fragment;
}
