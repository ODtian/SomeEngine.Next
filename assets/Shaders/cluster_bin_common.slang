#ifndef CLUSTER_BIN_COMMON_SLANG
#define CLUSTER_BIN_COMMON_SLANG

#include "cluster_structures.slang"
#include "cluster_bin_io.slang"

struct RasterBinningUniforms
{
    uint RasterMaxBins;
    uint SlotCapacity;
    uint MaxVisibleClusters;
    uint CollectMetrics;
    uint PolicyTarget;
    uint ScreenWidth;
    uint ScreenHeight;
    uint ViewCount;
};

static const uint RASTER_WORK_RANGE_BIT = 0x200u;
static const uint RASTER_WORK_CONSUMED = 0x3FFu;

uint RasterWorkVisibleIndex(uint work)
{
    return work & 0x3FFFFFu;
}

uint RasterRangeWorkIndex(uint firstMaterialRange, uint materialRangeCount)
{
    return RASTER_WORK_RANGE_BIT |
        (firstMaterialRange & 3u) |
        ((materialRangeCount - 1u) << 2u);
}

bool RasterWorkIsRange(uint workIndex)
{
    return workIndex != RASTER_WORK_CONSUMED && (workIndex & RASTER_WORK_RANGE_BIT) != 0u;
}

uint RasterWorkFirstMaterialRange(uint workIndex)
{
    return workIndex & 3u;
}

uint RasterWorkMaterialRangeCount(uint workIndex)
{
    return ((workIndex >> 2u) & 3u) + 1u;
}

bool RasterSoftwareUsesVertReuseBatches(
    StructuredBuffer<uint> stages,
    RasterBinningUniforms uniforms,
    uint stageOffset,
    uint localMaterialIndex)
{
    uint value = GetStageSlot(stages, stageOffset, localMaterialIndex, uniforms.SlotCapacity);
    return value != SLOT_INVALID && (value & 0x80000000u) != 0u;
}

bool RasterUsesVertReuseBatches(
    StructuredBuffer<uint> stages, RasterBinningUniforms uniforms,
    uint stageOffset, uint localMaterialIndex, bool isSoftware, uint vertexCount, uint triangleCount)
{
    uint value = GetStageSlot(stages, stageOffset, localMaterialIndex, uniforms.SlotCapacity);
    return isSoftware ? (value & 0x80000000u) != 0u &&
        ((value & 0x20000000u) == 0u || vertexCount > 64u) :
        (value & 0x40000000u) == 0u || vertexCount > 64u || triangleCount > 128u;
}

uint4 LoadVertReuseBatchWords(ByteAddressBuffer pageHeap, uint clusterOffset)
{
    return uint4(
        pageHeap.Load(clusterOffset + 80u),
        pageHeap.Load(clusterOffset + 84u),
        pageHeap.Load(clusterOffset + 88u),
        pageHeap.Load(clusterOffset + 92u));
}

void DecodeRasterWorkRange(
    ByteAddressBuffer pageHeap,
    DrawRequest entry,
    uint work,
    out uint triangleStart,
    out uint triangleEnd)
{
    triangleStart = 0u;
    triangleEnd = 0u;
    uint workIndex = work >> 22u;
    if (workIndex == RASTER_WORK_CONSUMED)
        return;

    uint clusterOffset = entry.PageOffset + pageHeap.Load(entry.PageOffset + 16u) +
        DrawRequestClusterID(entry) * PACKED_CLUSTER_STRIDE_BYTES;
    uint triangleCount = (pageHeap.Load(clusterOffset + 60u) >> 8u) & 0xFFu;
    uint packedRanges = pageHeap.Load(clusterOffset + 72u);
    uint rangeCount = MaterialRangeCount(packedRanges, triangleCount);

    if (RasterWorkIsRange(workIndex))
    {
        uint firstRange = RasterWorkFirstMaterialRange(workIndex);
        uint workRangeCount = RasterWorkMaterialRangeCount(workIndex);
        if (firstRange >= rangeCount || firstRange + workRangeCount > rangeCount)
            return;
        triangleStart = firstRange == 0u
            ? 0u
            : MaterialRangeEnd(packedRanges, triangleCount, firstRange - 1u);
        triangleEnd = MaterialRangeEnd(
            packedRanges,
            triangleCount,
            firstRange + workRangeCount - 1u);
        return;
    }

    uint4 words = LoadVertReuseBatchWords(pageHeap, clusterOffset);
    uint flatBatchIndex = 0u;
    for (uint rangeIndex = 0u; rangeIndex < rangeCount; rangeIndex++)
    {
        uint rangeEnd = MaterialRangeEnd(packedRanges, triangleCount, rangeIndex);
        uint batchCount = VertReuseBatchCount(words, rangeIndex);
        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            uint count = VertReuseBatchTriangleCount(words, flatBatchIndex);
            triangleEnd = min(triangleStart + count, rangeEnd);
            if (flatBatchIndex == workIndex)
                return;
            triangleStart = triangleEnd;
            flatBatchIndex++;
        }
        triangleStart = rangeEnd;
    }

    triangleEnd = triangleStart;
}

