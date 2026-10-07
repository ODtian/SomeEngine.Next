#include "cluster_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
ByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer RasterWorkArguments;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
[shader("compute")]
[numthreads(64, 1, 1)]
void ResetBins(uint3 threadId : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint clusterCount = args.SWCount + args.HWCount;
    uint clustersPerBin = Uniforms.MaxVisibleClusters / max(Uniforms.RasterMaxBins, 1u);
    uint binCapacity = clusterCount <= clustersPerBin ? clustersPerBin * Uniforms.BinnedEntriesPerCluster : 0u;
    ClearRaster(RasterBinMeta, threadId.x, Uniforms.RasterMaxBins);
    if (threadId.x < Uniforms.RasterMaxBins)
    {
        RasterBinMeta[threadId.x].ClusterOffset = threadId.x * binCapacity;
        RasterBinMeta[threadId.x].BinCapacity = binCapacity;
        RasterBinMeta[threadId.x].HardwareOffset = threadId.x * binCapacity + args.SWCount * Uniforms.BinnedEntriesPerCluster;
    }
    if (threadId.x != 0u)
        return;

    RasterWorkArguments.Store4(0u, uint4((clusterCount + 63u) / 64u, 1u, 1u, 0u));

    // The second raster phase can leave visibility and depth untouched. Its
    // fullscreen work then reuses the first phase's output, including all HiZ
    // levels. Tessellation is also seeded exclusively from these clusters.
    uint viewCount = clusterCount != 0u ? Uniforms.ViewCount : 0u;
    RasterWorkArguments.Store4(16u, uint4(3u, viewCount, 0u, 0u));
    RasterWorkArguments.Store3(32u, uint3(
        max(1u, Uniforms.ScreenWidth >> 4u),
        max(1u, Uniforms.ScreenHeight >> 4u), viewCount));
    RasterWorkArguments.Store3(44u, uint3(1u, 1u, viewCount));
    RasterWorkArguments.Store3(56u, uint3(binCapacity == 0u ? (clusterCount + 63u) / 64u : 0u, 1u, 1u));
    uint argumentBytes;
    RasterWorkArguments.GetDimensions(argumentBytes);
    for (uint offset = 68u, mip = 0u; offset < argumentBytes; offset += 12u, mip++)
    {
        uint2 extent = max(uint2(Uniforms.ScreenWidth, Uniforms.ScreenHeight) >> mip, 1u);
        RasterWorkArguments.Store3(offset, uint3((extent + 7u) / 8u, viewCount));
    }
    ReserveCounters[0] = 0u;
    ReserveCounters[1] = 0u;
    if (Uniforms.CollectMetrics == 0u)
    {
        ReserveCounters[2] = 0u;
        ReserveCounters[3] = 0u;
    }
}
