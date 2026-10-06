#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view_access.slang"
#include "cluster_target_policy_contract.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"
#include "wave_queue.slang"

StructuredBuffer<ClusterViewUniforms> Views;

ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer PageHeap;
StructuredBuffer<ClusterCandidate> CandidateClusters;
StructuredBuffer<uint> CandidateCount;
#if CLUSTER_CULL_PHASE == 1
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DrawArgs;
ByteAddressBuffer BaseDrawArgs0;
ByteAddressBuffer BaseDrawArgs1;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DrawRequest> VisibleClusters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> VisibleClusterRasterWindows;
Texture2DArray<float> HiZTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> NextCandidates;
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
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> VisibleClusterRasterWindows;
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
    c.VertReuseBatchWord0 = PageHeap.Load(offset + 80);
    c.VertReuseBatchWord1 = PageHeap.Load(offset + 84);
    c.VertReuseBatchWord2 = PageHeap.Load(offset + 88);
    c.VertReuseBatchWord3 = PageHeap.Load(offset + 92);

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
    float errorScale = view.TargetPolicy != 0u
        ? abs(t.Scale) * min(min(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z))
        : maxScale;
    return ClusterSphereError(worldCenter, worldRadius, lodError * errorScale, view) <= view.LodThreshold;
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

struct ClusterScreenBounds
{
    float2 MinUv;
    float2 MaxUv;
    float NearDepth;
    uint MipLevel;
};

ClusterScreenBounds ProjectClusterScreenBounds(PackedCluster cluster,
    RenderInstanceTransform transform, float boundsExpansion, bool usePrev, ClusterViewUniforms view)
{
    ClusterScreenBounds bounds;
    BuildClusterAabbScreenBoundsAndNearDepth(cluster, transform, usePrev, boundsExpansion, view,
        bounds.MinUv, bounds.MaxUv, bounds.NearDepth, bounds.MipLevel);
    return bounds;
}

