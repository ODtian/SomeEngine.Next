#ifndef TERRAIN_BLEND_INPUT
#define TERRAIN_BLEND_INPUT

#include "terrain_height_input.slang"
#include "terrain_voxel_input.slang"

struct TerrainSurfaceNode
{
    int Parent;
    int Source;
    int SecondChild;
    float Weight;
    float3 RegionMin;
    float FadeDistance;
    float3 RegionMax;
    int HasRegion;
};

StructuredBuffer<TerrainSurfaceNode> TerrainSurfaceNodes;

float TerrainBlendWeight(TerrainSurfaceNode node, float3 position)
{
    float weight = node.Weight;
    if (node.HasRegion != 0)
    {
        float3 distances = min(position - node.RegionMin, node.RegionMax - position);
        float distance = min(distances.x, min(distances.y, distances.z));
        float fraction = clamp(distance / node.FadeDistance, 0.0f, 1.0f);
        weight *= fraction * fraction * (3.0f - 2.0f * fraction);
    }
    return weight;
}

float SampleTerrainSurface(float3 position)
{
    uint nodeCount, stride;
    TerrainSurfaceNodes.GetDimensions(nodeCount, stride);
    float value = 0;
    for (uint index = 0; index < nodeCount; index++)
    {
        TerrainSurfaceNode node = TerrainSurfaceNodes[index];
        if (node.Source < 0) continue;
        TerrainScalarSource source = TerrainSources[node.Source];
        float sample = source.Kind == 0 ? position.y - SampleTerrainHeight(source, position.xz)
            : SampleTerrainVoxels(source, position);
        // Each blend scales its first and second subtrees by complementary weights.
        int child = int(index);
        int parent = node.Parent;
        while (parent >= 0)
        {
            TerrainSurfaceNode blend = TerrainSurfaceNodes[parent];
            float weight = TerrainBlendWeight(blend, position);
            sample *= child == blend.SecondChild ? weight : 1.0f - weight;
            child = parent;
            parent = blend.Parent;
        }
        value += sample;
    }
    return value;
}

#endif
