#include "vertex_evaluate.slang"
#include "cluster_view.slang"

struct DrawUniforms
{
    uint DebugMode;
    uint ScreenWidth;
    uint ScreenHeight;
    uint Pad0;
};

struct DrawDispatchUniforms
{
    uint DrawArgsByteOffset;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<DrawUniforms> Uniforms;
StructuredBuffer<ClusterViewUniforms> Views;
ConstantBuffer<DrawDispatchUniforms> DispatchUniforms;

ByteAddressBuffer PageHeap;
StructuredBuffer<uint3> BinnedClusterIndexBuffer;
StructuredBuffer<uint4> VisibleClusters;
ByteAddressBuffer VisibleClusterMeta;

ByteAddressBuffer DeformCache;
StructuredBuffer<uint> CacheOffsets;

static const uint DRAW_CACHE_OFFSET_INVALID = 0xFFFFFFFDu;

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
    uint RenderTargetIndex : SV_RenderTargetArrayIndex;
};

uint GetBinnedClusterIndex(uint instanceID)
{
    DrawInstancedArgs args = LoadDrawInstancedArgs(VisibleClusterMeta, DispatchUniforms.DrawArgsByteOffset);
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
    uint3 binEntry = BinnedClusterIndexBuffer[binnedIndex];
    visibleClusterIndex = binEntry.x;
    viewIndex = binEntry.z;
    uint packedRange = binEntry.y;

    uint4 req = VisibleClusters[visibleClusterIndex];
    uint pageOffset = req.x;
    clusterID = req.y;
    realInstanceID = req.z;
    if (req.w != viewIndex)
        return false;
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

    uint rangeStart = (packedRange >> 16u) & 0xFFFFu;
    uint rangeEnd = packedRange & 0xFFFFu;
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

bool LoadDrawDeformedVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
    uint visibleClusterIndex,
    uint vertexCount,
    VertexEvalContext ctx,
    out TVE.DeformedVertex vertex)
{
    if (useDeformCache)
    {
        uint cacheBaseByte = CacheOffsets[visibleClusterIndex];
        if (cacheBaseByte >= DRAW_CACHE_OFFSET_INVALID)
            return false;
        vertex = evaluator.readCache(
            DeformCache,
            cacheBaseByte,
            vertexCount,
            ctx.localVertexIndex);
        return true;
    }

    vertex = evaluator.evaluate(ctx);
    return true;
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

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSInput VSMain(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    PSInput output;

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
        WriteInvalidPSInput(output);
        return output;
    }

    StaticVertexEval eval;
    VertexEvalContext ctx = MakeDrawVertexEvalContext(
        vertexBlobAddress,
        vertexBlobSize,
        localVertIdx,
        realInstanceID,
        GetCurrentInstanceTransform(realInstanceID));
    StaticVertexEval.DeformedVertex vertex;
    if (!LoadDrawDeformedVertex(eval, false, visibleClusterIndex, vertexCount, ctx, vertex))
    {
        WriteInvalidPSInput(output);
        return output;
    }

    output.Pos = mul(float4(eval.getPosition(vertex), 1.0), Views[viewIndex].ViewProj);
    output.ClusterID = clusterID;
    output.Normal = vertex.worldNormal;
    output.UV = vertex.uv0;
    output.RenderTargetIndex = viewIndex;

    if (Uniforms.DebugMode == 2u)
    {
        float l = float(lodLevel) / 10.0;
        output.Color = float3(l, 1.0 - l, 0.0);
    }
    else
    {
        output.Color = vertex.worldNormal * 0.5 + 0.5;
    }

    return output;
}

[shader("pixel")]
float4 PSMain(PSInput input) : SV_TARGET
{
    if (Uniforms.DebugMode == 1u)
    {
        uint h = PCGHash(input.ClusterID);
        return float4(ColorFromHash(h), 1.0);
    }

    return float4(input.Color, 1.0);
}

[shader("pixel")]
float4 PSOverdraw(PSInput input) : SV_TARGET
{
    return float4(0.05, 0.05, 0.05, 1.0);
}

PSVisBufferInput BuildVisBufferVertex<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    bool useDeformCache,
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
    TVE.DeformedVertex vertex;
    if (!LoadDrawDeformedVertex(
            evaluator,
            useDeformCache,
            visibleClusterIndex,
            vertexCount,
            ctx,
            vertex))
    {
        output.Pos = asfloat(uint4(0x7FC00000, 0x7FC00000, 0x7FC00000, 0x7FC00000));
        return output;
    }

    output.Pos = mul(float4(evaluator.getPosition(vertex), 1.0), Views[viewIndex].ViewProj);
    output.VisibleClusterIndex = visibleClusterIndex;
    output.TriangleID = triangleID;
    output.RenderTargetIndex = viewIndex;
    return output;
}

[MaterialTarget("cluster.raster.vs")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSVisBufferInput VSVisBuffer(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    return BuildVisBufferVertex(eval, false, vertexID, instanceID);
}

[MaterialTarget("cluster.raster.vs.cached")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("vertex")]
PSVisBufferInput VSVisBufferCached(uint vertexID: SV_VertexID, uint instanceID: SV_InstanceID)
{
    StaticVertexEval eval;
    return BuildVisBufferVertex(eval, true, vertexID, instanceID);
}

[MaterialTarget("cluster.raster.ps")]
[shader("pixel")]
uint PSVisBuffer(PSVisBufferInput input) : SV_TARGET
{
    return EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
}
