#ifndef CLUSTER_MATERIAL_ENTRIES_SLANG
#define CLUSTER_MATERIAL_ENTRIES_SLANG

#include "sw_raster_core.slang"
#include "cluster_policy_raster_output.slang"
#include "cluster_draw_core.slang"

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareImageAtomic64(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareBufferAtomic64(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
ClusterRasterPixelInput<VisibilityEvaluate.Varying> ClusterRasterVertex(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint vertexID : SV_VertexID,
    uint instanceID : SV_InstanceID)
{
    return BuildVisBufferVertex<VertexEvaluate, VisibilityEvaluate>(
        vertex, visibility, vertexID, instanceID);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void ClusterRasterMesh(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<VisibilityEvaluate.Varying> vertices[32],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    BuildVisBufferMesh<VertexEvaluate, VisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

[ForceInline]
void EvaluateClusterRasterPixelImageAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input);
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
void EvaluateClusterRasterPixelBufferAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input);
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
PSVisBufferOutput EvaluateClusterRasterPixelHardwareOnly(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input);
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
    uniform VisibilityEvaluate visibility,
    ClusterRasterVertexData<VisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelImageAtomic64(
        visibility,
        MakeMeshPixelInput<VisibilityEvaluate.Varying>(vertex, primitive));
}

[shader("pixel")]
void ClusterRasterMeshPixelBufferAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterVertexData<VisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelBufferAtomic64(
        visibility,
        MakeMeshPixelInput<VisibilityEvaluate.Varying>(vertex, primitive));
}

[shader("pixel")]
PSVisBufferOutput ClusterRasterMeshPixelHardwareOnly(
    uniform VisibilityEvaluate visibility,
    ClusterRasterVertexData<VisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluateClusterRasterPixelHardwareOnly(
        visibility,
        MakeMeshPixelInput<VisibilityEvaluate.Varying>(vertex, primitive));
}


[shader("pixel")]
void ClusterRasterPixelImageAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelImageAtomic64(visibility, input);
}

[shader("pixel")]
void ClusterRasterPixelBufferAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelBufferAtomic64(visibility, input);
}

[shader("pixel")]
PSVisBufferOutput ClusterRasterPixelHardwareOnly(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    return EvaluateClusterRasterPixelHardwareOnly(visibility, input);
}

#endif
