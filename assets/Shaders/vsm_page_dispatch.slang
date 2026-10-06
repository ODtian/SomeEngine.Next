#ifndef VSM_PAGE_DISPATCH_SLANG
#define VSM_PAGE_DISPATCH_SLANG

// X enumerates pages in a band, Y is tile X, Z is (page band, tile Y).
// Never flatten tiles squared into one dimension: large pages have a legal
// square tile grid but its area can exceed the portable dispatch dimension.
// The final page band can be short, so consumers check the real count.
static const uint VSM_PAGE_DISPATCH_WIDTH = 65535u;

uint3 VirtualShadowPageDispatch(uint pageCount, uint tiles)
{
    uint bands = (pageCount + VSM_PAGE_DISPATCH_WIDTH - 1u) / VSM_PAGE_DISPATCH_WIDTH;
    return uint3(min(pageCount, VSM_PAGE_DISPATCH_WIDTH), tiles, bands * tiles);
}

uint VirtualShadowDispatchPage(uint3 groupId, uint tiles)
{
    return groupId.x + (groupId.z / tiles) * VSM_PAGE_DISPATCH_WIDTH;
}

uint2 VirtualShadowDispatchTile(uint3 groupId, uint tiles)
{
    return uint2(groupId.y, groupId.z % tiles);
}
#endif
