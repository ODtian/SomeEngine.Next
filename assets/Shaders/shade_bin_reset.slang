#include "shade_bin_common.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> ScatterCounts;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ReserveCounters;

[shader("compute")]
[numthreads(128, 1, 1)]
void ResetShadeBins(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x < Uniforms.ShadeBinCount)
    {
        BinCounts[threadId.x * 2u] = 0u;
        BinCounts[threadId.x * 2u + 1u] = 0u;
        ScatterCounts[threadId.x * 2u] = 0u;
        ScatterCounts[threadId.x * 2u + 1u] = 0u;
    }
    if (threadId.x == 0u)
        ReserveCounters[0] = 0u;
}
