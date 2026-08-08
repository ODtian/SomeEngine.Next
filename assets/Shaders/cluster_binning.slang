#include "cluster_common.slang"

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

ConstantBuffer<RasterDeformBinningUniforms> Uniforms;

// ─── Input ───
StructuredBuffer<uint4> VisibleClusters;

ByteAddressBuffer DrawArgs;                  // IndirectArgs: must stay raw, layout = CullDrawArgs
ByteAddressBuffer ClusterReadOffsetArgs;     // IndirectArgs: must stay raw, layout = CullDrawArgs
StructuredBuffer<uint> SlotBuffer;   // SOA layout, StructuredBuffer<uint>
ByteAddressBuffer PageHeap;                  // Cluster geometry pages (for reading VRBBatchInfo)

// ─── Output ───
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
RWStructuredBuffer<uint2> BinnedClusterIndexBuffer; // .x = visibleClusterIndex, .y = rangeStart<<16 | rangeEnd
RWByteAddressBuffer BinnedDrawArgs;          // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedHWDrawArgs;        // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinningDispatchArgs;     // IndirectDrawArgs: must stay raw
RWByteAddressBuffer BinnedSWDispatchArgs;    // Per-bin IndirectDispatchArgs: [0..MaxBins) = Total(SW+HW), [MaxBins..2*MaxBins) = SW only
RWStructuredBuffer<uint> ReserveCounters;    // [0] = total batch allocator, [1] = SW batch allocator
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
RWStructuredBuffer<uint2> DeformBinnedClusterIndexBuffer;
RWByteAddressBuffer PreDeformDispatchArgs;
RWStructuredBuffer<uint> DeformReserveCounters;
RWStructuredBuffer<uint> CacheOffsetsWrite;
RWStructuredBuffer<uint> CacheAllocationCounter;

static const uint CACHE_OFFSET_OVERFLOW = 0xFFFFFFFDu;
static const uint CACHE_OFFSET_UNINITIALIZED = 0xFFFFFFFEu;

#include "cluster_bin_io.slang"

// ─── Helper: decode VRB batch count and compute total entries ───
struct VRBDecoded
{
    uint batchCount;     // number of fast batches (≤5)
    uint triCounts[5];   // tri count per fast batch
};

VRBDecoded DecodeVRB(uint vrbInfo, uint triangleCount)
{
    VRBDecoded d;
    d.batchCount = min(((vrbInfo >> 25) & 0x7) + 1, 5u);
    for (uint i = 0; i < 5; i++)
    {
        if (i < d.batchCount)
        {
            d.triCounts[i] = ((vrbInfo >> (i * 5)) & 0x1F) + 1;
        }
        else
        {
            d.triCounts[i] = 0;
        }
    }
    return d;
}

bool EmitsSw(bool clusterWasClassifiedSW, uint batchIndex, uint encodedBatchCount)
{
    return clusterWasClassifiedSW && batchIndex < encodedBatchCount;
}

// ─── Helper: read binning payload from PageHeap ───
void ReadClusterData(
    uint4 entry,
    out uint vrbInfo,
    out uint triangleCount,
    out uint packedMaterials,
    out uint packedRanges)
{
    uint pageOffset = entry.x;
    uint clusterID = entry.y;
    uint clustersOff = PageHeap.Load(pageOffset + 16); // header.ClustersOffset
    uint clusterOffset = pageOffset + clustersOff + clusterID * GPU_CLUSTER_STRIDE_BYTES;
    uint packedCounts = PageHeap.Load(clusterOffset + 44);
    triangleCount = (packedCounts >> 8) & 0xFF;
    packedMaterials = PageHeap.Load(clusterOffset + 48);
    packedRanges = PageHeap.Load(clusterOffset + 52);
    vrbInfo = PageHeap.Load(clusterOffset + 60);
}

uint RasterKey(uint slotOffset, uint localMaterialIndex)
{
    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        Uniforms.RasterBinFieldIndex,
        Uniforms.SlotCapacity,
        Uniforms.RasterMaxBins);
}

uint DeformKey(uint slotOffset, uint localMaterialIndex)
{
    return SlotBin(
        SlotBuffer,
        slotOffset,
        localMaterialIndex,
        Uniforms.DeformBinFieldIndex,
        Uniforms.SlotCapacity,
        Uniforms.DeformMaxBins);
}

void ClearRasterBin(uint binIdx)
{
    ClearRaster(RasterBinMeta, binIdx, Uniforms.RasterMaxBins);
}

void ClearDeformBin(uint binIdx)
{
    ClearDeform(DeformBinMeta, binIdx, Uniforms.DeformMaxBins);
}

