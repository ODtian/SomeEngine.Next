#ifndef TERRAIN_VOXEL_INPUT
#define TERRAIN_VOXEL_INPUT

#include "terrain_surface_input.slang"

struct VoxelSurfaceParameters
{
    int PageSize;
    int SourcePageX;
    int SourcePageY;
    int SourcePageZ;
    int SourcePageWidth;
    int SourcePageHeight;
    float Spacing;
    float InitialValue;
};

ConstantBuffer<VoxelSurfaceParameters> TerrainVoxelParameters;
StructuredBuffer<float> TerrainVoxelSamples;
StructuredBuffer<int> TerrainVoxelPageOffsets;

int TerrainVoxelPage(int coordinate)
{
    int size = TerrainVoxelParameters.PageSize;
    return coordinate >= 0 ? coordinate / size : (coordinate + 1) / size - 1;
}

float TerrainVoxelAt(int x, int y, int z)
{
    int3 page = int3(TerrainVoxelPage(x), TerrainVoxelPage(y), TerrainVoxelPage(z));
    int3 relative = page - int3(TerrainVoxelParameters.SourcePageX, TerrainVoxelParameters.SourcePageY,
        TerrainVoxelParameters.SourcePageZ);
    int tableIndex = (relative.z * TerrainVoxelParameters.SourcePageHeight + relative.y)
        * TerrainVoxelParameters.SourcePageWidth + relative.x;
    int offset = TerrainVoxelPageOffsets[tableIndex];
    int size = TerrainVoxelParameters.PageSize;
    int3 local = int3(x, y, z) - page * size;
    return offset < 0 ? TerrainVoxelParameters.InitialValue
        : TerrainVoxelSamples[offset + (local.z * size + local.y) * size + local.x];
}

float SampleTerrainVoxels(float3 position)
{
    float3 sample = position / TerrainVoxelParameters.Spacing;
    int3 lower = int3(floor(sample));
    float3 fraction = sample - float3(lower);
    float lowYLowZ = lerp(TerrainVoxelAt(lower.x, lower.y, lower.z), TerrainVoxelAt(lower.x + 1, lower.y, lower.z), fraction.x);
    float highYLowZ = lerp(TerrainVoxelAt(lower.x, lower.y + 1, lower.z), TerrainVoxelAt(lower.x + 1, lower.y + 1, lower.z), fraction.x);
    float lowYHighZ = lerp(TerrainVoxelAt(lower.x, lower.y, lower.z + 1), TerrainVoxelAt(lower.x + 1, lower.y, lower.z + 1), fraction.x);
    float highYHighZ = lerp(TerrainVoxelAt(lower.x, lower.y + 1, lower.z + 1), TerrainVoxelAt(lower.x + 1, lower.y + 1, lower.z + 1), fraction.x);
    return lerp(lerp(lowYLowZ, highYLowZ, fraction.y), lerp(lowYHighZ, highYHighZ, fraction.y), fraction.z);
}

#endif
