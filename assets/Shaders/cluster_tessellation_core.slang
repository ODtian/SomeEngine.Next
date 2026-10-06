#include "cluster_bin_common.slang"
#ifndef CLUSTER_TESSELLATION_CORE_SLANG
#define CLUSTER_TESSELLATION_CORE_SLANG

#include "cluster_structures.slang"
#include "cluster_tessellation_types.slang"
#include "cluster_view_access.slang"
#include "resource_effects.slang"
#include "vertex_consumer_cache.slang"
#include "cluster_work_status.slang"

ConstantBuffer<TessellationUniforms> TessellationUniformData;
StructuredBuffer<ClusterViewUniforms> TessellationViews;
StructuredBuffer<ClusterViewUniforms> TessellationPolicyViews;
StructuredBuffer<DrawRequest> TessellationVisibleClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> TessellationBinnedClusterIndexBuffer;
StructuredBuffer<RasterBinEntry> TessellationRasterBinMeta;
StructuredBuffer<uint> TessellationRasterSlots;
Texture2DArray<float> TessellationHiZTexture;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TessellationCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> TessellationWorkPatches0;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> TessellationWorkPatches1;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TessellationLevelCounts;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TessellationNextLevelArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<TessellationFinalPatch> TessellationFinalPatches;
StructuredBuffer<TessellationFinalPatch> TessellationRasterPatches;
StructuredBuffer<uint> GroupedPatchIndices;
StructuredBuffer<uint> TessellationBinCounts;
StructuredBuffer<uint> TessellationBinOffsets;

#include "cluster_hiz_shared.slang"
#include "cluster_raster_shared.slang"
#include "cluster_raster_target_filter.slang"

static const uint TESS_PORTABLE_DISPATCH_DIMENSION = 65535u;
static const uint TESS_DICE_THREADS = 32u;
static const uint TESS_DICE_MAX_VERTICES = 45u;

uint TessellationGroupIndex(uint3 groupId)
{
    return groupId.x + groupId.y * TESS_PORTABLE_DISPATCH_DIMENSION;
}

bool HasTessellationTriangle(
    uint visibleClusterIndex,
    uint originalTriangle)
{
    DrawRequest request = TessellationVisibleClusters[visibleClusterIndex];
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint triangleStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        request.PageOffset,
        DrawRequestClusterID(request),
        vertexBlobAddress,
        vertexBlobSize,
        triangleStart,
        packedCounts);
    uint triangleCount = (packedCounts >> 8u) & 0xFFu;
    return originalTriangle < triangleCount;
}

TDomain.TriangleData LoadTessellationTriangle<TVE, TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    uint visibleClusterIndex,
    uint originalTriangle)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    DrawRequest request = TessellationVisibleClusters[visibleClusterIndex];
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint triangleStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        request.PageOffset,
        DrawRequestClusterID(request),
        vertexBlobAddress,
        vertexBlobSize,
        triangleStart,
        packedCounts);

    uint indexOffset = PageHeap.Load(request.PageOffset + 24u);
    TVE.Vertex vertices[3];
    [unroll]
    for (uint corner = 0u; corner < 3u; corner++)
    {
        uint localVertex = FetchVertexIndex(
            PageHeap,
            request.PageOffset,
            indexOffset,
            triangleStart,
            originalTriangle,
            corner);
        VertexEvalContext context;
        context.pageHeap = PageHeap;
        context.vertexBlobAddress = vertexBlobAddress;
        context.vertexBlobSize = vertexBlobSize;
        context.localVertexIndex = localVertex;
        context.instanceID = request.InstanceID;
        context.transform = GetCurrentInstanceTransform(request.InstanceID);
        context.IsPreviousFrame = 0u;
        vertices[corner] = EvaluateVertexWave(
            evaluator,
            context,
            visibleClusterIndex);
    }
    return domain.makeTriangle(vertices[0], vertices[1], vertices[2]);
}

