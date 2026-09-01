#ifndef CLUSTER_TESSELLATION_CORE_SLANG
#define CLUSTER_TESSELLATION_CORE_SLANG

#include "cluster_structures.slang"
#include "cluster_tessellation_types.slang"
#include "cluster_view.slang"
#include "resource_effects.slang"
#include "triangle_surface.slang"
#include "vertex_evaluate.slang"

ConstantBuffer<TessellationUniforms> Uniforms;
StructuredBuffer<ClusterViewUniforms> Views;
ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> VisibleClusters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint3> BinnedClusterIndexBuffer;
StructuredBuffer<RasterBinEntry> RasterBinMeta;
ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TessellationCounters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<TessellationWorkItem> SplitWorkItems;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DiceDispatchArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DicedTriangle> DicedTriangles;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer RasterDispatchArgs;

static const uint TESS_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;
static const uint TESS_PORTABLE_DISPATCH_DIMENSION = 65535u;

uint DiceThreadIndex(uint3 threadId)
{
    return threadId.x + threadId.y * Uniforms.DispatchGroupWidth * 64u;
}

void GrowPortableDispatch(
    RWByteAddressBuffer arguments,
    uint itemCount,
    uint itemsPerGroup)
{
    uint groupCount = (itemCount + itemsPerGroup - 1u) / itemsPerGroup;
    uint groupX = min(groupCount, TESS_PORTABLE_DISPATCH_DIMENSION);
    uint groupY = max(
        1u,
        (groupCount + TESS_PORTABLE_DISPATCH_DIMENSION - 1u) /
            TESS_PORTABLE_DISPATCH_DIMENSION);
    uint ignored;
    arguments.InterlockedMax(0u, groupX, ignored);
    arguments.InterlockedMax(4u, groupY, ignored);
}

void LoadTessellationTriangle<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint visibleClusterIndex,
    uint originalTriangle,
    out bool valid,
    out Triangle triangle)
{
    triangle = {};
    uint4 request = VisibleClusters[visibleClusterIndex];
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint triangleStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        request.x,
        request.y,
        vertexBlobAddress,
        vertexBlobSize,
        triangleStart,
        packedCounts);
    uint vertexCount = packedCounts & 0xFFu;
    uint triangleCount = (packedCounts >> 8u) & 0xFFu;
    uint cacheBaseByte = CacheOffsets[visibleClusterIndex];
    valid = originalTriangle < triangleCount && cacheBaseByte < TESS_CACHE_OFFSET_INVALID;
    if (!valid)
        return;

    uint indexOffset = PageHeap.Load(request.x + 24u);
    SurfaceVertex vertices[3];
    [unroll]
    for (uint corner = 0u; corner < 3u; corner++)
    {
        uint localVertex = FetchVertexIndex(
            PageHeap,
            request.x,
            indexOffset,
            triangleStart,
            originalTriangle,
            corner);
        TVE.DeformedVertex value = evaluator.readCache(
            DeformCache,
            cacheBaseByte,
            vertexCount,
            localVertex);
        vertices[corner].Position = evaluator.getPosition(value);
        vertices[corner].Normal = evaluator.getNormal(value);
        vertices[corner].TexCoord = evaluator.getTexCoord0(value);
    }
    triangle = MakeTriangle(vertices[0], vertices[1], vertices[2]);
}

float2 TessellationScreenPosition(
    float3 position,
    ClusterViewUniforms view)
{
    float4 clip = mul(float4(position, 1.0), view.ViewProj);
    float inverseW = abs(clip.w) > 1e-8 ? rcp(clip.w) : 0.0;
    float2 ndc = clip.xy * inverseW;
    return (ndc * float2(0.5, -0.5) + 0.5) *
        float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
}

