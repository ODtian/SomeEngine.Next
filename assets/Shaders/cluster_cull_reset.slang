#include "cluster_structures.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> Phase2CandidateCount;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer Phase2CandidateArgs;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer Phase2DrawArgs;

[shader("compute")]
[numthreads(1, 1, 1)]
void ClearCull(uint3 threadId : SV_DispatchThreadID)
{
    if (any(threadId != 0u))
        return;

    StoreCullDrawArgs(DrawArgs, 0u, CullDrawArgs(96u, 0u, 0u));
    DrawArgs.Store(12u, 0u);
    Phase2CandidateCount[0] = 0u;
    StoreDispatchArgs(Phase2CandidateArgs, 0u, DispatchArgs(0u, 1u, 1u));
    StoreCullDrawArgs(Phase2DrawArgs, 0u, CullDrawArgs(96u, 0u, 0u));
    Phase2DrawArgs.Store(12u, 0u);
}
