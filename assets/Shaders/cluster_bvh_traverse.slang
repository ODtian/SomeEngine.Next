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
groupshared uint WorkViewLanes;
groupshared bool WorkFinished;

void ClaimReadyNodes(uint thread, bool multipleViews)
{
    uint leader = WaveMaskFirstLane(WaveActiveBallot(thread == 0u));
    uint4 active = WaveActiveBallot(true);
    uint index = WaveMaskPrefix(active, WaveGetLaneIndex());
    [allow_uav_condition]
    for (;;)
    {
        uint first = 0u;
        uint available = 0u;
        uint viewLanes = 1u;
        if (thread == 0u)
        {
            NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_READ, 0u, first);
            if (first < TraversalLevel.MaxNodes)
            {
                uint ready;
                InterlockedAdd(TraversalReady[first], 0u, ready);
                if (ready != 0u)
                {
                    if (multipleViews)
                    {
                        // The publisher orders its payload before the ready exchange.
                        DeviceMemoryBarrier();
                        viewLanes = min(ClusterParallelViewLanes(countbits(TraversalNodes[first].ViewMask)), CLUSTER_BVH_PARENTS_PER_GROUP);
                    }
                    available = min(CLUSTER_BVH_PARENTS_PER_GROUP / viewLanes, TraversalLevel.MaxNodes - first);
                }
            }
        }

        first = WaveReadLaneAt(first, leader);
        available = WaveReadLaneAt(available, leader);
        uint ready = available != 0u && index == 0u ? 1u : 0u;
        if (index != 0u && index < available) InterlockedAdd(TraversalReady[first + index], 0u, ready);
        uint4 unavailable = WaveActiveBallot(ready == 0u);
        uint count = min(available, WaveMaskBitCount(active));
        if (any(unavailable != 0u)) count = min(count, WaveMaskPrefix(active, WaveMaskFirstLane(unavailable)));
        uint retry = 0u;
        if (thread == 0u)
        {
            uint observed = first;
            if (count != 0u)
                NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_READ, first, first + count, observed);
            retry = observed != first ? 1u : 0u;
            if (retry == 0u)
            {
                WorkNodeStart = first;
                WorkNodeCount = count;
                WorkViewLanes = viewLanes;
            }
        }
        // Only a successful CAS owns a ready prefix; a stale scan is discarded.
        if (WaveReadLaneAt(retry, leader) == 0u) return;
    }
}

bool ClaimReadyClusters(out uint batch, out uint count)
{
    // Resume completed this queue before the dispatch; inline leaves do not append here.
    uint total = NodeIndirectArgs.Load(CLUSTER_BATCH_COUNT);
    uint batches = (total + CLUSTER_BATCH_SIZE - 1u) / CLUSTER_BATCH_SIZE;
    batch = 0u;
    count = 0u;
    [allow_uav_condition]
    for (;;)
    {
        uint first;
        NodeIndirectArgs.InterlockedAdd(CLUSTER_BATCH_READ, 0u, first);
        if (first >= batches) return false;
        uint expected = min(CLUSTER_BATCH_SIZE, total - first * CLUSTER_BATCH_SIZE);
        uint ready;
        InterlockedAdd(ClusterBatchReady[first], 0u, ready);
        uint observed;
        if (ready == expected)
        {
            NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_BATCH_READ, first, first + 1u, observed);
            if (observed == first)
            {
                batch = first;
                count = expected;
                return true;
            }
        }
        else
        {
            NodeIndirectArgs.InterlockedAdd(CLUSTER_BATCH_READ, 0u, observed);
            if (observed == first) return false;
        }
    }
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
            WorkFinished = false;
            if (WorkNodeCount == 0u && !ClaimReadyClusters(WorkClusterBatch, WorkClusterReady))
            {
                uint remaining;
                NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 0u, remaining);
                // Each live parent covers its children through their ready exchanges.
                // Idle groups keep participating while that finite work can expand.
                WorkFinished = remaining == 0u;
            }
        }
        GroupMemoryBarrierWithGroupSync();
        if (WorkNodeCount != 0u)
        {
            DeviceMemoryBarrier();
            if (thread < WorkNodeCount)
            {
                WorkNodes[thread] = TraversalNodes[WorkNodeStart + thread];
                uint ignored;
                InterlockedExchange(TraversalReady[WorkNodeStart + thread], 0u, ignored);
            }
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
            if (thread == 0u)
            {
                uint ignored;
                InterlockedExchange(ClusterBatchReady[WorkClusterBatch], 0u, ignored);
            }
            if (thread < count) CullClusterCandidate(WorkClusterBatch * CLUSTER_BATCH_SIZE + thread, targetFilters);
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
