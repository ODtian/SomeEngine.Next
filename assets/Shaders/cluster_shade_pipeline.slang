#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "cluster_common.slang"
#include "vertex_consumer_cache.slang"
#include "material_interfaces.slang"
#include "cluster_view.slang"
#include "vsm_receiver_pixel.slang"
#include "shade_bin_common.slang"

struct ShadeUniforms
{
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    uint ShadingBin;
    uint ShadeBinCount;
    uint MaterialSlotCapacity;
    uint ViewCount;
    uint SurfaceDataStride;
    uint VisibilityDepth;
    uint3 SurfacePadding;
};

ConstantBuffer<ShadeUniforms> Uniforms;

StructuredBuffer<ShadePixelWork> PixelCoordBuffer;
StructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint> BinCounts;
StructuredBuffer<ClusterViewUniforms> Views;

Texture2DArray<uint> VisBuffer;
Texture2DArray<float> DepthBuffer;

StructuredBuffer<VisibleCluster> VisibleClusters;
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

bool LoadTriangleInfo(DrawRequest req, uint triangleID, out TriangleInfo tri)
{
    tri = {};
    tri.triangleID = triangleID;
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

    uint localMaterial = GetClusterLocalMaterial(PageHeap, req.PageOffset, DrawRequestClusterID(req), tri.triangleID);
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

bool decodeTriangle(uint3 pixelCoord, out TriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return false;
    uint visibility = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visibility))
        return false;
    uint visible = DecodeVisBufferVisibleClusterIndex(visibility);
    DrawRequest request = VisibleClusterRequest(VisibleClusters[visible]);
    if (DrawRequestViewIndex(request) != pixelCoord.z)
        return false;
    bool loaded = LoadTriangleInfo(request, DecodeVisBufferTriangleID(visibility), tri);
    tri.visibleClusterIndex = visible;
    return loaded;
}

struct PipelineTriangleProjection
{
    float2 Origin;
    float2 FirstEdge;
    float2 SecondEdge;
    float InverseArea;
    float3 InverseW;
};

float3 TriangleBarycentricAt(PipelineTriangleProjection projection, float2 pixel)
{
    float2 offset = pixel - projection.Origin;
    float second = (offset.x * projection.SecondEdge.y - offset.y * projection.SecondEdge.x) * projection.InverseArea;
    float third = (projection.FirstEdge.x * offset.y - projection.FirstEdge.y * offset.x) * projection.InverseArea;
    float3 weighted = float3(1.0 - second - third, second, third) * projection.InverseW;
    return weighted / dot(weighted, 1.0.xxx);
}

VertexEvalContext makeVertexEvalContext(
    TriangleInfo tri,
    RenderInstanceTransform transform,
    uint isPreviousFrame,
    uint localVertIdx)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    LoadVertexData(ctx, tri.vertexBlobAddress);
    ctx.localVertexIndex = localVertIdx;
    ctx.visibleClusterIndex = tri.visibleClusterIndex;
    ctx.instanceID = tri.instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
}

TDomain.TriangleData evaluateTriangleWithTransform<TVE, TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TriangleInfo tri,
    RenderInstanceTransform transform,
    uint isPreviousFrame)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    TVE.Vertex v0 = EvaluateVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0),
        tri.visibleClusterIndex);
    TVE.Vertex v1 = EvaluateVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1),
        tri.visibleClusterIndex);
    TVE.Vertex v2 = EvaluateVertexWave(
        evaluator,
        makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2),
        tri.visibleClusterIndex);
    return domain.makeTriangle(v0, v1, v2, tri.materialSlot);
}

TDomain.TriangleData evaluateTriangle<TVE, TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TriangleInfo tri)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    return evaluateTriangleWithTransform<TVE, TDomain>(
        evaluator,
        domain,
        tri,
        GetCurrentInstanceTransform(tri.instanceID),
        0u);
}

float3 computeTriangleBarycentricAt<TVertex, TDomain>(
    uniform TDomain domain,
    float2 pixelPosition,
    ClusterViewUniforms view,
    TDomain.TriangleData triangle)
    where TDomain : IDomainEvaluate<TVertex>
{
    ScreenProjection sp0 = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 0u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp1 = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 1u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    ScreenProjection sp2 = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 2u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
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

SurfaceBarycentrics ResolveBarycentricsAtDepth<TVertex, TDomain>(
    uniform TDomain domain,
    uint3 pixelCoord,
    ClusterViewUniforms view,
    TDomain.TriangleData triangle,
    float depth,
    out float3 positionFromDepth)
    where TDomain : IDomainEvaluate<TVertex>
{
    float2 pixelCenter = float2(pixelCoord.xy) + 0.5;
    float3 seed = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter, view, triangle);
    float3 seedX = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter + float2(1.0, 0.0), view, triangle);
    float3 seedY = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter + float2(0.0, 1.0), view, triangle);

    positionFromDepth = ReconstructWorldPositionAt(pixelCenter, depth, view);
    SurfaceReconstructionInput input;
    input.PixelCenter = pixelCenter;
    input.TargetPosition = positionFromDepth;
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
    input.WorldTranslation = view.WorldTranslation;
    return domain.reconstruct(triangle, input);
}

