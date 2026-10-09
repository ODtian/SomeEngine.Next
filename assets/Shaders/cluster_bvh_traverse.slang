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
groupshared uint WorkNodeCount;
groupshared ClusterTraversalNode WorkNodes[CLUSTER_BVH_PARENTS_PER_GROUP];
groupshared uint WorkClusterBatch;
groupshared uint WorkClusterReady;
groupshared uint WorkNodesRemaining;
groupshared bool WorkFinished;

bool ClaimReadyNodes(out uint first, out uint count)
{
    first = NodeIndirectArgs.Load(CLUSTER_NODE_READ);
    uint end = min(NodeIndirectArgs.Load(CLUSTER_NODE_WRITE), TraversalLevel.MaxNodes);
    count = 0u;
    while (count < CLUSTER_BVH_PARENTS_PER_GROUP && first + count < end && TraversalReady[first + count] != 0u)
        count++;
    if (count == 0u) return false;
    uint observed;
    NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_READ, first, first + count, observed);
    if (observed != first) { count = 0u; return false; }
    return true;
}

bool ClaimReadyClusters(uint nodesRemaining, out uint batch, out uint count)
{
    batch = NodeIndirectArgs.Load(CLUSTER_BATCH_READ);
    uint batches = (CandidateCount[0] + CLUSTER_BATCH_SIZE - 1u) / CLUSTER_BATCH_SIZE;
    count = batch < batches ? ClusterBatchReady[batch] : 0u;
    if (count != CLUSTER_BATCH_SIZE && (nodesRemaining != 0u || count == 0u)) return false;
    uint observed;
    NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_BATCH_READ, batch, batch + 1u, observed);
    return observed == batch;
}

void CullPersistentWork(uint thread)
{
    for (;;)
    {
        GroupMemoryBarrierWithGroupSync();
        if (thread == 0u)
        {
            ClaimReadyNodes(WorkNodeStart, WorkNodeCount);
            WorkClusterReady = 0u;
            WorkNodesRemaining = NodeIndirectArgs.Load(CLUSTER_NODE_OUTSTANDING);
            if (WorkNodeCount == 0u && !ClaimReadyClusters(WorkNodesRemaining, WorkClusterBatch, WorkClusterReady))
                WorkClusterReady = 0u;
            WorkFinished = WorkNodeCount == 0u && WorkClusterReady == 0u && WorkNodesRemaining == 0u &&
                NodeIndirectArgs.Load(CLUSTER_BATCH_READ) >= (CandidateCount[0] + CLUSTER_BATCH_SIZE - 1u) / CLUSTER_BATCH_SIZE;
        }
        GroupMemoryBarrierWithGroupSync();
        if (WorkNodeCount != 0u)
        {
            DeviceMemoryBarrier();
            if (thread < WorkNodeCount)
                WorkNodes[thread] = TraversalNodes[WorkNodeStart + thread];
            GroupMemoryBarrierWithGroupSync();
            uint count = WorkNodeCount;
            uint parent = min(thread / CLUSTER_BVH_FANOUT, count - 1u);
            ProcessTraversalNode(WorkNodes[parent], thread / CLUSTER_BVH_FANOUT < count,
                thread & (CLUSTER_BVH_FANOUT - 1u));
            DeviceMemoryBarrierWithGroupSync();
            if (thread < count) TraversalReady[WorkNodeStart + thread] = 0u;
            DeviceMemoryBarrierWithGroupSync();
            if (thread == 0u)
            {
                uint ignored;
                NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 0u - count, ignored);
            }
            continue;
        }
        uint count = WorkClusterReady;
        if (count != 0u)
        {
            DeviceMemoryBarrier();
            if (thread < count) CullClusterCandidate(WorkClusterBatch * CLUSTER_BATCH_SIZE + thread);
            DeviceMemoryBarrierWithGroupSync();
            if (thread == 0u) ClusterBatchReady[WorkClusterBatch] = 0u;
        }
        else if (WorkFinished)
            break;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusterWork(uint groupThreadIndex : SV_GroupThreadID)
{
    CullPersistentWork(groupThreadIndex);
}
