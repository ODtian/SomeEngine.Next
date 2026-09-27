#include "cluster_bin_common.slang"
#ifndef CLUSTER_DRAW_CORE_SLANG
#define CLUSTER_DRAW_CORE_SLANG

#include "vertex_consumer_cache.slang"
#include "material_interfaces.slang"
#include "cluster_view_access.slang"
#include "cluster_mesh_topology.slang"

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
    float2 rasterScale = float2(
        view.ScreenWidth / float(HardwareRasterUniformData.RasterWidth),
        view.ScreenHeight / float(HardwareRasterUniformData.RasterHeight));
    output.Pos.xy = output.Pos.xy * rasterScale + output.Pos.w * float2(
        rasterScale.x - 1.0,
        1.0 - rasterScale.y);
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
    uint batchIndex = groupId.x + groupId.y * 65535u;
    DrawInstancedArgs args = LoadDrawInstancedArgs(HardwareRasterVisibleClusterMeta,
        HardwareRasterDispatchUniformData.DrawArgsByteOffset);
    if (batchIndex >= args.InstanceCount)
        return;
    uint batch = HardwareRasterBinnedClusterIndexBuffer[args.StartInstance + batchIndex];
    uint visible = RasterWorkVisibleIndex(batch);
    DrawRequest request = HardwareRasterVisibleClusters[visible];
    uint first, end;
    DecodeRasterWorkRange(PageHeap, request, batch, first, end);
    uint triangleCount = end > first ? end - first : 0u;
    uint address, size, triangleStart, counts;
    LoadClusterInfo(PageHeap, request.PageOffset, DrawRequestClusterID(request),
        address, size, triangleStart, counts);
    uint indexOffset = PageHeap.Load(request.PageOffset + 24u);
    uint3 triangle = 0u.xxx;
    if (lane < triangleCount)
    {
        [unroll]
        for (uint corner = 0u; corner < 3u; corner++)
        {
            uint index = LoadLocalVertexIndex(request.PageOffset, indexOffset, triangleStart, first + lane, corner);
            triangle[corner] = index;
        }
    }
    ClusterMeshVertexMask used = BuildClusterMeshVertexMask(triangle, lane < triangleCount, lane);
    SetMeshOutputCounts(ClusterMeshVertexCount(used), triangleCount);
    uint viewIndex = DrawRequestViewIndex(request);
    ClusterViewUniforms view = LoadClusterView(HardwareRasterViews, HardwareRasterPolicyViews, viewIndex);
    float2 rasterScale = float2(view.ScreenWidth, view.ScreenHeight) /
        float2(HardwareRasterUniformData.RasterWidth, HardwareRasterUniformData.RasterHeight);
    for (uint localVertex = lane; localVertex < (counts & 0xFFu); localVertex += 32u)
    {
        uint mask = used.Words[localVertex / 32u];
        if ((mask & (1u << (localVertex & 31u))) == 0u) continue;
        VertexEvalContext context = MakeDrawVertexEvalContext(address, size, localVertex,
            request.InstanceID, GetCurrentInstanceTransform(request.InstanceID));
        TVE.Vertex value = evaluator.evaluate(context);
        ClusterRasterVertexData<TVisibility.Varying> output = {};
        output.Pos = mul(float4(evaluator.getPosition(value), 1.0), view.ViewProj);
        output.Pos.xy = output.Pos.xy * rasterScale + output.Pos.w *
            float2(rasterScale.x - 1.0, 1.0 - rasterScale.y);
        VisibilityVertexInput visibilityInput;
        visibilityInput.VisibleClusterIndex = visible;
        visibilityInput.InstanceID = request.InstanceID;
        visibilityInput.ViewIndex = viewIndex;
        visibilityInput.LocalVertexIndex = localVertex;
        output.Varying = visibility.makeVarying(value, visibilityInput);
        vertices[ClusterMeshVertexRank(used, localVertex)] = output;
    }
    if (lane < triangleCount)
    {
        indices[lane] = uint3(ClusterMeshVertexRank(used, triangle.x),
            ClusterMeshVertexRank(used, triangle.y), ClusterMeshVertexRank(used, triangle.z));
        ClusterMeshPrimitive output;
        output.VisibleClusterIndex = visible;
        output.TriangleID = first + lane;
        output.ViewIndex = viewIndex;
        output.RenderTargetIndex = view.TargetPolicy == 0u ? view.TargetViewIndex : 0u;
        primitives[lane].VisibleClusterIndex = output.VisibleClusterIndex;
        primitives[lane].TriangleID = output.TriangleID;
        primitives[lane].ViewIndex = output.ViewIndex;
        primitives[lane].RenderTargetIndex = output.RenderTargetIndex;
    }
}

ClusterRasterPixelInput<TVarying> MakeMeshPixelInput<TVarying>(
    ClusterRasterVertexData<TVarying> vertex,
    ClusterMeshPrimitive primitive)
{
    ClusterRasterPixelInput<TVarying> result;
    result.Pos = vertex.Pos;
    result.Varying = vertex.Varying;
    result.VisibleClusterIndex = primitive.VisibleClusterIndex;
    result.TriangleID = primitive.TriangleID;
    result.ViewIndex = primitive.ViewIndex;
    result.RenderTargetIndex = primitive.RenderTargetIndex;
    return result;
}

#endif
