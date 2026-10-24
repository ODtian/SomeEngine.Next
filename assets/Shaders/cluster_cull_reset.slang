#include "cluster_structures.slang"
#include "resource_effects.slang"

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer DrawArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer Phase2DrawArgs;

[shader("compute")]
[numthreads(1, 1, 1)]
void ClearCull(uint3 threadId : SV_DispatchThreadID)
{
    if (any(threadId != 0u))
        return;

    StoreCullDrawArgs(DrawArgs, 0u, CullDrawArgs(96u, 0u, 0u));
    DrawArgs.Store(12u, 0u);
    StoreCullDrawArgs(Phase2DrawArgs, 0u, CullDrawArgs(96u, 0u, 0u));
    Phase2DrawArgs.Store(12u, 0u);
}
