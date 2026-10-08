#ifndef CLUSTER_SHADE_PIPELINE_SLANG
#define CLUSTER_SHADE_PIPELINE_SLANG

#include "vertex_consumer_cache.slang"
#include "material_interfaces.slang"
#include "cluster_view.slang"
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
StructuredBuffer<uint> MaterialSurfaceOffsets;
StructuredBuffer<uint> BinOffsets;
StructuredBuffer<uint> BinCounts;
StructuredBuffer<ClusterViewUniforms> Views;

Texture2DArray<uint> VisBuffer;
Texture2DArray<float> DepthBuffer;

StructuredBuffer<VisibleCluster> VisibleClusters;
ByteAddressBuffer PageHeap;

RWTexture2DArray<float4> OutputColor;
RWTexture2DArray<float2> OutputMotionVectors;
RWStructuredBuffer<SurfaceReceiver> SurfaceReceiverOutput;
RWStructuredBuffer<SurfaceReceiver> AdditionalSurfaceReceiverOutput;
StructuredBuffer<SurfaceReceiver> SurfaceReceiverInputs;
RWByteAddressBuffer MaterialSurfaceOutputs;
ByteAddressBuffer MaterialSurfaceInputs;

// Layers are water, front translucency and hair. User shaders provide current-camera depth.
void WriteAdditionalSurfaceReceiver(uint3 pixel, uint layer, SurfaceReceiver receiver)
{
    WriteSurfaceReceiverLayer(AdditionalSurfaceReceiverOutput, pixel,
        uint2(Uniforms.ScreenWidth, Uniforms.ScreenHeight), Uniforms.ViewCount, layer, receiver);
}

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

    DrawRequest req = VisibleClusterRequest(VisibleClusters[tri.visibleClusterIndex]);
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
        PageHeap, clusterOffset, req.PageOffset,
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
    return domain.makeTriangle(v0, v1, v2);
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

SurfaceBarycentrics ResolveBarycentrics<TVertex, TDomain>(
    uniform TDomain domain,
    uint3 pixelCoord,
    ClusterViewUniforms view,
    TDomain.TriangleData triangle)
    where TDomain : IDomainEvaluate<TVertex>
{
    float2 pixelCenter = float2(pixelCoord.xy) + 0.5;
    float3 seed = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter, view, triangle);
    float3 seedX = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter + float2(1.0, 0.0), view, triangle);
    float3 seedY = computeTriangleBarycentricAt<TVertex, TDomain>(
        domain, pixelCenter + float2(0.0, 1.0), view, triangle);
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
    input.WorldTranslation = view.WorldTranslation;
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

bool TryEvaluateMaterialPixel<TVE, TDomain>(uniform TVE vertexEval, uniform TDomain domain, uint3 tid,
    out uint3 pixelCoord, out TriangleInfo tri, out ClusterViewUniforms view,
    out TDomain.TriangleData triangle, out float3 bary, out TDomain.Surface evaluated, out uint surfaceIndex)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
{
    if (!TryReadMaterialPixel(tid, pixelCoord, surfaceIndex)) return false;
    if (!decodeTriangle(pixelCoord, tri)) return false;
    view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, tri.instanceID, Views[tri.viewIndex]);
    triangle = evaluateTriangle<TVE, TDomain>(vertexEval, domain, tri);
    SurfaceBarycentrics coordinates = ResolveBarycentrics<TVE.Vertex, TDomain>(
        domain, pixelCoord, view, triangle);
    if (coordinates.Valid == 0u) return false;
    bary = coordinates.Value;
    evaluated = domain.eval(triangle, bary);
    return true;
}


bool TryReadMaterialPixel(uint3 tid, out uint3 pixelCoord, out uint surfaceIndex)
{
    uint group = tid.x / 64u + tid.y * 65535u, lane = tid.x % 64u;
    uint tiles = BinCounts[Uniforms.ShadingBin * 2u];
    uint quads = BinCounts[Uniforms.ShadingBin * 2u + 1u];
    bool fullTile = group < tiles;
    uint workIndex = fullTile ? group : (group - tiles) * 16u + lane / 4u;
    pixelCoord = 0u.xxx;
    surfaceIndex = 0u;
    if (!fullTile && workIndex >= quads) return false;
    ShadePixelWork work = PixelCoordBuffer[BinOffsets[Uniforms.ShadingBin * 2u + (fullTile ? 0u : 1u)] + workIndex];
    uint row = work.PixelIndex / Uniforms.ScreenWidth;
    pixelCoord = uint3(work.PixelIndex % Uniforms.ScreenWidth, row % Uniforms.ScreenHeight, row / Uniforms.ScreenHeight);
    uint corner = lane & 3u;
    if (!fullTile && (work.Mask & (1u << corner)) == 0u) return false;
    uint quad = lane / 4u;
    pixelCoord.xy += uint2(corner & 1u, corner >> 1u);
    if (fullTile) pixelCoord.xy += uint2(quad % 4u, quad / 4u) * 2u;
    surfaceIndex = work.SurfaceDataIndex + (fullTile ? lane : countbits(work.Mask & ((1u << corner) - 1u)));
    return pixelCoord.x < Uniforms.ScreenWidth && pixelCoord.y < Uniforms.ScreenHeight;
}

