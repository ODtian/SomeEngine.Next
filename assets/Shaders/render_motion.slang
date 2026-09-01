#include "vertex_evaluate.slang"
#include "cluster_view.slang"

struct MotionFeatureUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint ViewCapacity;
    uint Enabled;
};

ConstantBuffer<MotionFeatureUniforms> MotionUniforms;
StructuredBuffer<ClusterViewUniforms> Views;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;
ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;
RWTexture2DArray<float2> OutputMotionVectors;

static const uint MOTION_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;

struct MotionTriangle
{
    uint visibleClusterIndex;
    uint triangleID;
    uint instanceID;
    uint viewIndex;
    uint vi0;
    uint vi1;
    uint vi2;
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint vertexCount;
};

bool DecodeMotionTriangle(uint3 pixelCoord, out MotionTriangle tri)
{
    tri = {};
    uint visData = VisBuffer[pixelCoord];
    if (IsVisBufferEmpty(visData))
        return false;

    tri.visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    tri.triangleID = DecodeVisBufferTriangleID(visData);
    DrawRequest request = VisibleClusters[tri.visibleClusterIndex];
    if (request.ViewIndex != pixelCoord.z)
        return false;

    tri.instanceID = request.InstanceID;
    tri.viewIndex = request.ViewIndex;
    uint triangleStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        request.PageOffset,
        request.ClusterID,
        tri.vertexBlobAddress,
        tri.vertexBlobSize,
        triangleStart,
        packedCounts);
    tri.vertexCount = packedCounts & 0xFFu;
    uint indexOffset = PageHeap.Load(request.PageOffset + 24u);
    tri.vi0 = FetchVertexIndex(
        PageHeap,
        request.PageOffset,
        indexOffset,
        triangleStart,
        tri.triangleID,
        0u);
    tri.vi1 = FetchVertexIndex(
        PageHeap,
        request.PageOffset,
        indexOffset,
        triangleStart,
        tri.triangleID,
        1u);
    tri.vi2 = FetchVertexIndex(
        PageHeap,
        request.PageOffset,
        indexOffset,
        triangleStart,
        tri.triangleID,
        2u);
    return true;
}

VertexEvalContext MakeMotionContext(
    MotionTriangle tri,
    RenderInstanceTransform transform,
    uint previous,
    uint localVertex)
{
    VertexEvalContext context;
    context.pageHeap = PageHeap;
    context.vertexBlobAddress = tri.vertexBlobAddress;
    context.vertexBlobSize = tri.vertexBlobSize;
    context.localVertexIndex = localVertex;
    context.instanceID = tri.instanceID;
    context.transform = transform;
    context.IsPreviousFrame = previous;
    return context;
}

void LoadMotionVertices(
    StaticVertexEval evaluator,
    MotionTriangle tri,
    out StaticVertexEval.DeformedVertex v0,
    out StaticVertexEval.DeformedVertex v1,
    out StaticVertexEval.DeformedVertex v2,
    out StaticVertexEval.DeformedVertex previousV0,
    out StaticVertexEval.DeformedVertex previousV1,
    out StaticVertexEval.DeformedVertex previousV2)
{
    uint cacheBase = CacheOffsets[tri.visibleClusterIndex];
    if (cacheBase < MOTION_CACHE_OFFSET_INVALID)
    {
        v0 = evaluator.readCache(DeformCache, cacheBase, tri.vertexCount, tri.vi0);
        v1 = evaluator.readCache(DeformCache, cacheBase, tri.vertexCount, tri.vi1);
        v2 = evaluator.readCache(DeformCache, cacheBase, tri.vertexCount, tri.vi2);
        previousV0 = evaluator.readPreviousCache(DeformCache, cacheBase, tri.vertexCount, tri.vi0);
        previousV1 = evaluator.readPreviousCache(DeformCache, cacheBase, tri.vertexCount, tri.vi1);
        previousV2 = evaluator.readPreviousCache(DeformCache, cacheBase, tri.vertexCount, tri.vi2);
        return;
    }

    RenderInstanceTransform currentTransform = GetCurrentInstanceTransform(tri.instanceID);
    RenderInstanceTransform previousTransform = GetPreviousInstanceTransform(tri.instanceID);
    v0 = evaluator.evaluate(MakeMotionContext(tri, currentTransform, 0u, tri.vi0));
    v1 = evaluator.evaluate(MakeMotionContext(tri, currentTransform, 0u, tri.vi1));
    v2 = evaluator.evaluate(MakeMotionContext(tri, currentTransform, 0u, tri.vi2));
    previousV0 = evaluator.evaluate(MakeMotionContext(tri, previousTransform, 1u, tri.vi0));
    previousV1 = evaluator.evaluate(MakeMotionContext(tri, previousTransform, 1u, tri.vi1));
    previousV2 = evaluator.evaluate(MakeMotionContext(tri, previousTransform, 1u, tri.vi2));
}

