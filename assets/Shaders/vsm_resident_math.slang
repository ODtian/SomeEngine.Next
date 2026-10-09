#ifndef VSM_RESIDENT_MATH_SLANG
#define VSM_RESIDENT_MATH_SLANG

int2 VirtualShadowCoarseClipmapPage(uint2 page, VirtualShadowMapParameters requested,
    VirtualShadowMapParameters coarse, uint levelOffset)
{
    int quarterPages = int(VirtualShadowPagesPerRow(requested) / 4u);
    int2 requestedCorner = int2(requested.ClipmapCornerRelativeOffsetX, requested.ClipmapCornerRelativeOffsetY);
    int2 coarseCorner = int2(coarse.ClipmapCornerRelativeOffsetX, coarse.ClipmapCornerRelativeOffsetY);
    return (int2(page) - quarterPages * requestedCorner + ((quarterPages * coarseCorner) << levelOffset)) >> levelOffset;
}

float4 VirtualShadowClipmapScaleBias(VirtualShadowMapParameters requested,
    VirtualShadowMapParameters coarse, uint levelOffset)
{
    float scale = rcp(float(1u << levelOffset));
    float2 requestedCorner = float2(requested.ClipmapCornerRelativeOffsetX, requested.ClipmapCornerRelativeOffsetY);
    float2 coarseCorner = float2(coarse.ClipmapCornerRelativeOffsetX, coarse.ClipmapCornerRelativeOffsetY);
    return float4(scale, .25 * (coarseCorner - scale * requestedCorner),
        coarse.LightViewProjection[3][2] - scale * requested.LightViewProjection[3][2]);
}

float VirtualShadowDepthInRequestedClipmap(float coarseDepth, float4 scaleBias)
{
    return (coarseDepth - scaleBias.w) / scaleBias.x;
}

float VirtualShadowReverseDepthInRequestedClipmap(float coarseDepth, float4 scaleBias)
{
    // Preserve Native's subtract-then-divide depth mapping through shader optimization.
    precise float reverseBias = (1.0 - scaleBias.x) - scaleBias.w;
    precise float depth = (coarseDepth - reverseBias) / scaleBias.x;
    return depth;
}

#endif
