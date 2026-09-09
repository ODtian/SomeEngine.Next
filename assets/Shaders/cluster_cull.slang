#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view_access.slang"
#include "cluster_target_policy_contract.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

StructuredBuffer<ClusterViewUniforms> Views;

ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer PageHeap;
StructuredBuffer<DrawRequest> CandidateClusters;
StructuredBuffer<uint> CandidateCount;
#if CLUSTER_CULL_PHASE == 1
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DrawArgs;
ByteAddressBuffer BaseDrawArgs0;
ByteAddressBuffer BaseDrawArgs1;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> VisibleClusters;
Texture2DArray<float> HiZTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> NextCandidates;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> NextCandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextCandidateArgs;
#elif CLUSTER_CULL_PHASE == 2
ByteAddressBuffer BaseDrawArgs0;
ByteAddressBuffer BaseDrawArgs1;
ByteAddressBuffer BaseDrawArgs2;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> VisibleClusters;
Texture2DArray<float> HiZTexture;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PhaseDrawArgs;
#else
#error CLUSTER_CULL_PHASE must be 1 or 2
#endif

StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;

float3 LoadFloat3(ByteAddressBuffer buffer, uint offset)
{
    return asfloat(uint3(
        buffer.Load(offset + 0),
        buffer.Load(offset + 4),
        buffer.Load(offset + 8)));
}

PackedCluster LoadCluster(uint pageOffset, uint clusterIndex)
{
    uint headerOffset = pageOffset;
    uint clustersOffset = PageHeap.Load(headerOffset + 16);
    uint offset = headerOffset + clustersOffset + (clusterIndex * PACKED_CLUSTER_STRIDE_BYTES);
    PackedCluster c;

    c.VertexBlobOffset = PageHeap.Load(offset + 0);
    c.VertexBlobSize = PageHeap.Load(offset + 4);
    c.ExtensionOffset = PageHeap.Load(offset + 8);
    c.ExtensionSize = PageHeap.Load(offset + 12);

    // LODCenter (16..27), LODRadius (28)
    uint4 uLOD = PageHeap.Load<uint4>(offset + 16);
    c.LODCenter = asfloat(uLOD.xyz);
    c.LODRadius = asfloat(uLOD.w);

    c.BoundMin = LoadFloat3(PageHeap, offset + 32);
    c.BoundMax = LoadFloat3(PageHeap, offset + 44);
    c.PackedLodErrorTriangleStart = PageHeap.Load(offset + 56);
    c.PackedCounts = PageHeap.Load(offset + 60);
    c.PackedGroup = PageHeap.Load(offset + 64);
    c.PackedMaterials = PageHeap.Load(offset + 68);
    c.PackedRanges = PageHeap.Load(offset + 72);
    c.MaterialTableOffset = PageHeap.Load(offset + 76);
    c.VRBBatchInfo = PageHeap.Load(offset + 80);
    c.Reserved = PageHeap.Load(offset + 84);

    return c;
}

#include "cluster_hiz_shared.slang"

bool PassesLODFilter(
    PackedCluster cluster,
    RenderInstanceTransform t,
    ClusterViewUniforms view)
{
    // Without a conservative deformation error we cannot select an approximate
    // mesh LOD. Emit the original level once instead of overlapping every LOD.
    if (view.CullingEnabled == 0u)
        return ((cluster.PackedCounts >> 16u) & 0xFFu) == 0u;

    float3 worldCenter = TransformInstanceLocalPosition(cluster.LODCenter, t);
    float maxScale = abs(t.Scale) * max(max(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z));
    float worldRadius = cluster.LODRadius * maxScale;
    float lodError = DecodeClusterLODError(cluster);
    return ClusterSphereError(worldCenter, worldRadius, lodError * maxScale, view) <= view.LodThreshold;
}

void BuildClusterAabbScreenBoundsAndNearDepth(
    PackedCluster cluster,
    RenderInstanceTransform transform,
    bool usePrev,
    float boundsExpansion,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    uint vertexCount = cluster.PackedCounts & 0xFF;
    if (vertexCount == 0)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0;
        return;
    }

    float3 localMin = cluster.BoundMin;
    float3 localMax = cluster.BoundMax;

    float3 localCorners[8];
    localCorners[0] = float3(localMin.x, localMin.y, localMin.z);
    localCorners[1] = float3(localMax.x, localMin.y, localMin.z);
    localCorners[2] = float3(localMin.x, localMax.y, localMin.z);
    localCorners[3] = float3(localMax.x, localMax.y, localMin.z);
    localCorners[4] = float3(localMin.x, localMin.y, localMax.z);
    localCorners[5] = float3(localMax.x, localMin.y, localMax.z);
    localCorners[6] = float3(localMin.x, localMax.y, localMax.z);
    localCorners[7] = float3(localMax.x, localMax.y, localMax.z);

    float3 worldMin = 1e20.xxx;
    float3 worldMax = -1e20.xxx;
    [unroll]
    for (uint cornerIdx = 0; cornerIdx < 8; cornerIdx++)
    {
        float3 worldPos = TransformInstanceLocalPosition(localCorners[cornerIdx], transform);
        worldMin = min(worldMin, worldPos);
        worldMax = max(worldMax, worldPos);
    }

    float expansion = max(0.0, boundsExpansion);
    worldMin -= expansion.xxx;
    worldMax += expansion.xxx;

    BuildWorldAabbScreenBoundsAndNearDepth(
        worldMin, worldMax, usePrev, view,
        minUv, maxUv, nearDepth, mipLevel);
}