float2 MotionClipToUv(float4 clip)
{
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 1.0 - (ndc.y * 0.5 + 0.5));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void BuildMotionVectors(uint3 pixelCoord : SV_DispatchThreadID)
{
    if (pixelCoord.x >= MotionUniforms.ScreenWidth ||
        pixelCoord.y >= MotionUniforms.ScreenHeight ||
        pixelCoord.z >= MotionUniforms.ViewCapacity)
        return;
    if (MotionUniforms.Enabled == 0u)
    {
        OutputMotionVectors[pixelCoord] = float2(0.0);
        return;
    }

    MotionTriangle tri;
    if (!DecodeMotionTriangle(pixelCoord, tri))
    {
        OutputMotionVectors[pixelCoord] = float2(0.0);
        return;
    }
    ClusterViewUniforms view = Views[tri.viewIndex];
    if (view.HasPrevHistory == 0u)
    {
        OutputMotionVectors[pixelCoord] = float2(0.0);
        return;
    }

    StaticVertexEval evaluator;
    StaticVertexEval.DeformedVertex v0;
    StaticVertexEval.DeformedVertex v1;
    StaticVertexEval.DeformedVertex v2;
    StaticVertexEval.DeformedVertex previousV0;
    StaticVertexEval.DeformedVertex previousV1;
    StaticVertexEval.DeformedVertex previousV2;
    LoadMotionVertices(
        evaluator,
        tri,
        v0,
        v1,
        v2,
        previousV0,
        previousV1,
        previousV2);

    float3 p0 = evaluator.getPosition(v0);
    float3 p1 = evaluator.getPosition(v1);
    float3 p2 = evaluator.getPosition(v2);
    ScreenProjection projection0 = ProjectToScreenProjection(
        p0,
        view.ViewProj,
        MotionUniforms.ScreenWidth,
        MotionUniforms.ScreenHeight);
    ScreenProjection projection1 = ProjectToScreenProjection(
        p1,
        view.ViewProj,
        MotionUniforms.ScreenWidth,
        MotionUniforms.ScreenHeight);
    ScreenProjection projection2 = ProjectToScreenProjection(
        p2,
        view.ViewProj,
        MotionUniforms.ScreenWidth,
        MotionUniforms.ScreenHeight);
    float3 bary = ComputePerspectiveBarycentric(
        float2(pixelCoord.xy) + 0.5,
        projection0,
        projection1,
        projection2);
    float3 currentPosition = bary.x * p0 + bary.y * p1 + bary.z * p2;
    float3 previousPosition =
        bary.x * evaluator.getPosition(previousV0) +
        bary.y * evaluator.getPosition(previousV1) +
        bary.z * evaluator.getPosition(previousV2);
    float4 currentClip = mul(float4(currentPosition, 1.0), view.ViewProj);
    float4 previousClip = mul(float4(previousPosition, 1.0), view.PrevViewProj);
    OutputMotionVectors[pixelCoord] =
        abs(currentClip.w) < 1e-6 || abs(previousClip.w) < 1e-6
            ? float2(0.0)
            : MotionClipToUv(currentClip) - MotionClipToUv(previousClip);
}
