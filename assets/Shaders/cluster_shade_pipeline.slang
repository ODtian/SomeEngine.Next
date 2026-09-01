#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "vertex_evaluate.slang"
#include "material_interfaces.slang"
#include "triangle_surface_inverse.slang"
#include "cluster_view.slang"

struct ShadeUniforms
{
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    uint ShadingBin;
    uint ShadeBinCount;
    uint Pad1;
    uint Pad2;
    uint Pad3;
};

ConstantBuffer<ShadeUniforms> Uniforms;

StructuredBuffer<uint2> PixelCoordBuffer;
StructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint> BinCounts;
StructuredBuffer<ClusterViewUniforms> Views;

Texture2DArray<uint> VisBuffer;
Texture2DArray<float> DepthBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;

RWTexture2DArray<float4> OutputColor;

ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;
static const uint SHADE_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;

struct TriangleInfo
{
    uint visibleClusterIndex;
    uint triangleID;
    uint instanceID;
    uint vi0;
    uint vi1;
    uint vi2;
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint vertexCount;
    uint viewIndex;
};

bool decodeTriangle(uint3 pixelCoord, out TriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;

    uint visData = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visData))
        return false;

    tri.visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    tri.triangleID = DecodeVisBufferTriangleID(visData);

    DrawRequest req = VisibleClusters[tri.visibleClusterIndex];
    if (req.ViewIndex != pixelCoord.z)
        return false;
    tri.instanceID = req.InstanceID;
    tri.viewIndex = req.ViewIndex;

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        req.PageOffset,
        req.ClusterID,
        tri.vertexBlobAddress,
        tri.vertexBlobSize,
        tStart,
        packedCounts);
    tri.vertexCount = packedCounts & 0xFFu;

    uint idxOffset = PageHeap.Load(req.PageOffset + 24u);

    tri.vi0 = FetchVertexIndex(PageHeap, req.PageOffset, idxOffset, tStart, tri.triangleID, 0);
    tri.vi1 = FetchVertexIndex(PageHeap, req.PageOffset, idxOffset, tStart, tri.triangleID, 1);
    tri.vi2 = FetchVertexIndex(PageHeap, req.PageOffset, idxOffset, tStart, tri.triangleID, 2);
    return true;
}

VertexEvalContext makeVertexEvalContext(
    TriangleInfo tri,
    RenderInstanceTransform transform,
    uint isPreviousFrame,
    uint localVertIdx)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.vertexBlobAddress = tri.vertexBlobAddress;
    ctx.vertexBlobSize = tri.vertexBlobSize;
    ctx.localVertexIndex = localVertIdx;
    ctx.instanceID = tri.instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
}

void evaluateTriangleVerticesWithTransform<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    RenderInstanceTransform transform,
    uint isPreviousFrame,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    v0 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0));
    v1 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1));
    v2 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2));
}

void evaluateTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    evaluateTriangleVerticesWithTransform(
        evaluator,
        tri,
        GetCurrentInstanceTransform(tri.instanceID),
        0,
        v0,
        v1,
        v2);
}

bool readCachedTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    ByteAddressBuffer cache,
    StructuredBuffer<uint> offsets,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    uint cacheBaseByte = offsets[tri.visibleClusterIndex];
    if (cacheBaseByte >= SHADE_CACHE_OFFSET_INVALID)
        return false;

    v0 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi0);
    v1 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi1);
    v2 = evaluator.readCache(cache, cacheBaseByte, tri.vertexCount, tri.vi2);
    return true;
}

bool loadTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    bool useDeformCache,
    out TVE.DeformedVertex v0,
    out TVE.DeformedVertex v1,
    out TVE.DeformedVertex v2)
{
    if (useDeformCache)
        return readCachedTriangleVertices(
            evaluator,
            tri,
            DeformCache,
            CacheOffsets,
            v0,
            v1,
            v2);

    evaluateTriangleVertices(evaluator, tri, v0, v1, v2);
    return true;
}

