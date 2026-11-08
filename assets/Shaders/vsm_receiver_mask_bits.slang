#ifndef VSM_RECEIVER_MASK_BITS_SLANG
#define VSM_RECEIVER_MASK_BITS_SLANG

bool VirtualShadowReceiverMaskCell(uint2 mask, uint2 cell)
{
    return (mask[cell.y >> 2u] & (1u << ((cell.y & 3u) * 8u + cell.x))) != 0u;
}

uint VirtualShadowReceiverWordRectangle(uint2 firstCell, uint2 lastCell)
{
    uint columns = (0xffu << firstCell.x) & (0xffu >> (7u - lastCell.x));
    uint rows = (0xffffffffu << (firstCell.y * 8u)) & (0xffffffffu >> ((3u - lastCell.y) * 8u));
    return columns * 0x01010101u & rows;
}

bool VirtualShadowReceiverMaskRectangle(uint2 masks, uint2 firstCell, uint2 lastCell)
{
    for (uint word = firstCell.y >> 2u; word <= lastCell.y >> 2u; word++)
    {
        uint2 low = uint2(firstCell.x, max(firstCell.y, word * 4u) - word * 4u);
        uint2 high = uint2(lastCell.x, min(lastCell.y, word * 4u + 3u) - word * 4u);
        if ((masks[word] & VirtualShadowReceiverWordRectangle(low, high)) != 0u) return true;
    }
    return false;
}

// Each R32 texel represents eight columns and four rows. Reduce adjacent 2x2 cells.
uint ShrinkVirtualShadowReceiverMask(uint mask)
{
    uint rows = (mask | (mask >> 8u)) & 0x00ff00ffu;
    uint pairs = (rows | (rows >> 1u)) & 0x00550055u;
    uint first = (pairs & 1u) | ((pairs >> 1u) & 2u) |
        ((pairs >> 2u) & 4u) | ((pairs >> 3u) & 8u);
    uint second = ((pairs >> 12u) & 0x10u) | ((pairs >> 13u) & 0x20u) |
        ((pairs >> 14u) & 0x40u) | ((pairs >> 15u) & 0x80u);
    return first | second;
}

#endif