bool IsOccludedByHiZ(ClusterScreenBounds bounds, bool usePrev, uint viewIndex, ClusterViewUniforms view)
{
    if (view.CullingEnabled == 0u || view.HiZMipCount == 0u)
        return false;
    if (view.TargetPolicy != 0u)
        return ClusterTargetPolicyScreenBoundsOccluded(bounds.MinUv, bounds.MaxUv, bounds.NearDepth, usePrev, view);
    float hizDepth = SampleHiZMax(HiZTexture, bounds.MinUv, bounds.MaxUv, bounds.MipLevel, viewIndex, view);
    float epsilon = 5e-5 + 0.5 * max(view.HiZInvSize.x, view.HiZInvSize.y);
    return bounds.NearDepth > hizDepth + epsilon;
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
void AppendVisibleWindow(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint2 rasterWindow,
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
        totalBase = ReserveClusterWork(DrawArgs, 12u, activeCount,
            maxCandidates - min(baseCount, maxCandidates), 8u);
    totalBase = WaveReadLaneAt(totalBase, leader);
    bool admitted = baseCount + totalBase + WaveMaskPrefix(activeMask, lane) < maxCandidates;
    if (!admitted)
    {
        RejectClusterWork(8u);
        return;
    }

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
    if (LoadClusterCullView(Views, PolicyClusterViews, viewIndex).TargetPolicy != 0u)
        AddClusterWorkMetric(isSW ? 56u : 40u, 1u);
    if (isSW)
    {
        uint globalIdx = baseSWCount + outIdx;
        if (globalIdx < maxCandidates)
        {
            VisibleClusters[globalIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
            VisibleClusterRasterWindows[globalIdx] = rasterWindow;
        }
    }
    else
    {
        uint hwIdx = maxCandidates - 1 - baseHWCount - outIdx;
        if (baseHWCount + outIdx < maxCandidates)
        {
            VisibleClusters[hwIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
            VisibleClusterRasterWindows[hwIdx] = rasterWindow;
        }
    }
}

void AppendCandidate(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint maxCandidates,
    uint keepCoarseGeometry)
{
    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint waveCount = WaveMaskBitCount(activeMask);
    uint waveBase = 0u;
    if (lane == leader)
        waveBase = ReserveClusterWork(NextCandidateCount, waveCount, maxCandidates, 4u);
    waveBase = WaveReadLaneAt(waveBase, leader);
    uint outIdx = waveBase + WaveMaskPrefix(activeMask, lane);
    if (outIdx >= maxCandidates)
    {
        RejectClusterWork(4u);
        return;
    }

    if (lane == leader)
    {
        uint groups = (min(waveBase + waveCount, maxCandidates) + 63u) / 64u;
        uint unused;
        NextCandidateArgs.InterlockedMax(0, groups, unused);
    }
    NextCandidates[outIdx] = MakeClusterCandidate(
        MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), keepCoarseGeometry);
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
    out float boundsExpansion,
    out uint2 rasterWindow,
    out ClusterScreenBounds currentBounds)
{
    pageOffset = 0;
    clusterID = 0;
    instanceID = 0;
    viewIndex = 0;
    view = (ClusterViewUniforms)0;
    cluster = (PackedCluster)0;
    t = (RenderInstanceTransform)0;
    boundsExpansion = 0.0;
    rasterWindow = 0u.xx;
    currentBounds = (ClusterScreenBounds)0;
    currentBounds.MaxUv = 1.0.xx;

    uint count = CandidateCount[0];
    uint capacity;
    uint stride;
    CandidateClusters.GetDimensions(capacity, stride);
    if (threadId >= min(count, capacity))
        return false;

    ClusterCandidate work = CandidateClusters[threadId];
    DrawRequest candidate = work.Request;
    pageOffset = candidate.PageOffset;
    clusterID = DrawRequestClusterID(candidate);
    instanceID = candidate.InstanceID;
    viewIndex = DrawRequestViewIndex(candidate);
    view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
    if (threadId >= view.MaxCandidates)
        return false;
    if (view.TargetPolicy != 0u) AddClusterWorkMetric(8u, 1u);

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

    if (!(work.KeepCoarseGeometry != 0u && view.CullingEnabled != 0u) &&
        !PassesLODFilter(cluster, t, view))
        return false;

#if CLUSTER_CULL_PHASE == 2
    bool needsCurrentBounds = true;
#else
    bool needsCurrentBounds = view.TargetPolicy != 0u || view.HasPrevHistory == 0u;
#endif
    if (needsCurrentBounds && view.CullingEnabled != 0u)
        currentBounds = ProjectClusterScreenBounds(cluster, t, boundsExpansion, false, view);
    if (view.TargetPolicy != 0u && !ClusterTargetPolicyRasterBoundsWindow(
            currentBounds.MinUv, currentBounds.MaxUv, view, rasterWindow))
        return false;
    if (view.CullingEnabled == 0u)
    {
        currentBounds = (ClusterScreenBounds)0;
        currentBounds.MaxUv = 1.0.xx;
    }
    return true;
}

#if CLUSTER_CULL_PHASE == 2
void AppendVisibleWindow(uint pageOffset, uint clusterID, uint instanceID,
    uint viewIndex, uint2 rasterWindow, bool isSW, uint maxCandidates);
#endif

// Hardware work is bounded by the target's raster window, independently of
// its virtual resolution. Empty windows never enter the raster bins.
void AppendVisible(uint pageOffset, uint clusterID, uint instanceID,
    uint viewIndex, uint2 rasterWindow, bool isSW, uint maxCandidates,
    ClusterScreenBounds currentBounds)
{
    ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex);
    if (isSW || view.TargetPolicy == 0u)
    {
        if (view.TargetPolicy != 0u) AddClusterWorkMetric(48u, 1u);
        AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex, rasterWindow, isSW, maxCandidates);
        return;
    }
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    uint windowSize = target.RasterWindowPages;
    if (windowSize == 0u)
    {
        AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex, rasterWindow, false, maxCandidates);
        return;
    }
    uint4 pages = ClusterUnpackRasterTargetPageWindow(rasterWindow);
    uint2 firstPixel;
    uint2 lastPixel;
    if (!ClusterTargetPixelBounds(currentBounds.MinUv, currentBounds.MaxUv,
            view, target, firstPixel, lastPixel)) return;
    // Partition on the page grid, including small rectangles crossing a boundary.
    // Each partition repeats all draw-eligibility tests, not just request bits.
    uint2 firstWindow = pages.xy / windowSize * windowSize;
    for (uint y = firstWindow.y; y <= pages.w; y += windowSize)
    for (uint x = firstWindow.x; x <= pages.z; x += windowSize)
    {
        uint2 first = max(uint2(x, y), pages.xy);
        uint2 last = min(uint2(x, y) + windowSize - 1u, pages.zw);
        uint2 clipped;
        AddClusterWorkMetric(16u, 1u);
        if (ClusterTargetCropRasterWindow(target, uint4(first, last), firstPixel, lastPixel, clipped))
        {
            AddClusterWorkMetric(32u, 1u);
            AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex,
                clipped, false, maxCandidates);
        }
        else AddClusterWorkMetric(24u, 1u);
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
    uint2 rasterWindow;
    ClusterScreenBounds currentBounds;
    bool valid = LoadAndFilterCandidate(
            dispatchThreadID.x,
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion,
            rasterWindow,
            currentBounds);
    if (!valid)
        return;

    ClusterScreenBounds bounds = currentBounds;
    bool occluded = false;
    if (view.HasPrevHistory != 0u && view.CullingEnabled != 0u)
    {
        bounds = ProjectClusterScreenBounds(cluster, t, boundsExpansion, true, view);
        occluded = IsOccludedByHiZ(bounds, true, viewIndex, view);
    }
    // Missing history cannot occlude geometry. Draw it now; only actual previous-frame
    // occlusion belongs in the second-pass candidate list.
    if (occluded)
    {
        AppendCandidate(pageOffset, clusterID, instanceID, viewIndex, view.MaxCandidates,
            CandidateClusters[dispatchThreadID.x].KeepCoarseGeometry);
    }
    else
    {
        bool isSW = ShouldUseSWRaster(bounds.MinUv, bounds.MaxUv, view);
        AppendVisible(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            rasterWindow,
            isSW,
            view.MaxCandidates,
            currentBounds);
    }
}
#elif CLUSTER_CULL_PHASE == 2

