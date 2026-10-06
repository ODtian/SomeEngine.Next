#include "cluster_bin_common.slang"
#ifndef CLUSTER_DRAW_CORE_SLANG
#define CLUSTER_DRAW_CORE_SLANG

#include "vertex_consumer_cache.slang"
#include "material_interfaces.slang"
#include "cluster_view_access.slang"
#include "cluster_mesh_topology.slang"
#include "cluster_policy_raster_output.slang"

struct DrawUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint RasterWidth;
    uint RasterHeight;
};

struct DrawDispatchUniforms
{
    uint DrawArgsByteOffset;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<DrawUniforms> HardwareRasterUniformData;
StructuredBuffer<ClusterViewUniforms> HardwareRasterViews;
StructuredBuffer<ClusterViewUniforms> HardwareRasterPolicyViews;
ConstantBuffer<DrawDispatchUniforms> HardwareRasterDispatchUniformData;

StructuredBuffer<uint> HardwareRasterBinnedClusterIndexBuffer;
StructuredBuffer<DrawRequest> HardwareRasterVisibleClusters;
ByteAddressBuffer HardwareRasterVisibleClusterMeta;
groupshared float4 HardwareRasterClipPositions[64];

struct PSVisBufferOutput
{
    uint Visibility : SV_TARGET;
    float Depth : SV_Depth;
};

VisibilityPixelResult EvaluateHardwareRasterPixel<TVertex, TVisibility>(
    uniform TVisibility visibility,
    ClusterRasterPixelInput<TVisibility.Varying> input)
    where TVisibility : IVisibilityEvaluate<TVertex>
{
    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        input.ViewIndex);
    VisibilityPixelInput pixelInput;
    pixelInput.PixelCoord = uint3(uint2(input.Pos.xy), input.ViewIndex);
    pixelInput.Depth = input.Pos.z;
    pixelInput.VisibleClusterIndex = input.VisibleClusterIndex;
    pixelInput.TriangleID = input.TriangleID;
    pixelInput.ViewIndex = input.ViewIndex;
    pixelInput.TargetPolicy = view.TargetPolicy;
    pixelInput.TargetViewIndex = view.TargetViewIndex;
    return visibility.evaluate(pixelInput, input.Varying);
}

uint GetBinnedClusterIndex(uint instanceID)
{
    DrawInstancedArgs args = LoadDrawInstancedArgs(
        HardwareRasterVisibleClusterMeta,
        HardwareRasterDispatchUniformData.DrawArgsByteOffset);
    return args.StartInstance + instanceID;
}

uint LoadLocalVertexIndex(uint pageOffset, uint idxOffset, uint tStart, uint triIdx, uint corner)
{
    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3u + corner;
    uint alignedAddr = indexByteAddr & ~3u;
    uint shift = (indexByteAddr & 3u) * 8u;
    uint packedIndex = PageHeap.Load(alignedAddr);
    return (packedIndex >> shift) & 0xFFu;
}

// A three-byte triangle tuple spans at most two aligned words.
uint3 LoadLocalTriangleIndices(uint pageOffset, uint indexOffset, uint triangleStart, uint triangleIndex)
{
    uint address = pageOffset + indexOffset + triangleStart + triangleIndex * 3u;
    uint wordAddress = address & ~3u;
    uint shift = (address & 3u) * 8u;
    uint indices = PageHeap.Load(wordAddress) >> shift;
    if (shift > 8u) indices |= PageHeap.Load(wordAddress + 4u) << (32u - shift);
    return uint3(indices & 255u, (indices >> 8u) & 255u, (indices >> 16u) & 255u);
}

