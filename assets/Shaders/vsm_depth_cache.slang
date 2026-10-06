#ifndef VSM_DEPTH_CACHE_SLANG
#define VSM_DEPTH_CACHE_SLANG

static const uint VSM_PAGE_DYNAMIC_UNCACHED = 1u << 2u;
static const uint VSM_PAGE_STATIC_UNCACHED = 1u << 3u;
static const uint VSM_PAGE_INVALIDATE_DYNAMIC = 1u << 4u;
static const uint VSM_PAGE_INVALIDATE_STATIC = 1u << 5u;

uint InitializeVirtualShadowDynamicDepth(uint staticDepth, uint pageFlags)
{
    return (pageFlags & VSM_PAGE_STATIC_UNCACHED) != 0u ? 0u : staticDepth;
}

uint MergeVirtualShadowDepth(uint dynamicDepth, uint staticDepth)
{
    return max(dynamicDepth, staticDepth);
}

#endif
