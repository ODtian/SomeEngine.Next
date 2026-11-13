__exported import cluster_structures;
__exported import common;
#include "cluster_instance_properties.slang"
__exported import cluster_view_access;
#include "cluster_target_policy.slang"
__exported import resource_effects;
__exported import wave_aggregation;
__exported import wave_queue;
__exported import cluster_bin_common;

StructuredBuffer<uint> ClusterRasterStages;
StructuredBuffer<uint2> LayerCullMaterials;
struct LayerCullParametersData { uint TileSize; uint HardwareWindowTiles; uint2 Padding; };
ConstantBuffer<LayerCullParametersData> LayerCullParameters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> LayerClusterTileWindows;

#ifndef CLUSTER_PERSISTENT_CULL
StructuredBuffer<ClusterViewUniforms> Views;

ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
StructuredBuffer<uint> MaterialBounds;
StructuredBuffer<ClusterCandidate> CandidateClusters;
StructuredBuffer<uint> CandidateCount;
#endif
ByteAddressBuffer PageHeap;
#if CLUSTER_CULL_PHASE == 1
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DrawArgs;
ByteAddressBuffer BaseDrawArgs0;
ByteAddressBuffer BaseDrawArgs1;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<VisibleCluster> VisibleClusters;
#ifndef CLUSTER_PERSISTENT_CULL
Texture2DArray<float> HiZTexture;
#endif
#ifndef CLUSTER_PERSISTENT_CULL
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterCandidate> NextCandidates;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> NextCandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextCandidateArgs;
#else
ByteAddressBuffer BaseDrawArgs2;
#endif
#elif CLUSTER_CULL_PHASE == 2
ByteAddressBuffer BaseDrawArgs0;
ByteAddressBuffer BaseDrawArgs1;
ByteAddressBuffer BaseDrawArgs2;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<VisibleCluster> VisibleClusters;
#ifndef CLUSTER_PERSISTENT_CULL
Texture2DArray<float> HiZTexture;
#endif
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PhaseDrawArgs;
#else
#error CLUSTER_CULL_PHASE must be 1 or 2
#endif

#ifndef CLUSTER_PERSISTENT_CULL
StructuredBuffer<ClusterViewUniforms> PolicyClusterViews;
#endif

bool ClusterHasTargetMaterials(uint pageOffset, uint clusterID, uint instanceID,
    bool shadowPages, bool layerTiles, out bool clipByOpaqueDepth)
{
    clipByOpaqueDepth = true;
    if (shadowPages) return true;
    uint cluster = pageOffset + PageHeap.Load(pageOffset + 16u) + clusterID * PACKED_CLUSTER_STRIDE_BYTES;
    uint triangleCount = (PageHeap.Load(cluster + 60u) >> 8u) & 255u;
    ClusterMaterialRanges materials = LoadClusterMaterialRanges(PageHeap, cluster, pageOffset);
    uint offset = LoadClusterMaterialSlotOffset(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), instanceID);
    uint capacity, stride;
    ClusterRasterStages.GetDimensions(capacity, stride);
    bool accepts = false;
    uint count = ClusterMaterialRangeCount(materials, triangleCount);
    for (uint index = 0u; index < count; index++)
    {
        uint2 range = ClusterMaterialRange(PageHeap, materials, triangleCount, index);
        uint stage = GetStageSlot(ClusterRasterStages, offset, range.y, capacity);
        if (stage == SLOT_INVALID || ((stage & 0x08000000u) != 0u) != layerTiles) continue;
        accepts = true;
        if (layerTiles) clipByOpaqueDepth = clipByOpaqueDepth && (LayerCullMaterials[offset + range.y].y & 256u) != 0u;
    }
    return accepts;
}

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
    c.PackedMaximumEdgeLength = PageHeap.Load(offset + 64);
    c.PackedMaterials = PageHeap.Load(offset + 68);
    c.PackedRanges = PageHeap.Load(offset + 72);
    c.MaterialTableOffset = PageHeap.Load(offset + 76);
    c.VertReuseBatchWord0 = PageHeap.Load(offset + 80);
    c.VertReuseBatchWord1 = PageHeap.Load(offset + 84);
    c.VertReuseBatchWord2 = PageHeap.Load(offset + 88);
    c.VertReuseBatchWord3 = PageHeap.Load(offset + 92);

    return c;
}

__exported import cluster_hiz_shared;
#include "cluster_deform_bounds.slang"

