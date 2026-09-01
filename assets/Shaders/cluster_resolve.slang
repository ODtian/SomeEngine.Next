#include "vertex_evaluate.slang"

struct ResolveUniforms
{
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    uint Pad0;
};

ConstantBuffer<ResolveUniforms> Uniforms;

Texture2DArray<uint> VisBuffer;
Texture2DArray<float> DepthBuffer;

StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer PageHeap;

RWTexture2DArray<float4> OutputColor;

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(8, 8, 1)]
void CSResolve(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint3 pixelCoord = dispatchThreadID;

    if (pixelCoord.x >= Uniforms.ScreenWidth || pixelCoord.y >= Uniforms.ScreenHeight)
        return;

    uint visData = VisBuffer[pixelCoord];

    // 0 means no geometry (clear value)
    if (IsVisBufferEmpty(visData))
    {
        OutputColor[pixelCoord] = float4(0, 0, 0, 1);
        return;
    }

    uint visibleClusterIndex = DecodeVisBufferVisibleClusterIndex(visData);
    uint triangleID = DecodeVisBufferTriangleID(visData);

    // Look up the draw request
    DrawRequest req = VisibleClusters[visibleClusterIndex];
    if (req.ViewIndex != pixelCoord.z)
        return;
    uint pageOffset = req.PageOffset;
    uint clusterID = req.ClusterID;
    uint realInstanceID = req.InstanceID;

    // Load cluster info
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint tStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        pageOffset,
        clusterID,
        vertexBlobAddress,
        vertexBlobSize,
        tStart,
        packedCounts);

    uint lodLevel = (packedCounts >> 16) & 0xFF;

    // Load header offsets
    uint idxOffset = PageHeap.Load(pageOffset + 24u);

    // Fetch 3 vertex indices for this triangle
    uint vi0 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 0);
    uint vi1 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 1);
    uint vi2 = FetchVertexIndex(PageHeap, pageOffset, idxOffset, tStart, triangleID, 2);

    RenderInstanceTransform t = GetCurrentInstanceTransform(realInstanceID);
    VertexEvalContext context;
    context.pageHeap = PageHeap;
    context.vertexBlobAddress = vertexBlobAddress;
    context.vertexBlobSize = vertexBlobSize;
    context.instanceID = realInstanceID;
    context.transform = t;
    context.IsPreviousFrame = 0u;

    StaticVertexEval evaluator;
    context.localVertexIndex = vi0;
    StaticVertexEval.DeformedVertex v0 = evaluator.evaluate(context);
    context.localVertexIndex = vi1;
    StaticVertexEval.DeformedVertex v1 = evaluator.evaluate(context);
    context.localVertexIndex = vi2;
    StaticVertexEval.DeformedVertex v2 = evaluator.evaluate(context);
    float3 wp0 = evaluator.getPosition(v0);
    float3 wp1 = evaluator.getPosition(v1);
    float3 wp2 = evaluator.getPosition(v2);

    // Compute face normal from world-space triangle
    float3 edge1 = wp1 - wp0;
    float3 edge2 = wp2 - wp0;
    float3 faceNormal = normalize(cross(edge1, edge2));

    // Output based on debug mode
    float3 color;
    if (Uniforms.DebugMode == 1) // ClusterID visualization
    {
        uint h = PCGHash(clusterID);
        color = ColorFromHash(h);
    }
    else if (Uniforms.DebugMode == 2) // LOD visualization
    {
        float l = float(lodLevel) / 10.0;
        color = float3(l, 1.0 - l, 0.0);
    }
    else if (Uniforms.DebugMode == 0) // Normal shading (face normal fallback)
    {
        color = faceNormal * 0.5 + 0.5;
    }
    else
    {
        // Material debug modes are handled by the material shade pass.
        // Don't write here; let the shade pass handle them.
        return;
    }

    OutputColor[pixelCoord] = float4(color, 1.0);
}
