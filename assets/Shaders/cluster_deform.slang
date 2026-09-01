// cluster_deform.slang — Pre-deformation Compute Shader
// 64 threads/group, 1 group = a fixed block of DeformBin cluster entries.
// The selected allocator is size -> global prefix -> write. DeformCache remains binned by
// VertexEval while raster remains independently binned.

#ifndef CLUSTER_DEFORM_SLANG
#define CLUSTER_DEFORM_SLANG

#include "cluster_common.slang"
#include "cluster_structures.slang"
#include "common.slang"
#include "resource_effects.slang"
#include "vertex_evaluate.slang"

struct DeformUniforms
{
    uint   MaxDeformCacheBytes;
    uint   MaxClusterVertices;
    uint   CurrentBin;
    uint   Pass;
};

cbuffer PushConstants : register(b100, space0)
{
    DeformUniforms Uniforms;
};

ByteAddressBuffer PageHeap;
StructuredBuffer<uint4> VisibleClusters;
StructuredBuffer<uint2> BinnedClusterIndexBuffer;
StructuredBuffer<DeformBinEntry> DeformBinMeta;

RWByteAddressBuffer      DeformCache;
RWStructuredBuffer<uint> CacheOffsetsWrite;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> DeformCacheRequests;

static const uint CACHE_OFFSET_OVERFLOW    = 0xFFFFFFFDu;
static const uint DEFORM_PASS_SIZE = 0u;
static const uint DEFORM_PASS_WRITE = 1u;

uint MaskedBitCount(uint2 bits, uint index)
{
    uint mask = (1u << (index & 31u)) - 1u;
    uint a = (index < 32u) ? bits.x : bits.y;
    uint b = (index < 32u) ? 0u : countbits(bits.x);
    return countbits(a & mask) + b;
}

int FindNthSetBit(uint mask, int n)
{
    int last = int(countbits(mask)) - n - 1;
    uint p = 16u;
    p += (countbits(mask >> p) <= uint(last)) ? -8u : 8u;
    p += (countbits(mask >> p) <= uint(last)) ? -4u : 4u;
    p += (countbits(mask >> p) <= uint(last)) ? -2u : 2u;
    p += (countbits(mask >> p) <= uint(last)) ? -1u : 1u;
    p  = (countbits(mask >> p) == uint(last)) ? (p - 1u) : p;
    return int(p);
}

int FindNthSetBit2(uint2 mask, int n)
{
    int lowPop = int(countbits(mask.x));
    return FindNthSetBit(
        (n < lowPop) ? mask.x : mask.y,
        (n < lowPop) ? n : (n - lowPop)
    ) + ((n < lowPop) ? 0 : 32);
}

uint CountBits2(uint2 v)
{
    return countbits(v.x) + countbits(v.y);
}

VertexEvalContext MakeDeformVertexEvalContext(
    uint vertexBlobAddress,
    uint vertexBlobSize,
    uint localVertIdx,
    uint instanceID,
    RenderInstanceTransform transform,
    uint isPreviousFrame)
{
    VertexEvalContext ctx;
    ctx.pageHeap = PageHeap;
    ctx.vertexBlobAddress = vertexBlobAddress;
    ctx.vertexBlobSize = vertexBlobSize;
    ctx.localVertexIndex = localVertIdx;
    ctx.instanceID = instanceID;
    ctx.transform = transform;
    ctx.IsPreviousFrame = isPreviousFrame;
    return ctx;
}

struct VertexDedup
{
    uint  numUniqueVerts;
    uint  laneVertIndex;
    uint3 cornerLanes;
};

VertexDedup DeduplicateVertIndexes(uint3 vertIndexes, uint laneIndex, bool bTriValid)
{
    VertexDedup result;

    uint minVert = bTriValid ? min(min(vertIndexes.x, vertIndexes.y), vertIndexes.z) : 0xFFFFFFFFu;
    uint baseVertIndex = WaveActiveMin(minVert);

    uint2 triVertMask = uint2(0, 0);
    if (bTriValid)
    {
        vertIndexes -= baseVertIndex;
        for (uint i = 0; i < 3; i++)
        {
            bool bLow = vertIndexes[i] < 32u;
            uint dstMask = 1u << (vertIndexes[i] & 31u);
            if (bLow) triVertMask.x |= dstMask;
            else      triVertMask.y |= dstMask;
        }
    }
    else
    {
        vertIndexes = uint3(0, 0, 0);
    }

    uint2 usedVertMask;
    usedVertMask.x = WaveActiveBitOr(triVertMask.x);
    usedVertMask.y = WaveActiveBitOr(triVertMask.y);

    result.cornerLanes.x = MaskedBitCount(usedVertMask, vertIndexes.x);
    result.cornerLanes.y = MaskedBitCount(usedVertMask, vertIndexes.y);
    result.cornerLanes.z = MaskedBitCount(usedVertMask, vertIndexes.z);
    result.laneVertIndex = uint(FindNthSetBit2(usedVertMask, int(laneIndex))) + baseVertIndex;
    result.numUniqueVerts = CountBits2(usedVertMask);

    return result;
}

