#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view_access.slang"
#include "cluster_bvh_cull_common.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

struct ClusterTraversalSeedUniforms
{
    uint ViewCount;
    uint ViewIndexMode;
    uint MaxNodes;
    uint ArgsOffset;
    uint UseInstanceGroups;
    uint3 Padding;
};

StructuredBuffer<ClusterViewUniforms> Views;
StructuredBuffer<ClusterBVHNode> GlobalBVH;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NodeIndirectArgs;
ConstantBuffer<ClusterTraversalSeedUniforms> TraversalSeed;
StructuredBuffer<uint> PolicyActiveViewCount;
Texture2DArray<float> HierarchyHiZ;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> DeferredWork;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeferredWorkCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DeferredWorkDispatchArgs;

#include "cluster_hiz_shared.slang"
#include "cluster_target_policy_contract.slang"
StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;

groupshared float3 ClusterSceneWorldMin[64];
groupshared float3 ClusterSceneWorldMax[64];
groupshared uint ClusterSceneActiveViewMask;
groupshared uint ClusterSceneOccludedViewMask;

uint ClusterSeedViewCount()
{
    if (TraversalSeed.ViewIndexMode == 0u) return TraversalSeed.ViewCount;
    return min(PolicyActiveViewCount[0], TraversalSeed.ViewCount);
}

uint ClusterSeedViewIndex(uint compactViewIndex)
{
    return TraversalSeed.ViewIndexMode == 0u
        ? compactViewIndex
        : ClusterEncodePolicyViewIndex(compactViewIndex);
}

// Reject sample-free roots before expanding their hierarchy.
bool ClusterSeedTargetAccepts(ClusterProjectedBounds bounds, ClusterViewUniforms view)
{
    [branch]
    if (view.TargetPolicy == 0u) return true;
    if (view.CullingEnabled == 0u || bounds.CrossesNearPlane)
        return ClusterTargetPolicyScreenBoundsAccepts(0.0.xx, 1.0.xx, view);
    float2 minUv = float2(bounds.NdcMin.x, -bounds.NdcMax.y) * 0.5 + 0.5;
    float2 maxUv = float2(bounds.NdcMax.x, -bounds.NdcMin.y) * 0.5 + 0.5;
    if (!RasterBoundsCoverPixelCenters(minUv, maxUv, uint2(view.ScreenWidth, view.ScreenHeight)))
        return false;
    return ClusterTargetPolicyScreenBoundsAccepts(minUv, maxUv, view);
}

uint ClusterSeedBoundsState(
    float3 worldMin,
    float3 worldMax,
    ClusterProjectedBounds projectedBounds,
    uint viewIndex,
    ClusterViewUniforms view,
    uint depthCacheClass = 1u)
{
    if (!ClusterSeedTargetAccepts(projectedBounds, view))
        return 0u;
    if (view.CullingEnabled == 0u ||
        view.HasPrevHistory == 0u ||
        view.HiZMipCount == 0u)
    {
        return 1u;
    }

    bool occluded;
    if (view.TargetPolicy != 0u)
    {
        occluded = ClusterTargetPolicyWorldBoundsOccluded(
            worldMin,
            worldMax,
            true,
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
        true,
        viewIndex,
        view,
        unusedMinUv,
        unusedMaxUv);
    }
    return occluded ? 2u : 1u;
}

#include "cluster_raster_target_filter.slang"
StructuredBuffer<uint> SceneInstances;
StructuredBuffer<float4> SceneChunks;
StructuredBuffer<float4> InstanceWorldBounds;

