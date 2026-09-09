#include "cluster_bvh_traverse_core.slang"
#include "cluster_raster_target_filter.slang"

[shader("compute")]
[numthreads(64, 1, 1)]
void TraverseHierarchyLevel(
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ProcessTraversalLevel(groupID.x + groupID.y * 65535u, groupThreadIndex);
}