void CSShade<TVE, TDomain, TSurface>(uniform TVE vertexEval, uniform TDomain domain,
    uniform TSurface surface, uint3 tid)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ISurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    uint3 coord;
    uint surfaceIndex;
    if (!TryReadMaterialPixel(tid, coord, surfaceIndex)) return;
    uint index = (coord.z * Uniforms.ScreenHeight + coord.y) * Uniforms.ScreenWidth + coord.x;
    SurfaceReceiver receiver = SurfaceReceiverInputs[index];
    if ((receiver.Flags & SURFACE_RECEIVER_PRESENT) == 0u) return;
    TSurface.Data data = MaterialSurfaceInputs.Load<TSurface.Data>(MaterialSurfaceOffsets[Uniforms.ShadingBin] + surfaceIndex * Uniforms.SurfaceDataStride);
    OutputColor[coord] = surface.shadeSurface(coord, Uniforms.DebugMode, data, receiver);
}

void CSEvaluateSurface<TVE, TDomain, TSurface>(uniform TVE vertexEval, uniform TDomain domain,
    uniform TSurface surface, uint3 tid)
    where TVE : IVertexEvaluate
    where TDomain : IDomainEvaluate<TVE.Vertex>
    where TSurface : ISurfaceEvaluate<TDomain.Surface, TDomain.TriangleData>
{
    uint3 coord;
    TriangleInfo tri;
    ClusterViewUniforms view;
    TDomain.TriangleData triangle;
    float3 bary;
    TDomain.Surface evaluated;
    uint surfaceIndex;
    if (!TryEvaluateMaterialPixel<TVE, TDomain>(vertexEval, domain, tid,
        coord, tri, view, triangle, bary, evaluated, surfaceIndex)) return;
    SurfacePixel pixel;
    pixel.PixelCoord = coord;
    pixel.InstanceID = tri.instanceID;
    pixel.MaterialIndex = tri.materialSlot;
    pixel.DebugMode = Uniforms.DebugMode;
    pixel.VisibilityDepth = Uniforms.VisibilityDepth;
    pixel.Depth = DepthBuffer.Load(int4(coord, 0));
    pixel.PositionFromDepth = ReconstructWorldPositionAt(float2(coord.xy) + .5, pixel.Depth, view);
    SurfaceReceiver receiver;
    TSurface.Data data = surface.evaluateSurface(pixel, evaluated, bary, triangle, receiver);
    ClusterViewUniforms sourceView = Views[tri.viewIndex];
    receiver.Position = TransformFirstPersonWorldPosition(InstanceData, InstanceProperties, tri.instanceID,
        sourceView, receiver.Position);
    receiver.ShadingNormal = TransformFirstPersonWorldNormal(InstanceData, InstanceProperties, tri.instanceID,
        sourceView, receiver.ShadingNormal);
    receiver.GeometricNormal = TransformFirstPersonWorldNormal(InstanceData, InstanceProperties, tri.instanceID,
        sourceView, receiver.GeometricNormal);
    receiver.Flags |= SURFACE_RECEIVER_PRESENT;
    if ((LoadInstanceFirstPerson(InstanceData, InstanceProperties, tri.instanceID).x & 3u) == 1u)
        receiver.Flags |= SURFACE_RECEIVER_FIRST_PERSON;
    uint index = (coord.z * Uniforms.ScreenHeight + coord.y) * Uniforms.ScreenWidth + coord.x;
    SurfaceReceiverOutput[index] = receiver;
    MaterialSurfaceOutputs.Store<TSurface.Data>(MaterialSurfaceOffsets[Uniforms.ShadingBin] + surfaceIndex * Uniforms.SurfaceDataStride, data);
    WriteClusterMotion<TVE, TDomain>(vertexEval, domain, tri, view, triangle, bary, coord);
}

#endif
