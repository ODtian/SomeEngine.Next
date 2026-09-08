#include "cluster_bin_common.slang"
// sw_raster.slang — Software Rasterizer Compute Shader
// Group-shared vertex reuse cache + WaveQueue pixel distribution.
//
// 32 threads per group, 1 wave = 1 cluster.
// Stage 1: group-wide vertex dedup + fused transform (wave aggregation + 1 KiB LDS)
// Stage 2: Triangle Setup (subpixel edge equations)
// Stage 3: WaveQueue Pixel Distribution (IWaveTask)

#ifndef SW_RASTER_CORE_SLANG
#define SW_RASTER_CORE_SLANG

#include "cluster_structures.slang"
#include "cluster_view_access.slang"
#include "common.slang"
#include "vertex_evaluate.slang"

// ─── Uniforms ───
struct ClusterSoftwareRasterUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint MaxBins;
    uint DebugDump;  // non-zero = dump debug data this frame
    uint CurrentBin; // Per-bin dispatch: which bin this dispatch covers
    uint _pad0;
    uint _pad1;
    uint _pad2;
};

cbuffer ClusterSoftwareRasterPushConstants : register(b100, space0)
{
    ClusterSoftwareRasterUniforms ClusterSoftwareRasterUniformData;
};

// Instance-property accessors are declared in cluster_common.slang (via vertex_evaluate.slang).
StructuredBuffer<ClusterViewUniforms> SoftwareRasterViews;
StructuredBuffer<ClusterViewUniforms> SoftwareRasterPolicyViews;
StructuredBuffer<DrawRequest> SoftwareRasterVisibleClusters;
StructuredBuffer<uint> SoftwareRasterBinnedClusterIndexBuffer; // visible index, triangle range
StructuredBuffer<RasterBinEntry> SoftwareRasterBinMeta;

#include "cluster_raster_shared.slang"

// Debug: per-cluster SW raster dump. Layout:
// [0]: atomic counter (uint)
// [4 + i*24 + 0]:  binnedIndex
// [4 + i*24 + 4]:  originalIndex
// [4 + i*24 + 8]:  clusterID
// [4 + i*24 + 12]: triangleCount
// [4 + i*24 + 16]: binIdx
// [4 + i*24 + 20]: vertexCount
// Max 8192 entries → buffer size = 4 + 8192*24 = 196612 bytes
RWByteAddressBuffer SoftwareRasterDebugOutput;

// ─── Group-wide Vertex Deduplication ───

static const uint SOFTWARE_RASTER_VERTEX_CAPACITY = 64u;
static const uint SOFTWARE_RASTER_GROUP_SIZE = 32u;

groupshared uint SoftwareRasterUsedVertexMask[2];
groupshared float4 SoftwareRasterVertexCache[SOFTWARE_RASTER_VERTEX_CAPACITY];

// MaskedBitCount: count set bits in mask below position `index`
uint MaskedBitCount(uint2 bits, uint index)
{
    uint mask = (1u << (index & 31u)) - 1u;
    uint a = (index < 32u) ? bits.x : bits.y;
    uint b = (index < 32u) ? 0u : countbits(bits.x);
    return countbits(a & mask) + b;
}

// FindNthSetBit: find position of the N-th set bit (0-indexed) in a 32-bit mask
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

struct VertexDedup
{
    uint  numUniqueVerts;
    uint2 usedVertMask;
    uint3 cornerCacheIndices;
};