bool PassesLODFilter(
    PackedCluster cluster,
    RenderInstanceTransform t,
    ClusterViewUniforms view,
    float lodError)
{
    // Current leaves provide complete coverage while detail pages are streaming.
    if (view.CullingEnabled == 0u)
        return ((cluster.PackedCounts >> 24u) & 6u) != 0u;

    float3 worldCenter = TransformInstanceLocalPosition(cluster.LODCenter, t);
    float maxScale = abs(t.Scale) * max(max(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z));
    float worldRadius = cluster.LODRadius * maxScale;
    float errorScale = abs(t.Scale) * min(min(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z));
    return ClusterSphereError(worldCenter, worldRadius, lodError * errorScale, view) <= view.LodThreshold ||
        ((cluster.PackedCounts >> 24u) & 2u) != 0u;
}

struct ClusterWorldBounds
{
    float3 Min;
    float3 Max;
    uint VertexCount;
};

ClusterWorldBounds TransformClusterBounds(
    PackedCluster cluster,
    RenderInstanceTransform transform,
    float boundsExpansion)
{
    ClusterWorldBounds bounds = (ClusterWorldBounds)0;
    bounds.VertexCount = cluster.PackedCounts & 0xFF;
    if (bounds.VertexCount == 0u) return bounds;
    bounds.Min = 1e20.xxx;
    bounds.Max = -1e20.xxx;
    [unroll]
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 local = float3(
            (corner & 1u) != 0u ? cluster.BoundMax.x : cluster.BoundMin.x,
            (corner & 2u) != 0u ? cluster.BoundMax.y : cluster.BoundMin.y,
            (corner & 4u) != 0u ? cluster.BoundMax.z : cluster.BoundMin.z);
        float3 world = TransformInstanceLocalPosition(local, transform);
        bounds.Min = min(bounds.Min, world);
        bounds.Max = max(bounds.Max, world);
    }
    float expansion = max(0.0, boundsExpansion);
    bounds.Min -= expansion.xxx;
    bounds.Max += expansion.xxx;
    return bounds;
}

bool ProjectClusterAabb(
    ClusterWorldBounds worldBounds,
    bool usePrev,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel,
    uint depthCacheClass, bool shadowPages = false)
{
    if (worldBounds.VertexCount == 0u)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0;
        return true;
    }

    if (!usePrev && shadowPages && !ClusterTargetPolicyWorldDepthAccepts(worldBounds.Min, worldBounds.Max, view, depthCacheClass))
    {
        minUv = 0.0.xx;
        maxUv = 0.0.xx;
        nearDepth = 0.0;
        mipLevel = 0u;
        return false;
    }

    BuildWorldAabbScreenBoundsAndNearDepth(
        worldBounds.Min, worldBounds.Max, usePrev, view,
        minUv, maxUv, nearDepth, mipLevel);
    return true;
}

struct ClusterScreenBounds
{
    float2 MinUv;
    float2 MaxUv;
    float NearDepth;
    uint MipLevel;
    bool DepthAccepted;
    bool ClipByOpaqueDepth;
};

ClusterScreenBounds ProjectClusterScreenBounds(ClusterWorldBounds worldBounds,
    bool usePrev, ClusterViewUniforms view,
    uint depthCacheClass = 1u, bool shadowPages = false)
{
    ClusterScreenBounds bounds = (ClusterScreenBounds)0;
    bounds.DepthAccepted = ProjectClusterAabb(worldBounds, usePrev, view,
        bounds.MinUv, bounds.MaxUv, bounds.NearDepth, bounds.MipLevel, depthCacheClass, shadowPages);
    return bounds;
}

bool IsOccludedByHiZ(ClusterScreenBounds bounds, bool usePrev, uint viewIndex, ClusterViewUniforms view,
    uint depthCacheClass, bool shadowPages = false)
{
    if (view.CullingEnabled == 0u || view.HiZMipCount == 0u)
        return false;
    if (shadowPages)
        return ClusterTargetPolicyScreenBoundsOccluded(bounds.MinUv, bounds.MaxUv, bounds.NearDepth, usePrev, view, depthCacheClass);
    float hizDepth = SampleHiZMax(HiZTexture, bounds.MinUv, bounds.MaxUv, bounds.MipLevel, viewIndex, view);
    float epsilon = (abs(bounds.NearDepth) + abs(hizDepth) + 1.0) * 9.5367431640625e-7;
    return bounds.NearDepth > hizDepth + epsilon;
}