void WriteBinningDispatchArgs()
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint groupCount = (visibleCount + 63) / 64;
    StoreDispatchArgs(BinningDispatchArgs, 0u, DispatchArgs(groupCount, 1, 1));
}

void DecodeVisibleCluster(uint logicalIdx, out uint visibleIndex, out bool isSW)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    CullDrawArgs readOffset = LoadCullDrawArgs(ClusterReadOffsetArgs, 0u);
    uint swCount = args.SWCount;
    uint hwCount = args.HWCount;
    uint swBase = readOffset.SWCount;
    uint hwBase = Uniforms.MaxVisibleClusters - readOffset.HWCount - hwCount;

    isSW = logicalIdx < swCount;
    visibleIndex = isSW ? (swBase + logicalIdx) : (hwBase + logicalIdx - swCount);
}

[numthreads(64, 1, 1)]
void ResetRasterDeformBins(uint3 dtid : SV_DispatchThreadID)
{
    ClearRasterBin(dtid.x);
    ClearDeformBin(dtid.x);

    if (dtid.x == 0)
    {
        WriteBinningDispatchArgs();
        ReserveCounters[0] = 0;
        ReserveCounters[1] = 0;
        DeformReserveCounters[0] = 0;
        if (Uniforms.ResetCacheAllocationState != 0u)
        {
            ReserveCounters[2] = 0;
            ReserveCounters[3] = 0;
            DeformReserveCounters[1] = 0;
            CacheAllocationCounter[0] = 0;
            CacheAllocationCounter[1] = 0;
        }
    }
}

void CountRasterBatches(
    uint slotOffset,
    uint vrbInfo,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSW)
{
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        if (triStart >= triangleCount)
            break;

        uint triEnd = min(triStart + vrb.triCounts[b], triangleCount);
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = RasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        CountRaster(RasterBinMeta, binKey, emitSW);
        triStart = triEnd;
    }

    while (triStart < triangleCount)
    {
        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = RasterKey(slotOffset, localMaterialIndex);
        CountRaster(RasterBinMeta, binKey, false);
        triStart = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));
    }
}

void CountDeformClusters(uint slotOffset, uint triangleCount, uint packedMaterials, uint packedRanges)
{
    if (triangleCount == 0u)
        return;

    uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, 0u);
    uint binKey = DeformKey(slotOffset, localMaterialIndex);
    CountDeform(DeformBinMeta, binKey);
}

[numthreads(64, 1, 1)]
void CountRasterDeformBins(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleCluster(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];
    CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    uint slotOffset = GetClusterMaterialSlotOffset(entry.z);

    CountRasterBatches(slotOffset, vrbInfo, triangleCount, packedMaterials, packedRanges, isSW);
    CountDeformClusters(slotOffset, triangleCount, packedMaterials, packedRanges);
}

#define RASTER_DEFORM_RESERVE_BLOCK_SIZE 128

groupshared uint RasterReserveTotalPrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE];
groupshared uint RasterReserveSWPrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE];
groupshared uint DeformReservePrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE];
groupshared uint RasterReserveTotalBlockBase;
groupshared uint RasterReserveSWBlockBase;
groupshared uint DeformReserveBlockBase;

