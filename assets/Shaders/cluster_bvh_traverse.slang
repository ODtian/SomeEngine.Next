#define CLUSTER_PERSISTENT_CULL 1
#include "cluster_bvh_traverse_core.slang"
#include "cluster_raster_target_filter.slang"
#define CLUSTER_CULL_PHASE 1
#define HiZTexture HierarchyHiZ
#define NextCandidates DeferredWork
#define NextCandidateCount DeferredWorkCount
#define NextCandidateArgs DeferredWorkDispatchArgs
#include "cluster_cull.slang"

groupshared uint WorkNodeStart;
groupshared uint WorkNodeReadyMask;
groupshared ClusterTraversalNode WorkNodes[CLUSTER_BVH_PARENTS_PER_GROUP];
groupshared uint WorkClusterBatch;
groupshared uint WorkClusterReady;
groupshared uint WorkNodesRemaining;

void CullPersistentWork(uint thread)
{
    uint nodeStart = 0u;
    uint nodeUsed = CLUSTER_BVH_PARENTS_PER_GROUP;
    uint clusterBatch = 0xffffffffu;
    bool nodesWithinCapacity = true;
    for (;;)
    {
        GroupMemoryBarrierWithGroupSync();
        if (thread == 0u) WorkNodeReadyMask = 0u;
        GroupMemoryBarrierWithGroupSync();
        if (nodesWithinCapacity)
        {
            if (nodeUsed == CLUSTER_BVH_PARENTS_PER_GROUP)
            {
                if (thread == 0u)
                    NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_READ, CLUSTER_BVH_PARENTS_PER_GROUP, WorkNodeStart);
                GroupMemoryBarrierWithGroupSync();
                nodeStart = WorkNodeStart;
                nodeUsed = 0u;
                nodesWithinCapacity = nodeStart < TraversalLevel.MaxNodes;
            }
            if (nodesWithinCapacity && thread < CLUSTER_BVH_PARENTS_PER_GROUP)
            {
                uint index = nodeStart + nodeUsed + thread;
                if (nodeUsed + thread < CLUSTER_BVH_PARENTS_PER_GROUP && index < TraversalLevel.MaxNodes && TraversalReady[index] != 0u)
                {
                    DeviceMemoryBarrier();
                    WorkNodes[thread] = TraversalNodes[index];
                    InterlockedOr(WorkNodeReadyMask, 1u << thread);
                }
            }
            AllMemoryBarrierWithGroupSync();
            uint readyMask = WorkNodeReadyMask;
            if ((readyMask & 1u) != 0u)
            {
                uint count = firstbitlow(~readyMask);
                if (count > CLUSTER_BVH_PARENTS_PER_GROUP) count = CLUSTER_BVH_PARENTS_PER_GROUP;
                uint parent = min(thread / CLUSTER_BVH_FANOUT, count - 1u);
                ProcessTraversalNode(WorkNodes[parent], thread / CLUSTER_BVH_FANOUT < count,
                    thread & (CLUSTER_BVH_FANOUT - 1u));
                DeviceMemoryBarrierWithGroupSync();
                if (thread < count) TraversalReady[nodeStart + nodeUsed + thread] = 0u;
                DeviceMemoryBarrierWithGroupSync();
                if (thread == 0u)
                {
                    uint ignored;
                    NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 0u - count, ignored);
                }
                nodeUsed += count;
                continue;
            }
        }
        if (clusterBatch == 0xffffffffu)
        {
            if (thread == 0u) NodeIndirectArgs.InterlockedAdd(CLUSTER_BATCH_READ, 1u, WorkClusterBatch);
            GroupMemoryBarrierWithGroupSync();
            clusterBatch = WorkClusterBatch;
        }
        if (thread == 0u)
        {
            WorkNodesRemaining = NodeIndirectArgs.Load(CLUSTER_NODE_OUTSTANDING);
            WorkClusterReady = clusterBatch < (TraversalLevel.MaxNodes + CLUSTER_BATCH_SIZE - 1u) / CLUSTER_BATCH_SIZE
                ? ClusterBatchReady[clusterBatch] : 0u;
        }
        AllMemoryBarrierWithGroupSync();
        uint count = WorkClusterReady;
        if (count == CLUSTER_BATCH_SIZE || WorkNodesRemaining == 0u && count != 0u)
        {
            if (thread < count) CullClusterCandidate(clusterBatch * CLUSTER_BATCH_SIZE + thread);
            DeviceMemoryBarrierWithGroupSync();
            if (thread == 0u) ClusterBatchReady[clusterBatch] = 0u;
            DeviceMemoryBarrierWithGroupSync();
            clusterBatch = 0xffffffffu;
        }
        else if (WorkNodesRemaining == 0u && count == 0u)
            break;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusterWork(uint groupThreadIndex : SV_GroupThreadID)
{
    CullPersistentWork(groupThreadIndex);
}
