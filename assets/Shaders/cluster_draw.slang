#include "raster_faces.slang"
__exported import vertex_evaluate_interfaces;

ByteAddressBuffer PageHeap;

#include "cluster_draw_core.slang"
#include "cluster_policy_raster_output.slang"


[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void MSVisBuffer<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE vertexEvaluate, uniform TVisibility visibilityEvaluate,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<TVisibility.Varying> vertices[32],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    TVE vertex = vertexEvaluate;
    TVisibility visibility = visibilityEvaluate;
    BuildVisBufferMesh<TVE, TVisibility>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void MSVisBufferMeshlet<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE vertexEvaluate, uniform TVisibility visibilityEvaluate,
    uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<TVisibility.Varying> vertices[64],
    out indices uint3 indices[128],
    out primitives ClusterMeshPrimitive primitives[128])
{
    TVE vertex = vertexEvaluate;
    TVisibility visibility = visibilityEvaluate;
    BuildVisBufferMeshlet<TVE, TVisibility>(
        vertex, visibility, groupId, lane, vertices, indices, primitives);
}

[shader("vertex")]
ClusterRasterPixelInput<TVisibility.Varying> VSVisBuffer<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE vertexEvaluate, uniform TVisibility visibilityEvaluate,
    uint vertexID: SV_VertexID,
    uint instanceID: SV_InstanceID)
{
    TVE eval = vertexEvaluate;
    TVisibility visibility = visibilityEvaluate;
    return BuildVisBufferVertex<TVE, TVisibility>(
        eval, visibility, vertexID, instanceID);
}

[ForceInline]
void EvaluatePSVisBufferImageAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel<ClusterRasterOutputImageAtomic64>(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (uint(VisibilityWinnerImage[uint3(outputPixel.Position, view.TargetViewIndex)] >> 32u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, 0u);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<TVertex, TVisibility>(
            visibility, input, view, encodedDepth);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    WriteVisibilityWinnerImage(
        int2(input.Pos.xy),
        view.TargetViewIndex,
        saturate(pixelResult.Depth),
        EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID));
}

[ForceInline]
void EvaluatePSVisBufferBufferAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel<ClusterRasterOutputImageAtomic64>(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (VisibilityWinnerBuffer.Load(((view.TargetViewIndex * HardwareRasterUniformData.ScreenHeight + uint(outputPixel.Position.y)) * HardwareRasterUniformData.ScreenWidth + uint(outputPixel.Position.x)) * 8u + 4u)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, 0u);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<TVertex, TVisibility>(
            visibility, input, view, encodedDepth);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    WriteVisibilityWinnerBuffer(
        int2(input.Pos.xy),
        view.TargetViewIndex,
        HardwareRasterUniformData.ScreenWidth,
        HardwareRasterUniformData.ScreenHeight,
        saturate(pixelResult.Depth),
        EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID));
}

[ForceInline]
void EvaluatePSVisBufferNonAtomic<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel<ClusterRasterOutputImageAtomic64>(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    uint encodedDepth = accepted ? (ReadVisibilityNonAtomicDepth(outputPixel.Position, view.TargetViewIndex, HardwareRasterUniformData.ScreenWidth, HardwareRasterUniformData.ScreenHeight)) : 0u;
    float rasterDepth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    accepted = accepted && ClusterRasterDepthAccepts(rasterDepth, encodedDepth,
        HardwareRasterDispatchUniformData.EarlyDepthEnabled, 0u);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<TVertex, TVisibility>(
            visibility, input, view, encodedDepth);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    WriteVisibilityNonAtomic(
        int2(input.Pos.xy),
        view.TargetViewIndex,
        HardwareRasterUniformData.ScreenWidth,
        HardwareRasterUniformData.ScreenHeight,
        saturate(pixelResult.Depth),
        EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID));
}

[ForceInline]
PSVisBufferOutput EvaluatePSVisBufferHardwareOnly<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, input.ViewIndex);
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel<ClusterRasterOutputImageAtomic64>(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelResult pixelResult =
        EvaluateHardwareRasterPixel<TVertex, TVisibility>(
            visibility, input, view, 0u);
    if (!accepted || pixelResult.Visible == 0u)
        discard;
    PSVisBufferOutput output;
    output.Visibility = EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
    output.Depth = saturate(pixelResult.Depth);
    return output;
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSMeshVisBufferImageAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterVertexData<TVisibility.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferImageAtomic64<TVertex, TVisibility>(visibility,
        MakeMeshPixelInput<TVisibility.Varying>(vertex, primitive));
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSMeshVisBufferBufferAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterVertexData<TVisibility.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferBufferAtomic64<TVertex, TVisibility>(visibility,
        MakeMeshPixelInput<TVisibility.Varying>(vertex, primitive));
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSMeshVisBufferNonAtomic<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterVertexData<TVisibility.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    EvaluatePSVisBufferNonAtomic<TVertex, TVisibility>(visibility,
        MakeMeshPixelInput<TVisibility.Varying>(vertex, primitive));
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
PSVisBufferOutput PSMeshVisBufferHardwareOnly<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterVertexData<TVisibility.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    return EvaluatePSVisBufferHardwareOnly<TVertex, TVisibility>(visibility,
        MakeMeshPixelInput<TVisibility.Varying>(vertex, primitive));
}


[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSVisBufferImageAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    EvaluatePSVisBufferImageAtomic64<TVertex, TVisibility>(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSVisBufferBufferAtomic64<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    EvaluatePSVisBufferBufferAtomic64<TVertex, TVisibility>(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
void PSVisBufferNonAtomic<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    EvaluatePSVisBufferNonAtomic<TVertex, TVisibility>(visibility, input);
}

[RasterFaces(CLUSTER_RASTER_FACES, CLUSTER_TARGET_RASTER_FACES)]
[shader("pixel")]
PSVisBufferOutput PSVisBufferHardwareOnly<TVertex, TVisibility : IVisibilityEvaluate<TVertex>>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
{
    return EvaluatePSVisBufferHardwareOnly<TVertex, TVisibility>(visibility, input);
}
