#define VSM_WHOLE_PAGE_SAMPLING 1
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
RWStructuredBuffer<uint> ShadowPageTable;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> PhysicalPageOwners;
#include "vsm_page_lists.slang"
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<VirtualShadowPageMetadata> PageMetadata;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedShadowPages;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedPageFlags;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RequestedPageCount;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2D<uint> PageReceiverMasks;
RWTexture2D<uint> PageReceiverMaskMip1;
RWTexture2D<uint> PageReceiverMaskMip2;
RWTexture2D<uint> PageReceiverMaskMip3;
RWTexture2D<uint> PageReceiverMaskMip4;
RWTexture2D<uint> PageReceiverMaskMip5;
RWTexture2D<uint> PageReceiverMaskMip6;
RWTexture2D<uint> PageReceiverMaskMip7;

static const uint VSM_UNOWNED_PAGE = 0xFFFFFFFFu;

void ClearReceiverTexture(RWTexture2D<uint> texture, uint index)
{
    uint width, height;
    texture.GetDimensions(width, height);
    if (index < width * height) texture[uint2(index % width, index / width)] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeVirtualShadowState(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = (group.y * 65535u + group.x) * 64u + lane;
    ClearReceiverTexture(PageReceiverMasks, index);
    ClearReceiverTexture(PageReceiverMaskMip1, index);
    ClearReceiverTexture(PageReceiverMaskMip2, index);
    ClearReceiverTexture(PageReceiverMaskMip3, index);
    ClearReceiverTexture(PageReceiverMaskMip4, index);
    ClearReceiverTexture(PageReceiverMaskMip5, index);
    ClearReceiverTexture(PageReceiverMaskMip6, index);
    ClearReceiverTexture(PageReceiverMaskMip7, index);

    if (index < StateInit.PageTableEntryCount)
        ShadowPageTable[index] = VSM_PAGE_UNMAPPED;
    if (index < StateInit.RequestEpochEntryCount)
    {
        RequestedShadowPages[index] = 0u;
        RequestedPageFlags[index] = 0u;
    }
    if (index == 0u)
    {
        RequestedPageCount[0] = 0u;
        for (uint list = 0u; list < VSM_PAGE_LIST_COUNT; list++)
            ShadowPageLists[ShadowPageListCountIndex(list, StateInit.MaxPhysicalPages)] =
                list == VSM_PAGE_LIST_REQUESTED ? StateInit.MaxPhysicalPages : 0u;
    }
    if (index < StateInit.MaxPhysicalPages)
    {
        PhysicalPageOwners[index] = VSM_UNOWNED_PAGE;
        PageMetadata[index] = (VirtualShadowPageMetadata)0;
        for (uint list = 0u; list < VSM_PAGE_LIST_COUNT; list++)
            ShadowPageLists[ShadowPageListStart(list, StateInit.MaxPhysicalPages) + index] =
                list == VSM_PAGE_LIST_REQUESTED ? index : VSM_UNOWNED_PAGE;
    }
}