VertexDedup DeduplicateVertIndexes(
    uint3 vertIndexes,
    uint groupThreadIndex,
    bool bTriValid)
{
    if (groupThreadIndex < 2u)
        SoftwareRasterUsedVertexMask[groupThreadIndex] = 0u;
    GroupMemoryBarrierWithGroupSync();

    uint2 laneMask = 0u.xx;
    if (bTriValid)
    {
        // Cooked Cluster local vertex indices are bounded to [0, 63].
        for (uint i = 0; i < 3; i++)
        {
            uint vertex = vertIndexes[i];
            uint bit = 1u << (vertex & 31u);
            if (vertex < 32u)
                laneMask.x |= bit;
            else
                laneMask.y |= bit;
        }
    }

    // One atomic pair per hardware wave, independent of subgroup width.
    uint2 waveMask = uint2(
        WaveActiveBitOr(laneMask.x),
        WaveActiveBitOr(laneMask.y));
    if (WaveIsFirstLane())
    {
        InterlockedOr(SoftwareRasterUsedVertexMask[0], waveMask.x);
        InterlockedOr(SoftwareRasterUsedVertexMask[1], waveMask.y);
    }
    GroupMemoryBarrierWithGroupSync();

    VertexDedup result;
    result.usedVertMask = uint2(
        SoftwareRasterUsedVertexMask[0],
        SoftwareRasterUsedVertexMask[1]);
    result.numUniqueVerts = CountBits2(result.usedVertMask);
    result.cornerCacheIndices = bTriValid
        ? uint3(
            MaskedBitCount(result.usedVertMask, vertIndexes.x),
            MaskedBitCount(result.usedVertMask, vertIndexes.y),
            MaskedBitCount(result.usedVertMask, vertIndexes.z))
        : 0u.xxx;

    return result;
}

// ─── Vertex Fetch (from PageHeap, same as cluster_draw.slang) ───

void LoadClusterGeometry(uint pageOffset, uint clusterID,
    out uint vertexBlobAddress, out uint vertexBlobSize, out uint tStart,
    out uint vertexCount, out uint triangleCount,
    out uint idxOffset)
{
    idxOffset = PageHeap.Load(pageOffset + 24u);
    uint packedCounts;
    LoadClusterInfo(
        PageHeap,
        pageOffset,
        clusterID,
        vertexBlobAddress,
        vertexBlobSize,
        tStart,
        packedCounts);
    vertexCount  = packedCounts & 0xFF;
    triangleCount = (packedCounts >> 8) & 0xFF;
}

uint FetchVertexIndex(uint pageOffset, uint idxOffset, uint tStart, uint triIdx, uint corner)
{
    uint byteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    uint aligned = byteAddr & ~3u;
    uint shift = (byteAddr & 3u) * 8u;
    return (PageHeap.Load(aligned) >> shift) & 0xFFu;
}

VertexEvalContext MakeSWRasterVertexEvalContext(
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

float4 EvaluateSWRasterClipPosition<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    VertexEvalContext ctx,
    ClusterViewUniforms view)
{
    TVE.Vertex vertex = evaluator.evaluate(ctx);
    return mul(float4(evaluator.getPosition(vertex), 1.0), view.ViewProj);
}

// ─── Generic SW Rasterizer Kernel ───

