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
groupshared uint WorkViewLanes;
groupshared bool WorkFinished;

void ClaimReadyNodes(uint thread, bool multipleViews)
{
    uint first = 0u;
    uint available = 0u;
    uint viewLanes = 1u;
    if (thread == 0u)
    {
        first = NodeIndirectArgs.Load(CLUSTER_NODE_READ);
        uint end = min(NodeIndirectArgs.Load(CLUSTER_NODE_WRITE), TraversalLevel.MaxNodes);
        if (first < end && TraversalReady[first] != 0u)
        {
            DeviceMemoryBarrier();
            viewLanes = multipleViews ? ClusterParallelViewLanes(countbits(TraversalNodes[first].ViewMask)) : 1u;
            available = min(CLUSTER_BVH_PARENTS_PER_GROUP / viewLanes, end - first);
        }
    }

    // One wave reads the bounded ready prefix together; only thread zero claims it.
    uint leader = WaveMaskFirstLane(WaveActiveBallot(thread == 0u));
    first = WaveReadLaneAt(first, leader);
    available = WaveReadLaneAt(available, leader);
    uint4 active = WaveActiveBallot(true);
    uint index = WaveMaskPrefix(active, WaveGetLaneIndex());
    bool ready = false;
    if (index < available) ready = TraversalReady[first + index] != 0u;
    uint4 unavailable = WaveActiveBallot(!ready);
    uint count = min(available, WaveMaskBitCount(active));
    if (any(unavailable != 0u)) count = WaveMaskPrefix(active, WaveMaskFirstLane(unavailable));
    if (thread == 0u)
    {
        WorkNodeStart = first;
        WorkNodeCount = 0u;
        WorkViewLanes = viewLanes;
        if (count != 0u)
        {
            uint observed;
            NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_READ, first, first + count, observed);
            if (observed == first) WorkNodeCount = count;
        }
    }
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

void CullPersistentWork(uint thread, bool targetFilters = true, bool multipleViews = true)
{
    for (;;)
    {
        GroupMemoryBarrierWithGroupSync();
        if (WaveActiveAnyTrue(thread == 0u)) ClaimReadyNodes(thread, multipleViews);
        if (thread == 0u)
        {
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
            uint viewLanes = multipleViews ? WorkViewLanes : 1u;
            uint parentIndex = thread / (CLUSTER_BVH_FANOUT * viewLanes);
            uint parent = min(parentIndex, count - 1u);
            uint child = thread & (CLUSTER_BVH_FANOUT - 1u);
            uint viewLane = (thread / CLUSTER_BVH_FANOUT) % viewLanes;
            ProcessTraversalNode(WorkNodes[parent], parentIndex < count, child,
                viewLane, viewLanes, parentIndex * CLUSTER_BVH_FANOUT + child, targetFilters, multipleViews);
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
            if (thread < count) CullClusterCandidate(WorkClusterBatch * CLUSTER_BATCH_SIZE + thread, targetFilters);
            DeviceMemoryBarrierWithGroupSync();
            if (thread == 0u) ClusterBatchReady[WorkClusterBatch] = 0u;
        }
        else if (WorkFinished)
            break;
    }
}

// View capabilities select an offline specialization of this shared traversal.
[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusterWork<let targetFilters : bool, let multipleViews : bool>(uint groupThreadIndex : SV_GroupThreadID)
{
    CullPersistentWork(groupThreadIndex, targetFilters, multipleViews);
}