DomainEvaluate.TriangleData LoadTessellationTriangleWithVisibility(
    uniform VertexEvaluate evaluator,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uint visibleClusterIndex,
    uint originalTriangle,
    out VisibilityEvaluate.Varying varyings[3])
{
    DrawRequest request = TessellationVisibleClusters[visibleClusterIndex];
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint triangleStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        request.PageOffset,
        DrawRequestClusterID(request),
        vertexBlobAddress,
        vertexBlobSize,
        triangleStart,
        packedCounts);

    uint indexOffset = PageHeap.Load(request.PageOffset + 24u);
    uint viewIndex = DrawRequestViewIndex(request);
    VertexEvaluate.Vertex vertices[3];
    [unroll]
    for (uint corner = 0u; corner < 3u; corner++)
    {
        uint localVertex = FetchVertexIndex(
            PageHeap,
            request.PageOffset,
            indexOffset,
            triangleStart,
            originalTriangle,
            corner);
        VertexEvalContext context;
        context.pageHeap = PageHeap;
        context.vertexBlobAddress = vertexBlobAddress;
        context.vertexBlobSize = vertexBlobSize;
        context.localVertexIndex = localVertex;
        context.instanceID = request.InstanceID;
        context.transform = GetCurrentInstanceTransform(request.InstanceID);
        context.IsPreviousFrame = 0u;
        vertices[corner] = EvaluateVertexWave(
            evaluator,
            context,
            visibleClusterIndex);
        VisibilityVertexInput visibilityInput;
        visibilityInput.VisibleClusterIndex = visibleClusterIndex;
        visibilityInput.InstanceID = request.InstanceID;
        visibilityInput.ViewIndex = viewIndex;
        visibilityInput.LocalVertexIndex = localVertex;
        varyings[corner] = visibility.makeVarying(vertices[corner], visibilityInput);
    }
    return domain.makeTriangle(vertices[0], vertices[1], vertices[2]);
}

float2 TessellationScreenPosition(float4 clip, ClusterViewUniforms view)
{
    if (clip.w <= 1e-5) return 1e20.xx;
    return (clip.xy / clip.w * float2(0.5, -0.5) + 0.5) * float2(view.ScreenWidth, view.ScreenHeight);
}
uint TessellationRequestedEdgeFactor(float edgePixels, float targetPixels)
{
    float requestedFloat = ceil(max(edgePixels, 0.0) / targetPixels);
    uint requested = requestedFloat >= 65536.0
        ? 65536u
        : max(1u, uint(requestedFloat));
    return TessellationNextPowerOfTwo(requested);
}

float CalculateTessellationEdgePixels(float3 first, float3 second, ClusterViewUniforms view)
{
    float3 firstView = mul(float4(first, 1.0), view.View).xyz;
    float3 secondView = mul(float4(second, 1.0), view.View).xyz;
    float edgeDepth = ClusterProjectionScales(view).z <= 1e-8
        ? 1.0 : max(min(-firstView.z, -secondView.z), view.NearPlane);
    return length(firstView - secondView) * CalculateClusterViewPixelScale(view) *
        view.GeometryLodScale / edgeDepth;
}

uint TessellationMaximumSplitDepth()
{
    uint factor = max(TessellationUniformData.MaxFactor, TESS_PATTERN_MAX_FACTOR);
    uint depth = 0u;
    while (factor > TESS_PATTERN_MAX_FACTOR)
    {
        factor >>= 1u;
        depth++;
    }
    return depth;
}

