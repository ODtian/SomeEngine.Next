#include "vsm_receiver_requests.slang"

void MarkVirtualShadowReceiverPixel(uint2 pixel, uint requiredPrimaryFlags = 0u, uint excludedExtraFlags = 0u)
{
    if (pixel.x >= PageDemand.ReceiverWidth || pixel.y >= PageDemand.ReceiverHeight)
        return;
    uint receiverIndex = pixel.y * PageDemand.ReceiverWidth + pixel.x;
#ifdef VSM_DEPTH_PAGE_REQUESTS
    SurfaceReceiver surfaceReceiver = (SurfaceReceiver)0;
    surfaceReceiver.Depth = ReceiverDepth.Load(int4(pixel, PageDemand.ReceiverViewIndex, 0));
    if (surfaceReceiver.Depth < 0.0 || surfaceReceiver.Depth >= 1.0) return;
    float2 uv = (float2(pixel) + .5) / float2(PageDemand.ReceiverWidth, PageDemand.ReceiverHeight);
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, surfaceReceiver.Depth, 1.0),
        ReceiverViews[PageDemand.ReceiverViewIndex].ClipToWorld);
    surfaceReceiver.Position = abs(world.w) > 1e-8 ? world.xyz / world.w : world.xyz;
    surfaceReceiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
#else
    SurfaceReceiver surfaceReceiver = ReceiverInputs[receiverIndex];
#endif
    bool firstPerson = (surfaceReceiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u;
    bool primaryEligible = SurfaceReceiverHasLighting(surfaceReceiver) &&
        (requiredPrimaryFlags == 0u || (surfaceReceiver.Flags & requiredPrimaryFlags) != 0u);
#ifdef VSM_DEPTH_PAGE_REQUESTS
    // Geometry-normal culling saves requests, but is not needed for correct sampling.
    bool backfaceCull = false;
#else
    bool backfaceCull = PageDemand.CullBackfacingPixels != 0u;
#endif
    if (!primaryEligible && PageDemand.AdditionalReceiverLayerCount == 0u) return;
    float3 worldPosition = surfaceReceiver.Position;
    float dynamicResolutionBias = DynamicResolutionBias[0];

    // Each directional family starts at face zero; regular and FP families
    // have independent level ranges and the same light index.
    for (uint baseViewIndex = 0u, familyLevels = 1u; baseViewIndex < PageDemand.ViewCount; baseViewIndex += familyLevels)
    {
        familyLevels = 1u;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        if (baseView.LightKind != VSM_LIGHT_DIRECTIONAL || baseView.FaceIndex != 0u) continue;
        // Directional families contain contiguous levels; only their first view publishes requests.
        familyLevels = baseView.ClipmapLevelCount;
#ifndef VSM_DEPTH_PAGE_REQUESTS
        for (uint layer = 0u; layer < PageDemand.AdditionalReceiverLayerCount; layer++)
        {
            SurfaceReceiver extra = AdditionalReceiverInputs[layer * PageDemand.ReceiverWidth * PageDemand.ReceiverHeight + receiverIndex];
            if (!SurfaceReceiverHasLighting(extra) || (extra.Flags & excludedExtraFlags) != 0u || extra.Depth < 0.0 || extra.Depth >= 1.0) continue;
            if (!SurfaceReceiverReceivesDirectionalShadow(extra, -baseView.LightDirection,
                baseView.SourceAngleRadians, backfaceCull)) continue;
            float3 extraPosition = extra.Position;
            uint level = CalculateDirectionalVirtualShadowBiasedLevel(ShadowViews, baseViewIndex, baseView.ClipmapLevelCount,
                extraPosition, dynamicResolutionBias,
                CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, extraPosition));
            if (level < baseView.ClipmapLevelCount) PublishProjectedShadowView(baseViewIndex + level, 0u, extraPosition);
        }
#endif
        if (primaryEligible)
            PublishDirectionalShadowReceiver(baseViewIndex, baseView, surfaceReceiver, dynamicResolutionBias, backfaceCull);
    }

    if (primaryEligible && (!firstPerson || PageDemand.FirstPersonPixelRequestBias >= 0.0))
        PublishLocalShadowReceiver(pixel, worldPosition, surfaceReceiver, dynamicResolutionBias, backfaceCull);
#ifndef VSM_DEPTH_PAGE_REQUESTS
    for (uint layer = 0u; layer < PageDemand.AdditionalReceiverLayerCount; layer++)
    {
        SurfaceReceiver extra = AdditionalReceiverInputs[layer * PageDemand.ReceiverWidth * PageDemand.ReceiverHeight + receiverIndex];
        if (!SurfaceReceiverHasLighting(extra) || (extra.Flags & excludedExtraFlags) != 0u || extra.Depth < 0.0 || extra.Depth >= 1.0) continue;
        PublishLocalShadowReceiver(pixel, extra.Position, extra, dynamicResolutionBias, backfaceCull);
    }
#endif
}

[shader("compute")]
[numthreads(8,8,1)]
#ifdef VSM_DEPTH_PAGE_REQUESTS
void MarkVirtualShadowDepthPages(uint3 pixel : SV_DispatchThreadID)
#else
void MarkVirtualShadowPages(uint3 pixel : SV_DispatchThreadID)
#endif
{
    MarkVirtualShadowReceiverPixel(pixel.xy * 2u);
}
