#ifndef CLUSTER_TESSELLATION_ENTRIES_SLANG
#define CLUSTER_TESSELLATION_ENTRIES_SLANG

#include "cluster_tessellation_core.slang"
#include "cluster_policy_raster_output.slang"

// User material entries declare their domain's bound. Linear/identity domains
// can select finite; displaced domains retain the unbounded default unless proven.
#ifndef CLUSTER_TESSELLATION_BOUNDS
#define CLUSTER_TESSELLATION_BOUNDS "unbounded"
#endif

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("compute")]
[numthreads(32, 1, 1)]
void ClusterTessellationSeed(
    uniform VertexEvaluate vertex,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    SeedTessellationRasterBin(vertex, groupId, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("compute")]
[numthreads(32, 1, 1)]
void ClusterTessellationSplit(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    SplitTessellationPatches(vertex, domain, groupId, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterTessellationDiceImageAtomic64(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterDicedRasterOutput<ClusterRasterOutputImageAtomic64> output;
    DiceTessellationPatches(
        vertex,
        domain,
        visibility,
        output,
        groupId,
        groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterTessellationDiceBufferAtomic64(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterDicedRasterOutput<ClusterRasterOutputBufferAtomic64> output;
    output.Primary.Width = TessellationUniformData.ScreenWidth;
    output.Primary.Height = TessellationUniformData.ScreenHeight;
    DiceTessellationPatches(vertex, domain, visibility, output, groupId, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("vertex")]
ClusterRasterPixelInput<VisibilityEvaluate.Varying> ClusterTessellationHardwareVertex(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uint vertexID : SV_VertexID,
    uint patchIndex : ATTRIBUTE0)
{
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> output = {};
    output.Pos = asfloat(uint4(0x7FC00000u, 0x7FC00000u, 0x7FC00000u, 0x7FC00000u));
    if (patchIndex >= TessellationUniformData.MaxPatchItems)
        return output;
    TessellationPatch patch = DecodeTessellationFinalPatch(
        TessellationRasterPatches[patchIndex]);
    if (TessellationPatchRasterBin(patch) != TessellationUniformData.CurrentBin)
        return output;
    uint factor = min(
        max(TessellationPatchEdgeFactors(patch).x,
            max(TessellationPatchEdgeFactors(patch).y,
                TessellationPatchEdgeFactors(patch).z)),
        TESS_PATTERN_MAX_FACTOR);
    if (vertexID >= TessellationGridVertexCount(factor))
        return output;

    uint sourceTriangle = TessellationPatchTriangle(patch);
    if (!HasTessellationTriangle(patch.VisibleClusterIndex, sourceTriangle))
        return output;
    VisibilityEvaluate.Varying sourceVaryings[3];
    DomainEvaluate.TriangleData triangle =
        LoadTessellationTriangleWithVisibility(
            vertex,
            domain,
            visibility,
            patch.VisibleClusterIndex,
            sourceTriangle,
            sourceVaryings);
    float3 localBary = TessellationGridBarycentric(
        factor,
        TessellationPatchEdgeFactors(patch),
        vertexID);
    float3 sourceBary = TransformPatchBarycentric(
        DecodeTessellationPatchBarycentric(patch, 0u),
        DecodeTessellationPatchBarycentric(patch, 1u),
        DecodeTessellationPatchBarycentric(patch, 2u),
        localBary);
    float3 position = domain.position(triangle, sourceBary);
    uint viewIndex = DrawRequestViewIndex(
        TessellationVisibleClusters[patch.VisibleClusterIndex]);
    ClusterViewUniforms view = LoadClusterView(
        TessellationViews,
        TessellationPolicyViews,
        viewIndex);
    view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, VisibleClusterRequest(TessellationVisibleClusters[patch.VisibleClusterIndex]).InstanceID, view);
    output.Pos = mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
    output.ClipDepth = CalculateClusterRasterDepthInput(position, output.Pos, view);
    output.Pos = ClusterHardwareRasterPosition(output.Pos, patch.VisibleClusterIndex, view,
        ClusterRasterTargetViewport(view));
    output.Varying = visibility.interpolateVertex(
        sourceVaryings[0],
        sourceVaryings[1],
        sourceVaryings[2],
        sourceBary);
    output.VisibleClusterIndex = patch.VisibleClusterIndex;
    output.TriangleID = TessellationPatchTriangle(patch);
    output.ViewIndex = viewIndex;
    output.RenderTargetIndex = view.TargetPolicy == 0u ? view.TargetViewIndex : 0u;
    return output;
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds(CLUSTER_TESSELLATION_BOUNDS)]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void ClusterTessellationHardwareMesh(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility,
    uint3 groupId : SV_GroupID,
    uint lane : SV_GroupIndex,
    out vertices ClusterRasterVertexData<VisibilityEvaluate.Varying> vertices[64],
    out indices uint3 indices[64],
    out primitives ClusterMeshPrimitive primitives[64])
{
    uint localPatch = groupId.x + groupId.y * 65535u;
    uint firstBin = TessellationUniformData.CurrentBin * TESS_PATTERN_MAX_FACTOR;
    uint factor = 1u;
    for (; factor <= TESS_PATTERN_MAX_FACTOR; factor++)
    {
        uint count = TessellationBinCounts[firstBin + factor - 1u];
        if (localPatch < count) break;
        localPatch -= count;
    }
    if (factor > TESS_PATTERN_MAX_FACTOR) return;
    uint groupedIndex = TessellationBinOffsets[firstBin + factor - 1u] + localPatch;
    if (groupedIndex >= TessellationUniformData.MaxPatchItems) return;
    TessellationPatch patch = DecodeTessellationFinalPatch(
        TessellationRasterPatches[GroupedPatchIndices[groupedIndex]]);
    uint sourceTriangle = TessellationPatchTriangle(patch);
    if (!HasTessellationTriangle(patch.VisibleClusterIndex, sourceTriangle)) return;
    VisibilityEvaluate.Varying sourceVaryings[3];
    DomainEvaluate.TriangleData triangle =
        LoadTessellationTriangleWithVisibility(
            vertex,
            domain,
            visibility,
            patch.VisibleClusterIndex,
            sourceTriangle,
            sourceVaryings);
    uint vertexCount = TessellationGridVertexCount(factor);
    uint triangleCount = TessellationGridTriangleCount(factor);
    SetMeshOutputCounts(vertexCount, triangleCount);
    uint viewIndex = DrawRequestViewIndex(TessellationVisibleClusters[patch.VisibleClusterIndex]);
    ClusterViewUniforms view = LoadClusterView(TessellationViews, TessellationPolicyViews, viewIndex);
    view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties, VisibleClusterRequest(TessellationVisibleClusters[patch.VisibleClusterIndex]).InstanceID, view);
    for (uint index = lane; index < vertexCount; index += 32u)
    {
        float3 localBary = TessellationGridBarycentric(factor, TessellationPatchEdgeFactors(patch), index);
        float3 sourceBary = TransformPatchBarycentric(
            DecodeTessellationPatchBarycentric(patch, 0u),
            DecodeTessellationPatchBarycentric(patch, 1u),
            DecodeTessellationPatchBarycentric(patch, 2u), localBary);
        float3 position = domain.position(triangle, sourceBary);
        float4 clip = mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
        vertices[index].ClipDepth = CalculateClusterRasterDepthInput(position, clip, view);
        vertices[index].Pos = ClusterHardwareRasterPosition(clip, patch.VisibleClusterIndex, view,
            ClusterRasterTargetViewport(view));
        vertices[index].VisibleClusterIndex = patch.VisibleClusterIndex;
        vertices[index].ViewIndex = viewIndex;
        vertices[index].Varying = visibility.interpolateVertex(
            sourceVaryings[0],
            sourceVaryings[1],
            sourceVaryings[2],
            sourceBary);
    }
    for (uint index = lane; index < triangleCount; index += 32u)
    {
        indices[index] = TessellationGridTriangleVertexIndices(factor, index);
        primitives[index].TriangleID = TessellationPatchTriangle(patch);
        primitives[index].RenderTargetIndex = view.TargetPolicy == 0u ? view.TargetViewIndex : 0u;
    }
}

struct ClusterTessellationHardwareOutput
{
    uint Visibility : SV_TARGET;
    float Depth : SV_Depth;
};

[ForceInline]
ClusterTessellationHardwareOutput EvaluateClusterTessellationHardwarePixel(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    ClusterViewUniforms view = LoadClusterView(
        TessellationViews,
        TessellationPolicyViews,
        input.ViewIndex);
    input.Pos.xy += float2(ClusterRasterTargetPixelOrigin(input.VisibleClusterIndex, view));
    ClusterRasterOutputPixel outputPixel;
    bool accepted = ResolveClusterHardwareRasterPixel(int2(input.Pos.xy), input.VisibleClusterIndex, view, outputPixel);
    if (!ClusterRasterQuadAcceptsPixel(accepted))
        discard;
    VisibilityPixelInput pixelInput;
    pixelInput.PixelCoord = uint3(uint2(input.Pos.xy), input.ViewIndex);
    pixelInput.Depth = ReadClusterRasterDepth(input.Pos, input.ClipDepth, view);
    pixelInput.EncodedTargetDepth = ReadClusterRasterTargetDepth(outputPixel, view.TargetPolicy, accepted);
    pixelInput.VisibleClusterIndex = input.VisibleClusterIndex;
    pixelInput.TriangleID = input.TriangleID;
    pixelInput.ViewIndex = input.ViewIndex;
    pixelInput.TargetPolicy = view.TargetPolicy;
    pixelInput.TargetViewIndex = view.TargetViewIndex;
    VisibilityPixelResult result = visibility.evaluate(pixelInput, input.Varying);
    if (!accepted || result.Visible == 0u)
        discard;
    if (view.TargetPolicy != 0u)
    {
        WriteRasterTargetPixelDepth(uint2(outputPixel.Position), result.Depth, outputPixel.DepthCacheClass);
        discard;
    }
    ClusterTessellationHardwareOutput output;
    output.Visibility = EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
    output.Depth = saturate(result.Depth);
    return output;
}

[shader("pixel")]
ClusterTessellationHardwareOutput ClusterTessellationHardwareMeshPixel(
    uniform VisibilityEvaluate visibility,
    ClusterRasterVertexData<VisibilityEvaluate.Varying> vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input;
    input.Pos = vertex.Pos;
    input.ClipDepth = vertex.ClipDepth;
    input.Varying = vertex.Varying;
    input.VisibleClusterIndex = vertex.VisibleClusterIndex;
    input.TriangleID = primitive.TriangleID;
    input.ViewIndex = vertex.ViewIndex;
    input.RenderTargetIndex = primitive.RenderTargetIndex;
    return EvaluateClusterTessellationHardwarePixel(visibility, input);
}


[shader("pixel")]
ClusterTessellationHardwareOutput ClusterTessellationHardwarePixel(
    uniform VisibilityEvaluate visibility,
    ClusterRasterPixelInput<VisibilityEvaluate.Varying> input)
{
    return EvaluateClusterTessellationHardwarePixel(visibility, input);
}

#endif
