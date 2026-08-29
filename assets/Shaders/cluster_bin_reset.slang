#include "cluster_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterDeformBinningUniforms> Uniforms;
ByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer BinningDispatchArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DeformReserveCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> CacheAllocationCounter;

[shader("compute")]
[numthreads(64, 1, 1)]
void ResetBins(uint3 threadId : SV_DispatchThreadID)
{
    ClearRaster(RasterBinMeta, threadId.x, Uniforms.RasterMaxBins);
    ClearDeform(DeformBinMeta, threadId.x, Uniforms.DeformMaxBins);

    if (threadId.x != 0u)
        return;

    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    StoreDispatchArgs(
        BinningDispatchArgs,
        0u,
        DispatchArgs((args.SWCount + args.HWCount + 63u) / 64u, 1u, 1u));
    ReserveCounters[0] = 0u;
    ReserveCounters[1] = 0u;
    DeformReserveCounters[0] = 0u;
    if (Uniforms.ResetCacheAllocationState == 0u)
        return;

    ReserveCounters[2] = 0u;
    ReserveCounters[3] = 0u;
    DeformReserveCounters[1] = 0u;
    CacheAllocationCounter[0] = 0u;
    CacheAllocationCounter[1] = 0u;
}