bool ShouldUseSWRaster(
    float2 minUv,
    float2 maxUv,
    float nearDepth,
    ClusterViewUniforms view,
    PackedCluster cluster,
    RenderInstanceTransform transform,
    float boundsExpansion)
{
    if (view.RasterMode == 1u || view.CullingEnabled == 0u) return false;
    if (ClusterViewClipsDepth(view) && nearDepth <= 0.0)
        return false;
    if (view.RasterMode == 2u) return true;
    float maximumEdge = abs(DecodeClusterMaximumEdgeLength(cluster));
    float maximumScale = abs(transform.Scale) *
        max(max(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
    float3 worldCenter = TransformInstanceLocalPosition(cluster.LODCenter, transform);
    float worldRadius = cluster.LODRadius * maximumScale + boundsExpansion;
    float worldEdge = maximumEdge * maximumScale + 2.0 * boundsExpansion;
    float edgePixels = worldEdge * CalculateClusterSpherePixelScale(worldCenter, worldRadius, view);
    return edgePixels <= view.SoftwareRasterEdgeThreshold;
}

uint ClusterDepthBucket(PackedCluster cluster, RenderInstanceTransform transform, ClusterViewUniforms view)
{
    float3 center = TransformInstanceLocalPosition((cluster.BoundMin + cluster.BoundMax) * 0.5, transform);
    float z = -mul(float4(ClusterPositionForView(center, view), 1.0), view.View).z;
    // UE's 1000..100000 cm logarithmic range expressed in project metres.
    return min(uint(max(floor(log2(clamp(z, 10.0, 1000.0) / 10.0) * (256.0 / log2(100.0))), 0.0)), 255u);
}

#include "cluster_raster_target_filter.slang"

#if CLUSTER_CULL_PHASE == 1
void AppendVisibleWindow(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint2 rasterWindow,
    uint depthBucket,
    bool isSW,
    uint maxCandidates, uint flags, bool targetFilters, bool layerTiles = false, uint4 tileWindow = 0u.xxxx)
{
    CullDrawArgs base0 = LoadCullDrawArgs(BaseDrawArgs0, 0u);
    CullDrawArgs base1 = LoadCullDrawArgs(BaseDrawArgs1, 0u);
    uint baseSWCount = base0.SWCount + base1.SWCount;
#ifdef CLUSTER_PERSISTENT_CULL
    CullDrawArgs base2 = LoadCullDrawArgs(BaseDrawArgs2, 0u);
    baseSWCount += base2.SWCount;
#endif
    uint baseHWCount = base0.HWCount + base1.HWCount;
#ifdef CLUSTER_PERSISTENT_CULL
    baseHWCount += base2.HWCount;
#endif
    uint baseCount = baseSWCount + baseHWCount;
    uint4 activeMask = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(activeMask);
    uint activeCount = WaveMaskBitCount(activeMask);
    uint totalBase = 0u;
    if (lane == leader)
        totalBase = ReserveClusterVisibleWork(DrawArgs, 12u, activeCount,
            maxCandidates - min(baseCount, maxCandidates));
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
    if (isSW)
    {
        if (targetFilters)
            AddClusterWorkMetric(56u, 1u);
        uint globalIdx = baseSWCount + outIdx;
        if (globalIdx < maxCandidates)
        {
            VisibleClusters[globalIdx] = MakeVisibleCluster(MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), rasterWindow, depthBucket, 1u | flags);
            if (layerTiles) LayerClusterTileWindows[globalIdx] = tileWindow;
        }
    }
    else
    {
        if (targetFilters)
            AddClusterWorkMetric(40u, 1u);
        uint hwIdx = maxCandidates - 1 - baseHWCount - outIdx;
        if (baseHWCount + outIdx < maxCandidates)
        {
            VisibleClusters[hwIdx] = MakeVisibleCluster(MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), rasterWindow, depthBucket, flags);
            if (layerTiles) LayerClusterTileWindows[hwIdx] = tileWindow;
        }
    }
}

void AppendCandidate(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint maxCandidates,
    uint flags)
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
        MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), flags);
}
#endif

