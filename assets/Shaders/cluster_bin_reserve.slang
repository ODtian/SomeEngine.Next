#include "cluster_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterDeformBinningUniforms> Uniforms;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinnedDrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinnedHWDrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinnedSWDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer PreDeformDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeformReserveCounters;

static const uint BLOCK_SIZE = 128u;
groupshared uint RasterTotalPrefix[BLOCK_SIZE];
groupshared uint RasterSoftwarePrefix[BLOCK_SIZE];
groupshared uint DeformPrefix[BLOCK_SIZE];
groupshared uint RasterTotalBase;
groupshared uint RasterSoftwareBase;
groupshared uint DeformBase;

[shader("compute")]
[numthreads(BLOCK_SIZE, 1, 1)]
void ReserveBins(uint3 threadId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    uint bin = threadId.x;
    uint softwareCount = bin < Uniforms.RasterMaxBins
        ? RasterBinMeta[bin].BinSWCount
        : 0u;
    uint hardwareCount = bin < Uniforms.RasterMaxBins
        ? RasterBinMeta[bin].BinHWCount
        : 0u;
    uint deformCount = bin < Uniforms.DeformMaxBins
        ? DeformBinMeta[bin].BinCount
        : 0u;

    RasterTotalPrefix[groupIndex] = softwareCount + hardwareCount;
    RasterSoftwarePrefix[groupIndex] = softwareCount;
    DeformPrefix[groupIndex] = deformCount;
    GroupMemoryBarrierWithGroupSync();

    for (uint offset = 1u; offset < BLOCK_SIZE; offset <<= 1u)
    {
        uint total = groupIndex >= offset ? RasterTotalPrefix[groupIndex - offset] : 0u;
        uint software = groupIndex >= offset ? RasterSoftwarePrefix[groupIndex - offset] : 0u;
        uint deform = groupIndex >= offset ? DeformPrefix[groupIndex - offset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        RasterTotalPrefix[groupIndex] += total;
        RasterSoftwarePrefix[groupIndex] += software;
        DeformPrefix[groupIndex] += deform;
        GroupMemoryBarrierWithGroupSync();
    }

    uint blockLast = BLOCK_SIZE - 1u;
    if (groupIndex == 0u)
    {
        InterlockedAdd(ReserveCounters[0], RasterTotalPrefix[blockLast], RasterTotalBase);
        InterlockedAdd(ReserveCounters[1], RasterSoftwarePrefix[blockLast], RasterSoftwareBase);
        InterlockedAdd(ReserveCounters[2], RasterTotalPrefix[blockLast]);
        InterlockedAdd(ReserveCounters[3], RasterSoftwarePrefix[blockLast]);
        InterlockedAdd(DeformReserveCounters[0], DeformPrefix[blockLast], DeformBase);
        InterlockedAdd(DeformReserveCounters[1], DeformPrefix[blockLast]);
    }
    GroupMemoryBarrierWithGroupSync();

    ReserveRaster(
        RasterBinMeta,
        BinnedDrawArgs,
        BinnedHWDrawArgs,
        BinnedSWDispatchArgs,
        bin,
        Uniforms.RasterMaxBins,
        RasterTotalBase,
        RasterSoftwareBase,
        RasterTotalPrefix[groupIndex],
        RasterSoftwarePrefix[groupIndex],
        softwareCount,
        hardwareCount);
    ReserveDeform(
        DeformBinMeta,
        PreDeformDispatchArgs,
        bin,
        Uniforms.DeformMaxBins,
        DeformBase,
        DeformPrefix[groupIndex],
        deformCount);
}