bool IsOccludedByHiZ(
    PackedCluster cluster,
    RenderInstanceTransform transform,
    float boundsExpansion,
    bool usePrev,
    uint viewIndex,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv)
{
    if (view.CullingEnabled == 0u)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        return false;
    }

    float nearDepth;
    uint mipLevel;
    BuildClusterAabbScreenBoundsAndNearDepth(
        cluster, transform, usePrev, boundsExpansion, view,
        minUv, maxUv, nearDepth, mipLevel);

    float hizDepth = 0.0;
    float epsilon = 5e-5 + 0.5 * max(view.HiZInvSize.x, view.HiZInvSize.y);

    if (view.HiZMipCount == 0)
        return false;

    if (view.TargetPolicy != 0u)
        return ClusterTargetPolicyScreenBoundsOccluded(
            minUv,
            maxUv,
            nearDepth,
            usePrev,
            view);
    hizDepth = SampleHiZMax(HiZTexture, minUv, maxUv, mipLevel, viewIndex, view);
    return nearDepth > (hizDepth + epsilon);
}

bool ShouldUseSWRaster(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view)
{
    float width = (maxUv.x - minUv.x) * view.ScreenWidth;
    float height = (maxUv.y - minUv.y) * view.ScreenHeight;
    if (width > 64.0 || height > 64.0)
        return false;
    if (view.RasterMode == 1u)
        return false;
    if (view.RasterMode == 2u)
        return true;
    if (view.CullingEnabled == 0u)
        return false;

    float maximumEdge = max(width, height);
    return maximumEdge <= view.SoftwareRasterEdgeThreshold;
}

#include "cluster_raster_target_filter.slang"