SurfaceBarycentrics ResolveBarycentrics<TVertex, TDomain>(
    uniform TDomain domain, uint3 pixelCoord, ClusterViewUniforms view,
    TDomain.TriangleData triangle)
    where TDomain : IDomainEvaluate<TVertex>
{
    float3 positionFromDepth;
    return ResolveBarycentricsAtDepth<TVertex, TDomain>(
        domain, pixelCoord, view, triangle, DepthBuffer.Load(int4(pixelCoord, 0)), positionFromDepth);
}

float2 ClusterMotionClipToUv(float4 clip)
{
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 1.0 - (ndc.y * 0.5 + 0.5));
}

void WriteClusterMotion<
    TVE,
    TDomain>(
    uniform TVE evaluator,
    uniform TDomain domain,
    TriangleInfo tri,
    ClusterViewUniforms view,
    TDomain.TriangleData currentTriangle,
    float3 bary,
    uint3 pixelCoord)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    if (view.HasPrevHistory == 0u)
    {
        OutputMotionVectors[pixelCoord] = 0.0.xx;
        return;
    }

    TDomain.TriangleData previousTriangle = evaluateTriangleWithTransform<TVE, TDomain>(
        evaluator,
        domain,
        tri,
        GetPreviousInstanceTransform(tri.instanceID),
        1u);
    float3 currentPosition = domain.position(currentTriangle, bary);
    float3 previousPosition = domain.position(previousTriangle, bary);
    float4 currentClip = mul(float4(ClusterPositionForView(currentPosition, view), 1.0), view.ViewProj);
    float4 previousClip = mul(float4(ClusterPreviousPositionForView(previousPosition, view), 1.0), view.PrevViewProj);
    OutputMotionVectors[pixelCoord] =
        abs(currentClip.w) < 1e-6 || abs(previousClip.w) < 1e-6
            ? 0.0.xx
            : ClusterMotionClipToUv(currentClip) - ClusterMotionClipToUv(previousClip);
}

bool TryEvaluateSurfacePixel<TVE, TDomain>(
    uniform TVE vertexEval, uniform TDomain domain, uint3 tid, uint visibilityDepth,
    out TriangleInfo tri, out ClusterViewUniforms view, out TDomain.TriangleData triangle,
    out float3 bary, out TDomain.Surface evaluated, out SurfacePixelInput input)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    tri = {};
    view = {};
    triangle = {};
    bary = 0.0.xxx;
    evaluated = {};
    input = {};
    uint group = tid.x / 64u + tid.y * 65535u, lane = tid.x % 64u;
    uint tiles = BinCounts[Uniforms.ShadingBin * 2u];
    uint quads = BinCounts[Uniforms.ShadingBin * 2u + 1u];
    bool fullTile = group < tiles;
    uint workIndex = fullTile ? group : (group - tiles) * 16u + lane / 4u;
    if (!fullTile && workIndex >= quads) return false;
    ShadePixelWork work = PixelCoordBuffer[BinOffsets[Uniforms.ShadingBin * 2u + (fullTile ? 0u : 1u)] + workIndex];
    uint row = work.PixelIndex / Uniforms.ScreenWidth;
    uint3 coord = uint3(work.PixelIndex % Uniforms.ScreenWidth,
        row % Uniforms.ScreenHeight, row / Uniforms.ScreenHeight);
    uint corner = lane & 3u;
    if (!fullTile && (work.Mask & (1u << corner)) == 0u) return false;
    uint quad = lane / 4u;
    coord.xy += uint2(corner & 1u, corner >> 1u);
    if (fullTile) coord.xy += uint2(quad % 4u, quad / 4u) * 2u;
    if (!decodeTriangle(coord, tri)) return false;
    view = ApplyFirstPersonViewTransform(
        InstanceData, InstanceProperties, tri.instanceID, Views[tri.viewIndex]);
    triangle = evaluateTriangle<TVE, TDomain>(vertexEval, domain, tri);
    float depth = DepthBuffer.Load(int4(coord, 0));
    float3 positionFromDepth;
    SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<TVE.Vertex, TDomain>(
        domain, coord, view, triangle, depth, positionFromDepth);
    if (coordinates.Valid == 0u) return false;
    bary = coordinates.Value;
    evaluated = domain.eval(triangle, bary);
    VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
        DepthBuffer, coord, uint2(view.ScreenWidth, view.ScreenHeight), depth, view.ClipToWorld);
    input.PixelCoord = coord;
    input.InstanceID = tri.instanceID;
    input.MaterialIndex = tri.materialSlot;
    input.CameraPosition = view.CameraPos;
    input.PositionDx = receiver.PositionDx - receiver.Position;
    input.PositionDy = receiver.PositionDy - receiver.Position;
    input.BarycentricDx = coordinates.Dx;
    input.BarycentricDy = coordinates.Dy;
    input.Depth = depth;
    input.VisibilityDepth = visibilityDepth;
    input.PositionFromDepth = positionFromDepth;
    return true;
}

