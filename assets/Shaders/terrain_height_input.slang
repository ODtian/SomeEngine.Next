#ifndef TERRAIN_HEIGHT_INPUT
#define TERRAIN_HEIGHT_INPUT

#include "terrain_surface_sources.slang"

float TerrainHeightAt(TerrainScalarSource source, int x, int z)
{
    int size = source.PageSize;
    int page = (z / size - source.SourcePageZ) * source.SourcePageWidth + x / size - source.SourcePageX;
    int offset = TerrainSourcePageOffsets[source.PageOffsetsStart + page];
    return offset < 0 ? source.InitialValue
        : TerrainSourceSamples[source.SamplesStart + offset + (z % size) * size + x % size];
}

float SampleTerrainHeight(TerrainScalarSource source, float2 position)
{
    float2 sample = clamp(position / source.Spacing, 0.0f, float2(source.Width - 1, source.Depth - 1));
    int2 lower = int2(floor(sample));
    int2 upper = min(lower + 1, int2(source.Width - 1, source.Depth - 1));
    float2 fraction = sample - float2(lower);
    return lerp(lerp(TerrainHeightAt(source, lower.x, lower.y), TerrainHeightAt(source, upper.x, lower.y), fraction.x),
        lerp(TerrainHeightAt(source, lower.x, upper.y), TerrainHeightAt(source, upper.x, upper.y), fraction.x), fraction.y);
}

float2 TerrainColumnHeightRange(TerrainScalarSource source, float2 minimum, float2 maximum)
{
    int2 last = int2(source.Width - 1, source.Depth - 1);
    int2 first = clamp(int2(floor(minimum / source.Spacing)), 0, last);
    int2 end = clamp(int2(ceil(maximum / source.Spacing)), 0, last);
    float2 heights = float2(3.402823466e+38f, -3.402823466e+38f);
    for (int z = first.y; z <= end.y; z++)
    for (int x = first.x; x <= end.x; x++)
    {
        float value = TerrainHeightAt(source, x, z);
        heights = float2(min(heights.x, value), max(heights.y, value));
    }
    return heights;
}

#endif
