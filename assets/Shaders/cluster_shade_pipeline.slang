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
    uint Padding;
    uint VisibilityDepth;
    uint3 Padding1;
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
    uint visibility;
    uint sourceLane;
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

bool LoadTriangleInfo(uint visibility, uint viewIndex, out TriangleInfo tri)
{
    uint visible = DecodeVisBufferVisibleClusterIndex(visibility);
    DrawRequest request = VisibleClusterRequest(VisibleClusters[visible]);
    bool loaded = LoadTriangleInfo(request, DecodeVisBufferTriangleID(visibility), tri);
    tri.visibleClusterIndex = visible;
    return loaded && DrawRequestViewIndex(request) == viewIndex;
}

TriangleInfo ReadTriangleInfoLane(TriangleInfo value, uint lane)
{
    value.visibleClusterIndex = WaveReadLaneAt(value.visibleClusterIndex, lane);
    value.triangleID = WaveReadLaneAt(value.triangleID, lane);
    value.instanceID = WaveReadLaneAt(value.instanceID, lane);
    value.vi0 = WaveReadLaneAt(value.vi0, lane);
    value.vi1 = WaveReadLaneAt(value.vi1, lane);
    value.vi2 = WaveReadLaneAt(value.vi2, lane);
    value.vertexBlobAddress = WaveReadLaneAt(value.vertexBlobAddress, lane);
    value.vertexBlobSize = WaveReadLaneAt(value.vertexBlobSize, lane);
    value.vertexCount = WaveReadLaneAt(value.vertexCount, lane);
    value.viewIndex = WaveReadLaneAt(value.viewIndex, lane);
    value.materialSlot = WaveReadLaneAt(value.materialSlot, lane);
    return value;
}

bool decodeTriangle(uint3 pixelCoord, out TriangleInfo tri)
{
    tri = {};
    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight) return false;
    uint visibility = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visibility)) return false;
    uint leader = WaveMaskFirstLane(WaveMatchingLanes(visibility) & WaveMatchingLanes(pixelCoord.z));
    uint valid = 0u;
    if (WaveGetLaneIndex() == leader) valid = LoadTriangleInfo(visibility, pixelCoord.z, tri) ? 1u : 0u;
    valid = WaveReadLaneAt(valid, leader);
    tri = ReadTriangleInfoLane(tri, leader);
    tri.visibility = visibility;
    tri.sourceLane = leader;
    return valid != 0u;
}

uint GetTriangleWaveLeader(TriangleInfo tri, uint previousFrame)
{
    uint4 frameLanes = WaveMatchingLanes(previousFrame);
    if ((frameLanes[tri.sourceLane >> 5u] & (1u << (tri.sourceLane & 31u))) != 0u)
        return tri.sourceLane;
    // Surface reconstruction can end individual lanes before motion evaluation.
    return WaveMaskFirstLane(WaveMatchingLanes(tri.visibility) & WaveMatchingLanes(tri.viewIndex) & frameLanes);
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
    uint leader = GetTriangleWaveLeader(tri, isPreviousFrame);
    TDomain.TriangleData triangle;
    if (WaveGetLaneIndex() == leader)
    {
        TVE.Vertex v0 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi0));
        TVE.Vertex v1 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi1));
        TVE.Vertex v2 = evaluator.evaluate(makeVertexEvalContext(tri, transform, isPreviousFrame, tri.vi2));
        triangle = domain.makeTriangle(v0, v1, v2, tri.materialSlot);
    }
    return domain.readLane(triangle, leader);
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

PipelineTriangleProjection ProjectTriangleWave<TVertex, TDomain>(
    uniform TDomain domain,
    ClusterViewUniforms view,
    TDomain.TriangleData triangle,
    TriangleInfo info)
    where TDomain : IDomainEvaluate<TVertex>
{
    uint leader = GetTriangleWaveLeader(info, 0u);
    PipelineTriangleProjection projection;
    if (WaveGetLaneIndex() == leader)
    {
        ScreenProjection first = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 0u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
        ScreenProjection second = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 1u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
        ScreenProjection third = ProjectToScreenProjection(ClusterPositionForView(domain.cornerPosition(triangle, 2u), view), view.ViewProj, Uniforms.ScreenWidth, Uniforms.ScreenHeight);
        projection.Origin = first.Pixel;
        projection.FirstEdge = second.Pixel - first.Pixel;
        projection.SecondEdge = third.Pixel - first.Pixel;
        projection.InverseArea = 1.0 / (projection.FirstEdge.x * projection.SecondEdge.y - projection.FirstEdge.y * projection.SecondEdge.x);
        projection.InverseW = float3(first.InvW, second.InvW, third.InvW);
    }
    projection.Origin = WaveReadLaneAt(projection.Origin, leader);
    projection.FirstEdge = WaveReadLaneAt(projection.FirstEdge, leader);
    projection.SecondEdge = WaveReadLaneAt(projection.SecondEdge, leader);
    projection.InverseArea = WaveReadLaneAt(projection.InverseArea, leader);
    projection.InverseW = WaveReadLaneAt(projection.InverseW, leader);
    return projection;
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
    TriangleInfo info,
    float depth,
    out float3 positionFromDepth)
    where TDomain : IDomainEvaluate<TVertex>
{
    float2 pixelCenter = float2(pixelCoord.xy) + 0.5;
    PipelineTriangleProjection projection = ProjectTriangleWave<TVertex, TDomain>(domain, view, triangle, info);
    float3 seed = TriangleBarycentricAt(projection, pixelCenter);
    float3 seedX = TriangleBarycentricAt(projection, pixelCenter + float2(1.0, 0.0));
    float3 seedY = TriangleBarycentricAt(projection, pixelCenter + float2(0.0, 1.0));
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
    input.WorldTranslation = view.WorldTranslation;
    input.ViewProjection = view.ViewProj;
    input.ScreenSize = uint2(Uniforms.ScreenWidth, Uniforms.ScreenHeight);
    return domain.reconstruct(triangle, input);
}

SurfaceBarycentrics ResolveBarycentrics<TVertex, TDomain>(
    uniform TDomain domain, uint3 pixelCoord, ClusterViewUniforms view,
    TDomain.TriangleData triangle, TriangleInfo info, out float depth, out float3 positionFromDepth)
    where TDomain : IDomainEvaluate<TVertex>
{
    depth = DepthBuffer.Load(int4(pixelCoord, 0));
    return ResolveBarycentricsAtDepth<TVertex, TDomain>(domain, pixelCoord, view, triangle, info, depth, positionFromDepth);
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
        domain, coord, view, triangle, tri, depth, positionFromDepth);
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

void CSRequestShadowPages<TVE, TDomain, TSurface>(
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
    surface.markShadowPages(input, evaluated, bary, triangle);
}

void CSForwardShade<TVE, TDomain, TSurface>(
    uniform TVE vertexEval, uniform TDomain domain, uniform TSurface surface, uint3 tid,
    uint visibilityDepth, uint debugMode)
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
    WriteClusterMotion<TVE, TDomain>(vertexEval, domain, tri, view, triangle, bary, input.PixelCoord);
    if (debugMode != 0u)
        surface.evaluateDebug(debugMode, input, evaluated, bary, triangle, OutputColor);
    else
        surface.evaluateSurface(input, evaluated, bary, triangle, OutputColor);
}

#endif
