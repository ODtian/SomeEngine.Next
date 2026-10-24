#ifndef VSM_RECEIVER_MASK_COMMON_SLANG
#define VSM_RECEIVER_MASK_COMMON_SLANG

uint2 VirtualShadowReceiverLevelOffset(uint pagesPerRow, uint mipLevel)
{
    return mipLevel == 0u ? 0u.xx : uint2(pagesPerRow - (pagesPerRow >> (mipLevel - 1u)), pagesPerRow);
}

uint2 VirtualShadowReceiverMaskOrigin(VirtualShadowMapParameters view, uint mipLevel)
{
    return uint2(view.ReceiverMaskOriginX, view.ReceiverMaskOriginY) +
        uint2(1u, 2u) * VirtualShadowReceiverLevelOffset(VirtualShadowPagesPerRow(view), mipLevel);
}

// Two R32 texels retain 64 cells and support native 32-bit image atomics on both targets.
uint2 LoadVirtualShadowReceiverMask(Texture2D<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    return uint2(masks.Load(int3(address, 0)), masks.Load(int3(address + uint2(0, 1), 0)));
}

uint2 LoadVirtualShadowReceiverMask(RWTexture2D<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    return uint2(masks[address], masks[address + uint2(0, 1)]);
}

uint2 LoadVirtualShadowReceiverMask(Texture2DArray<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage, uint layer)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    return uint2(masks.Load(int4(address, layer, 0)), masks.Load(int4(address + uint2(0, 1), layer, 0)));
}

uint2 LoadVirtualShadowReceiverMask(RWTexture2DArray<uint> masks, VirtualShadowMapParameters view,
    uint mipLevel, uint2 localPage, uint layer)
{
    uint2 address = VirtualShadowReceiverMaskOrigin(view, mipLevel) + localPage * uint2(1u, 2u);
    return uint2(masks[uint3(address, layer)], masks[uint3(address + uint2(0, 1), layer)]);
}

#include "vsm_receiver_mask_bits.slang"
#endif