void EvaluateTessellationPatch<TVertex, TDomain>(
    uniform TDomain domain,
    TDomain.TriangleData triangle,
    ClusterViewUniforms view,
    float3 bary0,
    float3 bary1,
    float3 bary2,
    out uint3 edgeFactors,
    out uint splitMask,
    out bool splitInterior)
    where TDomain : IDomainEvaluate<TVertex>
{
    // Evaluate each corner exactly once. The domain receives those positions again when
    // producing error bounds so texture/analytic implementations do not resample them.
    float3 position0 = domain.position(triangle, bary0);
    float3 position1 = domain.position(triangle, bary1);
    float3 position2 = domain.position(triangle, bary2);
    DomainErrorBounds error = domain.errorBounds(
        triangle,
        bary0,
        bary1,
        bary2,
        position0,
        position1,
        position2);

    float4 clip0 = mul(float4(position0, 1.0), view.ViewProj);
    float4 clip1 = mul(float4(position1, 1.0), view.ViewProj);
    float4 clip2 = mul(float4(position2, 1.0), view.ViewProj);
    float2 screen0 = TessellationScreenPosition(clip0, view);
    float2 screen1 = TessellationScreenPosition(clip1, view);
    float2 screen2 = TessellationScreenPosition(clip2, view);
    float targetPixels = view.TessellationEdgePixels > 0.0
        ? view.TessellationEdgePixels : TessellationUniformData.TargetEdgePixels;
    float3 edgePixels = view.TargetPolicy != 0u
        ? float3(CalculateTessellationEdgePixels(position0, position1, view),
            CalculateTessellationEdgePixels(position1, position2, view),
            CalculateTessellationEdgePixels(position2, position0, view))
        : float3(length(screen1 - screen0), length(screen2 - screen1), length(screen0 - screen2));
    edgeFactors = uint3(
        TessellationRequestedEdgeFactor(edgePixels.x, targetPixels),
        TessellationRequestedEdgeFactor(edgePixels.y, targetPixels),
        TessellationRequestedEdgeFactor(edgePixels.z, targetPixels));

    float3 scales = ClusterProjectionScales(view);
    uint2 size = uint2(view.ScreenWidth, view.ScreenHeight);
    float3 edgeErrorPixels = float3(
        ClusterProjectedError(min(clip0.w, clip1.w), max(abs(clip0.xy), abs(clip1.xy)), error.Edge.x, scales, size),
        ClusterProjectedError(min(clip1.w, clip2.w), max(abs(clip1.xy), abs(clip2.xy)), error.Edge.y, scales, size),
        ClusterProjectedError(min(clip2.w, clip0.w), max(abs(clip2.xy), abs(clip0.xy)), error.Edge.z, scales, size));
    float interiorErrorPixels = ClusterProjectedError(min(clip0.w, min(clip1.w, clip2.w)),
        max(abs(clip0.xy), max(abs(clip1.xy), abs(clip2.xy))), error.Interior, scales, size);
    if (view.TargetPolicy != 0u)
    {
        edgeErrorPixels *= view.GeometryLodScale;
        interiorErrorPixels *= view.GeometryLodScale;
    }
    // A final patch may still be diced into smaller triangles. If both the actual
    // surface and every diced vertex stay within E of the patch's corner triangle,
    // the piecewise diced approximation is within at most 2E. Use half the requested
    // pixel tolerance here so the final approximation remains conservative.
    float errorTarget = targetPixels * 0.5;
    bool3 splitEdges =
        (edgeFactors > TESS_PATTERN_MAX_FACTOR.xxx) ||
        (edgeErrorPixels > errorTarget.xxx);
    splitMask =
        (splitEdges.x ? 1u : 0u) |
        (splitEdges.y ? 2u : 0u) |
        (splitEdges.z ? 4u : 0u);
    splitInterior = interiorErrorPixels > errorTarget;
}

bool IsTessellationBoundsVisible(
    float3 minimum,
    float3 maximum,
    uint viewIndex)
{
    ClusterViewUniforms view = LoadClusterView(
        TessellationViews,
        TessellationPolicyViews,
        viewIndex);
    bool usePrevious = TessellationUniformData.UsePreviousHiZ != 0u;
    if (WorldAabbOutsideFrustum(minimum, maximum, usePrevious, view))
        return false;
    if (view.TargetPolicy != 0u)
    {
        // Policy view IDs are not primary-camera texture layers. Target history
        // is qualified per requested page by the same resolver as traversal.
        return view.CullingEnabled == 0u || !ClusterTargetPolicyWorldBoundsOccluded(
            minimum, maximum, usePrevious, view);
    }
    float2 minUv;
    float2 maxUv;
    if (IsWorldAabbOccludedByHiZ(
            TessellationHiZTexture,
            minimum,
            maximum,
            usePrevious,
            viewIndex,
            view,
            minUv,
            maxUv))
        return false;
    return true;
}

bool IsTessellationDomainVisible<TVertex, TDomain>(
    uniform TDomain domain,
    TDomain.TriangleData triangle,
    uint viewIndex)
    where TDomain : IDomainEvaluate<TVertex>
{
    float3 minimum;
    float3 maximum;
    domain.bounds(triangle, minimum, maximum);
    return IsTessellationBoundsVisible(minimum, maximum, viewIndex);
}

