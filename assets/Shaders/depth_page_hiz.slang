#ifndef DEPTH_PAGE_HIZ_SLANG
#define DEPTH_PAGE_HIZ_SLANG

// An 8x8 tile grid keeps the page pool within texture array layer limits.
uint3 CalculateDepthPageHiZPixel(uint depthPage, uint2 pagePixel, uint pageExtent, uint atlasPagesPerRow)
{
    uint tileShift = firstbithigh(min(atlasPagesPerRow, 8u));
    uint tileMask = (1u << tileShift) - 1u;
    uint2 tile = uint2(depthPage & tileMask, (depthPage >> tileShift) & tileMask);
    return uint3(tile * pageExtent + pagePixel, depthPage >> (tileShift * 2u));
}

#endif
