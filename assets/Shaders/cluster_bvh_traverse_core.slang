#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view_access.slang"
#include "cluster_bvh_cull_common.slang"
#include "cluster_hiz_shared.slang"
#include "cluster_target_policy_contract.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"
#include "cluster_page_requests.slang"
#include "cluster_view_work.slang"

StructuredBuffer<ClusterViewUniforms> Views;

ByteAddressBuffer GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer CandidateArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
#ifdef CLUSTER_PERSISTENT_CULL
globallycoherent RWStructuredBuffer<ClusterCandidate> CandidateClusters;
#else
RWStructuredBuffer<ClusterCandidate> CandidateClusters;
#endif
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
globallycoherent RWStructuredBuffer<uint> CandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PageFaultBuffer;

// A single-view 64-thread group evaluates four slices of sixteen parents.
static const uint CLUSTER_TRAVERSAL_THREADS = 64u;
static const uint CLUSTER_BVH_FANOUT = 4u;
static const uint CLUSTER_BVH_PARENTS_PER_GROUP =
    CLUSTER_TRAVERSAL_THREADS / CLUSTER_BVH_FANOUT;
static const uint CLUSTER_NODE_ARG_STRIDE = 32u;

#ifdef CLUSTER_PERSISTENT_CULL
groupshared uint TraversalActiveViews[CLUSTER_TRAVERSAL_THREADS];
groupshared uint TraversalStreamingPriorities[CLUSTER_TRAVERSAL_THREADS];
#endif

struct ClusterTraversalLevelUniforms
{
    uint NodeLevel;
    uint MaxNodes;
    uint CurrentArgsOffset;
    uint NextArgsOffset;
    uint TraversalMode;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<ClusterTraversalLevelUniforms> TraversalLevel;
#ifdef CLUSTER_PERSISTENT_CULL
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
globallycoherent RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
globallycoherent RWByteAddressBuffer NodeIndirectArgs;
#include "cluster_work_queue.slang"
#else
StructuredBuffer<ClusterTraversalNode> CurrentTraversalNodes;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterTraversalNode> NextTraversalNodes;
ByteAddressBuffer CurrentNodeIndirectArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextNodeIndirectArgs;
#endif
Texture2DArray<float> HierarchyHiZ;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> DeferredWork;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DeferredWorkDispatchArgs;

StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;

#ifdef CLUSTER_PERSISTENT_CULL
uint ReservePersistentNodes(uint amount)
{
    uint current = NodeIndirectArgs.Load(CLUSTER_NODE_WRITE);
    for (;;)
    {
        uint admitted = min(amount, TraversalLevel.MaxNodes - min(current, TraversalLevel.MaxNodes));
        uint observed;
        NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_WRITE, current, current + admitted, observed);
        if (observed == current)
        {
            if (admitted != amount) RejectClusterWork(1u);
            return current;
        }
        current = observed;
    }
}
uint ReservePersistentClusters(uint amount)
{
    uint current = CandidateCount[0];
    for (;;)
    {
        uint admitted = min(amount, TraversalLevel.MaxNodes - min(current, TraversalLevel.MaxNodes));
        uint observed;
        InterlockedCompareExchange(CandidateCount[0], current, current + admitted, observed);
        if (observed == current)
        {
            if (admitted != amount) RejectClusterWork(2u);
            return current;
        }
        current = observed;
    }
}
#endif

void PublishCandidateDispatchArgs(uint start, uint count, uint maxCandidates)
{
    if (count > maxCandidates - min(start, maxCandidates))
        RejectClusterWork(2u);
    if (count == 0 || start >= maxCandidates)
        return;

    uint clampedCount = min(count, maxCandidates - start);
    uint clampedEnd = start + clampedCount;
    uint groups = (clampedEnd + 63) / 64;
    uint unused;
    CandidateArgs.InterlockedMax(0, groups, unused);
}

void PublishClusterPageFault(uint sliceIndex, uint pageIndex, uint capacity)
{
    uint4 active = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(active);
    uint first = 0u;
    if (lane == leader) PageFaultBuffer.InterlockedAdd(0u, WaveMaskBitCount(active), first);
    first = WaveReadLaneAt(first, leader);
    uint index = first + WaveMaskPrefix(active, lane);
    if (index < capacity) PageFaultBuffer.Store4(4u + index * 16u, uint4(sliceIndex, pageIndex, 1u, 0u));
}

