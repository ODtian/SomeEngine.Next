#ifndef VSM_RECEIVER_MASK_BITS_SLANG
#define VSM_RECEIVER_MASK_BITS_SLANG

bool VirtualShadowReceiverMaskCell(uint4 mask, uint2 cell)
{
    uint word = (cell.y >> 2u) * 2u + (cell.x >> 2u);
    uint bit = (cell.y & 3u) * 4u + (cell.x & 3u);
    return (mask[word] & (1u << bit)) != 0u;
}

bool VirtualShadowReceiverMaskRectangle(uint4 masks, uint2 firstCell, uint2 lastCell)
{
    for (uint y = firstCell.y >> 2u; y <= lastCell.y >> 2u; y++)
    for (uint x = firstCell.x >> 2u; x <= lastCell.x >> 2u; x++)
    {
        uint2 low = max(firstCell, uint2(x, y) * 4u) - uint2(x, y) * 4u;
        uint2 high = min(lastCell, uint2(x, y) * 4u + 3u) - uint2(x, y) * 4u;
        uint columns = (0xfu << low.x) & (0xfu >> (3u - high.x));
        uint rows = (0xffffu << (low.y * 4u)) & (0xffffu >> ((3u - high.y) * 4u));
        if ((masks[y * 2u + x] & (columns * 0x1111u) & rows) != 0u) return true;
    }
    return false;
}

uint ShrinkVirtualShadowReceiverMask(uint mask)
{
    uint vertical = (mask | (mask >> 4u)) & 0x0505u;
    vertical |= (mask | (mask >> 4u)) >> 1u & 0x0505u;
    return (vertical & 1u) | ((vertical >> 1u) & 2u) |
        ((vertical >> 4u) & 0x10u) | ((vertical >> 5u) & 0x20u);
}


#endif