void DeformKernel<TVE : IVertexEvaluate>(
    TVE evaluator,
    uint3 groupID,
    uint groupThreadIndex)
{
    uint blockIndex = groupID.x + groupID.y * 65535u;

    DeformBinEntry binMeta = DeformBinMeta[Uniforms.CurrentBin];
    uint blockStart = blockIndex * DEFORM_CACHE_CLUSTER_BLOCK_SIZE;
    if (blockStart >= binMeta.BinCount)
        return;

    if (Uniforms.Pass == DEFORM_PASS_SIZE)
    {
        if (groupThreadIndex < DEFORM_CACHE_CLUSTER_BLOCK_SIZE)
        {
            uint flatIndex = blockStart + groupThreadIndex;
            if (flatIndex >= binMeta.BinCount)
                return;
            uint binnedIndex = binMeta.ClusterOffset + flatIndex;
            uint visibleIndex = BinnedClusterIndexBuffer[binnedIndex].x;
            uint4 request = VisibleClusters[visibleIndex];
            uint vertexBlobAddress;
            uint vertexBlobSize;
            uint triangleStart;
            uint packedCounts;
            LoadClusterInfo(
                PageHeap,
                request.x,
                request.y,
                vertexBlobAddress,
                vertexBlobSize,
                triangleStart,
                packedCounts);
            uint vertexCount = packedCounts & 0xFFu;
            uint rawBytes = evaluator.getCacheByteSize(vertexCount);
            uint rawAlignment = evaluator.getCacheByteAlignment();
            bool valid =
                vertexCount != 0u && vertexCount <= Uniforms.MaxClusterVertices &&
                rawBytes != 0u && (rawBytes & 3u) == 0u &&
                rawAlignment >= 4u && rawAlignment <= Uniforms.MaxDeformCacheBytes &&
                (rawAlignment & (rawAlignment - 1u)) == 0u &&
                rawBytes <= Uniforms.MaxDeformCacheBytes - (rawAlignment - 1u);
            uint reservationBytes = valid
                ? rawBytes + rawAlignment - 1u
                : 0u;
            CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
            DeformCacheRequests[binnedIndex] = uint2(
                reservationBytes,
                valid ? rawAlignment : 0u);
        }
        return;
    }

    if (Uniforms.Pass != DEFORM_PASS_WRITE)
        return;

    for (uint blockCluster = 0u;
         blockCluster < DEFORM_CACHE_CLUSTER_BLOCK_SIZE;
         blockCluster++)
    {
        uint flatIndex = blockStart + blockCluster;
        if (flatIndex >= binMeta.BinCount)
            continue;
        uint binnedIndex = binMeta.ClusterOffset + flatIndex;
        uint visibleIndex = BinnedClusterIndexBuffer[binnedIndex].x;
        uint cacheBaseByte = CacheOffsetsWrite[visibleIndex];
        if (cacheBaseByte >= CACHE_OFFSET_OVERFLOW)
            continue;

        uint4 request = VisibleClusters[visibleIndex];
        uint vertexBlobAddress;
        uint vertexBlobSize;
        uint triangleStart;
        uint packedCounts;
        LoadClusterInfo(
            PageHeap,
            request.x,
            request.y,
            vertexBlobAddress,
            vertexBlobSize,
            triangleStart,
            packedCounts);
        uint vertexCount = packedCounts & 0xFFu;
        uint localVertIdx = groupThreadIndex;
        if (localVertIdx >= vertexCount)
            continue;

        RenderInstanceTransform transform = GetCurrentInstanceTransform(request.z);
        RenderInstanceTransform previousTransform = GetPreviousInstanceTransform(request.z);
        VertexEvalContext ctx = MakeDeformVertexEvalContext(
            vertexBlobAddress,
            vertexBlobSize,
            localVertIdx,
            request.z,
            transform,
            0u);
        TVE.DeformedVertex vertex = evaluator.evaluate(ctx);
        VertexEvalContext previousCtx = MakeDeformVertexEvalContext(
            vertexBlobAddress,
            vertexBlobSize,
            localVertIdx,
            request.z,
            previousTransform,
            1u);
        TVE.DeformedVertex previousVertex = evaluator.evaluate(previousCtx);
        evaluator.writeCache(
            DeformCache,
            cacheBaseByte,
            vertexCount,
            localVertIdx,
            vertex,
            previousVertex);
    }
}

[MaterialTarget("cluster.deform.eval")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformStatic(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    StaticVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

[MaterialTarget("cluster.deform.eval")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSDeformWave(uint3 groupID : SV_GroupID, uint gtid : SV_GroupThreadID)
{
    WaveVertexEval eval;
    DeformKernel(eval, groupID, gtid);
}

#endif // CLUSTER_DEFORM_SLANG
