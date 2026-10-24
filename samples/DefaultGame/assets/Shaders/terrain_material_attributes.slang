#include "terrain_height_input.slang"
#include "terrain_voxel_input.slang"

// Example channels: local X/Z texture coordinates and the first declared height field's lift.
StructuredBuffer<int> TerrainAttributeHeightSources;
StructuredBuffer<int> TerrainAttributeVoxelSources;
ByteAddressBuffer TerrainSamples;
ByteAddressBuffer TerrainPositions;
ByteAddressBuffer TerrainIndices;
ByteAddressBuffer TerrainCounts;
RWByteAddressBuffer TerrainAttributes;
RWByteAddressBuffer TerrainTriangleMaterials;

[shader("compute")]
[numthreads(64, 1, 1)]
void CreateGroundAttributes(uint3 threadId : SV_DispatchThreadID)
{
    uint vertex = threadId.x;
    if (vertex >= TerrainCounts.Load(0u)) return;
    float3 position = asfloat(TerrainSamples.Load3(vertex * 24u));
    float lift = SampleTerrainHeight(TerrainSources[TerrainAttributeHeightSources[0]], position.xz);
    TerrainAttributes.Store2(vertex * 12u, asuint(position.xz));
    TerrainAttributes.Store(vertex * 12u + 8u, asuint(lift));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CreateGroundMaterials(uint3 threadId : SV_DispatchThreadID)
{
    uint triangle = threadId.x;
    if (triangle >= TerrainCounts.Load(4u) / 3u) return;
    uint3 indices = TerrainIndices.Load3(triangle * 12u);
    float3 center = (asfloat(TerrainPositions.Load3(indices.x * 12u))
        + asfloat(TerrainPositions.Load3(indices.y * 12u))
        + asfloat(TerrainPositions.Load3(indices.z * 12u))) / 3.0f;
    float region = SampleTerrainVoxels(TerrainSources[TerrainAttributeVoxelSources[0]], center);
    // This example selects two material groups from the declared Region field.
    TerrainTriangleMaterials.Store(triangle * 4u, region < 0.0f ? 0u : 1u);
}