void SeedTessellationRasterBin<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint3 groupId,
    uint groupThreadIndex)
{
    uint localBatch = TessellationGroupIndex(groupId);
    RasterBinEntry bin = TessellationRasterBinMeta[TessellationUniformData.CurrentBin];
    uint batchCount = bin.BinSWCount + bin.BinHWCount;
    if (localBatch >= batchCount)
        return;

    uint binnedIndex = localBatch < bin.BinSWCount
        ? bin.ClusterOffset + localBatch : bin.HardwareOffset + localBatch - bin.BinSWCount;
    uint batch = TessellationBinnedClusterIndexBuffer[binnedIndex];
    uint visibleClusterIndex = RasterWorkVisibleIndex(batch);
    DrawRequest request = TessellationVisibleClusters[visibleClusterIndex];
    uint viewIndex = DrawRequestViewIndex(request);

    uint rangeStart, rangeEnd;
    DecodeRasterWorkRange(PageHeap, request, batch, rangeStart, rangeEnd);
    uint triangleCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0u;
    bool laneValid = groupThreadIndex < triangleCount;
    uint originalTriangle = rangeStart + groupThreadIndex;
    bool triangleValid = laneValid &&
        HasTessellationTriangle(visibleClusterIndex, originalTriangle);

    // Entry presence is the explicit runtime-domain opt-in. A factor of one must still
    // execute domain.position(); using factor>1 as the route decision silently skipped
    // constant/low-curvature displacement.
    bool domainBatch = WaveActiveAnyTrue(triangleValid);
    if (!domainBatch)
        return;

    if (groupThreadIndex == 0u)
        TessellationBinnedClusterIndexBuffer[binnedIndex] = batch | 0xFFC00000u;

    if (!triangleValid)
        return;

    TessellationPatch patch;
    patch.VisibleClusterIndex = visibleClusterIndex;
    patch.TriangleAndRasterBin = originalTriangle | (TessellationUniformData.CurrentBin << 8u);
    SetTessellationPatchBarycentrics(
        patch,
        float3(1.0, 0.0, 0.0),
        float3(0.0, 1.0, 0.0),
        float3(0.0, 0.0, 1.0));
    SetTessellationPatchState(patch, 0u, 1u.xxx);

    AddClusterWorkMetric(80u, 1u);
    uint4 active = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(active);
    uint waveCount = WaveMaskBitCount(active);
    uint baseIndex = 0u;
    if (lane == leader)
    {
        baseIndex = ReserveClusterWork(TessellationLevelCounts, waveCount,
            TessellationUniformData.MaxPatchItems, 32u);
        uint ignored;
        TessellationCounters.InterlockedAdd(
            TESS_COUNTER_ROOT_COUNT,
            waveCount,
            ignored);
    }
    baseIndex = WaveReadLaneAt(baseIndex, leader);
    uint outputIndex = baseIndex + WaveMaskPrefix(active, lane);
    bool admitted = outputIndex < TessellationUniformData.MaxPatchItems;
    if (admitted)
        TessellationWorkPatches0[outputIndex] = EncodeTessellationWorkPatch(patch);
    uint admittedEnd = min(baseIndex + waveCount, TessellationUniformData.MaxPatchItems);
    if (lane == leader)
    {
        if (baseIndex + waveCount > TessellationUniformData.MaxPatchItems)
        {
            uint ignored;
            TessellationCounters.InterlockedAdd(
                TESS_COUNTER_OVERFLOW,
                waveCount - (admittedEnd > baseIndex ? admittedEnd - baseIndex : 0u),
                ignored);
        }
        uint ignored;
        TessellationNextLevelArgs.InterlockedMax(
            0u,
            (admittedEnd + TESS_SPLIT_THREADS - 1u) / TESS_SPLIT_THREADS,
            ignored);
        TessellationNextLevelArgs.Store(4u, 1u);
        TessellationNextLevelArgs.Store(8u, 1u);
    }
}

bool IsTessellationPatchVisible<TVertex, TDomain>(
    uniform TDomain domain,
    TDomain.TriangleData triangle,
    TessellationPatch patch,
    bool rootPatch)
    where TDomain : IDomainEvaluate<TVertex>
{
    uint viewIndex = DrawRequestViewIndex(
        TessellationVisibleClusters[patch.VisibleClusterIndex]);
    float3 bary0 = DecodeTessellationPatchBarycentric(patch, 0u);
    float3 bary1 = DecodeTessellationPatchBarycentric(patch, 1u);
    float3 bary2 = DecodeTessellationPatchBarycentric(patch, 2u);
    float3 minimum;
    float3 maximum;
    if (rootPatch)
        domain.bounds(triangle, minimum, maximum);
    else
        domain.patchBounds(triangle, bary0, bary1, bary2, minimum, maximum);
    return IsTessellationBoundsVisible(minimum, maximum, viewIndex);
}

TessellationPatch MakeTessellationChild(
    TessellationPatch parent,
    float3 bary0,
    float3 bary1,
    float3 bary2)
{
    TessellationPatch child = parent;
    SetTessellationPatchBarycentrics(child, bary0, bary1, bary2);
    SetTessellationPatchState(
        child,
        min(TessellationPatchDepth(parent) + 1u, 255u),
        1u.xxx);
    return child;
}

