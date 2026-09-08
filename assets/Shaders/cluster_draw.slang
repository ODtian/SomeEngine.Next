#include "vertex_evaluate.slang"

ByteAddressBuffer PageHeap;

#include "cluster_draw_core.slang"
#include "cluster_policy_raster_output.slang"

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
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
    output.Normal = eval.getNormal(vertex);
    output.UV = eval.getTexCoord0(vertex);
    output.RenderTargetIndex = view.TargetPolicy == 0u
        ? view.TargetViewIndex
        : 0u;

    output.Color = eval.getNormal(vertex) * 0.5 + 0.5;

    return output;
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void MSVisBuffer(
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterMeshVertex vertices[96],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    StaticVertexEval vertex;
    BuildVisBufferMesh(vertex, groupId, lane, vertices, indices, primitives);
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


[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSVisBufferInput VSVisBuffer(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    return BuildVisBufferVertex(eval, vertexID, instanceID);
}

[ForceInline]
void EvaluatePSVisBufferImageAtomic64(PSVisBufferInput input)
{
    OpaquePixelEvaluate pixel;
    PixelEvaluateResult pixelResult = EvaluateHardwareRasterPixel(pixel, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
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
void EvaluatePSVisBufferBufferAtomic64(PSVisBufferInput input)
{
    OpaquePixelEvaluate pixel;
    PixelEvaluateResult pixelResult = EvaluateHardwareRasterPixel(pixel, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
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
PSVisBufferOutput EvaluatePSVisBufferHardwareOnly(PSVisBufferInput input)
{
    OpaquePixelEvaluate pixel;
    PixelEvaluateResult pixelResult = EvaluateHardwareRasterPixel(pixel, input);
    if (pixelResult.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
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
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferImageAtomic64(MakeMeshPixelInput(vertex, primitive));
}

[shader("pixel")]
void PSMeshVisBufferBufferAtomic64(
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferBufferAtomic64(MakeMeshPixelInput(vertex, primitive));
}

[shader("pixel")]
PSVisBufferOutput PSMeshVisBufferHardwareOnly(
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluatePSVisBufferHardwareOnly(MakeMeshPixelInput(vertex, primitive));
}


[shader("pixel")]
void PSVisBufferImageAtomic64(
    PSVisBufferInput input)
{
    EvaluatePSVisBufferImageAtomic64(input);
}

[shader("pixel")]
void PSVisBufferBufferAtomic64(
    PSVisBufferInput input)
{
    EvaluatePSVisBufferBufferAtomic64(input);
}

[shader("pixel")]
PSVisBufferOutput PSVisBufferHardwareOnly(
    PSVisBufferInput input)
{
    return EvaluatePSVisBufferHardwareOnly(input);
}
