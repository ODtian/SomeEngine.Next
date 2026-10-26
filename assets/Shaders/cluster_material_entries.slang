#ifndef CLUSTER_VERTEX_ENCODING
#define CLUSTER_VERTEX_ENCODING "someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1"
#endif

#ifndef CLUSTER_MATERIAL_ENTRIES_SLANG
#define CLUSTER_MATERIAL_ENTRIES_SLANG

#include "cluster_shade_pipeline.slang"
#include "sw_raster_core.slang"
#include "cluster_policy_raster_output.slang"
#include "cluster_draw_core.slang"

#ifndef CLUSTER_POSITION_RASTER
typedef VertexEvaluate RasterVertexEvaluate;
typedef VisibilityEvaluate RasterVisibilityEvaluate;
#endif

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareImageAtomic64(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareBufferAtomic64(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
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
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareMeshletImageAtomic64(
    uniform RasterVertexEvaluate vertex, uniform RasterVisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareMeshletBufferAtomic64(
    uniform RasterVertexEvaluate vertex, uniform RasterVisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}
#endif

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("vertex")]
[RasterMaterialInputs("evaluators")]
ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> ClusterRasterVertex(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
    uint vertexID : SV_VertexID,
    uint instanceID : SV_InstanceID)
{
    return BuildVisBufferVertex<RasterVertexEvaluate, RasterVisibilityEvaluate>(
        vertex, visibility, vertexID, instanceID);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterMesh(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertices[32],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    BuildVisBufferMesh<RasterVertexEvaluate, RasterVisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

// Opt in only when the user varying payload fits the larger mesh output.
#if defined(CLUSTER_MESHLET_RASTER)
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterMeshlet(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertices[64],
    out indices uint3 indices[128],
    out primitives ClusterMeshPrimitive primitives[128])
{
    BuildVisBufferMeshlet<RasterVertexEvaluate, RasterVisibilityEvaluate>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}
#endif

[ForceInline]
void EvaluateClusterRasterPixelImageAtomic64(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (view.TargetPolicy != 0u
        ? ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy) : uint(VisibilityWinnerImage[uint3(outputPixel.Position, view.TargetViewIndex)] >> 32u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, view.TargetPolicy);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<RasterVertexEvaluate.Vertex, RasterVisibilityEvaluate>(visibility, input, view,
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
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (view.TargetPolicy != 0u
        ? ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy) : VisibilityWinnerBuffer.Load(((view.TargetViewIndex * HardwareRasterUniformData.ScreenHeight + uint(outputPixel.Position.y)) * HardwareRasterUniformData.ScreenWidth + uint(outputPixel.Position.x)) * 8u + 4u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, view.TargetPolicy);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<RasterVertexEvaluate.Vertex, RasterVisibilityEvaluate>(visibility, input, view,
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
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy, accepted);
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, view.TargetPolicy);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<RasterVertexEvaluate.Vertex, RasterVisibilityEvaluate>(visibility, input, view,
            encodedDepth);
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

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
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


// Cooked variants retain the public opaque evaluator.
#if defined(CLUSTER_SURFACE_MODES)
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterShadeMode<let debugMode : uint>(uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform SurfaceEvaluate surface, uint3 tid : SV_DispatchThreadID)
{
    CSShade(vertex, domain, surface, tid, debugMode);
}
#endif

#if defined(CLUSTER_FORWARD_SHADING)
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterShadowRequests<let visibilityDepth : uint, let debugMode : uint>(
    uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform SurfaceEvaluate surface, uint3 tid : SV_DispatchThreadID)
{
    CSRequestShadowPages(vertex, domain, surface, tid, visibilityDepth);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void ClusterForwardShade<let visibilityDepth : uint, let debugMode : uint>(
    uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform SurfaceEvaluate surface, uint3 tid : SV_DispatchThreadID)
{
    CSForwardShade(vertex, domain, surface, tid, visibilityDepth, debugMode);
}
#endif

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterMeshPixelImageAtomic64(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelImageAtomic64(
        visibility,
        MakeMeshPixelInput<RasterVisibilityEvaluate.Varying>(vertex, primitive));
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterMeshPixelBufferAtomic64(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelBufferAtomic64(
        visibility,
        MakeMeshPixelInput<RasterVisibilityEvaluate.Varying>(vertex, primitive));
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
PSVisBufferOutput ClusterRasterMeshPixelHardwareOnly(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluateClusterRasterPixelHardwareOnly(
        visibility,
        MakeMeshPixelInput<RasterVisibilityEvaluate.Varying>(vertex, primitive));
}


[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterPixelImageAtomic64(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelImageAtomic64(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterPixelBufferAtomic64(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelBufferAtomic64(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
PSVisBufferOutput ClusterRasterPixelHardwareOnly(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    return EvaluateClusterRasterPixelHardwareOnly(visibility, input);
}

// Optional depth-target entries leave the primary visibility output unused.
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareTarget(
    uniform RasterVertexEvaluate vertex, uniform RasterVisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputTarget output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex);
}

#if defined(CLUSTER_SOFTWARE_MESHLET_RASTER)
[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareMeshletTarget(
    uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility,
    uint3 groupID : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputTarget output;
    SWRasterKernel(vertex, visibility, output, groupID, groupThreadIndex, true);
}
#endif

[ForceInline]
void EvaluateClusterRasterPixelTarget(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy, accepted);
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, view.TargetPolicy);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult result =
        EvaluateHardwareRasterPixel<RasterVertexEvaluate.Vertex, RasterVisibilityEvaluate>(visibility, input, view,
            encodedDepth);
    if (!accepted || result.Visible == 0u)
        discard;
    WriteRasterTargetPixelDepth(uint2(outputPixel.Position), result.Depth, outputPixel.DepthCacheClass);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterPixelTarget(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> input)
{
    EvaluateClusterRasterPixelTarget(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
[RasterMaterialInputs("evaluators")]
void ClusterRasterMeshPixelTarget(
    uniform RasterVisibilityEvaluate visibility,
    ClusterRasterVertexData<RasterVisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluateClusterRasterPixelTarget(visibility,
        MakeMeshPixelInput<RasterVisibilityEvaluate.Varying>(vertex, primitive));
}

#endif