uint ClusterHierarchyNodeState(
    float3 worldMin,
    float3 worldMax,
    ClusterProjectedBounds projectedBounds,
    uint viewIndex,
    ClusterViewUniforms view,
    uint depthCacheClass)
{
    if (!ClusterTargetPolicyAccepts(projectedBounds, view, depthCacheClass))
        return 0u;
    if (view.CullingEnabled == 0u ||
        view.HasPrevHistory == 0u ||
        view.HiZMipCount == 0u)
    {
        return 1u;
    }

    bool usePrevious = TraversalLevel.TraversalMode != 2u;
    bool occluded;
    if (view.TargetPolicy != 0u)
    {
        occluded = ClusterTargetPolicyWorldBoundsOccluded(
            worldMin,
            worldMax,
            usePrevious,
            view, depthCacheClass);
    }
    else
    {
        float2 unusedMinUv;
        float2 unusedMaxUv;
        occluded = IsWorldAabbOccludedByHiZ(
            HierarchyHiZ,
            worldMin,
            worldMax,
            usePrevious,
            viewIndex,
            view,
            unusedMinUv,
            unusedMaxUv);
    }
    if (!occluded)
        return 1u;
    return TraversalLevel.TraversalMode == 2u ? 0u : 2u;
}

void AppendDeferredTraversalNode(ClusterTraversalNode node, uint maxNodes)
{
    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint waveBase = 0u;
    uint waveCount = WaveMaskBitCount(activeMask);
    if (lane == leader)
    {
        waveBase = ReserveClusterWork(DeferredWorkCount, waveCount, maxNodes, 4u);
        uint published = min(waveBase + waveCount, maxNodes);
        uint ignored;
        DeferredWorkDispatchArgs.InterlockedMax(0u, (published + 63u) / 64u, ignored);
    }
    waveBase = WaveReadLaneAt(waveBase, leader);
    uint output = waveBase + WaveMaskPrefix(activeMask, lane);
    if (output < maxNodes)
        DeferredWork[output] = MakeClusterCandidate(MakeDeferredTraversalNode(node), 0u);
}

