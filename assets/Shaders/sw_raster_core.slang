#include "cluster_bin_common.slang"
// sw_raster.slang — Software Rasterizer Compute Shader
// Vertex-reuse batches + WaveQueue pixel distribution.
//
// 32 threads per group, 1 wave = 1 vertex-reuse batch.
// Stage 1: each unique user vertex is evaluated by one lane and retained in that lane.
// Stage 2: Triangle Setup (subpixel edge equations)
// Stage 3: WaveQueue Pixel Distribution (IWaveTask)

#ifndef SW_RASTER_CORE_SLANG
#define SW_RASTER_CORE_SLANG

#include "cluster_structures.slang"
#include "cluster_view_access.slang"
#include "common.slang"
#include "vertex_evaluate_interfaces.slang"

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

static const uint SOFTWARE_RASTER_GROUP_SIZE = 32u;
static const uint SOFTWARE_RASTER_CLUSTER_VERTEX_CAPACITY = 64u;

uint SoftwareRasterVertexRank(uint2 usedVertices, uint localVertex)
{
    uint bit = localVertex & 31u;
    uint lower = bit == 0u ? 0u : (1u << bit) - 1u;
    return localVertex < 32u
        ? countbits(usedVertices.x & lower)
        : countbits(usedVertices.x) + countbits(usedVertices.y & lower);
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

// ─── Generic SW Rasterizer Kernel ───

void SWRasterKernel<
    TOutput : IClusterRasterOutput>(
    uniform VertexEvaluate evaluator,
    uniform VisibilityEvaluate visibility,
    uniform TOutput rasterOutput,
    uint3 groupID,
    uint groupThreadIndex,
    uniform bool allowMaterialRanges = false)
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
    uint originalIndex = RasterWorkVisibleIndex(binEntry);

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

    int4 scissor = rasterOutput.scissor(
        originalIndex,
        view.TargetPolicy,
        view.TargetViewIndex,
        view.ScreenWidth,
        view.ScreenHeight);
    uint visibleIndexPlusOne = originalIndex + 1;

    uint rangeStart, rangeEnd;
    DecodeRasterWorkRange(PageHeap, req, binEntry, rangeStart, rangeEnd);
    uint rangeTriangleCount = rangeEnd > rangeStart ? rangeEnd - rangeStart : 0u;

    uint localTriIdx = rangeStart + groupThreadIndex;
    bool bTriValid = groupThreadIndex < rangeTriangleCount;
    uint3 vertIndices = 0u.xxx;
    if (bTriValid)
    {
        vertIndices.x = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 0u);
        vertIndices.y = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 1u);
        vertIndices.z = FetchVertexIndex(pageOffset, idxOffset, tStart, localTriIdx, 2u);
    }

    uint2 laneUsedVertices = 0u.xx;
    if (bTriValid)
    {
        [unroll]
        for (uint corner = 0u; corner < 3u; corner++)
        {
            uint localVertex = vertIndices[corner];
            if (localVertex < 32u)
                laneUsedVertices.x |= 1u << localVertex;
            else
                laneUsedVertices.y |= 1u << (localVertex - 32u);
        }
    }
    uint2 usedVertices = uint2(
        WaveActiveBitOr(laneUsedVertices.x),
        WaveActiveBitOr(laneUsedVertices.y));
    uint uniqueVertexCount = countbits(usedVertices.x) + countbits(usedVertices.y);

    // A cooked vertex-reuse batch contains at most 32 unique vertices. Assign the
    // sorted unique local indices to wave lanes; the evaluated user vertex never
    // leaves its producer lane. Cluster only transports clip position and asks the
    // user visibility evaluator to transport its opaque Varying.
    uint producerLocalVertex = 0xFFFFFFFFu;
    if (groupThreadIndex < uniqueVertexCount)
    {
        [loop]
        for (uint localVertex = 0u;
             localVertex < min(vertexCount, SOFTWARE_RASTER_CLUSTER_VERTEX_CAPACITY);
             localVertex++)
        {
            uint mask = localVertex < 32u ? usedVertices.x : usedVertices.y;
            if ((mask & (1u << (localVertex & 31u))) == 0u)
                continue;
            if (SoftwareRasterVertexRank(usedVertices, localVertex) == groupThreadIndex)
            {
                producerLocalVertex = localVertex;
                break;
            }
        }
    }

    float4 producerClip = 0.0.xxxx;
    VisibilityEvaluate.Varying producerVarying = {};
    if (producerLocalVertex != 0xFFFFFFFFu)
    {
        VertexEvalContext ctx = MakeSWRasterVertexEvalContext(
            vertexBlobAddress,
            vertexBlobSize,
            producerLocalVertex,
            realInstanceID,
            transform);
        VertexEvaluate.Vertex vertex = evaluator.evaluate(ctx);
        producerClip = mul(float4(evaluator.getPosition(vertex), 1.0), view.ViewProj);
        VisibilityVertexInput visibilityInput;
        visibilityInput.VisibleClusterIndex = originalIndex;
        visibilityInput.InstanceID = realInstanceID;
        visibilityInput.ViewIndex = viewIndex;
        visibilityInput.LocalVertexIndex = producerLocalVertex;
        producerVarying = visibility.makeVarying(vertex, visibilityInput);
    }

    float4 clipVerts[3];
    VisibilityEvaluate.Varying varyings[3];
    [unroll]
    for (uint corner = 0u; corner < 3u; corner++)
    {
        uint producerLane = bTriValid
            ? SoftwareRasterVertexRank(usedVertices, vertIndices[corner])
            : 0u;
        clipVerts[corner] = WaveReadLaneAt(producerClip, producerLane);
        varyings[corner] = visibility.readLane(producerVarying, producerLane);
    }

    RasterizeClusterTriangleWave<
        VertexEvaluate.Vertex,
        VisibilityEvaluate,
        TOutput>(
        visibility,
        rasterOutput,
        clipVerts,
        varyings,
        scissor,
        visibleIndexPlusOne - 1u,
        localTriIdx,
        viewIndex,
        view.TargetPolicy,
        view.TargetViewIndex,
        bTriValid,
        view.ScreenWidth,
        view.ScreenHeight);
}

#endif // SW_RASTER_CORE_SLANG