uint ComputeTessellationFactor<TSurface : ITriangleSurface>(
    uniform TSurface surface,
    Triangle triangle,
    ClusterViewUniforms view)
{
    float3 bary0 = float3(1.0, 0.0, 0.0);
    float3 bary1 = float3(0.0, 1.0, 0.0);
    float3 bary2 = float3(0.0, 0.0, 1.0);
    float2 screen0 = TessellationScreenPosition(surface.position(triangle, bary0), view);
    float2 screen1 = TessellationScreenPosition(surface.position(triangle, bary1), view);
    float2 screen2 = TessellationScreenPosition(surface.position(triangle, bary2), view);
    float maximumEdge = max(
        length(screen1 - screen0),
        max(length(screen2 - screen1), length(screen0 - screen2)));

    // Midpoint deviation catches curved surfaces whose corner positions are unchanged (Phong is
    // the canonical example). The policy remains pipeline-owned and surface-type-independent.
    float3 midpointBarycentrics[3] =
    {
        float3(0.5, 0.5, 0.0),
        float3(0.0, 0.5, 0.5),
        float3(0.5, 0.0, 0.5),
    };
    float2 linearMidpoints[3] =
    {
        (screen0 + screen1) * 0.5,
        (screen1 + screen2) * 0.5,
        (screen2 + screen0) * 0.5,
    };
    float curvaturePixels = 0.0;
    [unroll]
    for (uint edge = 0u; edge < 3u; edge++)
    {
        float2 surfaceMidpoint = TessellationScreenPosition(
            surface.position(triangle, midpointBarycentrics[edge]),
            view);
        curvaturePixels = max(
            curvaturePixels,
            length(surfaceMidpoint - linearMidpoints[edge]));
    }

    float target = max(Uniforms.TargetEdgePixels, 1.0);
    uint edgeFactor = uint(ceil(maximumEdge / target));
    uint curvatureFactor = uint(ceil(sqrt(curvaturePixels / target) * 2.0));
    return clamp(max(1u, max(edgeFactor, curvatureFactor)), 1u, Uniforms.MaxFactor);
}

void AppendSplitWork(TessellationWorkItem item)
{
    uint workIndex;
    TessellationCounters.InterlockedAdd(
        TESS_COUNTER_SPLIT_COUNT,
        1u,
        workIndex);
    if (workIndex >= Uniforms.MaxSplitWorkItems)
    {
        uint ignored;
        TessellationCounters.InterlockedAdd(
            TESS_COUNTER_SPLIT_OVERFLOW,
            1u,
            ignored);
        return;
    }
    SplitWorkItems[workIndex] = item;
    GrowPortableDispatch(DiceDispatchArgs, workIndex + 1u, 64u);
}

void FactorRasterBin<TVE : IVertexEvaluate, TSurface : ITriangleSurface>(
    uniform TVE evaluator,
    uniform TSurface surface,
    uint3 threadId)
{
    RasterBinEntry bin = RasterBinMeta[Uniforms.CurrentBin];
    uint localBatch = threadId.x + threadId.y * TESS_PORTABLE_DISPATCH_DIMENSION;
    uint batchCount = bin.BinSWCount + bin.BinHWCount;
    if (localBatch >= batchCount)
        return;

    uint binnedIndex = bin.ClusterOffset + localBatch;
    uint3 batch = BinnedClusterIndexBuffer[binnedIndex];
    uint visibleClusterIndex = batch.x;
    uint viewIndex = batch.z;
    uint4 request = VisibleClusters[visibleClusterIndex];
    if (request.w != viewIndex)
        return;

    uint rangeStart = (batch.y >> 16u) & 0xFFFFu;
    uint rangeEnd = batch.y & 0xFFFFu;
    uint batchFactor = 1u;
    for (uint originalTriangle = rangeStart;
         originalTriangle < rangeEnd;
         originalTriangle++)
    {
        bool valid;
        Triangle triangle;
        LoadTessellationTriangle(
            evaluator,
            visibleClusterIndex,
            originalTriangle,
            valid,
            triangle);
        if (!valid)
            continue;
        batchFactor = max(batchFactor, ComputeTessellationFactor(
            surface,
            triangle,
            Views[viewIndex]));
    }
    if (batchFactor <= 1u)
        return;

    // A tessellated batch is consumed by this queue instead of the ordinary raster executor. The
    // existing RasterBin entry remains the owner; an empty triangle range makes the already-bounded
    // software/hardware dispatches no-op without introducing a TessBin.
    BinnedClusterIndexBuffer[binnedIndex] = uint3(batch.x, 0u, batch.z);
    for (uint originalTriangle = rangeStart;
         originalTriangle < rangeEnd;
         originalTriangle++)
    {
        bool valid;
        Triangle triangle;
        LoadTessellationTriangle(
            evaluator,
            visibleClusterIndex,
            originalTriangle,
            valid,
            triangle);
        if (!valid)
            continue;
        TessellationWorkItem item;
        item.VisibleClusterIndex = visibleClusterIndex;
        item.OriginalTriangle = originalTriangle;
        item.ViewIndex = viewIndex;
        item.Factor = ComputeTessellationFactor(
            surface,
            triangle,
            Views[viewIndex]);
        item.RasterBin = Uniforms.CurrentBin;
        AppendSplitWork(item);
    }
}

