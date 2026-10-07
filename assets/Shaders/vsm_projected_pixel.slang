#ifndef VSM_PROJECTED_PIXEL_SLANG
#define VSM_PROJECTED_PIXEL_SLANG

uint2 DecodeVirtualShadowProjectionPixel(uint lane)
{
    return uint2((lane & 1u) | ((lane >> 1u) & 2u) | ((lane >> 2u) & 4u),
        ((lane >> 1u) & 1u) | ((lane >> 2u) & 2u) | ((lane >> 3u) & 4u));
}

uint GetVirtualShadowProjectedPixelIndex(uint2 pixel, uint2 screen, uint lightView)
{
    uint2 tiles = (screen + 7u) / 8u;
    uint2 local = pixel & 7u;
    uint lane = (local.x & 1u) | ((local.y & 1u) << 1u) | ((local.x & 2u) << 1u) |
        ((local.y & 2u) << 2u) | ((local.x & 4u) << 2u) | ((local.y & 4u) << 3u);
    return ((lightView * tiles.y + pixel.y / 8u) * tiles.x + pixel.x / 8u) * 64u + lane;
}

#endif
