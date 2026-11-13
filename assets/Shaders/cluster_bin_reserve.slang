__exported import cluster_bin_common;
__exported import cluster_tessellation_types;
__exported import resource_effects;
__exported import cluster_work_status;
__exported import visbuffer_encoding;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
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
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> DepthBuckets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RasterBatchOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RasterSoftwareOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> RasterBatchDemand;
ByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer RasterSpillArguments;

[shader("compute")]
[numthreads(256,1,1)]
void PrepareRasterBinOffsets(uint3 id : SV_DispatchThreadID)
{
    if (id.x < Uniforms.RasterMaxBins)
    {
        RasterBatchOffsets[id.x] = RasterBinMeta[id.x].BinSWCount + RasterBinMeta[id.x].BinHWCount;
        RasterSoftwareOffsets[id.x] = RasterBinMeta[id.x].BinSWCount;
    }
    if (id.x == Uniforms.RasterMaxBins)
        RasterBatchOffsets[id.x] = RasterSoftwareOffsets[id.x] = 0u;
}

[shader("compute")]
[WaveSize(32)]
[numthreads(128,1,1)]
void ReserveBins(uint3 threadId : SV_DispatchThreadID)
{
    uint bin = threadId.x / 32u;
    if (bin >= Uniforms.RasterMaxBins) return;
    uint requestedSoftware = RasterBinMeta[bin].BinSWCount;
    uint requestedHardware = RasterBinMeta[bin].BinHWCount;
    uint required = requestedSoftware + requestedHardware;
    uint first = RasterBatchOffsets[bin];
    uint available = Uniforms.BatchCapacity - min(first, Uniforms.BatchCapacity);
    uint softwareCount = min(requestedSoftware, available);
    uint hardwareCount = min(requestedHardware, available - softwareCount);
    uint retained = softwareCount + hardwareCount;
    bool depthBuckets = RasterBinMeta[bin].DepthBucketEnabled != 0u && retained == required;
    if (WaveIsFirstLane())
    {
        RasterBinMeta[bin].BinSWCount = softwareCount;
        RasterBinMeta[bin].BinHWCount = hardwareCount;
        RasterBinMeta[bin].DepthBucketEnabled = depthBuckets ? 1u : 0u;
        InterlockedAdd(ReserveCounters[1], softwareCount);
        if (Uniforms.CollectMetrics != 0u)
        {
            InterlockedAdd(ReserveCounters[2], required);
            InterlockedAdd(ReserveCounters[3], requestedSoftware);
        }
        AddClusterWorkMetric(64u, requestedHardware);
        AddClusterWorkMetric(72u, requestedSoftware);
        if (retained != required) RejectClusterWork(16u);
        ReserveRaster(RasterBinMeta, BinnedHWDrawArgs, BinnedSWDispatchArgs, bin,
            Uniforms.RasterMaxBins, min(first, Uniforms.BatchCapacity), RasterSoftwareOffsets[bin],
            retained, softwareCount, softwareCount, hardwareCount);
        uint argumentOffset = bin * 12u;
        TessellationSeedDispatchArgs.Store3(argumentOffset,
            uint3(min(retained,65535u),max(1u,(retained+65534u)/65535u),1u));
        if (bin == 0u)
        {
            uint demand = RasterBatchOffsets[Uniforms.RasterMaxBins];
            ReserveCounters[0] = min(demand,Uniforms.BatchCapacity);
            InterlockedMax(RasterBatchDemand[0],demand);
        }
    }
    if (depthBuckets)
    {
        uint2 prefix = 0u;
        for (uint firstBucket = 0u; firstBucket < 256u; firstBucket += 32u)
        {
            uint softwareIndex = bin * 512u + firstBucket + WaveGetLaneIndex();
            uint hardwareIndex = softwareIndex + 256u;
            uint2 items = uint2(DepthBuckets[softwareIndex],DepthBuckets[hardwareIndex]);
            uint2 offsets = prefix + WavePrefixSum(items);
            DepthBuckets[softwareIndex] = offsets.x;
            DepthBuckets[hardwareIndex] = offsets.y;
            prefix += WaveActiveSum(items);
        }
    }
}

[shader("compute")]
[numthreads(1,1,1)]
void PrepareRasterSpillArguments()
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs,0u);
    uint count = RasterBatchOffsets[Uniforms.RasterMaxBins] > Uniforms.BatchCapacity
        ? args.SWCount + args.HWCount : 0u;
    RasterSpillArguments.Store4(0u,uint4(min(count,65535u),max(1u,(count+65534u)/65535u),1u,0u));
    RasterSpillArguments.Store4(16u,uint4((1u << VISBUFFER_TRIANGLE_BITS)*3u,count,0u,0u));
}
