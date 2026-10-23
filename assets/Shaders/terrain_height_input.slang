#ifndef TERRAIN_HEIGHT_INPUT
#define TERRAIN_HEIGHT_INPUT

struct TerrainSurfaceParameters
{
    float3 Min;
    float CellSize;
    int CellCount;
    int HeightWidth;
    int HeightDepth;
    float HeightSpacing;
    int PageSize;
    int SourcePageX;
    int SourcePageZ;
    int SourcePageWidth;
    float InitialHeight;
    uint VertexCapacity;
    uint TriangleCapacity;
};

ConstantBuffer<TerrainSurfaceParameters> TerrainParameters;
StructuredBuffer<float> TerrainHeights;
StructuredBuffer<int> TerrainPageOffsets;

float TerrainHeightAt(int x, int z)
{
    int size = TerrainParameters.PageSize;
    int page = (z / size - TerrainParameters.SourcePageZ) * TerrainParameters.SourcePageWidth
        + x / size - TerrainParameters.SourcePageX;
    int offset = TerrainPageOffsets[page];
    return offset < 0 ? TerrainParameters.InitialHeight
        : TerrainHeights[offset + (z % size) * size + x % size];
}

float SampleTerrainHeight(float2 position)
{
    float2 sample = clamp(position / TerrainParameters.HeightSpacing, 0.0f,
        float2(TerrainParameters.HeightWidth - 1, TerrainParameters.HeightDepth - 1));
    int2 lower = int2(floor(sample));
    int2 upper = min(lower + 1, int2(TerrainParameters.HeightWidth - 1, TerrainParameters.HeightDepth - 1));
    float2 fraction = sample - float2(lower);
    return lerp(lerp(TerrainHeightAt(lower.x, lower.y), TerrainHeightAt(upper.x, lower.y), fraction.x),
        lerp(TerrainHeightAt(lower.x, upper.y), TerrainHeightAt(upper.x, upper.y), fraction.x), fraction.y);
}

float SampleTerrainSurface(float3 position)
{
    return position.y - SampleTerrainHeight(position.xz);
}

float3 TerrainSurfaceNormal(float3 position)
{
    float step = TerrainParameters.CellSize;
    return normalize(float3(
        SampleTerrainSurface(position + float3(step, 0, 0)) - SampleTerrainSurface(position - float3(step, 0, 0)),
        SampleTerrainSurface(position + float3(0, step, 0)) - SampleTerrainSurface(position - float3(0, step, 0)),
        SampleTerrainSurface(position + float3(0, 0, step)) - SampleTerrainSurface(position - float3(0, 0, step))));
}

float2 TerrainColumnHeightRange(float2 minimum, float2 maximum)
{
    int2 last = int2(TerrainParameters.HeightWidth - 1, TerrainParameters.HeightDepth - 1);
    int2 first = clamp(int2(floor(minimum / TerrainParameters.HeightSpacing)), 0, last);
    int2 end = clamp(int2(ceil(maximum / TerrainParameters.HeightSpacing)), 0, last);
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
