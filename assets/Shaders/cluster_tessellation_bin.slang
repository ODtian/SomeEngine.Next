#include "cluster_tessellation_types.slang"
#include "cluster_structures.slang"
#include "resource_effects.slang"
#include "wave_aggregation.slang"

ConstantBuffer<TessellationBinUniforms> Uniforms;
ByteAddressBuffer TessellationCountersRead;
StructuredBuffer<TessellationFinalPatch> SourcePatches;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScatterCounts;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounter;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinDispatchArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer HardwareDrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer SourceDispatchArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> GroupedPatchIndices;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TessellationCountersWrite;

uint TessellationOutputBin(TessellationPatch patch)
{
    uint3 factors = TessellationPatchEdgeFactors(patch);
    uint factor = min(max(factors.x, max(factors.y, factors.z)), TESS_PATTERN_MAX_FACTOR);
    return TessellationPatchRasterBin(patch) * TESS_PATTERN_MAX_FACTOR + factor - 1u;
}

void StoreTessellationDraw(uint bin, uint count, uint offset)
{
    uint factor = bin % TESS_PATTERN_MAX_FACTOR + 1u;
    uint address = bin * 20u;
    HardwareDrawArgs.Store4(address, uint4(3u * factor * factor, count,
        TESS_PATTERN_TRIANGLE_OFFSETS[factor] * 3u, 0u));
    HardwareDrawArgs.Store(address + 16u, offset);
}

