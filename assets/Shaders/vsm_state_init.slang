#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowStateInitUniforms
{
    uint PageTableEntryCount;
    uint MaxPhysicalPages;
    uint RequestEpochEntryCount;
    uint Pad0;
    uint Pad1;
    uint Pad2;
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
RWStructuredBuffer<uint> RequestedPageFlags;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedPageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> PageReceiverMasks;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> AlternateRequestedShadowPages;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> AlternateRequestedPageFlags;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> AlternateRequestedPageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> AlternatePageReceiverMasks;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeVirtualShadowState(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index < StateInit.PageTableEntryCount)
        ShadowPageTable[index] = uint4(VSM_PAGE_UNMAPPED, 0u, 0u, 0u);
    if (index < StateInit.RequestEpochEntryCount)
    {
        RequestedShadowPages[index] = 0u;
        RequestedPageFlags[index] = 0u;
        PageReceiverMasks[index] = 0u.xx;
        AlternateRequestedShadowPages[index] = 0u;
        AlternateRequestedPageFlags[index] = 0u;
        AlternatePageReceiverMasks[index] = 0u.xx;
    }
    if (index == 0u)
    {
        RequestedPageCount[0] = 0u;
        AlternateRequestedPageCount[0] = 0u;
    }
    if (index < StateInit.MaxPhysicalPages)
    {
        PhysicalPageOwners[index] = VSM_UNOWNED_PAGE;
        PageMetadata[index] = 0u.xxxx;
    }
}