uint BuildTessellationChildren(
    TessellationPatch parent,
    uint splitMask,
    bool splitInterior,
    out TessellationPatch children[4])
{
    float3 a = DecodeTessellationPatchBarycentric(parent, 0u);
    float3 b = DecodeTessellationPatchBarycentric(parent, 1u);
    float3 c = DecodeTessellationPatchBarycentric(parent, 2u);
    if (splitMask == 0u)
    {
        if (!splitInterior)
            return 0u;

        // Interior refinement must not invent new points on a shared outer edge.
        // A centroid split preserves all three boundary segments exactly.
        float3 center = (a + b + c) / 3.0;
        children[0] = MakeTessellationChild(parent, a, b, center);
        children[1] = MakeTessellationChild(parent, b, c, center);
        children[2] = MakeTessellationChild(parent, c, a, center);
        return 3u;
    }

    float3 ab = (a + b) * 0.5;
    float3 bc = (b + c) * 0.5;
    float3 ca = (c + a) * 0.5;
    if (splitMask == 1u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, c);
        children[1] = MakeTessellationChild(parent, ab, b, c);
        return 2u;
    }
    if (splitMask == 2u)
    {
        children[0] = MakeTessellationChild(parent, b, bc, a);
        children[1] = MakeTessellationChild(parent, bc, c, a);
        return 2u;
    }
    if (splitMask == 4u)
    {
        children[0] = MakeTessellationChild(parent, c, ca, b);
        children[1] = MakeTessellationChild(parent, ca, a, b);
        return 2u;
    }
    if (splitMask == 3u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, c);
        children[1] = MakeTessellationChild(parent, ab, b, bc);
        children[2] = MakeTessellationChild(parent, ab, bc, c);
        return 3u;
    }
    if (splitMask == 5u)
    {
        children[0] = MakeTessellationChild(parent, a, ab, ca);
        children[1] = MakeTessellationChild(parent, ab, b, c);
        children[2] = MakeTessellationChild(parent, ab, c, ca);
        return 3u;
    }
    if (splitMask == 6u)
    {
        children[0] = MakeTessellationChild(parent, a, b, bc);
        children[1] = MakeTessellationChild(parent, a, bc, ca);
        children[2] = MakeTessellationChild(parent, bc, c, ca);
        return 3u;
    }

    children[0] = MakeTessellationChild(parent, a, ab, ca);
    children[1] = MakeTessellationChild(parent, ab, b, bc);
    children[2] = MakeTessellationChild(parent, ca, bc, c);
    children[3] = MakeTessellationChild(parent, ab, bc, ca);
    return 4u;
}

void AddTessellationCounter(uint byteOffset, uint value)
{
    uint waveValue = WaveActiveSum(value);
    if (WaveIsFirstLane() && waveValue != 0u)
    {
        uint ignored;
        TessellationCounters.InterlockedAdd(byteOffset, waveValue, ignored);
    }
}

bool AppendFinalTessellationPatch(TessellationPatch patch)
{
    AddClusterWorkMetric(88u, 1u);
    uint4 active = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(active);
    uint waveCount = WaveMaskBitCount(active);
    uint baseIndex = 0u;
    if (lane == leader)
        baseIndex = ReserveClusterWork(TessellationCounters, TESS_COUNTER_FINAL_COUNT,
            waveCount, TessellationUniformData.MaxPatchItems, 32u);
    baseIndex = WaveReadLaneAt(baseIndex, leader);
    uint index = baseIndex + WaveMaskPrefix(active, lane);
    bool admitted = index < TessellationUniformData.MaxPatchItems;
    if (admitted)
        TessellationFinalPatches[index] = EncodeTessellationFinalPatch(patch);
    if (lane == leader && baseIndex + waveCount > TessellationUniformData.MaxPatchItems)
    {
        uint ignored;
        TessellationCounters.InterlockedAdd(
            TESS_COUNTER_OVERFLOW,
            waveCount - (baseIndex < TessellationUniformData.MaxPatchItems
                ? TessellationUniformData.MaxPatchItems - baseIndex : 0u),
            ignored);
    }
    return admitted;
}

void RecordTessellationProcessed()
{
    AddTessellationCounter(TESS_COUNTER_PROCESSED, 1u);
}

uint ReserveTessellationLevel(uint level, uint amount)
{
    uint current = TessellationLevelCounts[level];
    for (;;)
    {
        uint accepted = min(amount, TessellationUniformData.MaxPatchItems - current);
        uint observed;
        InterlockedCompareExchange(TessellationLevelCounts[level], current, current + accepted, observed);
        if (observed == current)
        {
            if (accepted != amount) RejectClusterWork(32u);
            return current;
        }
        current = observed;
    }
}

