#ifndef CLUSTER_HIERARCHY_SLANG
#define CLUSTER_HIERARCHY_SLANG

// Mesh resources and GPU geometry builders share this 64-byte node.
struct ClusterBVHNode
{
    float4 BoundMin;
    float4 BoundMax;
    float4 LODSphere;
    float LODError;
    uint ChildPointer;
    uint ChildCount;
    uint NodeType;
};

ClusterBVHNode LoadClusterNode(ByteAddressBuffer hierarchy, uint nodeIndex)
{
    return hierarchy.Load<ClusterBVHNode>(nodeIndex * 64u);
}

#endif
