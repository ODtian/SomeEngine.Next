#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view_access.slang"
#include "cluster_bvh_cull_common.slang"
#include "cluster_hiz_shared.slang"
#include "cluster_target_policy_contract.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

StructuredBuffer<ClusterViewUniforms> Views;

StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer CandidateArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> CandidateClusters;
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
RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
ByteAddressBuffer CurrentNodeIndirectArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextNodeIndirectArgs;
Texture2DArray<float> HierarchyHiZ;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> DeferredWork;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DeferredWorkDispatchArgs;

StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;

void PublishCandidateDispatchArgs(uint start, uint count, uint maxCandidates)
{
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
        InterlockedAdd(DeferredWorkCount[0], waveCount, waveBase);
        uint published = min(waveBase + waveCount, maxNodes);
        uint ignored;
        DeferredWorkDispatchArgs.InterlockedMax(0u, (published + 63u) / 64u, ignored);
    }
    waveBase = WaveReadLaneAt(waveBase, leader);
    uint output = waveBase + WaveMaskPrefix(activeMask, lane);
    if (output < maxNodes)
        DeferredWork[output] = MakeDeferredTraversalNode(node);
}

void ProcessTraversalLevel(
    uint groupID,
    uint groupThreadIndex)
{
    uint inputStart = min(
        CurrentNodeIndirectArgs.Load(TraversalLevel.CurrentArgsOffset + 16u),
        TraversalLevel.MaxNodes);
    uint inputCount = min(
        CurrentNodeIndirectArgs.Load(TraversalLevel.CurrentArgsOffset + 12u),
        TraversalLevel.MaxNodes - inputStart);
    uint inputEnd = inputStart + inputCount;
    uint parentIndex = inputStart +
        groupID * CLUSTER_BVH_PARENTS_PER_GROUP +
        groupThreadIndex / CLUSTER_BVH_FANOUT;
    bool validParent = parentIndex < inputEnd;
    ClusterTraversalNode parent = validParent
        ? TraversalNodes[parentIndex]
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
        ClusterProjectedBounds projectedBounds;
        bool outside = ProjectWorldAabbForView(worldMin, worldMax, view, previousProjection, projectedBounds);
        if ((view.CullingEnabled != 0u && outside) || !ClusterNodePassesLod(node, transform, view))
            continue;
        uint state = ClusterHierarchyNodeState(worldMin, worldMax, projectedBounds, viewIndex, view);
        if (state == 2u)
            AppendDeferredTraversalNode(
                MakeClusterTraversalNode(nodeIndex, instanceID, viewIndex, 1u), TraversalLevel.MaxNodes);
        else if (state != 0u)
            activeViews |= 1u << bit;
    }

    bool pageFault = activeViews != 0u && node.NodeType == 1u && node.ChildPointer == 0xFFFFFFFFu;
    uint4 faultMask = WaveActiveBallot(pageFault);
    if (pageFault)
    {
        uint lane = WaveGetLaneIndex();
        uint leader = WaveMaskFirstLane(faultMask);
        uint faultBase = 0u;
        if (lane == leader)
            PageFaultBuffer.InterlockedAdd(0u, WaveMaskBitCount(faultMask), faultBase);
        faultBase = WaveReadLaneAt(faultBase, leader);
        uint faultIndex = faultBase + WaveMaskPrefix(faultMask, lane);
        if (faultIndex < firstView.MaxPageFaults)
            PageFaultBuffer.Store(4u + faultIndex * 4u, nodeIndex);
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
            NextNodeIndirectArgs.InterlockedAdd(
                TraversalLevel.NextArgsOffset + 12u,
                waveCount,
                waveBase);
            uint available = TraversalLevel.MaxNodes - inputEnd;
            uint publishedCount = min(waveBase + waveCount, available);
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
            NextNodeIndirectArgs.Store(TraversalLevel.NextArgsOffset + 16u, inputEnd);
        }
        waveBase = WaveReadLaneAt(waveBase, leader);
        uint outputIndex = inputEnd + waveBase + WaveMaskPrefix(nodeMask, lane);
        if (outputIndex < TraversalLevel.MaxNodes)
        {
            TraversalNodes[outputIndex] = MakeClusterTraversalNode(
                nodeIndex,
                instanceID,
                viewBase,
                activeViews);
        }
    }

    bool outputLeaf = activeViews != 0u && !pageFault && node.NodeType != 0u;
    uint clusterCount = outputLeaf ? node.ChildCount >> 12u : 0u;
    uint leafCount = clusterCount * countbits(activeViews);
    uint leafOffset = WavePrefixSum(leafCount);
    uint waveLeafCount = WaveActiveSum(leafCount);
    uint candidateBase = 0u;
    if (WaveIsFirstLane())
        InterlockedAdd(CandidateCount[0], waveLeafCount, candidateBase);
    candidateBase = WaveReadLaneFirst(candidateBase);
    if (WaveIsFirstLane())
        PublishCandidateDispatchArgs(candidateBase, waveLeafCount, TraversalLevel.MaxNodes);
    uint firstCluster = node.ChildCount & 0xFFFu;
    uint output = candidateBase + leafOffset;
    uint leafViews = outputLeaf ? activeViews : 0u;
    while (leafViews != 0u)
    {
        uint bit = firstbitlow(leafViews);
        leafViews &= leafViews - 1u;
        for (uint cluster = 0u; cluster < clusterCount; cluster++)
        {
            if (output < TraversalLevel.MaxNodes)
                CandidateClusters[output] = MakeDrawRequest(
                    node.ChildPointer, firstCluster + cluster, instanceID, viewBase + bit);
            output++;
        }
    }
}