bool LoadAndFilterCandidate(
    ClusterCandidate work,
    out uint pageOffset,
    out uint clusterID,
    out uint instanceID,
    out uint viewIndex,
    out ClusterViewUniforms view,
    out PackedCluster cluster,
    out RenderInstanceTransform t,
    out float boundsExpansion,
    out uint2 rasterWindow,
    out ClusterWorldBounds worldBounds,
    out ClusterScreenBounds currentBounds,
    bool targetFilters = false,
    bool currentProjection = true, bool layerTiles = false)
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
    worldBounds = (ClusterWorldBounds)0;
    currentBounds = (ClusterScreenBounds)0;
    currentBounds.MaxUv = 1.0.xx;

    DrawRequest candidate = CandidateRequest(work);
    pageOffset = candidate.PageOffset;
    clusterID = DrawRequestClusterID(candidate);
    instanceID = candidate.InstanceID;
    viewIndex = DrawRequestViewIndex(candidate);
    bool clipByOpaqueDepth;
    if (!ClusterHasTargetMaterials(pageOffset, clusterID, instanceID, targetFilters, layerTiles, clipByOpaqueDepth)) return false;
    view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex, targetFilters);
    view = ApplyClusterInstanceBounds(MaterialBounds, InstanceData, InstanceProperties, instanceID, view);
    if (!IsInstanceVisibleToView(LoadInstanceFirstPerson(InstanceData, InstanceProperties, instanceID), view)) return false;
    view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, instanceID, view);
    if (targetFilters) AddClusterWorkMetric(8u, 1u);

    cluster = LoadCluster(pageOffset, clusterID);
    float lodError = ApplyDrawClusterBounds(cluster, (PageHeap.Load(pageOffset + 44u) >> 1u) + clusterID, instanceID, false);
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

    if (!PassesLODFilter(cluster, t, view, lodError))
        return false;

    uint depthCacheClass = targetFilters
        ? LoadTargetDepthCacheClass(instanceID) : 1u;
    if (view.CullingEnabled != 0u)
    {
        worldBounds = TransformClusterBounds(cluster, t, boundsExpansion);
        if (currentProjection)
        {
            currentBounds = ProjectClusterScreenBounds(worldBounds, false, view, depthCacheClass, targetFilters);
            if (!currentBounds.DepthAccepted)
                return false;
        }
    }
    if (targetFilters && !ClusterTargetPolicyRasterBoundsWindow(
            currentBounds.MinUv, currentBounds.MaxUv, view, rasterWindow,
            depthCacheClass, ClusterTargetPolicyRequiredPageFlags(currentBounds.NearDepth, view)))
        return false;
    if (view.CullingEnabled == 0u)
    {
        currentBounds = (ClusterScreenBounds)0;
        currentBounds.MaxUv = 1.0.xx;
    }
    currentBounds.ClipByOpaqueDepth = clipByOpaqueDepth;
    return true;
}

#if CLUSTER_CULL_PHASE == 2
void AppendVisibleWindow(uint pageOffset, uint clusterID, uint instanceID,
    uint viewIndex, uint2 rasterWindow, uint depthBucket, bool isSW, uint maxCandidates, uint flags, bool targetFilters,
    bool layerTiles = false, uint4 tileWindow = 0u.xxxx);
#endif

// Hardware work is bounded by the target's raster window, independently of
// its virtual resolution. Empty windows never enter the raster bins.
void AppendVisible(uint pageOffset, uint clusterID, uint instanceID,
    uint viewIndex, uint2 rasterWindow, bool isSW, uint maxCandidates,
    ClusterScreenBounds currentBounds, PackedCluster cluster, RenderInstanceTransform transform,
    bool targetFilters = false, bool layerTiles = false)
{
    ClusterViewUniforms view = LoadClusterCullView(Views, PolicyClusterViews, viewIndex, targetFilters);
    uint depthBucket = ClusterDepthBucket(cluster, transform, view);
    uint requiredPageFlags = targetFilters ? ClusterTargetPolicyRequiredPageFlags(currentBounds.NearDepth, view) : 0u;
    uint flags = requiredPageFlags != 0u ? 2u : 0u;
    if (layerTiles)
    {
        float2 pixels = float2(view.ScreenWidth, view.ScreenHeight);
        float tileSize = float(LayerCullParameters.TileSize);
        uint2 first = uint2(floor(saturate(currentBounds.MinUv) * pixels / tileSize));
        uint2 last = uint2(ceil(saturate(currentBounds.MaxUv) * pixels / tileSize));
        AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex, 0u.xx, depthBucket,
            isSW, maxCandidates, 0u, false, true, uint4(first, last));
        return;
    }
    if (!targetFilters)
    {
        if (targetFilters) AddClusterWorkMetric(48u, 1u);
        AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex, rasterWindow, depthBucket, isSW, maxCandidates, flags, targetFilters);
        return;
    }
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    uint windowSize = isSW ? 8u : target.RasterWindowPages;
    if (windowSize == 0u)
    {
        AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex, rasterWindow, depthBucket, isSW, maxCandidates, flags, targetFilters);
        return;
    }
    uint4 pages = ClusterUnpackRasterTargetPageWindow(rasterWindow);
    uint2 firstPixel;
    uint2 lastPixel;
    if (!ClusterTargetPixelBounds(currentBounds.MinUv, currentBounds.MaxUv,
            view, target, firstPixel, lastPixel)) return;
    // Native windows start at this cluster's first page.
    // Each window repeats the draw-eligibility tests.
    uint2 firstWindow = pages.xy;
    for (uint y = firstWindow.y; y <= pages.w; y += windowSize)
    for (uint x = firstWindow.x; x <= pages.z; x += windowSize)
    {
        uint2 first = max(uint2(x, y), pages.xy);
        uint2 last = min(uint2(x, y) + windowSize - 1u, pages.zw);
        uint2 clipped;
        AddClusterWorkMetric(16u, 1u);
        if (ClusterTargetCropRasterWindow(target, uint4(first, last), firstPixel, lastPixel, clipped,
                LoadTargetDepthCacheClass(instanceID), requiredPageFlags))
        {
            AddClusterWorkMetric(32u, 1u);
            AppendVisibleWindow(pageOffset, clusterID, instanceID, viewIndex,
                clipped, depthBucket, isSW, maxCandidates, flags, targetFilters);
        }
        else AddClusterWorkMetric(24u, 1u);
    }
}

