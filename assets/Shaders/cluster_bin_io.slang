#ifndef CLUSTER_BIN_IO_SLANG
#define CLUSTER_BIN_IO_SLANG

#include "wave_aggregation.slang"

DispatchArgs BinDispatch(uint count)
{
    uint x = min(count, 65535u);
    uint y = max(1u, (count + 65534u) / 65535u);
    return DispatchArgs(x, y, 1);
}

bool IsBin(uint bin)
{
    return bin != BIN_INVALID;
}

uint ValidBin(uint key, uint maxBins)
{
    return key != SLOT_INVALID && key < maxBins ? key : BIN_INVALID;
}

uint SlotBin(
    StructuredBuffer<uint> slots,
    uint slotOffset,
    uint localMaterialIndex,
    uint capacity,
    uint maxBins)
{
    uint key = GetStageSlot(slots, slotOffset, localMaterialIndex, capacity);
    return ValidBin(key & 0x0FFFFFFFu, maxBins);
}

void WriteDispatch(
    RWByteAddressBuffer args,
    uint row,
    uint count)
{
    StoreDispatchArgs(args, row * 12u, BinDispatch(count));
}

void ClearRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    uint bin,
    uint maxBins)
{
    if (bin >= maxBins) return;

    bins[bin].BinSWCount = 0;
    bins[bin].BinHWCount = 0;
    bins[bin].SWCursor = 0;
    bins[bin].HWCursor = 0;
    bins[bin].ClusterOffset = 0;
    bins[bin].BinCapacity = 0;
    bins[bin].HardwareOffset = 0;
    bins[bin].DepthBucketEnabled = 0;
}

void CountRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    uint bin,
    bool sw)
{
    if (!IsBin(bin)) return;

    uint key = (bin << 1u) | (sw ? 1u : 0u);
    uint4 matching = WaveMatchingLanes(key);
    uint leader = WaveMaskFirstLane(matching);
    if (WaveGetLaneIndex() == leader)
    {
        uint unused;
        if (sw)
            InterlockedAdd(bins[bin].BinSWCount, WaveMaskBitCount(matching), unused);
        else
            InterlockedAdd(bins[bin].BinHWCount, WaveMaskBitCount(matching), unused);
    }
}

void CountShade(
    RWStructuredBuffer<uint> counts,
    uint bin)
{
    if (!IsBin(bin)) return;

    uint4 matching = WaveMatchingLanes(bin);
    uint leader = WaveMaskFirstLane(matching);
    if (WaveGetLaneIndex() == leader)
    {
        uint unused;
        InterlockedAdd(counts[bin], WaveMaskBitCount(matching), unused);
    }
}

void ReserveRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    RWByteAddressBuffer hwArgs,
    RWByteAddressBuffer swArgs,
    uint bin,
    uint maxBins,
    uint totalBase,
    uint swBase,
    uint totalInc,
    uint swInc,
    uint swCount,
    uint hwCount)
{
    if (bin >= maxBins) return;

    uint total = swCount + hwCount;
    uint totalOffset = totalBase + totalInc - total;
    uint swOffset = swBase + swInc - swCount;
    uint hardwareOffset = totalOffset + swCount;

    bins[bin].ClusterOffset = totalOffset;
    bins[bin].BinCapacity = total;
    bins[bin].SWCursor = 0;
    bins[bin].HWCursor = 0;
    bins[bin].SWOffset = swOffset;
    bins[bin].HardwareOffset = hardwareOffset;

    StoreDrawInstancedArgs(
        hwArgs,
        bin * 32u,
        DrawInstancedArgs(96, hwCount, 0, hardwareOffset));
    StoreDispatchArgs(hwArgs, bin * 32u + 16u, BinDispatch(hwCount));
    hwArgs.Store(bin * 32u + 28u, 0u);
    WriteDispatch(swArgs, bin, swCount);
}

bool ScatterRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    RWStructuredBuffer<uint> indices,
    uint bin,
    uint visible,
    uint workIndex,
    bool sw,
    RWStructuredBuffer<uint> depthBuckets,
    uint depthBucket)
{
    if (!IsBin(bin)) return true;

    uint offset = bins[bin].ClusterOffset;
    uint key = (bin << 1u) | (sw ? 1u : 0u);
    uint4 matching = WaveMatchingLanes(key);
    uint leader = WaveMaskFirstLane(matching);
    uint localBase = 0u;
    if (WaveGetLaneIndex() == leader)
    {
        if (sw)
            InterlockedAdd(bins[bin].SWCursor, WaveMaskBitCount(matching), localBase);
        else
            InterlockedAdd(bins[bin].HWCursor, WaveMaskBitCount(matching), localBase);
    }
    localBase = WaveReadLaneAt(localBase, leader);
    uint local = localBase + WaveMaskPrefix(matching, WaveGetLaneIndex());
    uint count = sw ? bins[bin].BinSWCount : bins[bin].BinHWCount;
    uint index = offset + (sw ? 0u : bins[bin].BinSWCount) + local;
    if (bins[bin].DepthBucketEnabled != 0u)
    {
        uint bucketItem;
        InterlockedAdd(depthBuckets[bin * 512u + (sw ? 0u : 256u) + depthBucket], 1u, bucketItem);
        index = offset + (sw ? 0u : bins[bin].BinSWCount) + bucketItem;
    }
    else if (sw)
        index = offset + bins[bin].BinSWCount - 1u - local;
    uint capacity, stride;
    indices.GetDimensions(capacity, stride);
    if (local >= count || index >= capacity) return false;
    indices[index] = visible | (workIndex << 22u);
    return true;
}

#endif
