#include "shade_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer IndirectArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScatterCounts;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
static const uint BLOCK_SIZE = 128u;

[shader("compute")]
[numthreads(BLOCK_SIZE, 1, 1)]
void ReserveShadeBins(uint3 threadId : SV_DispatchThreadID)
{
    uint bin = threadId.x;
    if (bin >= Uniforms.ShadeBinCount) return;
    uint tiles = BinCounts[bin * 2u];
    uint quads = BinCounts[bin * 2u + 1u];
    uint count = tiles + quads;
    // Each bin stores its own offsets, so waves can allocate in any order.
    uint waveCount = WaveActiveSum(count);
    uint first = 0u;
    if (WaveIsFirstLane() && waveCount != 0u)
        InterlockedAdd(ReserveCounters[0], waveCount, first);
    uint offset = WaveReadLaneFirst(first) + WavePrefixSum(count);
    BinOffsets[bin * 2u] = Uniforms.BinItemCapacity != 0u
        ? bin * Uniforms.BinItemCapacity : offset;
    BinOffsets[bin * 2u + 1u] = Uniforms.BinItemCapacity != 0u
        ? (bin + 1u) * Uniforms.BinItemCapacity - quads : offset + tiles;
    ScatterCounts[bin * 2u] = 0u;
    ScatterCounts[bin * 2u + 1u] = 0u;
    WriteDispatch(IndirectArgs, bin, tiles + (quads + 15u) / 16u);
}