[numthreads(RASTER_DEFORM_RESERVE_BLOCK_SIZE, 1, 1)]
void ReserveRasterDeformBins(uint3 tid : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = tid.x;
    uint rasterSWCount = 0;
    uint rasterHWCount = 0;
    uint deformCount = 0;

    if (bin < Uniforms.RasterMaxBins)
    {
        rasterSWCount = RasterBinMeta[bin].BinSWCount;
        rasterHWCount = RasterBinMeta[bin].BinHWCount;
    }
    if (bin < Uniforms.DeformMaxBins)
    {
        deformCount = DeformBinMeta[bin].BinCount;
    }

    uint rasterTotalCount = rasterSWCount + rasterHWCount;
    RasterReserveTotalPrefix[groupIndex] = rasterTotalCount;
    RasterReserveSWPrefix[groupIndex] = rasterSWCount;
    DeformReservePrefix[groupIndex] = deformCount;
    GroupMemoryBarrierWithGroupSync();

    for (uint scanOffset = 1; scanOffset < RASTER_DEFORM_RESERVE_BLOCK_SIZE; scanOffset <<= 1)
    {
        uint rasterTotalAddend = groupIndex >= scanOffset ? RasterReserveTotalPrefix[groupIndex - scanOffset] : 0;
        uint rasterSWAddend = groupIndex >= scanOffset ? RasterReserveSWPrefix[groupIndex - scanOffset] : 0;
        uint deformAddend = groupIndex >= scanOffset ? DeformReservePrefix[groupIndex - scanOffset] : 0;
        GroupMemoryBarrierWithGroupSync();
        RasterReserveTotalPrefix[groupIndex] += rasterTotalAddend;
        RasterReserveSWPrefix[groupIndex] += rasterSWAddend;
        DeformReservePrefix[groupIndex] += deformAddend;
        GroupMemoryBarrierWithGroupSync();
    }

    uint rasterTotalInclusive = RasterReserveTotalPrefix[groupIndex];
    uint rasterSWInclusive = RasterReserveSWPrefix[groupIndex];
    uint deformInclusive = DeformReservePrefix[groupIndex];
    uint rasterTotalBlockSum = RasterReserveTotalPrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE - 1];
    uint rasterSWBlockSum = RasterReserveSWPrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE - 1];
    uint deformBlockSum = DeformReservePrefix[RASTER_DEFORM_RESERVE_BLOCK_SIZE - 1];

    if (groupIndex == 0)
    {
        InterlockedAdd(ReserveCounters[0], rasterTotalBlockSum, RasterReserveTotalBlockBase);
        InterlockedAdd(ReserveCounters[1], rasterSWBlockSum, RasterReserveSWBlockBase);
        InterlockedAdd(ReserveCounters[2], rasterTotalBlockSum);
        InterlockedAdd(ReserveCounters[3], rasterSWBlockSum);
        InterlockedAdd(DeformReserveCounters[0], deformBlockSum, DeformReserveBlockBase);
        InterlockedAdd(DeformReserveCounters[1], deformBlockSum);
    }
    GroupMemoryBarrierWithGroupSync();

    ReserveRaster(
        RasterBinMeta,
        BinnedDrawArgs,
        BinnedHWDrawArgs,
        BinnedSWDispatchArgs,
        bin,
        Uniforms.RasterMaxBins,
        RasterReserveTotalBlockBase,
        RasterReserveSWBlockBase,
        rasterTotalInclusive,
        rasterSWInclusive,
        rasterSWCount,
        rasterHWCount);

    ReserveDeform(
        DeformBinMeta,
        PreDeformDispatchArgs,
        bin,
        Uniforms.DeformMaxBins,
        DeformReserveBlockBase,
        deformInclusive,
        deformCount);
}

void ScatterRasterBatches(
    uint visibleIndex,
    uint slotOffset,
    uint vrbInfo,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSW)
{
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo, triangleCount);
    uint triStart = 0;
    for (uint b = 0; b < vrb.batchCount; b++)
    {
        if (triStart >= triangleCount)
            break;

        uint triEnd = triStart + vrb.triCounts[b];
        triEnd = min(triEnd, triangleCount);

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = RasterKey(slotOffset, localMaterialIndex);
        bool emitSW = EmitsSw(isSW, b, vrb.batchCount);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            binKey,
            visibleIndex,
            triStart,
            triEnd,
            emitSW);
        triStart = triEnd;
    }

    while (triStart < triangleCount)
    {
        uint triEnd = min(triStart + 32u, MaterialEnd(packedRanges, triStart, triangleCount));

        uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, triStart);
        uint binKey = RasterKey(slotOffset, localMaterialIndex);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            binKey,
            visibleIndex,
            triStart,
            triEnd,
            false);
        triStart = triEnd;
    }
}

void ScatterDeformClusters(
    uint visibleIndex,
    uint slotOffset,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges)
{
    if (triangleCount == 0u)
        return;

    uint localMaterialIndex = LocalMaterial(packedMaterials, packedRanges, 0u);
    uint binKey = DeformKey(slotOffset, localMaterialIndex);
    ScatterDeform(
        DeformBinMeta,
        DeformBinnedClusterIndexBuffer,
        CacheOffsetsWrite,
        binKey,
        visibleIndex,
        CACHE_OFFSET_UNINITIALIZED);
}

[numthreads(64, 1, 1)]
void ScatterRasterDeformBins(uint3 dtid : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    uint idx = dtid.x;
    if (idx >= visibleCount) return;

    uint visibleIndex;
    bool isSW;
    DecodeVisibleCluster(idx, visibleIndex, isSW);

    uint4 entry = VisibleClusters[visibleIndex];
    uint vrbInfo, triangleCount, packedMaterials, packedRanges;
    ReadClusterData(entry, vrbInfo, triangleCount, packedMaterials, packedRanges);
    uint slotOffset = GetClusterMaterialSlotOffset(entry.z);

    ScatterRasterBatches(visibleIndex, slotOffset, vrbInfo, triangleCount, packedMaterials, packedRanges, isSW);
    ScatterDeformClusters(visibleIndex, slotOffset, triangleCount, packedMaterials, packedRanges);
}
