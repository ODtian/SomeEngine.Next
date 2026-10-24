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
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> DepthBuckets;

[shader("compute")]
[numthreads(64, 1, 1)]
void ResetBins(uint3 threadId : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint clusterCount = args.SWCount + args.HWCount;
    ClearRaster(RasterBinMeta, threadId.x, Uniforms.RasterMaxBins);
    if (Uniforms.DepthBucketsEnabled != 0u)
    {
        uint threadCount = ((Uniforms.RasterMaxBins + 63u) / 64u) * 64u;
        for (uint bucket = threadId.x; bucket < Uniforms.RasterMaxBins * 512u; bucket += threadCount)
            DepthBuckets[bucket] = 0u;
    }
    else if (Uniforms.DepthBucketsEnabled == 0u && threadId.x == 0u)
        DepthBuckets[0] = 0u;
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
    RasterWorkArguments.Store3(56u, uint3((clusterCount + 63u) / 64u, 1u, 1u));
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