uint TessellationSourceCount()
{
    return min(
        TessellationCountersRead.Load(Uniforms.SourceCounterOffset),
        Uniforms.MaxPatchItems);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResetTessellationBins(uint3 threadId : SV_DispatchThreadID)
{
    uint bin = threadId.x;
    if (bin < Uniforms.BinCount)
    {
        BinCounts[bin] = 0u;
        BinOffsets[bin] = 0u;
        ScatterCounts[bin] = 0u;
        uint diceArgumentOffset = bin * 12u;
        BinDispatchArgs.Store(diceArgumentOffset + 0u, 0u);
        BinDispatchArgs.Store(diceArgumentOffset + 4u, 1u);
        BinDispatchArgs.Store(diceArgumentOffset + 8u, 1u);
        StoreTessellationDraw(bin, 0u, 0u);
    }
    if (bin == 0u)
    {
        ReserveCounter[0] = 0u;
        uint sourceGroups = (TessellationSourceCount() + 63u) / 64u;
        SourceDispatchArgs.Store(0u, min(sourceGroups, 65535u));
        SourceDispatchArgs.Store(
            4u,
            max(1u, (sourceGroups + 65534u) / 65535u));
        SourceDispatchArgs.Store(8u, 1u);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CountTessellationBins(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x + threadId.y * 65535u * 64u;
    uint sourceCount = TessellationSourceCount();
    if (index >= sourceCount)
        return;
    TessellationPatch patch = DecodeTessellationFinalPatch(SourcePatches[index]);
    uint bin = TessellationOutputBin(patch);
    if (bin >= Uniforms.BinCount)
    {
        uint ignored;
        TessellationCountersWrite.InterlockedAdd(TESS_COUNTER_OVERFLOW, 1u, ignored);
        return;
    }
    uint4 matchingLanes = WaveMatchingLanes(bin);
    uint leader = WaveMaskFirstLane(matchingLanes);
    if (WaveGetLaneIndex() == leader)
    {
        uint ignoredCount;
        InterlockedAdd(
            BinCounts[bin],
            WaveMaskBitCount(matchingLanes),
            ignoredCount);
    }
}

static const uint TESS_BIN_PREFIX_THREADS = 128u;
// One slot per possible subgroup keeps the block prefix valid for subgroup sizes
// from 1 through 128 instead of assuming exactly four wave32 values.
groupshared uint TessBinWaveTotals[TESS_BIN_PREFIX_THREADS];
groupshared uint TessBinBase;

[shader("compute")]
[numthreads(TESS_BIN_PREFIX_THREADS, 1, 1)]
void ReserveTessellationBins(
    uint3 threadId : SV_DispatchThreadID,
    uint groupThreadIndex : SV_GroupIndex)
{
    uint bin = threadId.x;
    uint count = bin < Uniforms.BinCount ? BinCounts[bin] : 0u;
    TessBinWaveTotals[groupThreadIndex] = 0u;
    GroupMemoryBarrierWithGroupSync();

    uint waveSize = WaveGetLaneCount();
    uint waveIndex = groupThreadIndex / waveSize;
    uint wavePrefix = WavePrefixSum(count);
    uint waveTotal = WaveActiveSum(count);
    if (WaveIsFirstLane())
        TessBinWaveTotals[waveIndex] = waveTotal;
    GroupMemoryBarrierWithGroupSync();

    if (groupThreadIndex == 0u)
    {
        uint waveCount = (TESS_BIN_PREFIX_THREADS + waveSize - 1u) / waveSize;
        uint blockCount = 0u;
        for (uint wave = 0u; wave < waveCount; wave++)
            blockCount += TessBinWaveTotals[wave];
        InterlockedAdd(ReserveCounter[0], blockCount, TessBinBase);
    }
    GroupMemoryBarrierWithGroupSync();

    if (bin >= Uniforms.BinCount)
        return;
    uint blockPrefix = 0u;
    for (uint wave = 0u; wave < waveIndex; wave++)
        blockPrefix += TessBinWaveTotals[wave];
    uint offset = TessBinBase + blockPrefix + wavePrefix;
    uint available = offset < Uniforms.MaxPatchItems
        ? min(count, Uniforms.MaxPatchItems - offset)
        : 0u;
    BinOffsets[bin] = min(offset, Uniforms.MaxPatchItems);
    ScatterCounts[bin] = 0u;
    StoreTessellationDraw(bin, available, min(offset, Uniforms.MaxPatchItems));
    if (bin % TESS_PATTERN_MAX_FACTOR == 0u)
    {
        uint diceGroups = 0u;
        uint meshGroups = 0u;
        [unroll]
        for (uint factor = 1u; factor <= TESS_PATTERN_MAX_FACTOR; factor++)
        {
            meshGroups += BinCounts[bin + factor - 1u];
            uint patchesPerGroup = 32u / TessellationDicingLanes(factor);
            diceGroups += (BinCounts[bin + factor - 1u] + patchesPerGroup - 1u) / patchesPerGroup;
        }
        uint address = (bin / TESS_PATTERN_MAX_FACTOR) * 12u;
        HardwareDrawArgs.Store3(Uniforms.BinCount * 20u + address,
            uint3(min(meshGroups, 65535u), max(1u, (meshGroups + 65534u) / 65535u), 1u));
        BinDispatchArgs.Store3(address, uint3(min(diceGroups, 65535u),
            max(1u, (diceGroups + 65534u) / 65535u), 1u));
    }
    if (available != count)
    {
        uint ignored;
        TessellationCountersWrite.InterlockedAdd(
            TESS_COUNTER_OVERFLOW,
            count - available,
            ignored);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterTessellationBins(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x + threadId.y * 65535u * 64u;
    uint sourceCount = TessellationSourceCount();
    if (index >= sourceCount)
        return;
    TessellationPatch patch = DecodeTessellationFinalPatch(SourcePatches[index]);
    uint bin = TessellationOutputBin(patch);
    if (bin >= Uniforms.BinCount)
        return;
    uint4 matchingLanes = WaveMatchingLanes(bin);
    uint leader = WaveMaskFirstLane(matchingLanes);
    uint groupBase = 0u;
    if (WaveGetLaneIndex() == leader)
    {
        InterlockedAdd(
            ScatterCounts[bin],
            WaveMaskBitCount(matchingLanes),
            groupBase);
    }
    groupBase = WaveReadLaneAt(groupBase, leader);
    uint localIndex = groupBase + WaveMaskPrefix(
        matchingLanes,
        WaveGetLaneIndex());
    uint count = BinCounts[bin];
    uint destination = BinOffsets[bin] + localIndex;
    if (localIndex < count && destination < Uniforms.MaxPatchItems)
        GroupedPatchIndices[destination] = index;
}
