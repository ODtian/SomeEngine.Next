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
groupshared uint Prefix[BLOCK_SIZE];
groupshared uint BlockBase;

[shader("compute")]
[numthreads(BLOCK_SIZE, 1, 1)]
void ReserveShadeBins(uint3 threadId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = threadId.x;
    uint tiles = bin < Uniforms.ShadeBinCount ? BinCounts[bin * 2u] : 0u;
    uint quads = bin < Uniforms.ShadeBinCount ? BinCounts[bin * 2u + 1u] : 0u;
    uint count = tiles + quads;
    Prefix[groupIndex] = count;
    GroupMemoryBarrierWithGroupSync();

    for (uint offset = 1u; offset < BLOCK_SIZE; offset <<= 1u)
    {
        uint addend = groupIndex >= offset ? Prefix[groupIndex - offset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        Prefix[groupIndex] += addend;
        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0u)
        InterlockedAdd(ReserveCounters[0], Prefix[BLOCK_SIZE - 1u], BlockBase);
    GroupMemoryBarrierWithGroupSync();

    if (bin >= Uniforms.ShadeBinCount) return;
    uint offset = BlockBase + Prefix[groupIndex] - count;
    BinOffsets[bin * 2u] = offset;
    BinOffsets[bin * 2u + 1u] = offset + tiles;
    ScatterCounts[bin * 2u] = 0u;
    ScatterCounts[bin * 2u + 1u] = 0u;
    WriteDispatch(IndirectArgs, bin, tiles + (quads + 15u) / 16u);
}
