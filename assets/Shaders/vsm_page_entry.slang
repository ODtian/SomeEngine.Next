#ifndef VSM_PAGE_ENTRY_SLANG
#define VSM_PAGE_ENTRY_SLANG

static const uint VSM_PAGE_ANY_DEPTH = 0x80000000u;
static const uint VSM_PAGE_RENDERING = 0x40000000u;
static const uint VSM_PAGE_SECONDARY = 0x20000000u;
static const uint VSM_PAGE_PARTIAL_DEPTH = 0x04000000u;

struct VirtualShadowPageMetadata
{
    uint LastRequestedFrame;
    uint StaticDepthGeneration;
    uint RequestFlags;
    uint Flags;
    int2 WorldPage;
    uint Generation;
    uint ViewIndex;
};

uint PackVirtualShadowPage(uint pageEntry, uint pagesPerRow)
{
    if (pageEntry == 0u) return 0u;
    uint page = pageEntry - 1u;
    return (page % pagesPerRow) | ((page / pagesPerRow) << 10u) | VSM_PAGE_RENDERING;
}

uint VirtualShadowPageLevelOffset(uint entry)
{
    return (entry >> 20u) & 63u;
}

bool DecodeVirtualShadowPage(uint entry, uint pagesPerRow, out uint page)
{
    page = (entry & 1023u) + ((entry >> 10u) & 1023u) * pagesPerRow;
    return entry != 0u && entry != 0xffffffffu;
}

bool VirtualShadowPageHasOwnDepth(uint entry)
{
    return (entry & VSM_PAGE_ANY_DEPTH) != 0u && VirtualShadowPageLevelOffset(entry) == 0u;
}

uint VirtualShadowPageAncestor(uint entry, uint offset)
{
    return (entry & (0x000fffffu | VSM_PAGE_PARTIAL_DEPTH)) | (offset << 20u) | VSM_PAGE_ANY_DEPTH;
}

#endif
