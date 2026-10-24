#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowReceiverResetUniforms { uint ViewCount; uint3 Padding; };
ConstantBuffer<VirtualShadowReceiverResetUniforms> ReceiverReset;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
ByteAddressBuffer PageRequestDispatchArgs;
StructuredBuffer<uint> RequestedPageIndices;
RWTexture2D<uint> PageReceiverMasks;
RWTexture2D<uint> PageReceiverMaskMip1;
RWTexture2D<uint> PageReceiverMaskMip2;
RWTexture2D<uint> PageReceiverMaskMip3;
RWTexture2D<uint> PageReceiverMaskMip4;
RWTexture2D<uint> PageReceiverMaskMip5;
RWTexture2D<uint> PageReceiverMaskMip6;
RWTexture2D<uint> PageReceiverMaskMip7;

[shader("compute")]
[numthreads(64, 1, 1)]
void ResetVirtualShadowReceiverMasks(uint3 threadId : SV_DispatchThreadID)
{
    uint requestIndex = threadId.x;
    if (requestIndex >= PageRequestDispatchArgs.Load(12u)) return;
    uint tableIndex = RequestedPageIndices[requestIndex];
    uint viewIndex = VirtualShadowViewFromPageTableIndex(ShadowViews, ReceiverReset.ViewCount, tableIndex);
    if (viewIndex >= ReceiverReset.ViewCount) return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    uint mip;
    uint2 page;
    if (!VirtualShadowDecodePageTableIndex(view, tableIndex, mip, page)) return;
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mip) + page * 2u;
    PageReceiverMasks[address] = 0u;
    PageReceiverMasks[address + uint2(1, 0)] = 0u;
    PageReceiverMasks[address + uint2(0, 1)] = 0u;
    PageReceiverMasks[address + 1u.xx] = 0u;
    uint pages = VirtualShadowPagesPerRow(view);
    if (pages >= 2u) PageReceiverMaskMip1[address >> 1u] = 0u;
    if (pages >= 4u) PageReceiverMaskMip2[address >> 2u] = 0u;
    if (pages >= 8u) PageReceiverMaskMip3[address >> 3u] = 0u;
    if (pages >= 16u) PageReceiverMaskMip4[address >> 4u] = 0u;
    if (pages >= 32u) PageReceiverMaskMip5[address >> 5u] = 0u;
    if (pages >= 64u) PageReceiverMaskMip6[address >> 6u] = 0u;
    if (pages >= 128u) PageReceiverMaskMip7[address >> 7u] = 0u;
 }
