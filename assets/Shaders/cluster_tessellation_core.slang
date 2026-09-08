#include "cluster_bin_common.slang"
#ifndef CLUSTER_TESSELLATION_CORE_SLANG
#define CLUSTER_TESSELLATION_CORE_SLANG

#include "cluster_structures.slang"
#include "cluster_tessellation_types.slang"
#include "cluster_view_access.slang"
#include "resource_effects.slang"
#include "vertex_consumer_cache.slang"

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

static const uint TESS_PORTABLE_DISPATCH_DIMENSION = 65535u;
static const uint TESS_DICE_THREADS = 32u;
static const uint TESS_DICE_MAX_VERTICES = 45u;

uint TessellationGroupIndex(uint3 groupId)
{
    return groupId.x + groupId.y * TESS_PORTABLE_DISPATCH_DIMENSION;
}

void LoadTessellationTriangle<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint visibleClusterIndex,
    uint originalTriangle,
    out bool valid,
    out Triangle triangle)
{
    triangle = {};
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
    uint vertexCount = packedCounts & 0xFFu;
    uint triangleCount = (packedCounts >> 8u) & 0xFFu;
    valid = originalTriangle < triangleCount;
    if (!valid)
        return;

    uint indexOffset = PageHeap.Load(request.PageOffset + 24u);
    SurfaceVertex vertices[3];
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
        vertices[corner] = EvaluateSurfaceVertexWave(
            evaluator,
            context,
            visibleClusterIndex);
    }
    triangle = MakeTriangle(vertices[0], vertices[1], vertices[2]);
}

