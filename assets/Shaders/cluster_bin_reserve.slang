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
[numthreads(BLOCK_SIZE, 1, 1)]
void ReserveBins(uint3 threadId : SV_DispatchThreadID)
{
    uint bin = threadId.x;
    if (bin >= Uniforms.RasterMaxBins) return;
    uint softwareCount = RasterBinMeta[bin].BinSWCount;
    uint hardwareCount = RasterBinMeta[bin].BinHWCount;
    uint totalCount = softwareCount + hardwareCount;
    // Each bin stores its own offsets, so waves can allocate in any order.
    uint waveTotal = WaveActiveSum(totalCount);
    uint waveSoftware = WaveActiveSum(softwareCount);
    uint totalBase = 0u;
    uint softwareBase = 0u;
    if (WaveIsFirstLane())
    {
        if (waveTotal != 0u)
            totalBase = ReserveClusterRasterBatches(ReserveCounters, waveTotal,
                Uniforms.MaxVisibleClusters * Uniforms.BinnedEntriesPerCluster);
        if (waveSoftware != 0u)
            InterlockedAdd(ReserveCounters[1], waveSoftware, softwareBase);
        if (Uniforms.CollectMetrics != 0u)
        {
            InterlockedAdd(ReserveCounters[2], waveTotal);
            InterlockedAdd(ReserveCounters[3], waveSoftware);
        }
    }
    totalBase = WaveReadLaneFirst(totalBase);
    softwareBase = WaveReadLaneFirst(softwareBase);
    AddClusterWorkMetric(64u, hardwareCount);
    AddClusterWorkMetric(72u, softwareCount);
    if (totalBase + waveTotal > Uniforms.MaxVisibleClusters * Uniforms.BinnedEntriesPerCluster)
    {
        // No indirect consumer may execute a range not retained in the index buffer.
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
        WavePrefixSum(totalCount) + totalCount,
        WavePrefixSum(softwareCount) + softwareCount,
        softwareCount,
        hardwareCount);
    if (RasterBinMeta[bin].DepthBucketEnabled != 0u)
    {
        uint softwarePrefix = 0u, hardwarePrefix = 0u;
        for (uint bucket = 0u; bucket < 256u; bucket++)
        {
            uint softwareIndex = bin * 512u + bucket;
            uint hardwareIndex = softwareIndex + 256u;
            uint softwareItems = DepthBuckets[softwareIndex];
            uint hardwareItems = DepthBuckets[hardwareIndex];
            DepthBuckets[softwareIndex] = softwarePrefix;
            DepthBuckets[hardwareIndex] = hardwarePrefix;
            softwarePrefix += softwareItems;
            hardwarePrefix += hardwareItems;
        }
    }
    uint argumentOffset = bin * 12u;
    uint groupsX = min(totalCount, 65535u);
    uint groupsY = max(1u, (totalCount + 65534u) / 65535u);
    TessellationSeedDispatchArgs.Store(argumentOffset + 0u, groupsX);
    TessellationSeedDispatchArgs.Store(argumentOffset + 4u, groupsY);
    TessellationSeedDispatchArgs.Store(argumentOffset + 8u, 1u);
}