bool DecodeDrawVertex(
    uint vertexID,
    uint instanceID,
    out uint visibleClusterIndex,
    out uint clusterID,
    out uint realInstanceID,
    out uint viewIndex,
    out uint lodLevel,
    out uint triangleID,
    out uint localVertIdx,
    out uint vertexBlobAddress,
    out uint vertexBlobSize,
    out uint vertexCount)
{
    uint binnedIndex = GetBinnedClusterIndex(instanceID);
    uint binEntry = HardwareRasterBinnedClusterIndexBuffer[binnedIndex];
    visibleClusterIndex = RasterWorkVisibleIndex(binEntry);

    DrawRequest req = HardwareRasterVisibleClusters[visibleClusterIndex];
    viewIndex = DrawRequestViewIndex(req);
    uint pageOffset = req.PageOffset;
    clusterID = DrawRequestClusterID(req);
    realInstanceID = req.InstanceID;
    uint idxOffset = PageHeap.Load(pageOffset + 24u);

    uint tStart;
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        pageOffset,
        clusterID,
        vertexBlobAddress,
        vertexBlobSize,
        tStart,
        packedCounts);
    vertexCount = packedCounts & 0xFFu;
    lodLevel = (packedCounts >> 16u) & 0xFFu;

    uint rangeStart, rangeEnd;
    DecodeRasterWorkRange(PageHeap, req, binEntry, rangeStart, rangeEnd);
    uint batchTriCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0u;

    triangleID = rangeStart + vertexID / 3u;
    uint corner = vertexID % 3u;
    localVertIdx = 0u;

    if (vertexID >= batchTriCount * 3u)
        return false;

    localVertIdx = LoadLocalVertexIndex(pageOffset, idxOffset, tStart, triangleID, corner);
    return true;
}

VertexEvalContext MakeDrawVertexEvalContext(
    uint vertexBlobAddress,
    uint vertexBlobSize,
    uint localVertIdx,
    uint instanceID,
    RenderInstanceTransform transform)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.vertexBlobAddress = vertexBlobAddress;
    ctx.vertexBlobSize = vertexBlobSize;
    ctx.localVertexIndex = localVertIdx;
    ctx.instanceID = instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = 0u;
    return ctx;
}

TVE.Vertex EvaluateDrawVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    VertexEvalContext ctx)
{
    return evaluator.evaluate(ctx);
}

ClusterRasterPixelInput<TVisibility.Varying> BuildVisBufferVertex<TVE, TVisibility>(
    uniform TVE evaluator,
    uniform TVisibility visibility,
    uint vertexID,
    uint instanceID)
    where TVE : IVertexEvaluate
    where TVisibility : IVisibilityEvaluate<TVE.Vertex>
{
    ClusterRasterPixelInput<TVisibility.Varying> output = {};

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
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
        return output;
    }

    VertexEvalContext ctx = MakeDrawVertexEvalContext(
        vertexBlobAddress,
        vertexBlobSize,
        localVertIdx,
        realInstanceID,
        GetCurrentInstanceTransform(realInstanceID));
    TVE.Vertex vertex = EvaluateVertexWave(evaluator, ctx, visibleClusterIndex);

    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        viewIndex);
    output.Pos = mul(
        float4(evaluator.getPosition(vertex), 1.0),
        view.ViewProj);
    output.Pos = ClusterHardwareRasterPosition(output.Pos, visibleClusterIndex, view,
        float2(HardwareRasterUniformData.RasterWidth, HardwareRasterUniformData.RasterHeight));
    VisibilityVertexInput visibilityInput;
    visibilityInput.VisibleClusterIndex = visibleClusterIndex;
    visibilityInput.InstanceID = realInstanceID;
    visibilityInput.ViewIndex = viewIndex;
    visibilityInput.LocalVertexIndex = localVertIdx;
    output.Varying = visibility.makeVarying(vertex, visibilityInput);
    output.VisibleClusterIndex = visibleClusterIndex;
    output.TriangleID = triangleID;
    output.ViewIndex = viewIndex;
    output.RenderTargetIndex = view.TargetPolicy == 0u
        ? view.TargetViewIndex
        : 0u;
    return output;
}


