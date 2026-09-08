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
    uint Pad1;
    uint Pad2;
};

struct VRBDecoded
{
    uint batchCount;
    uint triCounts[5];
};

VRBDecoded DecodeVRB(uint vrbInfo)
{
    VRBDecoded decoded;
    decoded.batchCount = min(((vrbInfo >> 25) & 0x7) + 1, 5u);
    for (uint index = 0u; index < 5u; index++)
    {
        decoded.triCounts[index] = index < decoded.batchCount
            ? ((vrbInfo >> (index * 5u)) & 0x1Fu) + 1u
            : 0u;
    }
    return decoded;
}

bool EmitsSw(bool clusterWasClassifiedSW, uint batchIndex, uint encodedBatchCount)
{
    return clusterWasClassifiedSW && batchIndex < encodedBatchCount;
}

uint RasterBatchVisibleIndex(uint batch)
{
    return batch & 0x3FFFFFu;
}

uint EncodeRasterBatch(uint visible, uint batchIndex)
{
    return visible | (batchIndex << 22u);
}

void DecodeRasterBatchRange(ByteAddressBuffer pageHeap, DrawRequest entry,
    uint batch, out uint triangleStart, out uint triangleEnd)
{
    triangleStart = 0u;
    triangleEnd = 0u;
    uint ordinal = batch >> 22u;
    // Tessellation consumes a batch by marking its ordinal; the visible index
    // remains valid for readers that fetch the source before decoding its range.
    if (ordinal == 0x3FFu)
        return;
    uint clusterOffset = entry.PageOffset + pageHeap.Load(entry.PageOffset + 16u) +
        DrawRequestClusterID(entry) * PACKED_CLUSTER_STRIDE_BYTES;
    uint triangleCount = (pageHeap.Load(clusterOffset + 60u) >> 8u) & 0xFFu;
    uint packedRanges = pageHeap.Load(clusterOffset + 72u);
    VRBDecoded vrb = DecodeVRB(pageHeap.Load(clusterOffset + 80u));
    for (uint index = 0u; index <= ordinal && triangleStart < triangleCount; index++)
    {
        triangleEnd = index < vrb.batchCount
            ? min(triangleStart + vrb.triCounts[index], triangleCount)
            : min(triangleStart + 32u, MaterialEnd(packedRanges, triangleStart, triangleCount));
        if (index == ordinal)
            return;
        triangleStart = triangleEnd;
    }
    triangleEnd = triangleStart;
}

void ReadClusterBinData(
    ByteAddressBuffer pageHeap,
    DrawRequest entry,
    out uint vrbInfo,
    out uint triangleCount,
    out uint packedMaterials,
    out uint packedRanges)
{
    uint clusterOffset =
        entry.PageOffset + pageHeap.Load(entry.PageOffset + 16u) +
        DrawRequestClusterID(entry) * PACKED_CLUSTER_STRIDE_BYTES;
    uint packedCounts = pageHeap.Load(clusterOffset + 60u);
    triangleCount = (packedCounts >> 8u) & 0xFFu;
    packedMaterials = pageHeap.Load(clusterOffset + 68u);
    packedRanges = pageHeap.Load(clusterOffset + 72u);
    vrbInfo = pageHeap.Load(clusterOffset + 80u);
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