float2 TessellationScreenPosition(float4 clip, ClusterViewUniforms view)
{
    if (clip.w <= 1e-5) return 1e20.xx;
    return (clip.xy / clip.w * float2(0.5, -0.5) + 0.5) * float2(view.ScreenWidth, view.ScreenHeight);
}
uint TessellationRequestedEdgeFactor(float edgePixels)
{
    float target = max(TessellationUniformData.TargetEdgePixels, 1.0);
    float requestedFloat = ceil(max(edgePixels, 0.0) / target);
    uint requested = requestedFloat >= 65536.0
        ? 65536u
        : max(1u, uint(requestedFloat));
    return TessellationNextPowerOfTwo(requested);
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

void EvaluateTessellationPatch<TDomain : IDomainEvaluate>(
    uniform TDomain domain,
    Triangle triangle,
    ClusterViewUniforms view,
    float3 bary0,
    float3 bary1,
    float3 bary2,
    out uint3 edgeFactors,
    out uint splitMask,
    out bool splitInterior)
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
    edgeFactors = uint3(
        TessellationRequestedEdgeFactor(length(screen1 - screen0)),
        TessellationRequestedEdgeFactor(length(screen2 - screen1)),
        TessellationRequestedEdgeFactor(length(screen0 - screen2)));

    float3 scales = ClusterProjectionScales(view);
    uint2 size = uint2(view.ScreenWidth, view.ScreenHeight);
    float3 edgeErrorPixels = float3(
        ClusterProjectedError(min(clip0.w, clip1.w), max(abs(clip0.xy), abs(clip1.xy)), error.Edge.x, scales, size),
        ClusterProjectedError(min(clip1.w, clip2.w), max(abs(clip1.xy), abs(clip2.xy)), error.Edge.y, scales, size),
        ClusterProjectedError(min(clip2.w, clip0.w), max(abs(clip2.xy), abs(clip0.xy)), error.Edge.z, scales, size));
    float interiorErrorPixels = ClusterProjectedError(min(clip0.w, min(clip1.w, clip2.w)),
        max(abs(clip0.xy), max(abs(clip1.xy), abs(clip2.xy))), error.Interior, scales, size);
    // A final patch may still be diced into smaller triangles. If both the actual
    // surface and every diced vertex stay within E of the patch's corner triangle,
    // the piecewise diced approximation is within at most 2E. Use half the requested
    // pixel tolerance here so the final approximation remains conservative.
    float errorTarget = max(TessellationUniformData.TargetEdgePixels, 1.0) * 0.5;
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

bool IsTessellationDomainVisible<TDomain : IDomainEvaluate>(
    uniform TDomain domain,
    Triangle triangle,
    uint viewIndex)
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

    uint binnedIndex = bin.ClusterOffset + localBatch;
    uint batch = TessellationBinnedClusterIndexBuffer[binnedIndex];
    uint visibleClusterIndex = RasterBatchVisibleIndex(batch);
    DrawRequest request = TessellationVisibleClusters[visibleClusterIndex];
    uint viewIndex = DrawRequestViewIndex(request);

    uint rangeStart, rangeEnd;
    DecodeRasterBatchRange(PageHeap, request, batch, rangeStart, rangeEnd);
    uint triangleCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0u;
    bool laneValid = groupThreadIndex < triangleCount;
    uint originalTriangle = rangeStart + groupThreadIndex;
    bool triangleValid = false;
    if (laneValid)
    {
        Triangle triangle;
        LoadTessellationTriangle(
            evaluator,
            visibleClusterIndex,
            originalTriangle,
            triangleValid,
            triangle);
    }

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

    uint4 active = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(active);
    uint waveCount = WaveMaskBitCount(active);
    uint baseIndex = 0u;
    if (lane == leader)
    {
        InterlockedAdd(TessellationLevelCounts[0], waveCount, baseIndex);
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

bool IsTessellationPatchVisible<TDomain : IDomainEvaluate>(
    uniform TDomain domain,
    Triangle triangle,
    TessellationPatch patch,
    bool rootPatch)
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
    uint4 active = WaveActiveBallot(true);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(active);
    uint waveCount = WaveMaskBitCount(active);
    uint baseIndex = 0u;
    if (lane == leader)
        TessellationCounters.InterlockedAdd(TESS_COUNTER_FINAL_COUNT, waveCount, baseIndex);
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

bool PublishTessellationChildren(
    TessellationPatch children[4],
    uint childCount)
{
    uint nextLevel = TessellationUniformData.SplitLevel + 1u;
    uint laneOffset = WavePrefixSum(childCount);
    uint waveCount = WaveActiveSum(childCount);
    uint baseIndex = 0u;
    if (WaveIsFirstLane() && waveCount != 0u)
        InterlockedAdd(TessellationLevelCounts[nextLevel], waveCount, baseIndex);
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
    // Overflow is already recorded above; the capacity policy drops only the
    // non-admitted suffix.
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
        uint vrb, triangleCount, materials, ranges;
        ReadClusterBinData(PageHeap, source, vrb, triangleCount, materials, ranges);
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

void ProcessTessellationPatch<TVE : IVertexEvaluate, TDomain : IDomainEvaluate>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TessellationPatch patch,
    bool rootPatch)
{
    bool valid;
    Triangle triangle;
    LoadTessellationTriangle(
        evaluator,
        patch.VisibleClusterIndex,
        TessellationPatchTriangle(patch),
        valid,
        triangle);
    if (!valid)
    {
        RecordTessellationProcessed();
        return;
    }

    if (!IsTessellationPatchVisible(domain, triangle, patch, rootPatch))
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
    EvaluateTessellationPatch(
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

void SplitTessellationPatches<TVE : IVertexEvaluate, TDomain : IDomainEvaluate>(
    uniform TVE evaluator,
    uniform TDomain domain,
    uint3 groupId,
    uint groupThreadIndex)
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
    ProcessTessellationPatch(
        evaluator,
        domain,
        patch,
        level == 0u);
}

void DiceTessellationPatches<
    TVE : IVertexEvaluate,
    TDomain : IDomainEvaluate,
    TPixel : IPixelEvaluate,
    TOutput : IClusterRasterOutput>(
    uniform TVE evaluator,
    uniform TDomain domain,
    uniform TPixel pixel,
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
    Triangle triangle = {};
    ClusterViewUniforms view = {};
    uint viewIndex = 0u;
    if (valid)
    {
        patch = DecodeTessellationFinalPatch(
            TessellationRasterPatches[GroupedPatchIndices[patchIndex]]);
        LoadTessellationTriangle(evaluator, patch.VisibleClusterIndex,
            TessellationPatchTriangle(patch), valid, triangle);
        viewIndex = DrawRequestViewIndex(TessellationVisibleClusters[patch.VisibleClusterIndex]);
        view = LoadClusterView(TessellationViews, TessellationPolicyViews, viewIndex);
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
            if (bank == 0u) vertex0 = position;
            else if (bank == 1u) vertex1 = position;
            else vertex2 = position;
        }
    }

    int4 scissor = int4(0, 0, int(view.ScreenWidth), int(view.ScreenHeight));
    uint visibility = EncodeVisBuffer(patch.VisibleClusterIndex, TessellationPatchTriangle(patch));
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
        RasterizeClusterTriangleWave(pixel, rasterOutput, clipVerts, scissor, visibility,
            viewIndex, view.TargetPolicy, view.TargetViewIndex, active, view.ScreenWidth, view.ScreenHeight);
    }
}

#endif