bool PublishTessellationChildren(
    TessellationPatch children[4],
    uint childCount)
{
    uint nextLevel = TessellationUniformData.SplitLevel + 1u;
    uint laneOffset = WavePrefixSum(childCount);
    uint waveCount = WaveActiveSum(childCount);
    uint baseIndex = 0u;
    if (WaveIsFirstLane() && waveCount != 0u)
        baseIndex = ReserveTessellationLevel(nextLevel, waveCount);
    baseIndex = WaveReadLaneFirst(baseIndex);

    uint admittedEnd = min(baseIndex + waveCount, TessellationUniformData.MaxPatchItems);
    uint admittedCount = admittedEnd > baseIndex ? admittedEnd - baseIndex : 0u;
    [unroll]
    for (uint child = 0u; child < 4u; child++)
    {
        uint waveOffset = laneOffset + child;
        if (child < childCount && waveOffset < admittedCount)
        {
            uint outputIndex = baseIndex + waveOffset;
            if ((nextLevel & 1u) == 0u)
                TessellationWorkPatches0[outputIndex] = EncodeTessellationWorkPatch(children[child]);
            else
                TessellationWorkPatches1[outputIndex] = EncodeTessellationWorkPatch(children[child]);
        }
    }

    if (WaveIsFirstLane())
    {
        if (waveCount > admittedCount)
        {
            uint ignored;
            TessellationCounters.InterlockedAdd(
                TESS_COUNTER_OVERFLOW,
                waveCount - admittedCount,
                ignored);
        }
        uint ignored;
        TessellationNextLevelArgs.InterlockedMax(
            0u,
            (admittedEnd + TESS_SPLIT_THREADS - 1u) / TESS_SPLIT_THREADS,
            ignored);
        TessellationNextLevelArgs.Store(4u, 1u);
        TessellationNextLevelArgs.Store(8u, 1u);
    }
    // A capacity overflow may admit only a prefix of the wave.  Once any child
    // has been published the parent must not also be finalized, otherwise the
    // accepted children overlap the parent and rasterize the same surface twice.
    // The shared frame status rejects this incomplete frame. Its depth must not
    // become sample-valid; clamping here protects memory, not rendering quality.
    return admittedCount != 0u;
}

void FinalizeTessellationPatch(
    TessellationPatch patch,
    uint3 edgeFactors,
    bool qualityLimited)
{
    if (TessellationUniformData.CurrentBin == 0xFFFFFFFFu)
    {
        // Compatible split entries can share a work queue even when their pixel
        // stages differ. Recover the final raster bin from existing instance/mesh
        // material data, keeping the 16-byte intermediate work representation.
        DrawRequest source = TessellationVisibleClusters[patch.VisibleClusterIndex];
        uint4 vertReuseBatchWords;
        uint triangleCount, materials, ranges;
        ReadClusterBinData(PageHeap, source, vertReuseBatchWords, triangleCount, materials, ranges);
        uint slotOffset = LoadClusterMaterialSlotOffset(InstanceData,
            LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), source.InstanceID);
        uint slotCount, slotStride, binCount, binStride;
        TessellationRasterSlots.GetDimensions(slotCount, slotStride);
        TessellationRasterBinMeta.GetDimensions(binCount, binStride);
        uint triangle = TessellationPatchTriangle(patch);
        uint bin = SlotBin(TessellationRasterSlots, slotOffset, LocalMaterial(materials, ranges, triangle), slotCount, binCount);
        patch.TriangleAndRasterBin = triangle | (bin << 8u);
    }
    uint maximumLocalFactor = max(
        1u,
        min(TessellationUniformData.MaxFactor, TESS_PATTERN_MAX_FACTOR));
    edgeFactors = clamp(edgeFactors, 1u.xxx, maximumLocalFactor.xxx);
    SetTessellationPatchState(
        patch,
        TessellationPatchDepth(patch),
        edgeFactors);
    AppendFinalTessellationPatch(patch);
    if (qualityLimited)
        AddTessellationCounter(TESS_COUNTER_QUALITY_LIMIT, 1u);
    RecordTessellationProcessed();
}

void ProcessTessellationPatch<TVE, TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TessellationPatch patch,
    bool rootPatch)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    uint triangleIndex = TessellationPatchTriangle(patch);
    if (!HasTessellationTriangle(patch.VisibleClusterIndex, triangleIndex))
    {
        RecordTessellationProcessed();
        return;
    }
    TDomain.TriangleData triangle = LoadTessellationTriangle<TVE, TDomain>(
        evaluator,
        domain,
        patch.VisibleClusterIndex,
        triangleIndex);

    if (!IsTessellationPatchVisible<TVE.Vertex, TDomain>(
            domain, triangle, patch, rootPatch))
    {
        AddTessellationCounter(TESS_COUNTER_CULLED, 1u);
        RecordTessellationProcessed();
        return;
    }

    uint viewIndex = DrawRequestViewIndex(
        TessellationVisibleClusters[patch.VisibleClusterIndex]);
    float3 bary0 = DecodeTessellationPatchBarycentric(patch, 0u);
    float3 bary1 = DecodeTessellationPatchBarycentric(patch, 1u);
    float3 bary2 = DecodeTessellationPatchBarycentric(patch, 2u);
    uint3 edgeFactors;
    uint splitMask;
    bool splitInterior;
    EvaluateTessellationPatch<TVE.Vertex, TDomain>(
        domain,
        triangle,
        LoadClusterView(TessellationViews, TessellationPolicyViews, viewIndex),
        bary0,
        bary1,
        bary2,
        edgeFactors,
        splitMask,
        splitInterior);

    bool needsSplit = splitMask != 0u || splitInterior;
    uint depth = TessellationPatchDepth(patch);
    if (needsSplit &&
        depth < TessellationMaximumSplitDepth() &&
        TessellationUniformData.SplitLevel + 1u < TESS_SPLIT_LEVEL_COUNT)
    {
        TessellationPatch children[4];
        uint childCount = BuildTessellationChildren(
            patch,
            splitMask,
            splitInterior,
            children);
        if (childCount > 0u && PublishTessellationChildren(children, childCount))
        {
            RecordTessellationProcessed();
            return;
        }
    }

    FinalizeTessellationPatch(
        patch,
        edgeFactors,
        needsSplit);
}

