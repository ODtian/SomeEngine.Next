#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "vertex_consumer_cache.slang"
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
    uint MaterialSlotCapacity;
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
RWTexture2DArray<float2> OutputMotionVectors;

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
    uint materialSlot;
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
    if (DrawRequestViewIndex(req) != pixelCoord.z)
        return false;
    tri.instanceID = req.InstanceID;
    tri.viewIndex = DrawRequestViewIndex(req);

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        req.PageOffset,
        DrawRequestClusterID(req),
        tri.vertexBlobAddress,
        tri.vertexBlobSize,
        tStart,
        packedCounts);
    tri.vertexCount = packedCounts & 0xFFu;

    uint clusterOffset =
        req.PageOffset + PageHeap.Load(req.PageOffset + 16u) +
        DrawRequestClusterID(req) * PACKED_CLUSTER_STRIDE_BYTES;
    uint localMaterial = LocalMaterial(
        PageHeap.Load(clusterOffset + 68u),
        PageHeap.Load(clusterOffset + 72u),
        tri.triangleID);
    uint slotOffset = LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(
            InstanceProperties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        tri.instanceID);
    if (slotOffset >= Uniforms.MaterialSlotCapacity ||
        localMaterial >= Uniforms.MaterialSlotCapacity - slotOffset)
    {
        return false;
    }
    tri.materialSlot = slotOffset + localMaterial;

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

Triangle evaluateTriangleWithTransform<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri,
    RenderInstanceTransform transform,
    uint isPreviousFrame)
{
    SurfaceVertex v0 = EvaluateSurfaceVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0),
        tri.visibleClusterIndex);
    SurfaceVertex v1 = EvaluateSurfaceVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1),
        tri.visibleClusterIndex);
    SurfaceVertex v2 = EvaluateSurfaceVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2),
        tri.visibleClusterIndex);
    return MakeTriangle(v0, v1, v2);
}

Triangle evaluateTriangle<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    TriangleInfo tri)
{
    return evaluateTriangleWithTransform(
        evaluator,
        tri,
        GetCurrentInstanceTransform(tri.instanceID),
        0u);
}

float3 computeTriangleBarycentricAt(
    float2 pixelPosition,
    ClusterViewUniforms view,
    Triangle triangle)
{
    ScreenProjection sp0 = ProjectToScreenProjection(triangle.Position0, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp1 = ProjectToScreenProjection(triangle.Position1, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp2 = ProjectToScreenProjection(triangle.Position2, view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    return ComputePerspectiveBarycentric(pixelPosition, sp0, sp1, sp2);
}

float3 ReconstructWorldPositionAt(
    float2 pixelPosition,
    float depth,
    ClusterViewUniforms view)
{
    float2 uv = pixelPosition /
        float2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    float4 world = mul(
        float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0),
        view.ClipToWorld);
    return abs(world.w) > 1e-8 ? world.xyz / world.w : world.xyz;
}

SurfaceBarycentrics ResolveBarycentrics<TDomain : IDomainEvaluate>(
    uniform TDomain domain,
    uint3 pixelCoord,
    ClusterViewUniforms view,
    Triangle triangle)
{
    float2 pixelCenter = float2(pixelCoord.xy) + 0.5;
    float3 seed = computeTriangleBarycentricAt(
        pixelCenter, view, triangle);
    float3 seedX = computeTriangleBarycentricAt(
        pixelCenter + float2(1.0, 0.0), view, triangle);
    float3 seedY = computeTriangleBarycentricAt(
        pixelCenter + float2(0.0, 1.0), view, triangle);
    float depth = DepthBuffer.Load(int4(pixelCoord, 0));

    SurfaceReconstructionInput input;
    input.TargetPosition = ReconstructWorldPositionAt(pixelCenter, depth, view);
    input.TargetPositionDx = ReconstructWorldPositionAt(
        pixelCenter + float2(1.0, 0.0), depth, view);
    input.TargetPositionDy = ReconstructWorldPositionAt(
        pixelCenter + float2(0.0, 1.0), depth, view);
    input.CameraPosition = view.CameraPos;
    input.ParentValue = seed;
    input.ParentDx = seedX - seed;
    input.ParentDy = seedY - seed;
    input.ViewProjection = view.ViewProj;
    input.ScreenSize = uint2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    return domain.reconstruct(triangle, input);
}

float2 ClusterMotionClipToUv(float4 clip)
{
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 1.0 - (ndc.y * 0.5 + 0.5));
}

void WriteClusterMotion<
    TVE : IVertexEvaluate,
    TDomain : IDomainEvaluate>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TriangleInfo tri,
    ClusterViewUniforms view,
    SurfaceEval currentSurface,
    float3 bary,
    uint3 pixelCoord)
{
    if (view.HasPrevHistory == 0u)
    {
        OutputMotionVectors[pixelCoord] = 0.0.xx;
        return;
    }

    Triangle previousTriangle = evaluateTriangleWithTransform(
        evaluator,
        tri,
        GetPreviousInstanceTransform(tri.instanceID),
        1u);
    float3 previousPosition = domain.position(previousTriangle, bary);
    float4 currentClip = mul(float4(currentSurface.Position, 1.0), view.ViewProj);
    float4 previousClip = mul(float4(previousPosition, 1.0), view.PrevViewProj);
    OutputMotionVectors[pixelCoord] =
        abs(currentClip.w) < 1e-6 || abs(previousClip.w) < 1e-6
            ? 0.0.xx
            : ClusterMotionClipToUv(currentClip) - ClusterMotionClipToUv(previousClip);
}

