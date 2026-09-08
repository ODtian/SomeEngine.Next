#ifndef CLUSTER_MATERIAL_ENTRIES_SLANG
#define CLUSTER_MATERIAL_ENTRIES_SLANG

#include "sw_raster_core.slang"
#include "cluster_policy_raster_output.slang"
#include "cluster_draw_core.slang"

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
void ClusterRasterSoftwareImageAtomic64(
    uniform VertexEvaluate vertex,
    uniform PixelEvaluate pixel,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel(vertex, pixel, output, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
void ClusterRasterSoftwareBufferAtomic64(
    uniform VertexEvaluate vertex,
    uniform PixelEvaluate pixel,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(vertex, pixel, output, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSVisBufferInput ClusterRasterVertex(
    uniform VertexEvaluate vertex,
    uint vertexID : SV_VertexID,
    uint instanceID : SV_InstanceID)
{
    return BuildVisBufferVertex(vertex, vertexID, instanceID);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void ClusterRasterMesh(
    uniform VertexEvaluate vertex,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterMeshVertex vertices[96],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    BuildVisBufferMesh(vertex, groupId, lane, vertices, indices, primitives);
}

[ForceInline]
void EvaluateClusterRasterPixelImageAtomic64(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
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
void EvaluateClusterRasterPixelBufferAtomic64(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
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
PSVisBufferOutput EvaluateClusterRasterPixelHardwareOnly(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
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

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterShade(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform SurfaceEvaluate surface,
    uint3 tid : SV_DispatchThreadID)
{
    CSShade(vertex, domain, surface, tid);
}


[shader("pixel")]
void ClusterRasterMeshPixelImageAtomic64(
    uniform PixelEvaluate pixel,
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelImageAtomic64(pixel, MakeMeshPixelInput(vertex, primitive));
}

[shader("pixel")]
void ClusterRasterMeshPixelBufferAtomic64(
    uniform PixelEvaluate pixel,
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelBufferAtomic64(pixel, MakeMeshPixelInput(vertex, primitive));
}

[shader("pixel")]
PSVisBufferOutput ClusterRasterMeshPixelHardwareOnly(
    uniform PixelEvaluate pixel,
    ClusterMeshVertex vertex, [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluateClusterRasterPixelHardwareOnly(pixel, MakeMeshPixelInput(vertex, primitive));
}


[shader("pixel")]
void ClusterRasterPixelImageAtomic64(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
    EvaluateClusterRasterPixelImageAtomic64(pixel, input);
}

[shader("pixel")]
void ClusterRasterPixelBufferAtomic64(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
    EvaluateClusterRasterPixelBufferAtomic64(pixel, input);
}

[shader("pixel")]
PSVisBufferOutput ClusterRasterPixelHardwareOnly(
    uniform PixelEvaluate pixel,
    PSVisBufferInput input)
{
    return EvaluateClusterRasterPixelHardwareOnly(pixel, input);
}

#endif