float3 computeTriangleBarycentric<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint2 pixelCoord,
    ClusterViewUniforms view,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2)
{
    float3 p0 = evaluator.getPosition(v0);
    float3 p1 = evaluator.getPosition(v1);
    float3 p2 = evaluator.getPosition(v2);
    ScreenProjection sp0 = ProjectToScreenProjection(p0, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp1 = ProjectToScreenProjection(p1, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp2 = ProjectToScreenProjection(p2, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    return ComputePerspectiveBarycentric(float2(pixelCoord) + 0.5, sp0, sp1, sp2);
}

SurfaceVertex MakeSurfaceVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TVE.DeformedVertex value)
{
    SurfaceVertex result;
    result.Position = evaluator.getPosition(value);
    result.Normal = evaluator.getNormal(value);
    result.TexCoord = evaluator.getTexCoord0(value);
    return result;
}

Triangle MakeSurfaceTriangle<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2)
{
    return MakeTriangle(
        MakeSurfaceVertex(evaluator, v0),
        MakeSurfaceVertex(evaluator, v1),
        MakeSurfaceVertex(evaluator, v2));
}

float3 ReconstructWorldPosition(uint3 pixelCoord, ClusterViewUniforms view)
{
    float2 uv = (float2(pixelCoord.xy) + 0.5) /
        float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float depth = DepthBuffer.Load(int4(pixelCoord, 0));
    float4 world = mul(
        float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0),
        view.ClipToWorld);
    return abs(world.w) > 1e-8 ? world.xyz / world.w : world.xyz;
}

SurfaceBarycentrics ResolveBarycentrics<
    TVE : IVertexEvaluate,
    TSurface : ITriangleSurface>(
    uniform TVE evaluator,
    uniform TSurface surface,
    uint3 pixelCoord,
    ClusterViewUniforms view,
    Triangle triangle,
    TVE.DeformedVertex v0,
    TVE.DeformedVertex v1,
    TVE.DeformedVertex v2)
{
    float3 seed = computeTriangleBarycentric(evaluator, pixelCoord.xy, view, v0, v1, v2);
    return ResolveTriangleSurface(
        surface,
        triangle,
        ReconstructWorldPosition(pixelCoord, view),
        seed,
        view.ViewProj,
        uint2(Uniforms.ScreenWidth, Uniforms.ScreenHeight));
}

void CSShadeCore<
    TVE : IVertexEvaluate,
    TSurface : ITriangleSurface,
    TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TSurface surface,
    uniform TMaterial material,
    uint3 tid,
    bool useDeformCache)
{
    if (tid.x >= BinCounts[Uniforms.ShadingBin])
        return;

    uint pixelIdx = BinOffsets[Uniforms.ShadingBin] + tid.x;
    uint2 work = PixelCoordBuffer[pixelIdx];
    uint3 pixelCoord = uint3(
        work.x & 0xFFFFu,
        work.x >> 16,
        work.y);

    TriangleInfo tri;
    if (!decodeTriangle(pixelCoord, tri))
        return;
    ClusterViewUniforms view = Views[tri.viewIndex];

    TVE.DeformedVertex v0;
    TVE.DeformedVertex v1;
    TVE.DeformedVertex v2;
    if (!loadTriangleVertices(vertexEval, tri, useDeformCache, v0, v1, v2))
        return;

    Triangle triangle = MakeSurfaceTriangle(vertexEval, v0, v1, v2);
    SurfaceBarycentrics coordinates = ResolveBarycentrics(
        vertexEval,
        surface,
        pixelCoord,
        view,
        triangle,
        v0,
        v1,
        v2);
    float3 bary = coordinates.Valid != 0u
        ? coordinates.Value
        : computeTriangleBarycentric(vertexEval, pixelCoord.xy, view, v0, v1, v2);
    SurfaceEval surfaceEval = surface.eval(triangle, bary);

    if (Uniforms.DebugMode != 0)
    {
        material.evaluateDebug(
            vertexEval,
            Uniforms.DebugMode,
            pixelCoord,
            tri.instanceID,
            surfaceEval,
            bary,
            v0,
            v1,
            v2,
            OutputColor);
        return;
    }

    material.evaluateSurface(
        vertexEval,
        pixelCoord,
        tri.instanceID,
        surfaceEval,
        bary,
        v0,
        v1,
        v2,
        OutputColor);
}

void CSShade<
    TVE : IVertexEvaluate,
    TSurface : ITriangleSurface,
    TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TSurface surface,
    uniform TMaterial material,
    uint3 tid)
{
    CSShadeCore(vertexEval, surface, material, tid, false);
}

void CSShadeFromCache<
    TVE : IVertexEvaluate,
    TSurface : ITriangleSurface,
    TMaterial : ISurfaceEvaluate<TVE>>(
    uniform TVE vertexEval,
    uniform TSurface surface,
    uniform TMaterial material,
    uint3 tid)
{
    CSShadeCore(vertexEval, surface, material, tid, true);
}

#endif
