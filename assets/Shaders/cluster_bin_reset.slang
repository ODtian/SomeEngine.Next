#include "cluster_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
ByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinningDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
[shader("compute")]
[numthreads(64, 1, 1)]
void ResetBins(uint3 threadId : SV_DispatchThreadID)
{
    ClearRaster(RasterBinMeta, threadId.x, Uniforms.RasterMaxBins);
    if (threadId.x != 0u)
        return;

    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    StoreDispatchArgs(
        BinningDispatchArgs,
        0u,
        DispatchArgs((args.SWCount + args.HWCount + 63u) / 64u, 1u, 1u));
    ReserveCounters[0] = 0u;
    ReserveCounters[1] = 0u;
    if (Uniforms.CollectMetrics == 0u)
    {
        ReserveCounters[2] = 0u;
        ReserveCounters[3] = 0u;
    }
}
