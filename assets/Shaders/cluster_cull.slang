#include "cluster_structures.slang"
#include "common.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view.slang"
#include "resource_effects.slang"

StructuredBuffer<ClusterViewUniforms> Views;

ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> CandidateClusters;
StructuredBuffer<uint> CandidateCount;
#if CLUSTER_CULL_PHASE == 1
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> VisibleClusters;
Texture2DArray<float> HiZTexture;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> NextCandidates;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> NextCandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer NextCandidateArgs;
#elif CLUSTER_CULL_PHASE == 2
ByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> VisibleClusters;
Texture2DArray<float> HiZTexture;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer PhaseDrawArgs;
#else
#error CLUSTER_CULL_PHASE must be 1 or 2
#endif

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

bool PassesLODFilter(
    PackedCluster cluster,
    RenderInstanceTransform t,
    ClusterViewUniforms view)
{
    if (view.CullingEnabled == 0u)
        return true;

    float3 worldCenter = TransformInstanceLocalPosition(cluster.LODCenter, t);
    float maxScale = abs(t.Scale) * max(max(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z));
    float worldRadius = cluster.LODRadius * maxScale;
    float clusterDist = max(0.0, distance(worldCenter, view.CameraPos) - worldRadius);
    float lodError = DecodeClusterLODError(cluster);
    return lodError * view.LodScale <= view.LodThreshold * clusterDist;
}

void BuildWorldAabbScreenBoundsAndNearDepth(
    float3 worldMin,
    float3 worldMax,
    bool usePrev,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    float4x4 viewProjMat = usePrev ? view.PrevViewProj : view.ViewProj;

    float3 corners[8];
    corners[0] = float3(worldMin.x, worldMin.y, worldMin.z);
    corners[1] = float3(worldMax.x, worldMin.y, worldMin.z);
    corners[2] = float3(worldMin.x, worldMax.y, worldMin.z);
    corners[3] = float3(worldMax.x, worldMax.y, worldMin.z);
    corners[4] = float3(worldMin.x, worldMin.y, worldMax.z);
    corners[5] = float3(worldMax.x, worldMin.y, worldMax.z);
    corners[6] = float3(worldMin.x, worldMax.y, worldMax.z);
    corners[7] = float3(worldMax.x, worldMax.y, worldMax.z);

    float2 ndcMin = 1e20.xx;
    float2 ndcMax = -1e20.xx;
    uint validCornerCount = 0;
    bool touchesNearPlane = false;
    nearDepth = 1.0;

    [unroll]
    for (uint i = 0; i < 8; i++)
    {
        float4 clip = mul(float4(corners[i], 1.0), viewProjMat);
        if (clip.w <= 1e-5 || clip.z < 0.0)
        {
            touchesNearPlane = true;
            continue;
        }

        float3 ndc = clip.xyz / clip.w;
        ndcMin = min(ndcMin, ndc.xy);
        ndcMax = max(ndcMax, ndc.xy);
        nearDepth = min(nearDepth, saturate(ndc.z));
        validCornerCount++;
    }

    if (touchesNearPlane || validCornerCount == 0)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
    }
    else
    {
        ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
        ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);
        float2 uv1 = ndcMin * float2(0.5, -0.5) + 0.5.xx;
        float2 uv2 = ndcMax * float2(0.5, -0.5) + 0.5.xx;
        minUv = saturate(min(uv1, uv2));
        maxUv = saturate(max(uv1, uv2));
    }

    float2 onePixel = float2(1.0 / view.ScreenWidth, 1.0 / view.ScreenHeight);
    if (view.HiZMipCount > 0)
        onePixel = max(view.HiZInvSize, 1e-6.xx);
    maxUv = min(1.0.xx, max(maxUv, minUv + onePixel));

    float pixelDiameterX = (maxUv.x - minUv.x) / onePixel.x;
    float pixelDiameterY = (maxUv.y - minUv.y) / onePixel.y;
    float pixelDiameter = max(1.0, max(pixelDiameterX, pixelDiameterY));

    float mipF = max(0.0, ceil(log2(pixelDiameter)));
    uint maxMip = view.HiZMipCount > 0 ? view.HiZMipCount - 1 : 0;
    mipLevel = (uint)clamp((int)mipF, 0, (int)maxMip);

    if (any(isnan(minUv)) || any(isnan(maxUv)) || isnan(nearDepth))
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0;
    }
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

