#include "resource_effects.slang"

ByteAddressBuffer PageRequestDispatchArgs;
StructuredBuffer<uint> RequestedPageIndices;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> PageReceiverMasks;

[shader("compute")]
[numthreads(64, 1, 1)]
void ResetVirtualShadowReceiverMasks(uint3 threadId : SV_DispatchThreadID)
{
    uint requestIndex = threadId.x;
    if (requestIndex < PageRequestDispatchArgs.Load(12u))
        PageReceiverMasks[RequestedPageIndices[requestIndex]] = 0u.xx;
}