void SWRasterKernel<
    TVE : IVertexEvaluate,
    TPixel : IPixelEvaluate,
    TOutput : IClusterRasterOutput>(
    uniform TVE evaluator,
    uniform TPixel pixel,
    uniform TOutput rasterOutput,
    uint3 groupID,
    uint groupThreadIndex)
{
    uint flatClusterIdx = groupID.x + groupID.y * 65535u;

    // Per-bin dispatch: CPU dispatches per bin, CurrentBin tells us which bin
    uint binIdx = ClusterSoftwareRasterUniformData.CurrentBin;
    uint binSWCount = SoftwareRasterBinMeta[binIdx].BinSWCount;
    if (flatClusterIdx >= binSWCount) return;

    uint clusterOffset = SoftwareRasterBinMeta[binIdx].ClusterOffset;
    uint binCount      = binSWCount;

    uint binnedIndex = clusterOffset + flatClusterIdx;
    uint binEntry = SoftwareRasterBinnedClusterIndexBuffer[binnedIndex];
    uint originalIndex = RasterBatchVisibleIndex(binEntry);

    DrawRequest req = SoftwareRasterVisibleClusters[originalIndex];
    uint viewIndex = DrawRequestViewIndex(req);
    ClusterViewUniforms view = LoadClusterView(
        SoftwareRasterViews,
        SoftwareRasterPolicyViews,
        viewIndex);
    uint pageOffset = req.PageOffset;
    uint clusterID = DrawRequestClusterID(req);
    uint realInstanceID = req.InstanceID;

    // ─── Load Cluster Geometry ───
    uint vertexBlobAddress, vertexBlobSize, tStart, vertexCount, triangleCount, idxOffset;
    LoadClusterGeometry(pageOffset, clusterID,
        vertexBlobAddress, vertexBlobSize, tStart, vertexCount, triangleCount, idxOffset);

    RenderInstanceTransform transform = GetCurrentInstanceTransform(realInstanceID);

    // ─── Debug Dump (lane 0, after all early-outs) ───
    if (ClusterSoftwareRasterUniformData.DebugDump != 0 && groupThreadIndex == 0)
    {
        uint debugIdx;
        SoftwareRasterDebugOutput.InterlockedAdd(0, 1, debugIdx);
        if (debugIdx < 8192)
        {
            uint dbase = 4 + debugIdx * 24;
            SoftwareRasterDebugOutput.Store(dbase + 0,  binnedIndex);
            SoftwareRasterDebugOutput.Store(dbase + 4,  originalIndex);
            SoftwareRasterDebugOutput.Store(dbase + 8,  clusterID);
            SoftwareRasterDebugOutput.Store(dbase + 12, triangleCount);
            SoftwareRasterDebugOutput.Store(dbase + 16, binCount);       // total clusters in bin
            SoftwareRasterDebugOutput.Store(dbase + 20, vertexCount);
        }
    }

    int4 scissor = int4(0, 0, int(view.ScreenWidth), int(view.ScreenHeight));
    uint visibleIndexPlusOne = originalIndex + 1;

    // ─── Read batch range from binned entry (pre-computed by binning shader) ───
    uint rangeStart, rangeEnd;
    DecodeRasterBatchRange(PageHeap, req, binEntry, rangeStart, rangeEnd);
    uint batchTriCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0;

    {
        uint localTriIdx = rangeStart + groupThreadIndex;
        bool bTriValid = groupThreadIndex < batchTriCount;

        // Decode 3 vertex indices
        uint3 vertIndices = uint3(0, 0, 0);
        if (bTriValid)
        {
            vertIndices.x = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 0);
            vertIndices.y = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 1);
            vertIndices.z = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 2);
        }

        // ─── Stage 1: Vertex Dedup + Transform ───
        VertexDedup dedup = DeduplicateVertIndexes(
            vertIndices,
            groupThreadIndex,
            bTriValid);

        // A cooked Cluster has at most 64 vertices. The group evaluates the compacted
        // set in two 32-lane rounds and fuses material deformation into each evaluation.
        [unroll]
        for (uint round = 0u; round < 2u; round++)
        {
            uint compactVertex = groupThreadIndex + round * SOFTWARE_RASTER_GROUP_SIZE;
            if (compactVertex < dedup.numUniqueVerts)
            {
                uint localVertexIndex = uint(FindNthSetBit2(
                    dedup.usedVertMask,
                    int(compactVertex)));
                VertexEvalContext ctx = MakeSWRasterVertexEvalContext(
                    vertexBlobAddress,
                    vertexBlobSize,
                    localVertexIndex,
                    realInstanceID,
                    transform);
                SoftwareRasterVertexCache[compactVertex] = EvaluateSWRasterClipPosition(
                    evaluator,
                    ctx,
                    view);
            }
        }
        GroupMemoryBarrierWithGroupSync();

        float4 clipVerts[3];
        if (bTriValid)
        {
            clipVerts[0] = SoftwareRasterVertexCache[dedup.cornerCacheIndices.x];
            clipVerts[1] = SoftwareRasterVertexCache[dedup.cornerCacheIndices.y];
            clipVerts[2] = SoftwareRasterVertexCache[dedup.cornerCacheIndices.z];
        }
        else
        {
            clipVerts[0] = 0.0.xxxx;
            clipVerts[1] = 0.0.xxxx;
            clipVerts[2] = 0.0.xxxx;
        }

        RasterizeClusterTriangleWave(
            pixel,
            rasterOutput,
            clipVerts,
            scissor,
            EncodeVisBuffer(visibleIndexPlusOne - 1u, localTriIdx),
            viewIndex,
            view.TargetPolicy,
            view.TargetViewIndex,
            bTriValid,
            view.ScreenWidth,
            view.ScreenHeight);

    }
}

#endif // SW_RASTER_CORE_SLANG