void ReadClusterBinData(
    ByteAddressBuffer pageHeap,
    DrawRequest entry,
    out uint4 vertReuseBatchWords,
    out uint vertexCount,
    out uint triangleCount,
    out uint3 packedMaterials,
    out uint packedRanges)
{
    uint clusterOffset =
        entry.PageOffset + pageHeap.Load(entry.PageOffset + 16u) +
        DrawRequestClusterID(entry) * PACKED_CLUSTER_STRIDE_BYTES;
    uint packedCounts = pageHeap.Load(clusterOffset + 60u);
    vertexCount = packedCounts & 0xFFu;
    triangleCount = (packedCounts >> 8u) & 0xFFu;
    packedMaterials = LoadClusterMaterials(pageHeap, entry.PageOffset, clusterOffset);
    packedRanges = pageHeap.Load(clusterOffset + 72u);
    vertReuseBatchWords = LoadVertReuseBatchWords(pageHeap, clusterOffset);
}

// Existing callers that need material ranges can ignore the vertex count.
void ReadClusterBinData(
    ByteAddressBuffer pageHeap, DrawRequest entry, out uint4 vertReuseBatchWords,
    out uint triangleCount, out uint3 packedMaterials, out uint packedRanges)
{
    uint vertexCount;
    ReadClusterBinData(pageHeap, entry, vertReuseBatchWords, vertexCount,
        triangleCount, packedMaterials, packedRanges);
}

uint RasterKey(
    StructuredBuffer<uint> slots,
    RasterBinningUniforms uniforms,
    uint slotOffset,
    uint localMaterialIndex)
{
    return SlotBin(
        slots,
        slotOffset,
        localMaterialIndex,
        uniforms.SlotCapacity,
        uniforms.RasterMaxBins);
}

void DecodeVisibleCluster(
    ByteAddressBuffer drawArgs,
    ByteAddressBuffer readOffsetArgs0,
    ByteAddressBuffer readOffsetArgs1,
    ByteAddressBuffer readOffsetArgs2,
    uint maxVisibleClusters,
    uint logicalIndex,
    out uint visibleIndex,
    out bool isSoftware)
{
    CullDrawArgs args = LoadCullDrawArgs(drawArgs, 0u);
    CullDrawArgs offset0 = LoadCullDrawArgs(readOffsetArgs0, 0u);
    CullDrawArgs offset1 = LoadCullDrawArgs(readOffsetArgs1, 0u);
    CullDrawArgs offset2 = LoadCullDrawArgs(readOffsetArgs2, 0u);
    uint softwareOffset = offset0.SWCount + offset1.SWCount + offset2.SWCount;
    uint hardwareOffset = offset0.HWCount + offset1.HWCount + offset2.HWCount;
    isSoftware = logicalIndex < args.SWCount;
    uint hardwareBase = maxVisibleClusters - hardwareOffset - args.HWCount;
    visibleIndex = isSoftware
        ? softwareOffset + logicalIndex
        : hardwareBase + logicalIndex - args.SWCount;
}

#endif
