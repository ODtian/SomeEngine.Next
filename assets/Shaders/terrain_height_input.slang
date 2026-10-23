#ifndef TERRAIN_HEIGHT_INPUT
#define TERRAIN_HEIGHT_INPUT

#include "terrain_surface_input.slang"

struct HeightSurfaceParameters
{
    int Width;
    int Depth;
    float Spacing;
    float InitialHeight;
    int PageSize;
    int SourcePageX;
    int SourcePageZ;
    int SourcePageWidth;
};

ConstantBuffer<HeightSurfaceParameters> TerrainHeightParameters;
StructuredBuffer<float> TerrainHeightSamples;
StructuredBuffer<int> TerrainHeightPageOffsets;

float TerrainHeightAt(int x, int z)
{
    int size = TerrainHeightParameters.PageSize;
    int page = (z / size - TerrainHeightParameters.SourcePageZ) * TerrainHeightParameters.SourcePageWidth
        + x / size - TerrainHeightParameters.SourcePageX;
    int offset = TerrainHeightPageOffsets[page];
    return offset < 0 ? TerrainHeightParameters.InitialHeight
        : TerrainHeightSamples[offset + (z % size) * size + x % size];
}

float SampleTerrainHeight(float2 position)
{
    float2 sample = clamp(position / TerrainHeightParameters.Spacing, 0.0f,
        float2(TerrainHeightParameters.Width - 1, TerrainHeightParameters.Depth - 1));
    int2 lower = int2(floor(sample));
    int2 upper = min(lower + 1, int2(TerrainHeightParameters.Width - 1, TerrainHeightParameters.Depth - 1));
    float2 fraction = sample - float2(lower);
    return lerp(lerp(TerrainHeightAt(lower.x, lower.y), TerrainHeightAt(upper.x, lower.y), fraction.x),
        lerp(TerrainHeightAt(lower.x, upper.y), TerrainHeightAt(upper.x, upper.y), fraction.x), fraction.y);
}

float2 TerrainColumnHeightRange(float2 minimum, float2 maximum)
{
    int2 last = int2(TerrainHeightParameters.Width - 1, TerrainHeightParameters.Depth - 1);
    int2 first = clamp(int2(floor(minimum / TerrainHeightParameters.Spacing)), 0, last);
    int2 end = clamp(int2(ceil(maximum / TerrainHeightParameters.Spacing)), 0, last);
    float2 heights = float2(3.402823466e+38f, -3.402823466e+38f);
    for (int z = first.y; z <= end.y; z++)
    for (int x = first.x; x <= end.x; x++)
    {
        float value = TerrainHeightAt(x, z);
        heights = float2(min(heights.x, value), max(heights.y, value));
    }
    return heights;
}

#endif