#if CLUSTER_CULL_PHASE == 1
void AppendVisible(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    bool isSW,
    uint maxCandidates)
{
    CullDrawArgs base0 = LoadCullDrawArgs(BaseDrawArgs0, 0u);
    CullDrawArgs base1 = LoadCullDrawArgs(BaseDrawArgs1, 0u);
    uint baseSWCount = base0.SWCount + base1.SWCount;
    uint baseHWCount = base0.HWCount + base1.HWCount;
    uint baseCount = baseSWCount + baseHWCount;
    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint activeCount = WaveMaskBitCount(activeMask);
    uint totalBase = 0u;
    if (lane == leader)
        DrawArgs.InterlockedAdd(12, activeCount, totalBase);
    totalBase = WaveReadLaneAt(totalBase, leader);
    bool admitted = baseCount + totalBase + WaveMaskPrefix(activeMask, lane) < maxCandidates;
    if (!admitted)
        return;

    uint4 routeMask = WaveActiveBallot(admitted && isSW);
    if (!isSW)
        routeMask = WaveActiveBallot(admitted && !isSW);
    uint routeLeader = WaveMaskFirstLane(routeMask);
    uint routeCount = WaveMaskBitCount(routeMask);
    uint routeBase = 0u;
    if (lane == routeLeader)
    {
        if (isSW)
            DrawArgs.InterlockedAdd(4, routeCount, routeBase);
        else
            DrawArgs.InterlockedAdd(8, routeCount, routeBase);
    }
    routeBase = WaveReadLaneAt(routeBase, routeLeader);
    uint outIdx = routeBase + WaveMaskPrefix(routeMask, lane);
    if (isSW)
    {
        uint globalIdx = baseSWCount + outIdx;
        if (globalIdx < maxCandidates)
            VisibleClusters[globalIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
    }
    else
    {
        uint hwIdx = maxCandidates - 1 - baseHWCount - outIdx;
        if (baseHWCount + outIdx < maxCandidates)
            VisibleClusters[hwIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
    }
}

void AppendCandidate(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint maxCandidates)
{
    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint waveCount = WaveMaskBitCount(activeMask);
    uint waveBase = 0u;
    if (lane == leader)
        InterlockedAdd(NextCandidateCount[0], waveCount, waveBase);
    waveBase = WaveReadLaneAt(waveBase, leader);
    uint outIdx = waveBase + WaveMaskPrefix(activeMask, lane);
    if (outIdx >= maxCandidates)
        return;

    if (lane == leader)
    {
        uint groups = (min(waveBase + waveCount, maxCandidates) + 63u) / 64u;
        uint unused;
        NextCandidateArgs.InterlockedMax(0, groups, unused);
    }
    NextCandidates[outIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
}
#endif

bool LoadAndFilterCandidate(
    uint threadId,
    out uint pageOffset,
    out uint clusterID,
    out uint instanceID,
    out uint viewIndex,
    out ClusterViewUniforms view,
    out PackedCluster cluster,
    out RenderInstanceTransform t,
    out float boundsExpansion)
{
    pageOffset = 0;
    clusterID = 0;
    instanceID = 0;
    viewIndex = 0;
    view = (ClusterViewUniforms)0;
    cluster = (PackedCluster)0;
    t = (RenderInstanceTransform)0;
    boundsExpansion = 0.0;

    uint count = CandidateCount[0];
    uint capacity;
    uint stride;
    CandidateClusters.GetDimensions(capacity, stride);
    if (threadId >= min(count, capacity))
        return false;

    DrawRequest candidate = CandidateClusters[threadId];
    pageOffset = candidate.PageOffset;
    clusterID = DrawRequestClusterID(candidate);
    instanceID = candidate.InstanceID;
    viewIndex = DrawRequestViewIndex(candidate);
    bool rasterRegion = (viewIndex & CLUSTER_RASTER_REGION_BIT) != 0u;
    view = rasterRegion
        ? LoadClusterView(Views, PolicyClusterViews, viewIndex)
        : LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
    if (threadId >= view.MaxCandidates)
        return false;

    cluster = LoadCluster(pageOffset, clusterID);
    t = LoadRenderTransform(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, RENDER_CURRENT_TRANSFORM_METADATA_WORD),
        instanceID);
    boundsExpansion = max(
        0.0,
        LoadClusterBoundsExpansion(
            InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BOUNDS_EXPANSION_METADATA_WORD),
            instanceID));

    return rasterRegion || PassesLODFilter(cluster, t, view);
}

#if CLUSTER_CULL_PHASE == 2
void AppendVisiblePhase2(uint pageOffset, uint clusterID, uint instanceID,
    uint viewIndex, bool isSW, uint maxCandidates);
#endif

void CullRasterRegions(
    uint pageOffset, uint clusterID, uint instanceID,
    PackedCluster cluster, RenderInstanceTransform transform,
    float boundsExpansion, ClusterViewUniforms sourceView)
{
    float2 minUv = 0.0.xx;
    float2 maxUv = 1.0.xx;
    float nearDepth = 0.0;
    uint unusedMip;
    if (sourceView.CullingEnabled != 0u)
        BuildClusterAabbScreenBoundsAndNearDepth(
            cluster, transform, false, boundsExpansion, sourceView,
            minUv, maxUv, nearDepth, unusedMip);
    uint4 bounds = ClusterRasterGridBounds(minUv, maxUv, sourceView);
    for (uint row = bounds.y; row <= bounds.w; row++)
    {
        for (uint word = bounds.x / 32u; word <= bounds.z / 32u; word++)
        {
            uint mask = ClusterRasterGridMask(row, word, bounds, sourceView);
            while (mask != 0u)
            {
                uint column = word * 32u + firstbitlow(mask);
                mask &= mask - 1u;
                uint regionIndex = ClusterRasterGridRegion(row, column, sourceView);
                uint viewIndex = ClusterEncodePolicyViewIndex(regionIndex);
                ClusterViewUniforms view = ClusterApplyRasterRegion(
                    sourceView, RasterTargetInvocations[regionIndex], regionIndex);
                float2 regionMin, regionMax;
#if CLUSTER_CULL_PHASE == 1
                bool occluded = IsOccludedByHiZ(cluster, transform, boundsExpansion,
                    true, viewIndex, view, regionMin, regionMax);
                if (view.HasPrevHistory != 0u && occluded)
                    AppendCandidate(pageOffset, clusterID, instanceID,
                        viewIndex | CLUSTER_RASTER_REGION_BIT, view.MaxCandidates);
                else
                    AppendVisible(pageOffset, clusterID, instanceID, viewIndex,
                        ShouldUseSWRaster(regionMin, regionMax, view), view.MaxCandidates);
#else
                bool occluded = IsOccludedByHiZ(cluster, transform, boundsExpansion,
                    false, viewIndex, view, regionMin, regionMax);
                if (!occluded)
                    AppendVisiblePhase2(pageOffset, clusterID, instanceID, viewIndex,
                        ShouldUseSWRaster(regionMin, regionMax, view), view.MaxCandidates);
#endif
            }
        }
    }
}

#if CLUSTER_CULL_PHASE == 1
[shader("compute")]
[numthreads(64, 1, 1)]
void CullPhaseOne(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    uint viewIndex;
    ClusterViewUniforms view;
    PackedCluster cluster;
    RenderInstanceTransform t;
    float boundsExpansion;
    if (!LoadAndFilterCandidate(
            dispatchThreadID.x,
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion))
        return;

    if (view.TargetPolicy != 0u)
    {
        CullRasterRegions(pageOffset, clusterID, instanceID, cluster, t, boundsExpansion, view);
        return;
    }

    if (view.HasPrevHistory == 0)
    {
        AppendCandidate(pageOffset, clusterID, instanceID, viewIndex, view.MaxCandidates);
        return;
    }

    float2 minUv, maxUv;
    bool occluded = IsOccludedByHiZ(
        cluster,
        t,
        boundsExpansion,
        true,
        viewIndex,
        view,
        minUv,
        maxUv);
    if (occluded)
    {
        AppendCandidate(pageOffset, clusterID, instanceID, viewIndex, view.MaxCandidates);
    }
    else
    {
        bool isSW = ShouldUseSWRaster(minUv, maxUv, view);
        AppendVisible(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            isSW,
            view.MaxCandidates);
    }
}
#elif CLUSTER_CULL_PHASE == 2

void AppendVisiblePhase2(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    bool isSW,
    uint maxCandidates)
{
    uint localIdx;
    CullDrawArgs base0 = LoadCullDrawArgs(BaseDrawArgs0, 0u);
    CullDrawArgs base1 = LoadCullDrawArgs(BaseDrawArgs1, 0u);
    CullDrawArgs base2 = LoadCullDrawArgs(BaseDrawArgs2, 0u);
    uint baseSWCount = base0.SWCount + base1.SWCount + base2.SWCount;
    uint baseHWCount = base0.HWCount + base1.HWCount + base2.HWCount;
    uint baseCount = baseSWCount + baseHWCount;

    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint activeCount = WaveMaskBitCount(activeMask);
    uint totalBase = 0u;
    if (lane == leader)
        PhaseDrawArgs.InterlockedAdd(12, activeCount, totalBase);
    totalBase = WaveReadLaneAt(totalBase, leader);
    bool admitted = baseCount + totalBase + WaveMaskPrefix(activeMask, lane) < maxCandidates;
    if (!admitted)
        return;

    uint4 routeMask = WaveActiveBallot(admitted && isSW);
    if (!isSW)
        routeMask = WaveActiveBallot(admitted && !isSW);
    uint routeLeader = WaveMaskFirstLane(routeMask);
    uint routeCount = WaveMaskBitCount(routeMask);
    uint routeBase = 0u;
    if (lane == routeLeader)
    {
        if (isSW)
            PhaseDrawArgs.InterlockedAdd(4, routeCount, routeBase);
        else
            PhaseDrawArgs.InterlockedAdd(8, routeCount, routeBase);
    }
    routeBase = WaveReadLaneAt(routeBase, routeLeader);
    localIdx = routeBase + WaveMaskPrefix(routeMask, lane);
    if (isSW)
    {
        uint globalIdx = baseSWCount + localIdx;
        if (globalIdx < maxCandidates)
            VisibleClusters[globalIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
    }
    else
    {
        uint hwIdx = maxCandidates - 1 - baseHWCount - localIdx;
        if (localIdx < maxCandidates)
            VisibleClusters[hwIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullPhaseTwo(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    uint viewIndex;
    ClusterViewUniforms view;
    PackedCluster cluster;
    RenderInstanceTransform t;
    float boundsExpansion;
    if (!LoadAndFilterCandidate(
            dispatchThreadID.x,
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion))
        return;

    if (view.TargetPolicy != 0u && view.ViewPadding == 0u)
    {
        CullRasterRegions(pageOffset, clusterID, instanceID, cluster, t, boundsExpansion, view);
        return;
    }
    viewIndex &= ~CLUSTER_RASTER_REGION_BIT;

    float2 minUv, maxUv;
    bool occluded = IsOccludedByHiZ(
        cluster,
        t,
        boundsExpansion,
        false,
        viewIndex,
        view,
        minUv,
        maxUv);
    if (!occluded)
    {
        bool isSW = ShouldUseSWRaster(minUv, maxUv, view);
        AppendVisiblePhase2(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            isSW,
            view.MaxCandidates);
    }
}
#endif
