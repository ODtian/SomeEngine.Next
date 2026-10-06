#ifndef VSM_PAGE_DISPATCH_SLANG
#define VSM_PAGE_DISPATCH_SLANG

// X/Y enumerate dirty pages; Z enumerates the square 8x8 tile grid.
// The final page row can be short, so consumers check the real count.
static const uint VSM_PAGE_DISPATCH_WIDTH = 65535u;

uint3 VirtualShadowPageDispatch(uint pageCount, uint tiles)
{
    return uint3(min(pageCount, VSM_PAGE_DISPATCH_WIDTH),
        (pageCount + VSM_PAGE_DISPATCH_WIDTH - 1u) / VSM_PAGE_DISPATCH_WIDTH,
        tiles * tiles);
}

uint VirtualShadowDispatchPage(uint3 groupId)
{
    return groupId.x + groupId.y * VSM_PAGE_DISPATCH_WIDTH;
}

uint2 VirtualShadowDispatchTile(uint3 groupId, uint tiles)
{
    return uint2(groupId.z % tiles, groupId.z / tiles);
}
#endif