void AppendVisibleWindow(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint2 rasterWindow,
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
        totalBase = ReserveClusterWork(PhaseDrawArgs, 12u, activeCount,
            maxCandidates - min(baseCount, maxCandidates), 8u);
    totalBase = WaveReadLaneAt(totalBase, leader);
    bool admitted = baseCount + totalBase + WaveMaskPrefix(activeMask, lane) < maxCandidates;
    if (!admitted)
    {
        RejectClusterWork(8u);
        return;
    }

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
    if (LoadClusterCullView(Views, PolicyClusterViews, viewIndex).TargetPolicy != 0u)
        AddClusterWorkMetric(isSW ? 56u : 40u, 1u);
    if (isSW)
    {
        uint globalIdx = baseSWCount + localIdx;
        if (globalIdx < maxCandidates)
        {
            VisibleClusters[globalIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
            VisibleClusterRasterWindows[globalIdx] = rasterWindow;
        }
    }
    else
    {
        uint hwIdx = maxCandidates - 1 - baseHWCount - localIdx;
        if (localIdx < maxCandidates)
        {
            VisibleClusters[hwIdx] = MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex);
            VisibleClusterRasterWindows[hwIdx] = rasterWindow;
        }
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
    uint2 rasterWindow;
    ClusterScreenBounds currentBounds;
    bool valid = LoadAndFilterCandidate(
            dispatchThreadID.x,
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion,
            rasterWindow,
            currentBounds);
    if (!valid)
        return;

    bool occluded = IsOccludedByHiZ(currentBounds, false, viewIndex, view);
    if (!occluded)
    {
        bool isSW = ShouldUseSWRaster(currentBounds.MinUv, currentBounds.MaxUv, view);
        AppendVisible(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            rasterWindow,
            isSW,
            view.MaxCandidates,
            currentBounds);
    }
}
#endif

#include "cluster_work_status.slang"
