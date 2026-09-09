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
    return ValidBin(key, maxBins);
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

    bins[bin].ClusterOffset = totalOffset;
    bins[bin].BinCapacity = total;
    bins[bin].SWCursor = 0;
    bins[bin].HWCursor = 0;
    bins[bin].SWOffset = swOffset;

    StoreDrawInstancedArgs(
        hwArgs,
        bin * 32u,
        DrawInstancedArgs(96, hwCount, 0, totalOffset + swCount));
    StoreDispatchArgs(hwArgs, bin * 32u + 16u, BinDispatch(hwCount));
    hwArgs.Store(bin * 32u + 28u, 0u);
    WriteDispatch(swArgs, bin, swCount);
}

void ScatterRaster(
    RWStructuredBuffer<RasterBinEntry> bins,
    RWStructuredBuffer<uint> indices,
    uint bin,
    uint visible,
    uint batchIndex,
    bool sw)
{
    if (!IsBin(bin)) return;

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
    if (sw)
    {
        indices[offset + local] = visible | (batchIndex << 22u);
        return;
    }

    uint swCount = bins[bin].BinSWCount;
    indices[offset + swCount + local] = visible | (batchIndex << 22u);
}

#endif