void SplitTessellationPatches<TVE, TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    uint3 groupId,
    uint groupThreadIndex)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    uint level = TessellationUniformData.SplitLevel;
    uint index = TessellationGroupIndex(groupId) * TESS_SPLIT_THREADS + groupThreadIndex;
    if (index >= min(
            TessellationLevelCounts[level],
            TessellationUniformData.MaxPatchItems))
        return;

    TessellationPatch patch = DecodeTessellationWorkPatch(
        (level & 1u) == 0u ? TessellationWorkPatches0[index] : TessellationWorkPatches1[index],
        TessellationUniformData.CurrentBin, level);
    ProcessTessellationPatch<TVE, TDomain>(
        evaluator,
        domain,
        patch,
        level == 0u);
}

VisibilityEvaluate.Varying ReadTessellationDiceVarying(
    uniform VisibilityEvaluate visibility,
    uint index,
    uint lanesPerPatch,
    uint firstLane,
    uint registersPerLane,
    VisibilityEvaluate.Varying varying0,
    VisibilityEvaluate.Varying varying1,
    VisibilityEvaluate.Varying varying2)
{
    uint lane = firstLane + index % lanesPerPatch;
    VisibilityEvaluate.Varying result0 = visibility.readLane(varying0, lane);
    VisibilityEvaluate.Varying result1 = {};
    VisibilityEvaluate.Varying result2 = {};
    if (registersPerLane > 1u)
        result1 = visibility.readLane(varying1, lane);
    if (registersPerLane > 2u)
        result2 = visibility.readLane(varying2, lane);
    uint bank = index / lanesPerPatch;
    return bank == 0u ? result0 : bank == 1u ? result1 : result2;
}

