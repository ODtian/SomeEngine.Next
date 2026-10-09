#ifndef CLUSTER_HIERARCHY_SLANG
#define CLUSTER_HIERARCHY_SLANG

// UE 5.8 FPackedHierarchyNode: four 16B LOD/center/extent blocks, four 12B metadata blocks.
struct ClusterHierarchySlice
{
    float4 LODBounds;
    float3 BoundsCenter;
    float MinLODError;
    float3 BoundsExtent;
    float MaxParentLODError;
    uint ChildReference;
    uint PageRangeKey;
    uint GroupPartData;
    uint BoneData;
};

ClusterHierarchySlice LoadClusterNode(ByteAddressBuffer hierarchy, uint nodeIndex, uint childIndex)
{
    uint address = nodeIndex * 240u;
    uint4 center = hierarchy.Load<uint4>(address + 64u + childIndex * 16u);
    uint4 extent = hierarchy.Load<uint4>(address + 128u + childIndex * 16u);
    uint3 words = hierarchy.Load<uint3>(address + 192u + childIndex * 12u);
    ClusterHierarchySlice child;
    child.LODBounds = asfloat(hierarchy.Load<uint4>(address + childIndex * 16u));
    child.BoundsCenter = asfloat(center.xyz);
    child.MinLODError = f16tof32(center.w >> 16u);
    child.MaxParentLODError = f16tof32(center.w & 65535u);
    child.BoundsExtent = asfloat(extent.xyz);
    child.ChildReference = extent.w;
    child.PageRangeKey = words.x;
    child.GroupPartData = words.y;
    child.BoneData = words.z;
    return child;
}

bool ClusterSliceEnabled(ClusterHierarchySlice child) { return child.PageRangeKey != 0u || (child.GroupPartData >> 16u) != 0u; }
bool ClusterSliceLeaf(ClusterHierarchySlice child) { return child.PageRangeKey != 0xFFFFFFFFu; }
bool ClusterSliceAvailable(ClusterHierarchySlice child) { return child.ChildReference != 0xFFFFFFFFu; }
uint ClusterSlicePageOffset(ClusterHierarchySlice child) { return (child.ChildReference >> 8u) << 9u; }
uint ClusterSliceFirstCluster(ClusterHierarchySlice child) { return child.ChildReference & 255u; }
uint ClusterSliceClusterCount(ClusterHierarchySlice child) { return child.GroupPartData >> 16u; }
uint ClusterSliceFirstPage(ClusterHierarchySlice child) { return (child.PageRangeKey >> 5u) & 0x1FFFFFFu; }
uint ClusterSlicePageCount(ClusterHierarchySlice child) { return child.PageRangeKey & 31u; }

#endif
