#ifndef VSM_RECEIVER_MASK_COMMON_SLANG
#define VSM_RECEIVER_MASK_COMMON_SLANG

uint2 VirtualShadowReceiverLevelOffset(uint pagesPerRow, uint mipLevel)
{
    return mipLevel == 0u ? 0u.xx : uint2(pagesPerRow - (pagesPerRow >> (mipLevel - 1u)), pagesPerRow);
}

uint2 VirtualShadowReceiverMaskOrigin(VirtualShadowMapParameters view, uint mipLevel)
{
    return uint2(view.ReceiverMaskOriginX, view.ReceiverMaskOriginY) +
        2u * VirtualShadowReceiverLevelOffset(VirtualShadowPagesPerRow(view), mipLevel);
}

uint4 LoadVirtualShadowReceiverMask(Texture2D<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    return uint4(masks.Load(int3(address, 0)), masks.Load(int3(address + uint2(1, 0), 0)),
        masks.Load(int3(address + uint2(0, 1), 0)), masks.Load(int3(address + 1u.xx, 0)));
}

uint4 LoadVirtualShadowReceiverMask(RWTexture2D<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    return uint4(masks[address], masks[address + uint2(1, 0)],
        masks[address + uint2(0, 1)], masks[address + 1u.xx]);
}

uint4 LoadVirtualShadowReceiverMask(Texture2DArray<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage, uint layer)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    return uint4(masks.Load(int4(address, layer, 0)), masks.Load(int4(address + uint2(1, 0), layer, 0)),
        masks.Load(int4(address + uint2(0, 1), layer, 0)), masks.Load(int4(address + 1u.xx, layer, 0)));
}

uint4 LoadVirtualShadowReceiverMask(RWTexture2DArray<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage, uint layer)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * 2u;
    return uint4(masks[uint3(address, layer)], masks[uint3(address + uint2(1, 0), layer)],
        masks[uint3(address + uint2(0, 1), layer)], masks[uint3(address + 1u.xx, layer)]);
}

#include "vsm_receiver_mask_bits.slang"

#endif
