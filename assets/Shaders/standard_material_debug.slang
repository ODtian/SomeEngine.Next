__exported import vertex_evaluate;
#include "raster_faces.slang"
ByteAddressBuffer PageHeap;
#include "cluster_draw_core.slang"

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
        GetCurrentInstanceTransform(realInstanceID), visibleClusterIndex);
    StaticVertexEval.Vertex vertex = EvaluateDrawVertex(eval, ctx);

    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        viewIndex);
    view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, realInstanceID, view);
    output.Pos = mul(
        float4(ClusterPositionForView(eval.getPosition(vertex), view), 1.0),
        view.ViewProj);
    output.ClusterID = clusterID;
    output.Normal = vertex.worldNormal;
    output.UV = vertex.uv0;
    output.RenderTargetIndex = view.TargetViewIndex;

    output.Color = vertex.worldNormal * 0.5 + 0.5;

    return output;
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    return float4(input.Color, 1.0);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
float4 PSOverdraw(PSInput input) : SV_TARGET
{
    return float4(0.05, 0.05, 0.05, 1.0);
}