#if CLUSTER_CULL_PHASE == 1
void CullClusterCandidateData(ClusterCandidate candidate, bool targetFilters, bool layerTiles)
{
    // Target windows and the post-HiZ pass consume current bounds immediately.
    // Primary main needs them only after surviving previous-frame occlusion.
    bool currentProjection = targetFilters || layerTiles;
#ifdef CLUSTER_PERSISTENT_CULL
    currentProjection = currentProjection || TraversalLevel.TraversalMode == 2u;
#endif
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    uint viewIndex;
    ClusterViewUniforms view;
    PackedCluster cluster;
    RenderInstanceTransform t;
    float boundsExpansion;
    uint2 rasterWindow;
    ClusterWorldBounds worldBounds;
    ClusterScreenBounds currentBounds;
    bool valid = LoadAndFilterCandidate(
            candidate,
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion,
            rasterWindow,
            worldBounds,
            currentBounds, targetFilters, currentProjection, layerTiles);
    if (!valid)
        return;

#ifdef CLUSTER_PERSISTENT_CULL
    if (TraversalLevel.TraversalMode == 2u)
    {
        if ((layerTiles && !currentBounds.ClipByOpaqueDepth) || !IsOccludedByHiZ(currentBounds, false, layerTiles ? view.TargetViewIndex : viewIndex, view,
                targetFilters ? LoadTargetDepthCacheClass(instanceID) : 1u, targetFilters))
        {
            bool software = ShouldUseSWRaster(currentBounds.MinUv, currentBounds.MaxUv, currentBounds.NearDepth,
                view, cluster, t, boundsExpansion);
            AppendVisible(pageOffset, clusterID, instanceID, viewIndex, rasterWindow, software,
                view.MaxCandidates, currentBounds, cluster, t, targetFilters, layerTiles);
        }
        return;
    }
#endif
    ClusterScreenBounds bounds = currentBounds;
    bool occluded = false;
    if (view.HasPrevHistory != 0u && view.CullingEnabled != 0u &&
        (!layerTiles || currentBounds.ClipByOpaqueDepth))
    {
        PackedCluster previousCluster = LoadCluster(pageOffset, clusterID);
        ApplyDrawClusterBounds(previousCluster, (PageHeap.Load(pageOffset + 44u) >> 1u) + clusterID, instanceID, true);
        RenderInstanceTransform previous = LoadRenderTransform(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, RENDER_PREVIOUS_TRANSFORM_METADATA_WORD), instanceID);
        ClusterWorldBounds previousBounds = TransformClusterBounds(previousCluster, previous, boundsExpansion);
        bounds = ProjectClusterScreenBounds(previousBounds, true, view, 1u, targetFilters);
        occluded = IsOccludedByHiZ(bounds, true, viewIndex, view,
            targetFilters ? LoadTargetDepthCacheClass(instanceID) : 1u, targetFilters);
    }
    // Missing history cannot occlude geometry. Draw it now; only actual previous-frame
    // occlusion belongs in the second-pass candidate list.
    if (occluded)
    {
        AppendCandidate(pageOffset, clusterID, instanceID, viewIndex, view.MaxCandidates,
            CandidateFlags(candidate));
    }
    else
    {
        if (!currentProjection && view.CullingEnabled != 0u)
            currentBounds = ProjectClusterScreenBounds(worldBounds, false, view, 1u, targetFilters);
        ClusterScreenBounds rasterBounds = currentBounds;
        bool isSW = ShouldUseSWRaster(rasterBounds.MinUv, rasterBounds.MaxUv, rasterBounds.NearDepth, view, cluster, t, boundsExpansion);
        AppendVisible(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            rasterWindow,
            isSW,
            view.MaxCandidates,
            currentBounds, cluster, t, targetFilters, layerTiles);
    }
}

