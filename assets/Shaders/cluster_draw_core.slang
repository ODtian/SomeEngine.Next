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

struct PSInput
{
    float4 Pos : SV_POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD0;
    float3 Color : COLOR0;
    nointerpolation uint ClusterID : CLUSTER_ID;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

struct PSVisBufferInput
{
    float4 Pos : SV_POSITION;
    nointerpolation uint VisibleClusterIndex : VIS_CLUSTER_IDX;
    nointerpolation uint TriangleID : TRIANGLE_ID;
    nointerpolation uint ViewIndex : VIEW_INDEX;
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

struct PSVisBufferOutput
{
    uint Visibility : SV_TARGET;
    float Depth : SV_Depth;
};

PixelEvaluateResult EvaluateHardwareRasterPixel<TPixel : IPixelEvaluate>(
    uniform TPixel pixel,
    PSVisBufferInput input)
{
    PixelEvaluateInput pixelInput;
    pixelInput.PixelCoord = uint3(uint2(input.Pos.xy), input.ViewIndex);
    pixelInput.Depth = input.Pos.z;
    return pixel.evaluate(pixelInput);
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
    visibleClusterIndex = RasterBatchVisibleIndex(binEntry);

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
    DecodeRasterBatchRange(PageHeap, req, binEntry, rangeStart, rangeEnd);
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

void WriteInvalidPSInput(out PSInput output)
{
    output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
    output.Normal = float3(0.0);
    output.UV = float2(0.0);
    output.Color = float3(0.0);
    output.ClusterID = 0u;
    output.RenderTargetIndex = 0u;
}

PSVisBufferInput BuildVisBufferVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    uint vertexID,
    uint instanceID)
{
    PSVisBufferInput output;

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

    output.VisibleClusterIndex = 0u;
    output.TriangleID = 0u;
    output.RenderTargetIndex = 0u;

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
    SurfaceVertex vertex = EvaluateSurfaceVertexWave(evaluator, ctx, visibleClusterIndex);

    ClusterViewUniforms view = LoadClusterView(
        HardwareRasterViews,
        HardwareRasterPolicyViews,
        viewIndex);
    output.Pos = mul(
        float4(vertex.Position, 1.0),
        view.ViewProj);
    float2 rasterScale = float2(
        view.ScreenWidth / float(HardwareRasterUniformData.RasterWidth),
        view.ScreenHeight / float(HardwareRasterUniformData.RasterHeight));
    output.Pos.xy = output.Pos.xy * rasterScale + output.Pos.w * float2(
        rasterScale.x - 1.0,
        1.0 - rasterScale.y);
    output.VisibleClusterIndex = visibleClusterIndex;
    output.TriangleID = triangleID;
    output.ViewIndex = viewIndex;
    output.RenderTargetIndex = view.TargetPolicy == 0u
        ? view.TargetViewIndex
        : 0u;
    return output;
}


[ForceInline]
void BuildVisBufferMesh<TVE : IVertexEvaluate>(
    uniform TVE evaluator, uint3 groupId, uint lane,
    out vertices ClusterMeshVertex vertices[96],
    out indices uint3 indices[32],
    out primitives ClusterMeshPrimitive primitives[32])
{
    uint batchIndex = groupId.x + groupId.y * 65535u;
    DrawInstancedArgs args = LoadDrawInstancedArgs(HardwareRasterVisibleClusterMeta,
        HardwareRasterDispatchUniformData.DrawArgsByteOffset);
    if (batchIndex >= args.InstanceCount)
        return;
    uint batch = HardwareRasterBinnedClusterIndexBuffer[args.StartInstance + batchIndex];
    uint visible = RasterBatchVisibleIndex(batch);
    DrawRequest request = HardwareRasterVisibleClusters[visible];
    uint first, end;
    DecodeRasterBatchRange(PageHeap, request, batch, first, end);
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
        ClusterMeshVertex output;
        output.Pos = mul(float4(evaluator.getPosition(value), 1.0), view.ViewProj);
        output.Pos.xy = output.Pos.xy * rasterScale + output.Pos.w *
            float2(rasterScale.x - 1.0, 1.0 - rasterScale.y);
        vertices[ClusterMeshVertexRank(used, localVertex)].Pos = output.Pos;
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

PSVisBufferInput MakeMeshPixelInput(ClusterMeshVertex vertex, ClusterMeshPrimitive primitive)
{
    PSVisBufferInput result;
    result.Pos = vertex.Pos;
    result.VisibleClusterIndex = primitive.VisibleClusterIndex;
    result.TriangleID = primitive.TriangleID;
    result.ViewIndex = primitive.ViewIndex;
    result.RenderTargetIndex = primitive.RenderTargetIndex;
    return result;
}

#endif