[ForceInline]
// Import validation limits every hardware reuse batch to 32 distinct vertices
// and 32 triangles, independently of the cluster's vertex-index range.
[ForceInline]
void BuildClusterMesh<TVE, TVisibility, let VertexCapacity : int, let TriangleCapacity : int>(
    uniform TVE evaluator,
    uniform TVisibility visibility,
    uint3 groupId,
    uint lane,
    out vertices ClusterRasterVertexData<TVisibility.Varying> vertices[VertexCapacity],
    out indices uint3 indices[TriangleCapacity],
    out primitives ClusterMeshPrimitive primitives[TriangleCapacity])
    where TVE : IVertexEvaluate
    where TVisibility : IVisibilityEvaluate<TVE.Vertex>
{
    uint batchIndex = groupId.x + groupId.y * 65535u;
    DrawInstancedArgs args = LoadDrawInstancedArgs(HardwareRasterVisibleClusterMeta,
        HardwareRasterDispatchUniformData.DrawArgsByteOffset);
    bool validBatch = batchIndex < args.InstanceCount;
    bool fullRange = false;
    uint visible = 0u;
    DrawRequest request = (DrawRequest)0;
    uint first = 0u, end = 0u;
    uint address = 0u, size = 0u, triangleStart = 0u, counts = 0u, indexOffset = 0u;
    if (validBatch)
    {
        uint batch = HardwareRasterBinnedClusterIndexBuffer[args.StartInstance + batchIndex];
        visible = RasterWorkVisibleIndex(batch);
        fullRange = TriangleCapacity > 32 && RasterWorkIsRange(batch >> 22u);
        request = HardwareRasterVisibleClusters[visible];
        DecodeRasterWorkRange(PageHeap, request, batch, first, end);
        LoadClusterInfo(PageHeap, request.PageOffset, DrawRequestClusterID(request),
            address, size, triangleStart, counts);
        indexOffset = PageHeap.Load(request.PageOffset + 24u);
    }
    uint triangleCount = end > first ? end - first : 0u;
    uint3 triangle = 0u.xxx;
    if (!fullRange && lane < triangleCount)
        triangle = LoadLocalTriangleIndices(request.PageOffset, indexOffset, triangleStart, first + lane);
    ClusterMeshVertexMask used = (ClusterMeshVertexMask)0;
    if (!fullRange) used = BuildClusterMeshVertexMask(triangle, lane < triangleCount, lane);
    // Wrapped indirect dispatches include padding groups. Every group declares its
    // output counts, including zero, before any early return from the mesh shader.
    SetMeshOutputCounts(fullRange ? (counts & 0xFFu) : ClusterMeshVertexCount(used), triangleCount);
    if (!validBatch) return;
    uint viewIndex = DrawRequestViewIndex(request);
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, viewIndex);
    RenderInstanceTransform transform = GetCurrentInstanceTransform(request.InstanceID);
    float2 targetWindowSize = 0.0.xx;
    if (view.TargetPolicy != 0u)
    {
        int4 scissor = PrepareRasterTargetScissor(visible, view.TargetViewIndex,
            view.ScreenWidth, view.ScreenHeight, lane, 32u);
        targetWindowSize = float2(scissor.zw - scissor.xy);
    }
    for (uint localVertex = lane; localVertex < (counts & 0xFFu); localVertex += 32u)
    {
        uint mask = used.Words[localVertex / 32u];
        if (!fullRange && (mask & (1u << (localVertex & 31u))) == 0u) continue;
        VertexEvalContext context = MakeDrawVertexEvalContext(address, size, localVertex,
            request.InstanceID, transform);
        TVE.Vertex value = evaluator.evaluate(context);
        ClusterRasterVertexData<TVisibility.Varying> output = {};
        output.Pos = mul(float4(evaluator.getPosition(value), 1.0), view.ViewProj);
        output.Pos = ClusterHardwareRasterPosition(output.Pos, visible, view,
            float2(HardwareRasterUniformData.RasterWidth, HardwareRasterUniformData.RasterHeight));
        VisibilityVertexInput visibilityInput;
        visibilityInput.VisibleClusterIndex = visible;
        visibilityInput.InstanceID = request.InstanceID;
        visibilityInput.ViewIndex = viewIndex;
        visibilityInput.LocalVertexIndex = localVertex;
        output.Varying = visibility.makeVarying(value, visibilityInput);
        output.VisibleClusterIndex = visible;
        output.ViewIndex = viewIndex;
        uint outputVertex = fullRange ? localVertex : ClusterMeshVertexRank(used, localVertex);
        vertices[outputVertex] = output;
        if (view.TargetPolicy != 0u) HardwareRasterClipPositions[outputVertex] = output.Pos;
    }
    if (view.TargetPolicy != 0u) GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint part = 0u; part < uint(TriangleCapacity) / 32u; part++)
    {
        uint triangleIndex = lane + part * 32u;
        if (triangleIndex >= triangleCount) continue;
        uint3 outputTriangle = LoadLocalTriangleIndices(
            request.PageOffset, indexOffset, triangleStart, first + triangleIndex);
        uint3 outputIndices = fullRange ? outputTriangle : uint3(ClusterMeshVertexRank(used, outputTriangle.x),
            ClusterMeshVertexRank(used, outputTriangle.y), ClusterMeshVertexRank(used, outputTriangle.z));
        if (view.TargetPolicy != 0u)
        {
            float4 firstClip = HardwareRasterClipPositions[outputIndices.x];
            float4 secondClip = HardwareRasterClipPositions[outputIndices.y];
            float4 thirdClip = HardwareRasterClipPositions[outputIndices.z];
            if (firstClip.w > 0.0 && secondClip.w > 0.0 && thirdClip.w > 0.0)
            {
                float2 rasterSize = float2(HardwareRasterUniformData.RasterWidth, HardwareRasterUniformData.RasterHeight);
                float2 firstPixel = (firstClip.xy / firstClip.w * float2(.5, -.5) + .5) * rasterSize;
                float2 secondPixel = (secondClip.xy / secondClip.w * float2(.5, -.5) + .5) * rasterSize;
                float2 thirdPixel = (thirdClip.xy / thirdClip.w * float2(.5, -.5) + .5) * rasterSize;
                float2 minimumPixel = min(firstPixel, min(secondPixel, thirdPixel));
                float2 maximumPixel = max(firstPixel, max(secondPixel, thirdPixel));
                // The output viewport covers a fixed maximum window. The current
                // cluster can address a smaller rectangle of requested pages.
                if (any(maximumPixel < 0.0.xx) || any(minimumPixel > targetWindowSize) ||
                    !ClusterRasterTargetCachedBoundsAccepts(
                        minimumPixel, maximumPixel, view.TargetViewIndex, targetWindowSize,
                        RasterOutputDepthCacheClass(visible)))
                    outputIndices = 0u.xxx;
            }
        }
        indices[triangleIndex] = outputIndices;
        ClusterMeshPrimitive output;
        output.TriangleID = first + triangleIndex;
        output.RenderTargetIndex = view.TargetPolicy == 0u ? view.TargetViewIndex : 0u;
        primitives[triangleIndex].TriangleID = output.TriangleID;
        primitives[triangleIndex].RenderTargetIndex = output.RenderTargetIndex;
    }
}

