#ifndef CLUSTER_TESSELLATION_ENTRIES_SLANG
#define CLUSTER_TESSELLATION_ENTRIES_SLANG

#include "cluster_tessellation_core.slang"
#include "cluster_policy_raster_output.slang"

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("unbounded")]
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
[ConservativeBounds("unbounded")]
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
[ConservativeBounds("unbounded")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterTessellationDiceImageAtomic64(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform PixelEvaluate pixel,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 output;
    DiceTessellationPatches(
        vertex,
        domain,
        pixel,
        output,
        groupId,
        groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("unbounded")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void ClusterTessellationDiceBufferAtomic64(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uniform PixelEvaluate pixel,
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width = TessellationUniformData.ScreenWidth;
    output.Height = TessellationUniformData.ScreenHeight;
    DiceTessellationPatches(vertex, domain, pixel, output, groupId, groupThreadIndex);
}

struct ClusterTessellationHardwareInput
{
    float4 Pos : SV_POSITION;
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
    nointerpolation uint ViewIndex : VIEW_INDEX;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("unbounded")]
[shader("vertex")]
ClusterTessellationHardwareInput ClusterTessellationHardwareVertex(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uint vertexID : SV_VertexID,
    uint patchIndex : ATTRIBUTE0)
{
    ClusterTessellationHardwareInput output = (ClusterTessellationHardwareInput)0;
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

    bool valid;
    Triangle triangle;
    LoadTessellationTriangle(
        vertex,
        patch.VisibleClusterIndex,
        TessellationPatchTriangle(patch),
        valid,
        triangle);
    if (!valid)
        return output;
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
    output.Pos = mul(float4(position, 1.0), view.ViewProj);
    output.VisibleClusterIndex = patch.VisibleClusterIndex;
    output.TriangleID = TessellationPatchTriangle(patch);
    output.ViewIndex = viewIndex;
    output.RenderTargetIndex = view.TargetPolicy == 0u ? view.TargetViewIndex : 0u;
    return output;
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("unbounded")]
[shader("mesh")]
[outputtopology("triangle")]
[numthreads(32, 1, 1)]
void ClusterTessellationHardwareMesh(
    uniform VertexEvaluate vertex,
    uniform DomainEvaluate domain,
    uint3 groupId : SV_GroupID,
    uint lane : SV_GroupIndex,
    out vertices ClusterMeshVertex vertices[64],
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
    bool valid;
    Triangle triangle;
    LoadTessellationTriangle(vertex, patch.VisibleClusterIndex,
        TessellationPatchTriangle(patch), valid, triangle);
    if (!valid) return;
    uint vertexCount = TessellationGridVertexCount(factor);
    uint triangleCount = TessellationGridTriangleCount(factor);
    SetMeshOutputCounts(vertexCount, triangleCount);
    uint viewIndex = DrawRequestViewIndex(TessellationVisibleClusters[patch.VisibleClusterIndex]);
    ClusterViewUniforms view = LoadClusterView(TessellationViews, TessellationPolicyViews, viewIndex);
    for (uint index = lane; index < vertexCount; index += 32u)
    {
        float3 localBary = TessellationGridBarycentric(factor, TessellationPatchEdgeFactors(patch), index);
        float3 sourceBary = TransformPatchBarycentric(
            DecodeTessellationPatchBarycentric(patch, 0u),
            DecodeTessellationPatchBarycentric(patch, 1u),
            DecodeTessellationPatchBarycentric(patch, 2u), localBary);
        vertices[index].Pos = mul(float4(domain.position(triangle, sourceBary), 1.0), view.ViewProj);
    }
    for (uint index = lane; index < triangleCount; index += 32u)
    {
        indices[index] = TessellationGridTriangleVertexIndices(factor, index);
        primitives[index].VisibleClusterIndex = patch.VisibleClusterIndex;
        primitives[index].TriangleID = TessellationPatchTriangle(patch);
        primitives[index].ViewIndex = viewIndex;
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
    uniform PixelEvaluate pixel,
    ClusterTessellationHardwareInput input)
{
    PixelEvaluateInput pixelInput;
    pixelInput.PixelCoord = uint3(uint2(input.Pos.xy), input.ViewIndex);
    pixelInput.Depth = input.Pos.z;
    PixelEvaluateResult result = pixel.evaluate(pixelInput);
    if (result.Visible == 0u)
        discard;
    ClusterViewUniforms view = LoadClusterView(
        TessellationViews,
        TessellationPolicyViews,
        input.ViewIndex);
    if (view.TargetPolicy != 0u)
    {
        WriteRasterTargetDepth(
            int2(input.Pos.xy),
            view.TargetViewIndex,
            saturate(result.Depth));
        discard;
    }
    ClusterTessellationHardwareOutput output;
    output.Visibility = EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
    output.Depth = saturate(result.Depth);
    return output;
}

[shader("pixel")]
ClusterTessellationHardwareOutput ClusterTessellationHardwareMeshPixel(
    uniform PixelEvaluate pixel,
    ClusterMeshVertex vertex,
    [[vk::perprimitive]] ClusterMeshPrimitive primitive)
{
    ClusterTessellationHardwareInput input;
    input.Pos = vertex.Pos;
    input.VisibleClusterIndex = primitive.VisibleClusterIndex;
    input.TriangleID = primitive.TriangleID;
    input.ViewIndex = primitive.ViewIndex;
    input.RenderTargetIndex = primitive.RenderTargetIndex;
    return EvaluateClusterTessellationHardwarePixel(pixel, input);
}


[shader("pixel")]
ClusterTessellationHardwareOutput ClusterTessellationHardwarePixel(
    uniform PixelEvaluate pixel,
    ClusterTessellationHardwareInput input)
{
    return EvaluateClusterTessellationHardwarePixel(pixel, input);
}

#endif
