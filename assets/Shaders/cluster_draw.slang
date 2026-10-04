#include "vertex_evaluate.slang"

ByteAddressBuffer PageHeap;

#include "cluster_draw_core.slang"
#include "cluster_policy_raster_output.slang"

typedef OpaqueVisibilityEvaluate<StaticVertexEval.Vertex> DefaultVisibilityEvaluate;

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
    nointerpolation uint ClusterID : CLUSTER_ID;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

void WriteInvalidPSInput(out PSInput output)
{
    output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
    output.Normal = float3(0.0);
    output.UV = float2(0.0);
    output.Color = float3(0.0);
    output.ClusterID = 0u;
    output.RenderTargetIndex = 0u;
}

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSInput VSMain(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSInput output;

    uint visibleClusterIndex;
    uint clusterID;
    uint realInstanceID;
    uint viewIndex;
    uint lodLevel;
    uint triangleID;
    uint localVertIdx;
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint vertexCount;

    if (!DecodeDrawVertex(
            vertexID,
            instanceID,
            visibleClusterIndex,
            clusterID,
            realInstanceID,
            viewIndex,
            lodLevel,
            triangleID,
            localVertIdx,
            vertexBlobAddress,
            vertexBlobSize,
            vertexCount))
    {
        WriteInvalidPSInput(output);
        return output;
    }

    StaticVertexEval eval;
    VertexEvalContext ctx = MakeDrawVertexEvalContext(
        vertexBlobAddress,
        vertexBlobSize,
        localVertIdx,
        realInstanceID,
        GetCurrentInstanceTransform(realInstanceID));
    StaticVertexEval.Vertex vertex = EvaluateDrawVertex(eval, ctx);

    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        viewIndex);
    output.Pos = mul(
        float4(eval.getPosition(vertex), 1.0),
        view.ViewProj);
    output.ClusterID = clusterID;
    output.Normal = vertex.worldNormal;
    output.UV = vertex.uv0;
    output.RenderTargetIndex = view.TargetPolicy == 0u
        ? view.TargetViewIndex
        : 0u;

    output.Color = vertex.worldNormal * 0.5 + 0.5;

    return output;
}

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void MSVisBuffer(
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<DefaultVisibilityEvaluate.Varying> vertices[32],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    StaticVertexEval vertex;
    DefaultVisibilityEvaluate visibility;
    BuildVisBufferMesh<StaticVertexEval, DefaultVisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void MSVisBufferMeshlet(
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<DefaultVisibilityEvaluate.Varying> vertices[64],
    out indices uint3 indices[128],
    out primitives ClusterMeshPrimitive primitives[128])
{
    StaticVertexEval vertex;
    DefaultVisibilityEvaluate visibility;
    BuildVisBufferMeshlet<StaticVertexEval, DefaultVisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    return float4(input.Color, 1.0);
}

[shader("pixel")]
float4 PSOverdraw(PSInput input) : SV_TARGET
{
    return float4(0.05, 0.05, 0.05, 1.0);
}


[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> VSVisBuffer(
    uint vertexID: SV_VertexID,
    uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    DefaultVisibilityEvaluate visibility;
    return BuildVisBufferVertex<StaticVertexEval, DefaultVisibilityEvaluate>(
        eval, visibility, vertexID, instanceID);
}

[ForceInline]
void EvaluatePSVisBufferImageAtomic64(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    DefaultVisibilityEvaluate visibility;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<StaticVertexEval.Vertex, DefaultVisibilityEvaluate>(
            visibility, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
        if (!ClusterRasterTargetWindowContainsPixel(
                input.VisibleClusterIndex,
                view.TargetViewIndex,
                int2(input.Pos.xy)))
            discard;
        WriteRasterTargetDepth(
            int2(input.Pos.xy),
            view.TargetViewIndex,
            saturate(pixelResult.Depth));
        discard;
    }
    WriteVisibilityWinnerImage(
        int2(input.Pos.xy),
        view.TargetViewIndex,
        saturate(pixelResult.Depth),
        EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID));
}

[ForceInline]
void EvaluatePSVisBufferBufferAtomic64(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    DefaultVisibilityEvaluate visibility;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<StaticVertexEval.Vertex, DefaultVisibilityEvaluate>(
            visibility, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
        if (!ClusterRasterTargetWindowContainsPixel(
                input.VisibleClusterIndex,
                view.TargetViewIndex,
                int2(input.Pos.xy)))
            discard;
        WriteRasterTargetDepth(int2(input.Pos.xy), view.TargetViewIndex, saturate(pixelResult.Depth));
        discard;
    }
    WriteVisibilityWinnerBuffer(
        int2(input.Pos.xy),
        view.TargetViewIndex,
        HardwareRasterUniformData.ScreenWidth,
        HardwareRasterUniformData.ScreenHeight,
        saturate(pixelResult.Depth),
        EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID));
}

[ForceInline]
PSVisBufferOutput EvaluatePSVisBufferHardwareOnly(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    DefaultVisibilityEvaluate visibility;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<StaticVertexEval.Vertex, DefaultVisibilityEvaluate>(
            visibility, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
        if (!ClusterRasterTargetWindowContainsPixel(
                input.VisibleClusterIndex,
                view.TargetViewIndex,
                int2(input.Pos.xy)))
            discard;
        WriteRasterTargetDepth(
            int2(input.Pos.xy),
            view.TargetViewIndex,
            saturate(pixelResult.Depth));
        discard;
    }
    PSVisBufferOutput output;
    output.Visibility = EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
    output.Depth = saturate(pixelResult.Depth);
    return output;
}

[shader("pixel")]
void PSMeshVisBufferImageAtomic64(
    ClusterRasterVertexData<DefaultVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferImageAtomic64(
        MakeMeshPixelInput<DefaultVisibilityEvaluate.Varying>(vertex, primitive));
}

[shader("pixel")]
void PSMeshVisBufferBufferAtomic64(
    ClusterRasterVertexData<DefaultVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferBufferAtomic64(
        MakeMeshPixelInput<DefaultVisibilityEvaluate.Varying>(vertex, primitive));
}

[shader("pixel")]
PSVisBufferOutput PSMeshVisBufferHardwareOnly(
    ClusterRasterVertexData<DefaultVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluatePSVisBufferHardwareOnly(
        MakeMeshPixelInput<DefaultVisibilityEvaluate.Varying>(vertex, primitive));
}


[shader("pixel")]
void PSVisBufferImageAtomic64(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    EvaluatePSVisBufferImageAtomic64(input);
}

[shader("pixel")]
void PSVisBufferBufferAtomic64(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    EvaluatePSVisBufferBufferAtomic64(input);
}

[shader("pixel")]
PSVisBufferOutput PSVisBufferHardwareOnly(
    ClusterRasterPixelInput<DefaultVisibilityEvaluate.Varying> input)
{
    return EvaluatePSVisBufferHardwareOnly(input);
}
