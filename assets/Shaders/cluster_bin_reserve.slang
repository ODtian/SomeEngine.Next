#include "cluster_bin_common.slang"
#include "cluster_tessellation_types.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinnedHWDrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinnedSWDispatchArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer TessellationSeedDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PolicyRasterWorkCounter;
static const uint BLOCK_SIZE = 128u;
groupshared uint RasterTotalPrefix[BLOCK_SIZE];
groupshared uint RasterSoftwarePrefix[BLOCK_SIZE];
groupshared uint RasterTotalBase;
groupshared uint RasterSoftwareBase;

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
    RasterTotalPrefix[groupIndex] = softwareCount + hardwareCount;
    RasterSoftwarePrefix[groupIndex] = softwareCount;
    GroupMemoryBarrierWithGroupSync();

    for (uint offset = 1u; offset < BLOCK_SIZE; offset <<= 1u)
    {
        uint total = groupIndex >= offset ? RasterTotalPrefix[groupIndex - offset] : 0u;
        uint software = groupIndex >= offset ? RasterSoftwarePrefix[groupIndex - offset] : 0u;
        GroupMemoryBarrierWithGroupSync();
        RasterTotalPrefix[groupIndex] += total;
        RasterSoftwarePrefix[groupIndex] += software;
        GroupMemoryBarrierWithGroupSync();
    }

    uint blockLast = BLOCK_SIZE - 1u;
    if (groupIndex == 0u)
    {
        InterlockedAdd(ReserveCounters[0], RasterTotalPrefix[blockLast], RasterTotalBase);
        InterlockedAdd(ReserveCounters[1], RasterSoftwarePrefix[blockLast], RasterSoftwareBase);
        if (Uniforms.CollectMetrics != 0u)
        {
            InterlockedAdd(ReserveCounters[2], RasterTotalPrefix[blockLast]);
            InterlockedAdd(ReserveCounters[3], RasterSoftwarePrefix[blockLast]);
        }
        if (Uniforms.PolicyTarget != 0u)
            InterlockedAdd(PolicyRasterWorkCounter[0], RasterTotalPrefix[blockLast]);
    }
    GroupMemoryBarrierWithGroupSync();

    ReserveRaster(
        RasterBinMeta,
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
    if (bin < Uniforms.RasterMaxBins)
    {
        uint batchCount = softwareCount + hardwareCount;
        uint argumentOffset = bin * 12u;
        uint groupsX = min(batchCount, 65535u);
        uint groupsY = max(1u, (batchCount + 65534u) / 65535u);
        TessellationSeedDispatchArgs.Store(argumentOffset + 0u, groupsX);
        TessellationSeedDispatchArgs.Store(argumentOffset + 4u, groupsY);
        TessellationSeedDispatchArgs.Store(argumentOffset + 8u, 1u);

    }
}