void CSShadeCore<
    TVE : IVertexEvaluate,
    TDomain : IDomainEvaluate,
    TSurface : ISurfaceEvaluate>(
    uniform TVE vertexEval,
    uniform TDomain domain,
    uniform TSurface surface,
    uint3 tid)
{
    uint group = tid.x / 64u + tid.y * 65535u, lane = tid.x % 64u;
    uint tiles = BinCounts[Uniforms.ShadingBin * 2u];
    uint quads = BinCounts[Uniforms.ShadingBin * 2u + 1u];
    bool fullTile = group < tiles;
    uint workIndex = fullTile ? group : (group - tiles) * 16u + lane / 4u;
    if (!fullTile && workIndex >= quads) return;
    uint2 work = PixelCoordBuffer[BinOffsets[Uniforms.ShadingBin * 2u + (fullTile ? 0u : 1u)] + workIndex];
    uint row = work.x / Uniforms.ScreenWidth;
    uint3 pixelCoord = uint3(work.x % Uniforms.ScreenWidth,
        row % Uniforms.ScreenHeight, row / Uniforms.ScreenHeight);
    uint corner = lane & 3u;
    if (!fullTile && (work.y & (1u << corner)) == 0u) return;
    // Consecutive groups of four lanes always cover a spatial quad.
    uint quad = lane / 4u;
    pixelCoord.xy += uint2(corner & 1u, corner >> 1u);
    if (fullTile) pixelCoord.xy += uint2(quad % 4u, quad / 4u) * 2u;

    TriangleInfo tri;
    if (!decodeTriangle(pixelCoord, tri))
        return;
    ClusterViewUniforms view = Views[tri.viewIndex];

    Triangle triangle = evaluateTriangle(vertexEval, tri);
    SurfaceBarycentrics coordinates = ResolveBarycentrics(
        domain,
        pixelCoord,
        view,
        triangle);
    if (coordinates.Valid == 0u)
        return;
    float3 bary = coordinates.Value;
    SurfaceEval surfaceEval = domain.eval(triangle, bary);
    WriteClusterMotion(
        vertexEval,
        domain,
        tri,
        view,
        surfaceEval,
        bary,
        pixelCoord);

    if (Uniforms.DebugMode != 0)
    {
        surface.evaluateDebug(
            Uniforms.DebugMode,
            pixelCoord,
            tri.instanceID,
            tri.materialSlot,
            surfaceEval,
            bary,
            triangle,
            OutputColor);
        return;
    }

    surface.evaluateSurface(
        pixelCoord,
        tri.instanceID,
        tri.materialSlot,
        surfaceEval,
        bary,
        triangle,
        OutputColor);
}

void CSShade<
    TVE : IVertexEvaluate,
    TDomain : IDomainEvaluate,
    TSurface : ISurfaceEvaluate>(
    uniform TVE vertexEval,
    uniform TDomain domain,
    uniform TSurface surface,
    uint3 tid)
{
    CSShadeCore(vertexEval, domain, surface, tid);
}

#endif
