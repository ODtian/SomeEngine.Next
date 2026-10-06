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

StructuredBuffer<ClusterViewUniforms> Views;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer CandidateArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> CandidateClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> CandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PageFaultBuffer;

// One 64-thread group evaluates four 16-way hierarchy parents. This is the same
// group-wide breadth-first shape as Nanite's non-persistent NodeCull path, adapted
// to SomeEngine's 16-child cooked hierarchy nodes.
static const uint CLUSTER_TRAVERSAL_THREADS = 64u;
static const uint CLUSTER_BVH_FANOUT = 16u;
static const uint CLUSTER_BVH_PARENTS_PER_GROUP =
    CLUSTER_TRAVERSAL_THREADS / CLUSTER_BVH_FANOUT;
static const uint CLUSTER_NODE_ARG_STRIDE = 32u;

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
StructuredBuffer<ClusterTraversalNode> CurrentTraversalNodes;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterTraversalNode> NextTraversalNodes;
ByteAddressBuffer CurrentNodeIndirectArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextNodeIndirectArgs;
Texture2DArray<float> HierarchyHiZ;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> DeferredWork;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DeferredWorkDispatchArgs;

StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;

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

uint ClusterHierarchyNodeState(
    float3 worldMin,
    float3 worldMax,
    ClusterProjectedBounds projectedBounds,
    uint viewIndex,
    ClusterViewUniforms view)
{
    if (!ClusterTargetPolicyAccepts(projectedBounds, view))
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
            view);
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