void CullClusterCandidate(uint candidateIndex, bool targetFilters = true, bool layerTiles = false)
{
    uint capacity, stride;
    CandidateClusters.GetDimensions(capacity, stride);
    if (candidateIndex >= min(CandidateCount[0], capacity)) return;
    CullClusterCandidateData(CandidateClusters[candidateIndex], targetFilters, layerTiles);
}

#ifndef CLUSTER_PERSISTENT_CULL
[shader("compute")]
[numthreads(64, 1, 1)]
void CullPhaseOne(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    CullClusterCandidate(dispatchThreadID.x);
}
#endif
#elif CLUSTER_CULL_PHASE == 2

void AppendVisibleWindow(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint2 rasterWindow,
    uint depthBucket,
    bool isSW,
    uint maxCandidates, uint flags, bool targetFilters, bool layerTiles, uint4 tileWindow)
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
        totalBase = ReserveClusterVisibleWork(PhaseDrawArgs, 12u, activeCount,
            maxCandidates - min(baseCount, maxCandidates));
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
    if (isSW)
    {
        if (targetFilters)
            AddClusterWorkMetric(56u, 1u);
        uint globalIdx = baseSWCount + localIdx;
        if (globalIdx < maxCandidates)
        {
            VisibleClusters[globalIdx] = MakeVisibleCluster(MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), rasterWindow, depthBucket, 1u | flags);
            if (layerTiles) LayerClusterTileWindows[globalIdx] = tileWindow;
        }
    }
    else
    {
        if (targetFilters)
            AddClusterWorkMetric(40u, 1u);
        uint hwIdx = maxCandidates - 1 - baseHWCount - localIdx;
        if (localIdx < maxCandidates)
        {
            VisibleClusters[hwIdx] = MakeVisibleCluster(MakeDrawRequest(pageOffset, clusterID, instanceID, viewIndex), rasterWindow, depthBucket, flags);
            if (layerTiles) LayerClusterTileWindows[hwIdx] = tileWindow;
        }
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullPhaseTwo(uint3 dispatchThreadID: SV_DispatchThreadID)
{
    static const bool targetFilters = false;
    uint capacity, stride;
    CandidateClusters.GetDimensions(capacity, stride);
    if (dispatchThreadID.x >= min(CandidateCount[0], capacity)) return;
    uint pageOffset;
    uint clusterID;
    uint instanceID;
    uint viewIndex;
    ClusterViewUniforms view;
    PackedCluster cluster;
    RenderInstanceTransform t;
    float boundsExpansion;
    uint2 rasterWindow;
    ClusterWorldBounds worldBounds;
    ClusterScreenBounds currentBounds;
    bool valid = LoadAndFilterCandidate(
            CandidateClusters[dispatchThreadID.x],
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            view,
            cluster,
            t,
            boundsExpansion,
            rasterWindow,
            worldBounds,
            currentBounds);
    if (!valid)
        return;

    bool occluded = IsOccludedByHiZ(currentBounds, false, viewIndex, view,
            targetFilters ? LoadTargetDepthCacheClass(instanceID) : 1u, targetFilters);
    if (!occluded)
    {
        bool isSW = ShouldUseSWRaster(currentBounds.MinUv, currentBounds.MaxUv, currentBounds.NearDepth, view, cluster, t, boundsExpansion);
        AppendVisible(
            pageOffset,
            clusterID,
            instanceID,
            viewIndex,
            rasterWindow,
            isSW,
            view.MaxCandidates,
            currentBounds, cluster, t);
    }
}
#endif

__exported import cluster_work_status;