void ProcessTraversalNode(ClusterTraversalNode parent, bool validParent, uint childIndex,
    uint viewLane = 0u, uint viewLanes = 1u, uint groupChild = 0u)
{
    uint nodeIndex = ClusterTraversalNodeIndex(parent);
    ClusterHierarchySlice node = (ClusterHierarchySlice)0;
    if (validParent) node = LoadClusterNode(GlobalBVH, nodeIndex, childIndex);
    bool validNode = validParent && ClusterSliceEnabled(node) &&
        (!ClusterTraversalHasSlice(parent) || childIndex == ClusterTraversalSliceIndex(parent));
    uint instanceID = parent.InstanceID;
    uint viewBase = ClusterTraversalViewIndex(parent);
    ClusterViewUniforms firstView = validNode
        ? LoadClusterCullView(Views, PolicyClusterViews, viewBase)
        : (ClusterViewUniforms)0;
    validNode = validNode && instanceID < firstView.InstanceCount;
    RenderInstanceTransform transform = (RenderInstanceTransform)0;
    float boundsExpansion = 0.0;
    if (validNode)
    {
        transform = LoadRenderTransform(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, RENDER_CURRENT_TRANSFORM_METADATA_WORD), instanceID);
        boundsExpansion = max(0.0, LoadClusterBoundsExpansion(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BOUNDS_EXPANSION_METADATA_WORD), instanceID));
    }
    uint activeViews = 0u;
    uint streamingPriority = 1u;
    float3 worldMin = 0.0.xxx;
    float3 worldMax = 0.0.xxx;
    if (validNode)
        ClusterNodeWorldBounds(node, transform, boundsExpansion, worldMin, worldMax);
    uint inputViews = validNode ? parent.ViewMask : 0u;
    ClusterBoundsProjection previousProjection = (ClusterBoundsProjection)0;
    uint remainingViews = WaveActiveBitOr(inputViews);
    uint viewOrdinal = 0u;
    while (remainingViews != 0u)
    {
        uint bit = firstbitlow(remainingViews);
        remainingViews &= remainingViews - 1u;
        bool assigned = (viewOrdinal++ % viewLanes) == viewLane;
        if (!assigned) continue;
        if ((inputViews & (1u << bit)) == 0u)
            continue;
        uint viewIndex = viewBase + bit;
        ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
        view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, instanceID, view);
        if (view.TargetPolicy != 0u && !ClusterTargetPolicyWorldDepthAccepts(
                worldMin, worldMax, view,
                LoadTargetDepthCacheClass(instanceID)))
            continue;
        ClusterProjectedBounds projectedBounds;
        bool outside = ProjectWorldAabbForView(worldMin, worldMax, view, previousProjection, projectedBounds);
        if ((view.CullingEnabled != 0u && outside) ||
            !ClusterNodePassesLod(node, transform, view))
            continue;
        uint state = ClusterHierarchyNodeState(worldMin, worldMax, projectedBounds, viewIndex, view,
            view.TargetPolicy != 0u ? LoadTargetDepthCacheClass(instanceID) : 1u);
        if (state == 2u)
            AppendDeferredTraversalNode(
                MakeClusterTraversalNode(ClusterTraversalSlice(nodeIndex, childIndex), instanceID, viewIndex, 1u), TraversalLevel.MaxNodes);
        else if (state != 0u)
        {
            activeViews |= 1u << bit;
            float3 lodCenter = TransformInstanceLocalPosition(node.LODBounds.xyz, transform);
            float largestScale = abs(transform.Scale) * max(max(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
            float smallestScale = abs(transform.Scale) * min(min(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
            float errorScale = smallestScale;
            float error = ClusterSphereError(lodCenter, node.LODBounds.w * largestScale, abs(node.MinLODError) * errorScale, view);
            streamingPriority = max(streamingPriority,
                PackClusterStreamingPriority(error, view.LodThreshold, view.StreamingPriorityCategory));
        }
    }

#ifdef CLUSTER_PERSISTENT_CULL
    if (viewLanes > 1u)
    {
        if (viewLane == 0u)
        {
            TraversalActiveViews[groupChild] = 0u;
            TraversalStreamingPriorities[groupChild] = 1u;
        }
        GroupMemoryBarrierWithGroupSync();
        InterlockedOr(TraversalActiveViews[groupChild], activeViews);
        InterlockedMax(TraversalStreamingPriorities[groupChild], streamingPriority);
        GroupMemoryBarrierWithGroupSync();
        activeViews = viewLane == 0u ? TraversalActiveViews[groupChild] : 0u;
        streamingPriority = TraversalStreamingPriorities[groupChild];
    }
#endif

    bool leaf = validNode && ClusterSliceLeaf(node);
    bool pageFault = activeViews != 0u && leaf && !ClusterSliceAvailable(node);
    if (activeViews != 0u && leaf)
    {
        uint firstPage = ClusterSliceFirstPage(node);
        uint pageCount = ClusterSlicePageCount(node);
        for (uint page = firstPage; page < firstPage + pageCount; page++)
        {
            uint previous;
            PageFaultBuffer.InterlockedMax(ClusterPageRequestPriorityOffset(firstView.MaxPageFaults, page),
                streamingPriority, previous);
            if (pageFault && previous == 0u)
                PublishClusterPageFault(nodeIndex * 4u + childIndex, page, firstView.MaxPageFaults);
        }
    }

    bool outputNode = activeViews != 0u && !leaf;
    uint4 nodeMask = WaveActiveBallot(outputNode);
    if (outputNode)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(nodeMask);
        uint waveBase = 0u;
        uint waveCount = WaveMaskBitCount(nodeMask);
        if (lane == leader)
        {
#ifdef CLUSTER_PERSISTENT_CULL
            waveBase = ReservePersistentNodes(waveCount);
            uint accepted = min(waveCount, TraversalLevel.MaxNodes - min(waveBase, TraversalLevel.MaxNodes));
            uint ignored;
            NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, accepted, ignored);
#else
            waveBase = ReserveClusterWork(NextNodeIndirectArgs,
                TraversalLevel.NextArgsOffset + 12u, waveCount, TraversalLevel.MaxNodes, 1u);
            uint publishedCount = min(waveBase + waveCount, TraversalLevel.MaxNodes);
            uint groupCount =
                (publishedCount + CLUSTER_BVH_PARENTS_PER_GROUP - 1u) /
                CLUSTER_BVH_PARENTS_PER_GROUP;
            uint ignored;
            NextNodeIndirectArgs.InterlockedMax(
                TraversalLevel.NextArgsOffset,
                min(groupCount, 65535u),
                ignored);
            NextNodeIndirectArgs.InterlockedMax(TraversalLevel.NextArgsOffset + 4u, (groupCount + 65534u) / 65535u, ignored);
            NextNodeIndirectArgs.Store(TraversalLevel.NextArgsOffset + 8u, 1u);
            NextNodeIndirectArgs.Store(TraversalLevel.NextArgsOffset + 16u, 0u);
#endif
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint outputIndex = waveBase + WaveMaskPrefix(nodeMask, lane);
        if (outputIndex < TraversalLevel.MaxNodes)
        {
#ifdef CLUSTER_PERSISTENT_CULL
            TraversalNodes[outputIndex] = MakeClusterTraversalNode(node.ChildReference, instanceID, viewBase, activeViews);
            DeviceMemoryBarrier();
            uint ignored;
            InterlockedExchange(TraversalReady[outputIndex], 1u, ignored);
#else
            NextTraversalNodes[outputIndex] = MakeClusterTraversalNode(
                node.ChildReference, instanceID, viewBase, activeViews);
#endif
        }
    }

    bool outputLeaf = activeViews != 0u && leaf && !pageFault;
    uint clusterCount = outputLeaf ? ClusterSliceClusterCount(node) : 0u;
    uint firstCluster = ClusterSliceFirstCluster(node);
    uint leafCount = clusterCount * countbits(activeViews);
    uint leafOffset = WavePrefixSum(leafCount);
    uint waveLeafCount = WaveActiveSum(leafCount);
    uint candidateBase = 0u;
    if (WaveIsFirstLane() && waveLeafCount != 0u)
#ifdef CLUSTER_PERSISTENT_CULL
        candidateBase = ReservePersistentClusters(waveLeafCount);
#else
        candidateBase = ReserveClusterWork(CandidateCount, waveLeafCount, TraversalLevel.MaxNodes, 2u);
#endif
    candidateBase = WaveReadLaneFirst(candidateBase);
    if (WaveIsFirstLane())
        PublishCandidateDispatchArgs(candidateBase, waveLeafCount, TraversalLevel.MaxNodes);
    uint output = candidateBase + leafOffset;
    uint leafViews = outputLeaf ? activeViews : 0u;
    while (leafViews != 0u)
    {
        uint bit = firstbitlow(leafViews);
        leafViews &= leafViews - 1u;
        for (uint cluster = 0u; cluster < clusterCount; cluster++)
        {
            if (output < TraversalLevel.MaxNodes)
                CandidateClusters[output] = MakeClusterCandidate(MakeDrawRequest(
                    ClusterSlicePageOffset(node), firstCluster + cluster, instanceID, viewBase + bit),
                    1u);
            output++;
        }
    }
#ifdef CLUSTER_PERSISTENT_CULL
    if (leafCount != 0u)
        PublishClusterBatchRange(candidateBase + leafOffset, leafCount, TraversalLevel.MaxNodes);
#endif
}

#ifndef CLUSTER_PERSISTENT_CULL
void ProcessTraversalLevel(uint groupID, uint groupThreadIndex)
{
    uint count = min(CurrentNodeIndirectArgs.Load(TraversalLevel.CurrentArgsOffset + 12u), TraversalLevel.MaxNodes);
    uint index = groupID * CLUSTER_BVH_PARENTS_PER_GROUP + groupThreadIndex / CLUSTER_BVH_FANOUT;
    bool valid = index < count;
    ProcessTraversalNode(valid ? CurrentTraversalNodes[index] : (ClusterTraversalNode)0,
        valid, groupThreadIndex & (CLUSTER_BVH_FANOUT - 1u));
}
#endif

#include "cluster_work_status.slang"