RWStructuredBuffer<SurfaceReceiver> SurfaceReceiverOutput;

void WriteMaterialReceiver(SurfaceShadowReceiver source, TriangleInfo tri, SurfacePixelInput input)
{
    ClusterViewUniforms view = Views[tri.viewIndex];
    SurfaceReceiver receiver = (SurfaceReceiver)0;
    receiver.Position = TransformFirstPersonWorldPosition(InstanceData, InstanceProperties, tri.instanceID,
        view, source.Position);
    receiver.GeometricNormal = TransformFirstPersonWorldNormal(InstanceData, InstanceProperties, tri.instanceID,
        view, source.GeometricNormal);
    receiver.ShadingNormal = receiver.GeometricNormal;
    receiver.Flags = source.Flags | SURFACE_RECEIVER_PRESENT;
    receiver.Depth = source.Depth;
    if ((LoadInstanceFirstPerson(InstanceData, InstanceProperties, tri.instanceID).x & 3u) == 1u)
        receiver.Flags |= SURFACE_RECEIVER_FIRST_PERSON;
    uint3 coord = input.PixelCoord;
    uint index = (coord.z * Uniforms.ScreenHeight + coord.y) * Uniforms.ScreenWidth + coord.x;
    SurfaceReceiverOutput[index] = receiver;
}

void CSShadeCore<TVE, TDomain, TSurface>(
    uniform TVE vertexEval, uniform TDomain domain, uniform TSurface surface, uint3 tid,
    uint debugMode = Uniforms.DebugMode)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ISurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    TriangleInfo tri;
    ClusterViewUniforms view;
    TDomain.TriangleData triangle;
    float3 bary;
    TDomain.Surface evaluated;
    SurfacePixelInput input;
    if (!TryEvaluateSurfacePixel<TVE, TDomain>(vertexEval, domain, tid, Uniforms.VisibilityDepth,
        tri, view, triangle, bary, evaluated, input)) return;
    WriteClusterMotion<TVE, TDomain>(vertexEval, domain, tri, view, triangle, bary, input.PixelCoord);
    if (debugMode != 0u)
        surface.evaluateDebug(debugMode, input, evaluated, bary, triangle, OutputColor);
    else
        surface.evaluateSurface(input, evaluated, bary, triangle, OutputColor);
}

void CSShade<TVE, TDomain, TSurface>(
    uniform TVE vertexEval, uniform TDomain domain, uniform TSurface surface, uint3 tid,
    uint debugMode = Uniforms.DebugMode)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ISurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    CSShadeCore<TVE, TDomain, TSurface>(vertexEval, domain, surface, tid, debugMode);
}

void CSEvaluateShadowReceiver<TVE, TDomain, TSurface>(
    uniform TVE vertexEval, uniform TDomain domain, uniform TSurface surface, uint3 tid, uint visibilityDepth)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ITransparentSurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    TriangleInfo tri;
    ClusterViewUniforms view;
    TDomain.TriangleData triangle;
    float3 bary;
    TDomain.Surface evaluated;
    SurfacePixelInput input;
    if (!TryEvaluateSurfacePixel<TVE, TDomain>(vertexEval, domain, tid, visibilityDepth,
        tri, view, triangle, bary, evaluated, input)) return;
    WriteMaterialReceiver(surface.evaluateShadowReceiver(input, evaluated, bary, triangle), tri, input);
}

void CSForwardShade<TVE, TDomain, TSurface>(
    uniform TVE vertexEval, uniform TDomain domain, uniform TSurface surface, uint3 tid,
    uint visibilityDepth, uint debugMode, bool exportSurfaceReceivers)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ISurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
    where TSurface : ITransparentSurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    TriangleInfo tri;
    ClusterViewUniforms view;
    TDomain.TriangleData triangle;
    float3 bary;
    TDomain.Surface evaluated;
    SurfacePixelInput input;
    if (!TryEvaluateSurfacePixel<TVE, TDomain>(vertexEval, domain, tid, visibilityDepth,
        tri, view, triangle, bary, evaluated, input)) return;
    if (exportSurfaceReceivers)
        WriteMaterialReceiver(surface.evaluateShadowReceiver(input, evaluated, bary, triangle), tri, input);
    WriteClusterMotion<TVE, TDomain>(vertexEval, domain, tri, view, triangle, bary, input.PixelCoord);
    if (debugMode != 0u)
        surface.evaluateDebug(debugMode, input, evaluated, bary, triangle, OutputColor);
    else
        surface.evaluateSurface(input, evaluated, bary, triangle, OutputColor);
}

#endif