float SampleHiZMax(
    float2 minUv,
    float2 maxUv,
    uint mipLevel,
    uint viewIndex,
    ClusterViewUniforms view)
{
    float2 fullSize = 1.0.xx / max(view.HiZInvSize, 1e-6.xx);
    
    // Scale UVs to exact pixel coordinates
    float2 pxMin = minUv * fullSize;
    float2 pxMax = maxUv * fullSize;

    // The divisor maps pixels to texels exactly via integer truncation
    float divisor = exp2((float)mipLevel);
    
    // Calculate the mip texture boundary using exact truncation matching GPU
    float2 mipSize = max(1.0.xx, floor(fullSize / divisor));
    float2 maxCoord = mipSize - 1.0.xx;

    // Divide by 2^L and clamp to ensure we safely load within bounds.
    // Since mipLevel = ceil(log2(pixelDiameter)), pxMax/D - pxMin/D is GUARANTEED <= 1.
    // The 4 loaded texels are guaranteed to perfectly cover the whole cluster UV.
    int2 p0 = int2(clamp(pxMin / divisor, 0.0.xx, maxCoord));
    int2 p1 = int2(clamp(float2(pxMax.x, pxMin.y) / divisor, 0.0.xx, maxCoord));
    int2 p2 = int2(clamp(float2(pxMin.x, pxMax.y) / divisor, 0.0.xx, maxCoord));
    int2 p3 = int2(clamp(pxMax / divisor, 0.0.xx, maxCoord));

    float d0 = HiZTexture.Load(int4(p0, viewIndex, mipLevel));
    float d1 = HiZTexture.Load(int4(p1, viewIndex, mipLevel));
    float d2 = HiZTexture.Load(int4(p2, viewIndex, mipLevel));
    float d3 = HiZTexture.Load(int4(p3, viewIndex, mipLevel));

    return max(max(d0, d1), max(d2, d3));
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

    hizDepth = SampleHiZMax(minUv, maxUv, mipLevel, viewIndex, view);
    return nearDepth > (hizDepth + epsilon);
}

bool ShouldUseSWRaster(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view)
{
    if (view.CullingEnabled == 0u || view.ForceHardwareRaster != 0u)
        return false;

    float width = (maxUv.x - minUv.x) * view.ScreenWidth;
    float height = (maxUv.y - minUv.y) * view.ScreenHeight;
    float area = width * height;
    return area < view.SoftwareRasterAreaThreshold;
}

#if CLUSTER_CULL_PHASE == 1
void AppendVisible(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    bool isSW,
    uint maxCandidates)
{
    uint totalIdx;
    DrawArgs.InterlockedAdd(12, 1, totalIdx);
    if (totalIdx >= maxCandidates)
        return;

    uint outIdx;
    if (isSW)
    {
        // SW clusters: write from front (CullDrawArgs.SWCount at byte 4)
        DrawArgs.InterlockedAdd(4, 1, outIdx);
        if (outIdx < maxCandidates)
            VisibleClusters[outIdx] = uint4(pageOffset, clusterID, instanceID, viewIndex);
    }
    else
    {
        // HW clusters: write from back (CullDrawArgs.HWCount at byte 8)
        DrawArgs.InterlockedAdd(8, 1, outIdx);
        uint hwIdx = maxCandidates - 1 - outIdx;
        if (outIdx < maxCandidates)
            VisibleClusters[hwIdx] = uint4(pageOffset, clusterID, instanceID, viewIndex);
    }
}

void AppendCandidate(
    uint pageOffset,
    uint clusterID,
    uint instanceID,
    uint viewIndex,
    uint maxCandidates)
{
    uint outIdx;
    InterlockedAdd(NextCandidateCount[0], 1, outIdx);
    if (outIdx >= maxCandidates)
        return;

    uint groups = (outIdx + 64) / 64;
    uint unused;
    NextCandidateArgs.InterlockedMax(0, groups, unused);
    NextCandidates[outIdx] = uint4(pageOffset, clusterID, instanceID, viewIndex);
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
    if (threadId >= count)
        return false;

    uint4 candidate = CandidateClusters[threadId];
    pageOffset = candidate.x;
    clusterID = candidate.y;
    instanceID = candidate.z;
    viewIndex = candidate.w;
    view = Views[viewIndex];
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

    if (view.CullingEnabled == 0u)
        return true;

    float3 worldCenter = TransformInstanceLocalPosition(cluster.LODCenter, t);
    float maxScale = abs(t.Scale) * max(max(abs(t.Stretch.x), abs(t.Stretch.y)), abs(t.Stretch.z));
    float worldRadius = cluster.LODRadius * maxScale;
    float clusterDist = max(0.0, distance(worldCenter, view.CameraPos) - worldRadius);
    float lodError = DecodeClusterLODError(cluster);
    return lodError * view.LodScale <= view.LodThreshold * clusterDist;
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
    CullDrawArgs phase1 = LoadCullDrawArgs(DrawArgs, 0u);
    uint phase1SWCount = phase1.SWCount;
    uint phase1HWCount = phase1.HWCount;
    uint phase1Count = phase1SWCount + phase1HWCount;

    uint totalIdx;
    PhaseDrawArgs.InterlockedAdd(12, 1, totalIdx);
    if (phase1Count + totalIdx >= maxCandidates)
        return;

    if (isSW)
    {
        // Phase2 SW: write after Phase1 SW (CullDrawArgs.SWCount at byte 4)
        PhaseDrawArgs.InterlockedAdd(4, 1, localIdx);
        uint globalIdx = phase1SWCount + localIdx;
        if (globalIdx < maxCandidates)
            VisibleClusters[globalIdx] = uint4(pageOffset, clusterID, instanceID, viewIndex);
    }
    else
    {
        // Phase2 HW: write before Phase1 HW (CullDrawArgs.HWCount at byte 8)
        PhaseDrawArgs.InterlockedAdd(8, 1, localIdx);
        uint hwIdx = maxCandidates - 1 - phase1HWCount - localIdx;
        if (localIdx < maxCandidates)
            VisibleClusters[hwIdx] = uint4(pageOffset, clusterID, instanceID, viewIndex);
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
