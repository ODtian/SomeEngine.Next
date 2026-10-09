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

#if defined(CLUSTER_SOFTWARE_MESHLET_RASTER)
// Opt in when keeping two evaluated user vertices per lane fits the material's
// register budget. The same entry still accepts individual vertex-reuse batches.
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareMeshletImageAtomic64(
    uniform VertexEvaluate vertex, uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareMeshletBufferAtomic64(
    uniform VertexEvaluate vertex, uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}
#endif

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

// Opt in only when the user varying payload fits the larger mesh output.
#if defined(CLUSTER_MESHLET_RASTER)
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void ClusterRasterMeshlet(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<VisibilityEvaluate.Varying> vertices[64],
    out indices uint3 indices[128],
    out primitives ClusterMeshPrimitive primitives[128])
{
    BuildVisBufferMeshlet<VertexEvaluate, VisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}
#endif

[ForceInline]
void EvaluateClusterRasterPixelImageAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (view.TargetPolicy != 0u
        ? ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy) : uint(VisibilityWinnerImage[uint3(outputPixel.Position, view.TargetViewIndex)] >> 32u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    if (HardwareRasterDispatchUniformData.EarlyDepthEnabled != 0u)
        accepted = accepted && asuint(1.0 - saturate(rasterDepth)) >= encodedDepth;
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input, view,
            encodedDepth);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    if (view.TargetPolicy != 0u)
    {
        WriteRasterTargetPixelDepth(uint2(outputPixel.Position), pixelResult.Depth, outputPixel.DepthCacheClass);
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
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (view.TargetPolicy != 0u
        ? ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy) : VisibilityWinnerBuffer.Load(((view.TargetViewIndex * HardwareRasterUniformData.ScreenHeight + uint(outputPixel.Position.y)) * HardwareRasterUniformData.ScreenWidth + uint(outputPixel.Position.x)) * 8u + 4u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    if (HardwareRasterDispatchUniformData.EarlyDepthEnabled != 0u)
        accepted = accepted && asuint(1.0 - saturate(rasterDepth)) >= encodedDepth;
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input, view,
            encodedDepth);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    if (view.TargetPolicy != 0u)
    {
        WriteRasterTargetPixelDepth(uint2(outputPixel.Position), pixelResult.Depth, outputPixel.DepthCacheClass);
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
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input, view,
            ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy, accepted));
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    if (view.TargetPolicy != 0u)
    {
        WriteRasterTargetPixelDepth(uint2(outputPixel.Position), pixelResult.Depth, outputPixel.DepthCacheClass);
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

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterSurfaceData(uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform SurfaceEvaluate surface, uint3 tid : SV_DispatchThreadID)
{
    CSEvaluateSurface(vertex, domain, surface, tid);
}


[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
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

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
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

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
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


[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void ClusterRasterPixelImageAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelImageAtomic64(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void ClusterRasterPixelBufferAtomic64(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelBufferAtomic64(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
PSVisBufferOutput ClusterRasterPixelHardwareOnly(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    return EvaluateClusterRasterPixelHardwareOnly(visibility, input);
}

// Optional depth-target entries leave the primary visibility output unused.
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareTarget(
    uniform VertexEvaluate vertex, uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputTarget output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex);
}

#if defined(CLUSTER_SOFTWARE_MESHLET_RASTER)
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterRasterSoftwareMeshletTarget(
    uniform VertexEvaluate vertex,
    uniform VisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputTarget output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}
#endif

[ForceInline]
void EvaluateClusterRasterPixelTarget(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    if (!accepted)
        discard;
    VisibilityPixelResult result =
        EvaluateHardwareRasterPixel<VertexEvaluate.Vertex, VisibilityEvaluate>(visibility, input, view,
            ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy, accepted));
    if (!accepted || result.Visible == 0u)
        discard;
    WriteRasterTargetPixelDepth(uint2(outputPixel.Position), result.Depth, outputPixel.DepthCacheClass);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void ClusterRasterPixelTarget(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelTarget(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void ClusterRasterMeshPixelTarget(
    uniform VisibilityEvaluate visibility,
    ClusterRasterVertexData<VisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelTarget(visibility,
        MakeMeshPixelInput<VisibilityEvaluate.Varying>(vertex, primitive));
}

#endif
