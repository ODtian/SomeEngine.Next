#ifndef TERRAIN_VOXEL_INPUT
#define TERRAIN_VOXEL_INPUT

#include "terrain_surface_sources.slang"

int TerrainVoxelPage(TerrainScalarSource source, int coordinate)
{
    int size = source.PageSize;
    return coordinate >= 0 ? coordinate / size : (coordinate + 1) / size - 1;
}

float TerrainVoxelAt(TerrainScalarSource source, int x, int y, int z)
{
    int3 page = int3(TerrainVoxelPage(source, x), TerrainVoxelPage(source, y), TerrainVoxelPage(source, z));
    int3 relative = page - int3(source.SourcePageX, source.SourcePageY, source.SourcePageZ);
    int tableIndex = (relative.z * source.SourcePageHeight + relative.y) * source.SourcePageWidth + relative.x;
    int offset = TerrainSourcePageOffsets[source.PageOffsetsStart + tableIndex];
    int size = source.PageSize;
    int3 local = int3(x, y, z) - page * size;
    return offset < 0 ? source.InitialValue
        : TerrainSourceSamples[source.SamplesStart + offset + (local.z * size + local.y) * size + local.x];
}

float SampleTerrainVoxels(TerrainScalarSource source, float3 position)
{
    float3 sample = position / source.Spacing;
    int3 lower = int3(floor(sample));
    float3 fraction = sample - float3(lower);
    float lowYLowZ = lerp(TerrainVoxelAt(source, lower.x, lower.y, lower.z), TerrainVoxelAt(source, lower.x + 1, lower.y, lower.z), fraction.x);
    float highYLowZ = lerp(TerrainVoxelAt(source, lower.x, lower.y + 1, lower.z), TerrainVoxelAt(source, lower.x + 1, lower.y + 1, lower.z), fraction.x);
    float lowYHighZ = lerp(TerrainVoxelAt(source, lower.x, lower.y, lower.z + 1), TerrainVoxelAt(source, lower.x + 1, lower.y, lower.z + 1), fraction.x);
    float highYHighZ = lerp(TerrainVoxelAt(source, lower.x, lower.y + 1, lower.z + 1), TerrainVoxelAt(source, lower.x + 1, lower.y + 1, lower.z + 1), fraction.x);
    return lerp(lerp(lowYLowZ, highYLowZ, fraction.y), lerp(lowYHighZ, highYHighZ, fraction.y), fraction.z);
}

#endif
