#ifndef CLUSTER_DEFORM_BOUNDS_SLANG
#define CLUSTER_DEFORM_BOUNDS_SLANG

StructuredBuffer<float4> ClusterCurrentBounds;
StructuredBuffer<float4> ClusterPreviousBounds;

struct ClusterModelBounds
{
    float4 minimum;
    float4 maximum;
    float4 lodSphere;
};

uint2 LoadClusterDeformBounds(uint instance)
{
    return RenderInstanceLoad<uint2>(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_DEFORM_BOUNDS_METADATA_WORD), instance, 8u);
}

ClusterModelBounds ReadClusterModelBounds(uint index, bool previous)
{
    ClusterModelBounds value;
    value.minimum = previous ? ClusterPreviousBounds[index * 3u] : ClusterCurrentBounds[index * 3u];
    value.maximum = previous ? ClusterPreviousBounds[index * 3u + 1u] : ClusterCurrentBounds[index * 3u + 1u];
    value.lodSphere = previous ? ClusterPreviousBounds[index * 3u + 2u] : ClusterCurrentBounds[index * 3u + 2u];
    return value;
}

bool ApplyClusterNodeBounds(inout ClusterBVHNode node, uint nodeIndex, uint instance, bool previous)
{
    uint2 range = LoadClusterDeformBounds(instance);
    if (range.y == 0u) return false;
    uint root = LoadClusterBvhRoot(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BVH_ROOT_METADATA_WORD), instance);
    uint firstNode = root + 1u - range.y;
    ClusterModelBounds value = ReadClusterModelBounds(range.x + nodeIndex - firstNode, previous);
    node.BoundMin.xyz = value.minimum.xyz;
    node.BoundMax.xyz = value.maximum.xyz;
    node.LODSphere = value.lodSphere;
    node.LODError = value.minimum.w;
    return true;
}

float ApplyDrawClusterBounds(inout PackedCluster cluster, uint clusterIndex, uint instance, bool previous)
{
    uint2 range = LoadClusterDeformBounds(instance);
    if (range.y == 0u) return DecodeClusterLODError(cluster);
    ClusterModelBounds value = ReadClusterModelBounds(range.x + range.y + clusterIndex, previous);
    cluster.BoundMin = value.minimum.xyz;
    cluster.BoundMax = value.maximum.xyz;
    cluster.LODCenter = value.lodSphere.xyz;
    cluster.LODRadius = value.lodSphere.w;
    return value.minimum.w;
}

#endif
