#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "cluster_bvh_cull_common.slang"
#include "resource_effects.slang"

struct ClusterSceneUniforms
{
    uint InstanceCount;
    uint HasPreviousBounds;
    uint UseInstanceGroups;
    uint Pad1;
    float3 PreviousWorldTranslation;
    float TranslationPadding;
};
ConstantBuffer<ClusterSceneUniforms> Scene;
ByteAddressBuffer GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
StructuredBuffer<uint> SceneInstances;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float4> SceneChunks;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<float4> InstanceWorldBounds;
StructuredBuffer<uint> MaterialBounds;

groupshared float3 ChunkMin[64];
groupshared float3 ChunkMax[64];
groupshared uint ChunkFinite[64];

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildClusterSceneBounds(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = group.x * 64u + lane;
    float3 worldMin = 1e30.xxx;
    float3 worldMax = -1e30.xxx;
    uint finite = 1u;
    if (index < Scene.InstanceCount)
    {
        uint instance = Scene.UseInstanceGroups != 0u ? SceneInstances[index] : index;
        uint root = LoadClusterBvhRoot(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BVH_ROOT_METADATA_WORD), instance);
        float3 previousMin = worldMin;
        float3 previousMax = worldMax;
        if (root != 0xFFFFFFFFu)
        {
            finite = HasFiniteClusterInstanceBounds(MaterialBounds, InstanceData, InstanceProperties, instance) ? 1u : 0u;
            RenderInstanceTransform current = LoadRenderTransform(InstanceData,
                LoadRenderInstanceMetadata(InstanceProperties, RENDER_CURRENT_TRANSFORM_METADATA_WORD), instance);
            float expansion = max(0.0, LoadClusterBoundsExpansion(InstanceData,
                LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BOUNDS_EXPANSION_METADATA_WORD), instance));
            ClusterRootWorldBounds(GlobalBVH, root, current, expansion, worldMin, worldMax);
            if (Scene.HasPreviousBounds == 0u)
            {
                RenderInstanceTransform previous = LoadRenderTransform(InstanceData,
                    LoadRenderInstanceMetadata(InstanceProperties, RENDER_PREVIOUS_TRANSFORM_METADATA_WORD), instance);
                ClusterRootWorldBounds(GlobalBVH, root, previous, expansion, previousMin, previousMax);
            }
        }
        if (Scene.HasPreviousBounds != 0u)
        {
            previousMin = InstanceWorldBounds[instance * 4u].xyz;
            previousMax = InstanceWorldBounds[instance * 4u + 1u].xyz;
        }
        InstanceWorldBounds[instance * 4u] = float4(worldMin, 0.0);
        InstanceWorldBounds[instance * 4u + 1u] = float4(worldMax, 0.0);
        InstanceWorldBounds[instance * 4u + 2u] = float4(previousMin + Scene.PreviousWorldTranslation, 0.0);
        InstanceWorldBounds[instance * 4u + 3u] = float4(previousMax + Scene.PreviousWorldTranslation, 0.0);
    }
    if (Scene.UseInstanceGroups == 0u)
        return;

    uint wave = lane / WaveGetLaneCount();
    float3 low = WaveActiveMin(worldMin);
    float3 high = WaveActiveMax(worldMax);
    uint bounded = WaveActiveMin(finite);
    if (WaveIsFirstLane()) { ChunkMin[wave] = low; ChunkMax[wave] = high; ChunkFinite[wave] = bounded; }
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u)
    {
        for (uint index = 1u; index < (64u + WaveGetLaneCount() - 1u) / WaveGetLaneCount(); index++)
        {
            ChunkMin[0] = min(ChunkMin[0], ChunkMin[index]);
            ChunkMax[0] = max(ChunkMax[0], ChunkMax[index]);
            ChunkFinite[0] = min(ChunkFinite[0], ChunkFinite[index]);
        }
        SceneChunks[group.x * 2u] = float4(ChunkMin[0], float(ChunkFinite[0]));
        SceneChunks[group.x * 2u + 1u] = float4(ChunkMax[0], 0.0);
    }
}
