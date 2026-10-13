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
#include "cluster_work_status.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> DepthBuckets;

static const uint BLOCK_SIZE = 128u;

[shader("compute")]
[WaveSize(32)]
[numthreads(BLOCK_SIZE, 1, 1)]
void ReserveBins(uint3 threadId : SV_DispatchThreadID)
{
    uint bin = threadId.x / 32u;
    if (bin >= Uniforms.RasterMaxBins) return;
    if (WaveIsFirstLane())
    {
        uint softwareCount = RasterBinMeta[bin].BinSWCount;
        uint hardwareCount = RasterBinMeta[bin].BinHWCount;
        uint totalCount = softwareCount + hardwareCount;
        uint totalBase = 0u;
        uint softwareBase = 0u;
        if (totalCount != 0u)
            totalBase = ReserveClusterRasterBatches(ReserveCounters, totalCount,
                Uniforms.MaxVisibleClusters * Uniforms.BinnedEntriesPerCluster);
        if (softwareCount != 0u)
            InterlockedAdd(ReserveCounters[1], softwareCount, softwareBase);
        if (Uniforms.CollectMetrics != 0u)
        {
            InterlockedAdd(ReserveCounters[2], totalCount);
            InterlockedAdd(ReserveCounters[3], softwareCount);
        }
        AddClusterWorkMetric(64u, hardwareCount);
        AddClusterWorkMetric(72u, softwareCount);
        if (totalBase + totalCount > Uniforms.MaxVisibleClusters * Uniforms.BinnedEntriesPerCluster)
        {
            // Rejected bins publish empty indirect ranges.
            RasterBinMeta[bin].BinSWCount = 0u;
            RasterBinMeta[bin].BinHWCount = 0u;
            softwareCount = 0u;
            hardwareCount = 0u;
            totalCount = 0u;
        }
        ReserveRaster(
            RasterBinMeta,
            BinnedHWDrawArgs,
            BinnedSWDispatchArgs,
            bin,
            Uniforms.RasterMaxBins,
            totalBase,
            softwareBase,
            totalCount,
            softwareCount,
            softwareCount,
            hardwareCount);
        uint argumentOffset = bin * 12u;
        uint groupsX = min(totalCount, 65535u);
        uint groupsY = max(1u, (totalCount + 65534u) / 65535u);
        TessellationSeedDispatchArgs.Store(argumentOffset + 0u, groupsX);
        TessellationSeedDispatchArgs.Store(argumentOffset + 4u, groupsY);
        TessellationSeedDispatchArgs.Store(argumentOffset + 8u, 1u);
    }
    if (RasterBinMeta[bin].DepthBucketEnabled != 0u)
    {
        uint2 prefix = 0u;
        for (uint firstBucket = 0u; firstBucket < 256u; firstBucket += 32u)
        {
            uint softwareIndex = bin * 512u + firstBucket + WaveGetLaneIndex();
            uint hardwareIndex = softwareIndex + 256u;
            uint2 items = uint2(DepthBuckets[softwareIndex], DepthBuckets[hardwareIndex]);
            uint2 offsets = prefix + WavePrefixSum(items);
            DepthBuckets[softwareIndex] = offsets.x;
            DepthBuckets[hardwareIndex] = offsets.y;
            prefix += WaveActiveSum(items);
        }
    }
}