void ProcessTraversalLevel(
    uint groupID,
    uint groupThreadIndex)
{
    // Each level consumes one node buffer and writes the other from index zero.
    // Processed levels leave their capacity available to later levels.
    uint inputCount = min(
        CurrentNodeIndirectArgs.Load(TraversalLevel.CurrentArgsOffset + 12u),
        TraversalLevel.MaxNodes);
    uint parentIndex = groupID * CLUSTER_BVH_PARENTS_PER_GROUP +
        groupThreadIndex / CLUSTER_BVH_FANOUT;
    bool validParent = parentIndex < inputCount;
    ClusterTraversalNode parent = validParent
        ? CurrentTraversalNodes[parentIndex]
        : (ClusterTraversalNode)0;
    ClusterBVHNode parentNode = validParent
        ? GlobalBVH[parent.NodeIndex]
        : (ClusterBVHNode)0;
    uint childIndex = groupThreadIndex & (CLUSTER_BVH_FANOUT - 1u);
    bool validNode = validParent &&
        ((parentNode.NodeType == 0u && childIndex < parentNode.ChildCount) ||
         (parentNode.NodeType != 0u && childIndex == 0u));
    uint nodeIndex = parentNode.NodeType == 0u
        ? parentNode.ChildPointer + childIndex
        : parent.NodeIndex;
    ClusterBVHNode node = validNode
        ? GlobalBVH[nodeIndex]
        : (ClusterBVHNode)0;
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
    while (remainingViews != 0u)
    {
        uint bit = firstbitlow(remainingViews);
        remainingViews &= remainingViews - 1u;
        if ((inputViews & (1u << bit)) == 0u)
            continue;
        uint viewIndex = viewBase + bit;
        ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
        view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, instanceID, view);
        ClusterProjectedBounds projectedBounds;
        bool outside = ProjectWorldAabbForView(worldMin, worldMax, view, previousProjection, projectedBounds);
        if ((view.CullingEnabled != 0u && outside) ||
            !ClusterNodePassesLod(node, transform, view))
            continue;
        uint state = ClusterHierarchyNodeState(worldMin, worldMax, projectedBounds, viewIndex, view);
        if (state == 2u)
            AppendDeferredTraversalNode(
                MakeClusterTraversalNode(nodeIndex, instanceID, viewIndex, 1u), TraversalLevel.MaxNodes);
        else if (state != 0u)
        {
            activeViews |= 1u << bit;
            float3 lodCenter = TransformInstanceLocalPosition(node.LODSphere.xyz, transform);
            float largestScale = abs(transform.Scale) * max(max(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
            float smallestScale = abs(transform.Scale) * min(min(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
            float errorScale = view.TargetPolicy != 0u ? smallestScale : largestScale;
            float error = ClusterSphereError(lodCenter, node.LODSphere.w * largestScale, node.LODError * errorScale, view);
            streamingPriority = max(streamingPriority,
                PackClusterStreamingPriority(error, view.LodThreshold, view.StreamingPriorityCategory));
        }
    }

    uint leafFlags = uint(node.BoundMin.w);
    bool pageFault = activeViews != 0u && node.NodeType == 1u && node.ChildPointer == 0xFFFFFFFFu;
    bool pageRequest = activeViews != 0u && node.NodeType == 1u;
    bool firstFault = false;
    if (pageRequest)
    {
        // Several leaf ranges, instances, shadow views and cull phases may
        // need the same geometry page. Publish one representative leaf for
        // that page so repeated demand cannot consume the feedback capacity.
        uint pageIndex = asuint(node.BoundMax.w);
        uint priorityOffset = ClusterPageRequestPriorityOffset(firstView.MaxPageFaults, pageIndex);
        uint previous;
        PageFaultBuffer.InterlockedMax(priorityOffset, streamingPriority, previous);
        firstFault = previous == 0u;
    }
    uint4 faultMask = WaveActiveBallot(firstFault);
    if (firstFault)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(faultMask);
        uint faultBase = 0u;
        if (lane == leader)
            PageFaultBuffer.InterlockedAdd(0u, WaveMaskBitCount(faultMask), faultBase);
        faultBase = WaveReadLaneAt(faultBase, leader);
        uint faultIndex = faultBase + WaveMaskPrefix(faultMask, lane);
        if (faultIndex < firstView.MaxPageFaults)
            PageFaultBuffer.Store4(4u + faultIndex * 16u,
                uint4(nodeIndex, asuint(node.BoundMax.w), 1u, 0u));
    }

    bool outputNode = activeViews != 0u && !pageFault && node.NodeType == 0u;
    uint4 nodeMask = WaveActiveBallot(outputNode);
    if (outputNode)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(nodeMask);
        uint waveBase = 0u;
        uint waveCount = WaveMaskBitCount(nodeMask);
        if (lane == leader)
        {
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
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint outputIndex = waveBase + WaveMaskPrefix(nodeMask, lane);
        if (outputIndex < TraversalLevel.MaxNodes)
        {
            NextTraversalNodes[outputIndex] = MakeClusterTraversalNode(
                nodeIndex,
                instanceID,
                viewBase,
                activeViews);
        }
    }

    if ((leafFlags & 1u) != 0u && node.NodeType == 1u)
    {
        uint remaining = activeViews;
        while (remaining != 0u)
        {
            uint bit = firstbitlow(remaining);
            remaining &= remaining - 1u;
            if (LoadClusterCullView(Views, PolicyClusterViews, viewBase + bit).CullingEnabled != 0u)
                activeViews &= ~(1u << bit);
        }
    }
    bool outputLeaf = activeViews != 0u && !pageFault && node.NodeType != 0u;
    uint clusterCount = outputLeaf ? node.ChildCount >> 12u : 0u;
    uint firstCluster = node.ChildCount & 0xFFFu;
    uint leafCount = clusterCount * countbits(activeViews);
    uint leafOffset = WavePrefixSum(leafCount);
    uint waveLeafCount = WaveActiveSum(leafCount);
    uint candidateBase = 0u;
    if (WaveIsFirstLane())
        candidateBase = ReserveClusterWork(CandidateCount, waveLeafCount, TraversalLevel.MaxNodes, 2u);
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
                    node.ChildPointer, firstCluster + cluster, instanceID, viewBase + bit),
                    (leafFlags & 2u) != 0u ? 1u : 0u);
            output++;
        }
    }
}

#include "cluster_work_status.slang"