void AppendDicedTriangle(DicedTriangle triangle)
{
    uint triangleIndex;
    TessellationCounters.InterlockedAdd(
        TESS_COUNTER_DICED_COUNT,
        1u,
        triangleIndex);
    if (triangleIndex >= Uniforms.MaxDicedTriangles)
    {
        uint ignored;
        TessellationCounters.InterlockedAdd(
            TESS_COUNTER_DICED_OVERFLOW,
            1u,
            ignored);
        return;
    }
    DicedTriangles[triangleIndex] = triangle;
    GrowPortableDispatch(RasterDispatchArgs, triangleIndex + 1u, 1u);
}

void EmitSurfaceTriangle<TSurface : ITriangleSurface>(
    uniform TSurface surface,
    Triangle source,
    TessellationWorkItem work,
    float3 bary0,
    float3 bary1,
    float3 bary2)
{
    DicedTriangle diced;
    diced.Position0 = surface.position(source, bary0);
    diced.VisibleClusterIndex = work.VisibleClusterIndex;
    diced.Position1 = surface.position(source, bary1);
    diced.OriginalTriangle = work.OriginalTriangle;
    diced.Position2 = surface.position(source, bary2);
    diced.ViewIndex = work.ViewIndex;
    AppendDicedTriangle(diced);
}

void DiceSplitWork<TVE : IVertexEvaluate, TSurface : ITriangleSurface>(
    uniform TVE evaluator,
    uniform TSurface surface,
    uint3 threadId)
{
    uint workIndex = DiceThreadIndex(threadId);
    uint splitCount = TessellationCounters.Load(TESS_COUNTER_SPLIT_COUNT);
    if (workIndex >= min(splitCount, Uniforms.MaxSplitWorkItems))
        return;

    TessellationWorkItem work = SplitWorkItems[workIndex];
    if (work.RasterBin != Uniforms.CurrentBin)
        return;
    bool valid;
    Triangle source;
    LoadTessellationTriangle(
        evaluator,
        work.VisibleClusterIndex,
        work.OriginalTriangle,
        valid,
        source);
    if (!valid || VisibleClusters[work.VisibleClusterIndex].w != work.ViewIndex)
        return;

    uint factor = clamp(work.Factor, 1u, Uniforms.MaxFactor);
    float inverseFactor = rcp(float(factor));
    for (uint row = 0u; row < factor; row++)
    {
        uint columns = factor - row;
        for (uint column = 0u; column < columns; column++)
        {
            float2 uv00 = float2(column, row) * inverseFactor;
            float2 uv10 = float2(column + 1u, row) * inverseFactor;
            float2 uv01 = float2(column, row + 1u) * inverseFactor;
            EmitSurfaceTriangle(
                surface,
                source,
                work,
                Barycentric(uv00),
                Barycentric(uv10),
                Barycentric(uv01));
            if (column + 1u < columns)
            {
                float2 uv11 = float2(column + 1u, row + 1u) * inverseFactor;
                EmitSurfaceTriangle(
                    surface,
                    source,
                    work,
                    Barycentric(uv10),
                    Barycentric(uv11),
                    Barycentric(uv01));
            }
        }
    }
}

#endif