[shader("compute")]
[numthreads(64, 1, 1)]
void SeedHierarchyRoots(
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    uint sortedIndex = groupID.x * 64u + groupThreadIndex;
    bool useInstanceGroups = TraversalSeed.UseInstanceGroups != 0u;
    uint instanceID = 0xFFFFFFFFu;
    uint viewSlotBase = groupID.y * 32u;
    uint compactViewCount = ClusterSeedViewCount();
    uint remainingViews = viewSlotBase < compactViewCount
        ? compactViewCount - viewSlotBase
        : 0u;
    uint activeViewCount = min(remainingViews, 32u);
    uint firstViewIndex = ClusterSeedViewIndex(viewSlotBase);
    bool valid = activeViewCount != 0u &&
        sortedIndex < LoadClusterCullView(Views, PolicyClusterViews, firstViewIndex).InstanceCount;
    uint activeGroupViews = activeViewCount == 32u ? 0xFFFFFFFFu : (1u << activeViewCount) - 1u;
    uint occludedGroupViews = 0u;
    if (useInstanceGroups)
    {
        if (groupThreadIndex == 0u)
        {
            ClusterSceneWorldMin[0] = SceneChunks[groupID.x * 2u].xyz;
            ClusterSceneWorldMax[0] = SceneChunks[groupID.x * 2u + 1u].xyz;
            ClusterSceneActiveViewMask = 0u;
            ClusterSceneOccludedViewMask = 0u;
        }
        GroupMemoryBarrierWithGroupSync();

        if (groupThreadIndex < activeViewCount &&
            all(ClusterSceneWorldMin[0] <= ClusterSceneWorldMax[0]))
        {
            uint viewSlot = viewSlotBase + groupThreadIndex;
            uint viewIndex = ClusterSeedViewIndex(viewSlot);
            ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
            bool active = true;
            ClusterProjectedBounds sceneBounds;
            bool outside = ProjectWorldAabb(
                ClusterSceneWorldMin[0],
                ClusterSceneWorldMax[0],
                view,
                sceneBounds);
            // The chunk holds both world and camera-deformed geometry; per-instance
            // tests below select the corresponding projection.
            active = active && (view.FirstPersonTransformEnabled != 0u || view.CullingEnabled == 0u || !outside);
            uint boundsState = view.FirstPersonTransformEnabled != 0u ? 1u : active
                ? ClusterSeedBoundsState(
                    ClusterSceneWorldMin[0],
                    ClusterSceneWorldMax[0],
                    sceneBounds,
                    viewIndex,
                    view)
                : 0u;
            if (boundsState != 0u)
                InterlockedOr(ClusterSceneActiveViewMask, 1u << groupThreadIndex);
            if (boundsState == 2u)
                InterlockedOr(ClusterSceneOccludedViewMask, 1u << groupThreadIndex);
        }
        GroupMemoryBarrierWithGroupSync();

        activeGroupViews = ClusterSceneActiveViewMask;
        occludedGroupViews = ClusterSceneOccludedViewMask;
    }

    if (activeGroupViews == 0u)
        return;
    if (valid) instanceID = useInstanceGroups ? SceneInstances[sortedIndex] : sortedIndex;
    uint rootIndex = valid
        ? LoadClusterBvhRoot(
            InstanceData,
            LoadRenderInstanceMetadata(
                InstanceProperties,
                CLUSTER_BVH_ROOT_METADATA_WORD),
            instanceID)
        : 0xFFFFFFFFu;
    valid = valid && rootIndex != 0xFFFFFFFFu;
    float3 instanceWorldMin = 1e30.xxx;
    float3 instanceWorldMax = -1e30.xxx;
    ClusterBVHNode root = (ClusterBVHNode)0;
    RenderInstanceTransform transform = (RenderInstanceTransform)0;
    if (valid)
    {
        root = GlobalBVH[rootIndex];
        transform = LoadRenderTransform(
            InstanceData,
            LoadRenderInstanceMetadata(
                InstanceProperties,
                RENDER_CURRENT_TRANSFORM_METADATA_WORD),
            instanceID);
        instanceWorldMin = InstanceWorldBounds[instanceID * 4u].xyz;
        instanceWorldMax = InstanceWorldBounds[instanceID * 4u + 1u].xyz;
    }


    ClusterBoundsProjection previousProjection = (ClusterBoundsProjection)0;
    uint activeViewMask = 0u;
    uint deferredViewMask = 0u;
    if (valid)
    {
        [loop]
        for (uint bit = 0u; bit < activeViewCount; bit++)
        {
            if ((activeGroupViews & (1u << bit)) == 0u)
                continue;
            uint viewSlot = viewSlotBase + bit;
            uint viewIndex = ClusterSeedViewIndex(viewSlot);
            ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
            if (instanceID >= view.InstanceCount)
                continue;
            if (!IsInstanceVisibleToView(LoadInstanceFirstPerson(InstanceData, InstanceProperties, instanceID), view))
                continue;
            view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, instanceID, view);
            if (view.TargetPolicy != 0u && !ClusterTargetPolicyWorldDepthAccepts(
                    instanceWorldMin, instanceWorldMax, view,
                    LoadInstanceDepthCacheClass(InstanceData, InstanceProperties, instanceID)))
                continue;
            ClusterProjectedBounds projectedBounds;
            bool outsideFrustum = ProjectWorldAabbForView(
                instanceWorldMin,
                instanceWorldMax,
                view,
                previousProjection,
                projectedBounds);
            if (view.CullingEnabled != 0u && outsideFrustum)
                continue;
            if (!ClusterNodePassesLod(root, transform, view))
                continue;
            uint boundsState = (occludedGroupViews & (1u << bit)) != 0u
                ? (ClusterSeedTargetAccepts(projectedBounds, view) ? 2u : 0u)
                : ClusterSeedBoundsState(
                    instanceWorldMin,
                    instanceWorldMax,
                    projectedBounds,
                    viewIndex,
                    view, LoadInstanceDepthCacheClass(InstanceData, InstanceProperties, instanceID));
            if (boundsState == 0u)
                continue;
            if (boundsState == 2u)
                deferredViewMask |= 1u << bit;
            else
                activeViewMask |= 1u << bit;
        }
    }

    uint outputCount = activeViewMask != 0u ? 1u : 0u;
    uint outputOffset = WavePrefixSum(outputCount);
    uint waveCount = WaveActiveSum(outputCount);
    uint waveBase = 0u;
    if (WaveIsFirstLane() && waveCount != 0u)
    {
        waveBase = ReserveClusterWork(NodeIndirectArgs,
            TraversalSeed.ArgsOffset + 12u, waveCount, TraversalSeed.MaxNodes, 1u);
        uint publishedCount = min(waveBase + waveCount, TraversalSeed.MaxNodes);
        uint admittedCount = publishedCount > waveBase
            ? publishedCount - waveBase
            : 0u;
        uint ignoredRemaining;
        NodeIndirectArgs.InterlockedAdd(
            TraversalSeed.ArgsOffset + 24u,
            admittedCount,
            ignoredRemaining);
        uint groupCount = (publishedCount + 3u) / 4u;
        uint ignored;
        NodeIndirectArgs.InterlockedMax(
            TraversalSeed.ArgsOffset,
            min(groupCount, 65535u),
            ignored);
        NodeIndirectArgs.InterlockedMax(TraversalSeed.ArgsOffset + 4u, (groupCount + 65534u) / 65535u, ignored);
        NodeIndirectArgs.Store(TraversalSeed.ArgsOffset + 8u, 1u);
        NodeIndirectArgs.Store(TraversalSeed.ArgsOffset + 16u, 0u);
    }
    waveBase = WaveReadLaneFirst(waveBase);
    uint outputIndex = waveBase + outputOffset;
    if (activeViewMask != 0u && outputIndex < TraversalSeed.MaxNodes)
        TraversalNodes[outputIndex] = MakeClusterTraversalNode(
            rootIndex, instanceID, ClusterSeedViewIndex(viewSlotBase), activeViewMask);

    uint deferredCount = countbits(deferredViewMask);
    uint deferredOffset = WavePrefixSum(deferredCount);
    uint deferredWaveCount = WaveActiveSum(deferredCount);
    uint deferredWaveBase = 0u;
    if (WaveIsFirstLane() && deferredWaveCount != 0u)
    {
        deferredWaveBase = ReserveClusterWork(DeferredWorkCount,
            deferredWaveCount, TraversalSeed.MaxNodes, 4u);
        uint published = min(deferredWaveBase + deferredWaveCount, TraversalSeed.MaxNodes);
        uint ignored;
        DeferredWorkDispatchArgs.InterlockedMax(0u, (published + 63u) / 64u, ignored);
    }
    deferredWaveBase = WaveReadLaneFirst(deferredWaveBase);
    uint localDeferred = 0u;
    [loop]
    for (uint bit = 0u; bit < activeViewCount; bit++)
    {
        if ((deferredViewMask & (1u << bit)) == 0u)
            continue;
        uint outputIndex = deferredWaveBase + deferredOffset + localDeferred++;
        if (outputIndex >= TraversalSeed.MaxNodes)
            continue;
        uint viewSlot = viewSlotBase + bit;
        DeferredWork[outputIndex] = MakeClusterCandidate(MakeDeferredTraversalNode(
            MakeClusterTraversalNode(
                rootIndex,
                instanceID,
                ClusterSeedViewIndex(viewSlot),
                1u)), 0u);
    }
}

#include "cluster_work_status.slang"