[ForceInline]
void BuildVisBufferMesh<TVE, TVisibility>(
    uniform TVE evaluator,
    uniform TVisibility visibility,
    uint3 groupId,
    uint lane,
    out vertices ClusterRasterVertexData<TVisibility.Varying> vertices[32],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
    where TVE : IVertexEvaluate
    where TVisibility : IVisibilityEvaluate<TVE.Vertex>
{
    BuildClusterMesh<TVE, TVisibility, 32, 32>(
        evaluator, visibility, groupId, lane, vertices, indices, primitives);
}

[ForceInline]
void BuildVisBufferMeshlet<TVE, TVisibility>(
    uniform TVE evaluator,
    uniform TVisibility visibility,
    uint3 groupId,
    uint lane,
    out vertices ClusterRasterVertexData<TVisibility.Varying> vertices[64],
    out indices uint3 indices[128],
    out primitives ClusterMeshPrimitive primitives[128])
    where TVE : IVertexEvaluate
    where TVisibility : IVisibilityEvaluate<TVE.Vertex>
{
    BuildClusterMesh<TVE, TVisibility, 64, 128>(
        evaluator, visibility, groupId, lane, vertices, indices, primitives);
}

ClusterRasterPixelInput<TVarying> MakeMeshPixelInput<TVarying>(
    ClusterRasterVertexData<TVarying> vertex,
    ClusterMeshPrimitive primitive)
{
    ClusterRasterPixelInput<TVarying> result;
    result.Pos = vertex.Pos;
    result.Varying = vertex.Varying;
    result.VisibleClusterIndex = vertex.VisibleClusterIndex;
    result.TriangleID = primitive.TriangleID;
    result.ViewIndex = vertex.ViewIndex;
    result.RenderTargetIndex = primitive.RenderTargetIndex;
    return result;
}

#endif
