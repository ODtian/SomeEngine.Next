#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowStateInitUniforms
{
    uint PageTableEntryCount;
    uint MaxPhysicalPages;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<VirtualShadowStateInitUniforms> StateInit;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> ShadowPageTable;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> PhysicalPageOwners;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> PageMetadata;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedShadowPages;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeVirtualShadowState(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index < StateInit.PageTableEntryCount)
    {
        ShadowPageTable[index] = uint4(VSM_PAGE_UNMAPPED, 0u, 0u, 0u);
        RequestedShadowPages[index] = 0u;
    }
    if (index < StateInit.MaxPhysicalPages)
    {
        PhysicalPageOwners[index] = VSM_UNOWNED_PAGE;
        PageMetadata[index] = 0u.xxxx;
    }
}
