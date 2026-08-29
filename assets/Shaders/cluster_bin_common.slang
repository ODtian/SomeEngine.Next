#ifndef CLUSTER_BIN_COMMON_SLANG
#define CLUSTER_BIN_COMMON_SLANG

#include "cluster_structures.slang"
#include "cluster_bin_io.slang"

struct RasterDeformBinningUniforms
{
    uint RasterMaxBins;
    uint DeformMaxBins;
    uint SlotCapacity;
    uint RasterBinFieldIndex;
    uint DeformBinFieldIndex;
    uint MaxVisibleClusters;
    uint ResetCacheAllocationState;
    uint Pad0;
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

void ReadClusterBinData(
    ByteAddressBuffer pageHeap,
    uint4 entry,
    out uint vrbInfo,
    out uint triangleCount,
    out uint packedMaterials,
    out uint packedRanges)
{
    uint clusterOffset =
        entry.x + pageHeap.Load(entry.x + 16u) + entry.y * GPU_CLUSTER_STRIDE_BYTES;
    uint packedCounts = pageHeap.Load(clusterOffset + 44u);
    triangleCount = (packedCounts >> 8u) & 0xFFu;
    packedMaterials = pageHeap.Load(clusterOffset + 48u);
    packedRanges = pageHeap.Load(clusterOffset + 52u);
    vrbInfo = pageHeap.Load(clusterOffset + 60u);
}

uint RasterKey(
    StructuredBuffer<uint> slots,
    RasterDeformBinningUniforms uniforms,
    uint slotOffset,
    uint localMaterialIndex)
{
    return SlotBin(
        slots,
        slotOffset,
        localMaterialIndex,
        uniforms.RasterBinFieldIndex,
        uniforms.SlotCapacity,
        uniforms.RasterMaxBins);
}

uint DeformKey(
    StructuredBuffer<uint> slots,
    RasterDeformBinningUniforms uniforms,
    uint slotOffset,
    uint localMaterialIndex)
{
    return SlotBin(
        slots,
        slotOffset,
        localMaterialIndex,
        uniforms.DeformBinFieldIndex,
        uniforms.SlotCapacity,
        uniforms.DeformMaxBins);
}

void DecodeVisibleCluster(
    ByteAddressBuffer drawArgs,
    ByteAddressBuffer readOffsetArgs,
    uint maxVisibleClusters,
    uint logicalIndex,
    out uint visibleIndex,
    out bool isSoftware)
{
    CullDrawArgs args = LoadCullDrawArgs(drawArgs, 0u);
    CullDrawArgs offset = LoadCullDrawArgs(readOffsetArgs, 0u);
    isSoftware = logicalIndex < args.SWCount;
    uint hardwareBase = maxVisibleClusters - offset.HWCount - args.HWCount;
    visibleIndex = isSoftware
        ? offset.SWCount + logicalIndex
        : hardwareBase + logicalIndex - args.SWCount;
}

#endif