void DiceTessellationPatches<
    TOutput : IClusterRasterOutput>(
    uniform VertexEvaluate evaluator,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uniform TOutput rasterOutput,
    uint3 groupId,
    uint groupThreadIndex)
{
    uint localGroup = TessellationGroupIndex(groupId);
    uint firstBin = TessellationUniformData.CurrentBin * TESS_PATTERN_MAX_FACTOR;
    uint factor = 1u;
    uint count = 0u;
    uint patchesPerGroup = 32u;
    for (; factor <= TESS_PATTERN_MAX_FACTOR; factor++)
    {
        patchesPerGroup = 32u / TessellationDicingLanes(factor);
        count = TessellationBinCounts[firstBin + factor - 1u];
        uint groups = (count + patchesPerGroup - 1u) / patchesPerGroup;
        if (localGroup < groups)
            break;
        localGroup -= groups;
    }
    if (factor > TESS_PATTERN_MAX_FACTOR)
        return;

    uint lanesPerPatch = TessellationDicingLanes(factor);
    uint patchInGroup = groupThreadIndex / lanesPerPatch;
    uint patchLane = groupThreadIndex % lanesPerPatch;
    uint localPatch = localGroup * patchesPerGroup + patchInGroup;
    uint patchIndex = TessellationBinOffsets[firstBin + factor - 1u] + localPatch;
    bool valid = localPatch < count && patchIndex < TessellationUniformData.MaxPatchItems;
    TessellationPatch patch = {};
    DomainEvaluate.TriangleData triangle;
    VisibilityEvaluate.Varying sourceVaryings[3];
    ClusterViewUniforms view = {};
    uint viewIndex = 0u;
    if (valid)
    {
        patch = DecodeTessellationFinalPatch(
            TessellationRasterPatches[GroupedPatchIndices[patchIndex]]);
        valid = HasTessellationTriangle(
            patch.VisibleClusterIndex,
            TessellationPatchTriangle(patch));
        if (valid)
        {
            triangle = LoadTessellationTriangleWithVisibility(
                evaluator,
                domain,
                visibility,
                patch.VisibleClusterIndex,
                TessellationPatchTriangle(patch),
                sourceVaryings);
            viewIndex = DrawRequestViewIndex(TessellationVisibleClusters[patch.VisibleClusterIndex]);
            view = LoadClusterView(TessellationViews, TessellationPolicyViews, viewIndex);
        }
    }

    float3 patchBary0 = DecodeTessellationPatchBarycentric(patch, 0u);
    float3 patchBary1 = DecodeTessellationPatchBarycentric(patch, 1u);
    float3 patchBary2 = DecodeTessellationPatchBarycentric(patch, 2u);
    uint3 edgeFactors = TessellationPatchEdgeFactors(patch);
    uint vertexCount = TessellationGridVertexCount(factor);
    uint registersPerLane = (vertexCount + lanesPerPatch - 1u) / lanesPerPatch;
    uint firstLane = WaveGetLaneIndex() - patchLane;
    float4 vertex0 = float4(0.0, 0.0, 0.0, 1.0);
    float4 vertex1 = vertex0;
    float4 vertex2 = vertex0;
    VisibilityEvaluate.Varying varying0 = {};
    VisibilityEvaluate.Varying varying1 = {};
    VisibilityEvaluate.Varying varying2 = {};
    if (valid)
    {
        [unroll]
        for (uint bank = 0u; bank < 3u; bank++)
        {
            uint vertexIndex = patchLane + bank * lanesPerPatch;
            if (vertexIndex >= vertexCount)
                break;
            float3 localBary = TessellationGridBarycentric(factor, edgeFactors, vertexIndex);
            float3 sourceBary = TransformPatchBarycentric(patchBary0, patchBary1, patchBary2, localBary);
            float4 position = mul(
                float4(domain.position(triangle, sourceBary), 1.0), view.ViewProj);
            VisibilityEvaluate.Varying generatedVarying = visibility.interpolateVertex(
                sourceVaryings[0],
                sourceVaryings[1],
                sourceVaryings[2],
                sourceBary);
            if (bank == 0u)
            {
                vertex0 = position;
                varying0 = generatedVarying;
            }
            else if (bank == 1u)
            {
                vertex1 = position;
                varying1 = generatedVarying;
            }
            else
            {
                vertex2 = position;
                varying2 = generatedVarying;
            }
        }
    }

    int4 scissor = rasterOutput.scissor(
        patch.VisibleClusterIndex,
        view.TargetPolicy,
        view.TargetViewIndex,
        view.ScreenWidth,
        view.ScreenHeight);
    uint triangleCount = TessellationGridTriangleCount(factor);
    for (uint baseTriangle = 0u; baseTriangle < triangleCount; baseTriangle += lanesPerPatch)
    {
        uint triangleIndex = baseTriangle + patchLane;
        bool active = valid && triangleIndex < triangleCount;
        uint3 vertices = active ? TessellationGridTriangleVertexIndices(factor, triangleIndex) : 0u.xxx;
        float4 clipVerts[3] = {
            ReadTessellationDiceVertex(vertices.x, lanesPerPatch, firstLane, registersPerLane, vertex0, vertex1, vertex2),
            ReadTessellationDiceVertex(vertices.y, lanesPerPatch, firstLane, registersPerLane, vertex0, vertex1, vertex2),
            ReadTessellationDiceVertex(vertices.z, lanesPerPatch, firstLane, registersPerLane, vertex0, vertex1, vertex2)
        };
        VisibilityEvaluate.Varying varyings[3] = {
            ReadTessellationDiceVarying(
                visibility, vertices.x, lanesPerPatch, firstLane, registersPerLane,
                varying0, varying1, varying2),
            ReadTessellationDiceVarying(
                visibility, vertices.y, lanesPerPatch, firstLane, registersPerLane,
                varying0, varying1, varying2),
            ReadTessellationDiceVarying(
                visibility, vertices.z, lanesPerPatch, firstLane, registersPerLane,
                varying0, varying1, varying2)
        };
        RasterizeClusterTriangleWave<
            VertexEvaluate.Vertex,
            VisibilityEvaluate,
            TOutput>(
            visibility,
            rasterOutput,
            clipVerts,
            varyings,
            scissor,
            patch.VisibleClusterIndex,
            TessellationPatchTriangle(patch),
            viewIndex,
            view.TargetPolicy,
            view.TargetViewIndex,
            active,
            view.ScreenWidth,
            view.ScreenHeight);
    }
}

#endif
