#ifndef CLUSTER_DEFORM_BOUNDS_SLANG
#define CLUSTER_DEFORM_BOUNDS_SLANG

StructuredBuffer<float4> ClusterCurrentBounds;
StructuredBuffer<float4> ClusterPreviousBounds;

uint2 LoadClusterDeformBounds(uint instance)
{
    return RenderInstanceLoad<uint2>(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_DEFORM_BOUNDS_METADATA_WORD), instance, 8u);
}

void ReadClusterModelBounds(uint index, bool previous, out float3 minimum, out float3 maximum)
{
    minimum = previous ? ClusterPreviousBounds[index * 2u].xyz : ClusterCurrentBounds[index * 2u].xyz;
    maximum = previous ? ClusterPreviousBounds[index * 2u + 1u].xyz : ClusterCurrentBounds[index * 2u + 1u].xyz;
}

bool ApplyClusterNodeBounds(inout ClusterBVHNode node, uint nodeIndex, uint instance, bool previous)
{
    uint2 range = LoadClusterDeformBounds(instance);
    if (range.y == 0u) return false;
    uint root = LoadClusterBvhRoot(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BVH_ROOT_METADATA_WORD), instance);
    uint firstNode = root + 1u - range.y;
    float3 minimum, maximum;
    ReadClusterModelBounds(range.x + nodeIndex - firstNode, previous, minimum, maximum);
    node.BoundMin.xyz = minimum;
    node.BoundMax.xyz = maximum;
    node.LODSphere = float4((minimum + maximum) * 0.5, length(maximum - minimum) * 0.5);
    return true;
}

void ApplyDrawClusterBounds(inout PackedCluster cluster, uint clusterIndex, uint instance, bool previous)
{
    uint2 range = LoadClusterDeformBounds(instance);
    if (range.y == 0u) return;
    ReadClusterModelBounds(range.x + range.y + clusterIndex, previous, cluster.BoundMin, cluster.BoundMax);
    cluster.LODCenter = (cluster.BoundMin + cluster.BoundMax) * 0.5;
    cluster.LODRadius = length(cluster.BoundMax - cluster.BoundMin) * 0.5;
}

#endif
