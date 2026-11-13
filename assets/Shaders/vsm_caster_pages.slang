__exported import vsm_addressing;
__exported import vsm_depth_cache;
__exported import vsm_dynamic_depth;

bool ProjectVirtualShadowCasterBounds(VirtualShadowMapParameters view, float3 minimum, float3 maximum,
    out float2 uvMinimum, out float2 uvMaximum)
{
    float2 ndcMinimum = float2(1e20), ndcMaximum = float2(-1e20);
    float minimumW = 1e20, maximumW = -1e20;
    float4 sideDistances = float4(1e20);
    float2 depthDistances = float2(1e20);
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 position = float3((corner & 1u) != 0u ? maximum.x : minimum.x,
            (corner & 2u) != 0u ? maximum.y : minimum.y, (corner & 4u) != 0u ? maximum.z : minimum.z);
        float4 clip = ProjectVirtualShadowClip(view, position);
        minimumW = min(minimumW, clip.w);
        maximumW = max(maximumW, clip.w);
        sideDistances = min(sideDistances, float4(clip.xy, -clip.xy) - clip.w);
        depthDistances = min(depthDistances, float2(-clip.z, clip.z - clip.w));
        float2 ndc = clip.xy / max(clip.w, 1e-5);
        ndcMinimum = min(ndcMinimum, ndc);
        ndcMaximum = max(ndcMaximum, ndc);
    }
    uvMinimum = float2(0.0);
    uvMaximum = float2(1.0);
    if (maximumW <= 1e-5 || any(sideDistances > 0.0)
        || view.LightKind != VSM_LIGHT_DIRECTIONAL && any(depthDistances > 0.0)) return false;
    if (minimumW <= 1e-5) return true;
    ndcMinimum = clamp(ndcMinimum, float2(-1.0), float2(1.0));
    ndcMaximum = clamp(ndcMaximum, float2(-1.0), float2(1.0));
    float2 first = ndcMinimum * float2(0.5, -0.5) + 0.5;
    float2 last = ndcMaximum * float2(0.5, -0.5) + 0.5;
    uvMinimum = saturate(min(first, last));
    uvMaximum = saturate(max(first, last));
    return true;
}

bool VirtualShadowCasterOutsideLight(VirtualShadowMapParameters view, float3 minimum, float3 maximum)
{
    if (view.LightKind == VSM_LIGHT_DIRECTIONAL || view.LightRange <= 0.0) return false;
    float3 distance = max(max(minimum - view.LightPosition, view.LightPosition - maximum), float3(0.0));
    float radius = view.LightRange * 1.00001 + 1e-5;
    return dot(distance, distance) > radius * radius;
}

void InvalidateVirtualShadowCasterPage(VirtualShadowMapParameters view, uint mipLevel, uint2 localPage,
    uint pageTableEntryCount, uint depthCacheClass, bool deferred,
    StructuredBuffer<uint> requestedPageFlags, RWStructuredBuffer<VirtualShadowPageMetadata> pageMetadata,
    RWStructuredBuffer<uint> pageTable)
{
    uint tableIndex = VirtualShadowPageTableIndex(view, mipLevel, localPage);
    if (tableIndex >= pageTableEntryCount) return;
    uint entry = pageTable[tableIndex];
    uint atlasPage;
    if ((entry & VSM_PAGE_RENDERING) == 0u || !DecodeVirtualShadowPage(entry, view.AtlasPagesPerRow, atlasPage)) return;
    int2 worldPage = VirtualShadowWorldPage(view, mipLevel, localPage);
    if (pageMetadata[atlasPage].Generation != view.CacheGeneration
        || pageMetadata[atlasPage].RangeId != view.RangeId
        || pageMetadata[atlasPage].RangeGeneration != view.RangeGeneration
        || any(pageMetadata[atlasPage].WorldPage != worldPage)) return;
    uint requestFlags = pageMetadata[atlasPage].RequestFlags;
    if (requestedPageFlags[tableIndex] != 0u) requestFlags = requestedPageFlags[tableIndex];
    uint ignored;
    if (deferred)
    {
        InterlockedOr(pageMetadata[atlasPage].Flags, VSM_PAGE_DEFERRED_INVALIDATION, ignored);
        return;
    }
    if (depthCacheClass == 0u && (view.Flags & VSM_VIEW_FORCE_CACHE_DYNAMIC_COARSE) != 0u
        && (requestFlags & VSM_PAGE_REQUEST_DETAIL) == 0u) return;
    uint flags = depthCacheClass == 1u ? VSM_PAGE_INVALIDATE_STATIC | VSM_PAGE_INVALIDATE_DYNAMIC
        : VSM_PAGE_INVALIDATE_DYNAMIC;
    InterlockedOr(pageMetadata[atlasPage].Flags, flags, ignored);
    if (depthCacheClass == 1u) InterlockedExchange(pageMetadata[atlasPage].StaticDepthGeneration, 0u, ignored);
    InterlockedAnd(pageTable[tableIndex], ~VSM_PAGE_ANY_DEPTH, ignored);
}
