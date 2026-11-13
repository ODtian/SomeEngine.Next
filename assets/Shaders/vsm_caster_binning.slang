__exported import screen_binning;
__exported import vsm_addressing;
__exported import vsm_caster_pages;
__exported import cluster_depth_cache;

struct VirtualShadowInvalidationUniforms
{
    uint ChangedInstanceCount;
    uint ViewCount;
    uint PageTableEntryCount;
    uint PageDilation;
    uint FrameIndex;
    uint HierarchyWordsPerView;
    uint MipLevelCount;
    uint DeferredChangedInstanceCount;
    float3 PreviousWorldTranslation;
    uint Padding;
};

ConstantBuffer<VirtualShadowInvalidationUniforms> Invalidation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<float4> InstanceWorldBounds;
StructuredBuffer<uint> ChangedInstances;
StructuredBuffer<uint> PreviousDepthCacheClasses;
StructuredBuffer<uint> PreviousCasterRows;
StructuredBuffer<float4> LodWorldBounds;
StructuredBuffer<uint> RequestedPageFlags;
StructuredBuffer<uint> AllocatedPageHierarchy;
StructuredBuffer<uint4> AllocatedPageRectBounds;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<VirtualShadowPageMetadata> PageMetadata;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ShadowPageTable;

struct VirtualShadowCasterBounds
{
    uint ViewIndex;
    uint MipLevel;
    uint DepthCacheClass;
    bool Deferred;
    bool Unbounded;
    float3 Minimum;
    float3 Maximum;
};

VirtualShadowCasterBounds ReadVirtualShadowCasterBounds(uint itemIndex)
{
    uint boundsIndex = itemIndex & 1u;
    itemIndex >>= 1u;
    VirtualShadowCasterBounds bounds = (VirtualShadowCasterBounds)0;
    bounds.MipLevel = itemIndex % Invalidation.MipLevelCount;
    itemIndex /= Invalidation.MipLevelCount;
    bounds.ViewIndex = itemIndex % Invalidation.ViewCount;
    uint changedIndex = itemIndex / Invalidation.ViewCount;
    bounds.Deferred = changedIndex >= Invalidation.ChangedInstanceCount - Invalidation.DeferredChangedInstanceCount;
    uint instance = ChangedInstances[changedIndex];
    if ((instance & 0x80000000u) != 0u)
    {
        uint lodIndex = instance & 0x7fffffffu;
        bounds.Minimum = boundsIndex == 0u ? LodWorldBounds[lodIndex * 2u].xyz : float3(1.0);
        bounds.Maximum = boundsIndex == 0u ? LodWorldBounds[lodIndex * 2u + 1u].xyz : float3(-1.0);
        bounds.DepthCacheClass = LoadTargetDepthCacheClass(uint(LodWorldBounds[lodIndex * 2u].w));
        bounds.Unbounded = InstanceWorldBounds[uint(LodWorldBounds[lodIndex * 2u].w) * 4u].w != 0.0;
        bounds.Deferred = true;
        return bounds;
    }
    if ((instance & 0x20000000u) != 0u)
    {
        uint oldRow = instance & 0x1fffffffu;
        bounds.Minimum = boundsIndex == 0u ? InstanceWorldBounds[oldRow * 4u + 2u].xyz + Invalidation.PreviousWorldTranslation : float3(1.0);
        bounds.Maximum = boundsIndex == 0u ? InstanceWorldBounds[oldRow * 4u + 3u].xyz + Invalidation.PreviousWorldTranslation : float3(-1.0);
        bounds.Unbounded = InstanceWorldBounds[oldRow * 4u + 2u].w != 0.0;
        bounds.DepthCacheClass = (PreviousDepthCacheClasses[oldRow >> 5u] >> (oldRow & 31u)) & 1u;
        return bounds;
    }
    bool rasterInputsChanged = (instance & 0x40000000u) != 0u;
    instance &= 0x3fffffffu;
    uint oldRow = PreviousCasterRows[instance];
    if (boundsIndex != 0u && oldRow == 0xffffffffu)
    {
        bounds.Minimum = float3(1.0);
        bounds.Maximum = float3(-1.0);
        return bounds;
    }
    uint offset = (boundsIndex == 0u ? instance * 4u : oldRow * 4u + 2u);
    bounds.Minimum = InstanceWorldBounds[offset].xyz;
    bounds.Maximum = InstanceWorldBounds[offset + 1u].xyz;
    if (boundsIndex != 0u)
    {
        bounds.Minimum += Invalidation.PreviousWorldTranslation;
        bounds.Maximum += Invalidation.PreviousWorldTranslation;
    }
    bounds.Unbounded = InstanceWorldBounds[offset].w != 0.0;
    if (boundsIndex == 0u)
        bounds.DepthCacheClass = rasterInputsChanged ? 1u : LoadTargetDepthCacheClass(instance);
    else bounds.DepthCacheClass = rasterInputsChanged ? 1u :
        (PreviousDepthCacheClasses[oldRow >> 5u] >> (oldRow & 31u)) & 1u;
    return bounds;
}

struct VirtualShadowCasterCoverage : IScreenBinningCoverage
{
    static ScreenBinningBounds GetBounds(uint itemIndex)
    {
        ScreenBinningBounds rectangle = (ScreenBinningBounds)0;
        VirtualShadowCasterBounds bounds = ReadVirtualShadowCasterBounds(itemIndex);
        VirtualShadowMapParameters view = ShadowViews[bounds.ViewIndex];
        if ((view.Flags & VSM_VIEW_CACHED_DISTANT_LIGHT) != 0u
            || bounds.DepthCacheClass == 0u && VirtualShadowDynamicReceiverRefresh(view)
            || bounds.MipLevel < VirtualShadowFirstMip(view) || bounds.MipLevel >= max(view.MipLevelCount, 1u)
            || any(bounds.Minimum > bounds.Maximum)) return rectangle;
        float2 uvMinimum = 0.0.xx, uvMaximum = 1.0.xx;
        if (!bounds.Unbounded && (VirtualShadowCasterOutsideLight(view, bounds.Minimum, bounds.Maximum)
            || !ProjectVirtualShadowCasterBounds(view, bounds.Minimum, bounds.Maximum, uvMinimum, uvMaximum))) return rectangle;
        uint dimension = VirtualShadowMipPagesPerRow(view, bounds.MipLevel);
        uint2 first = min(uint2(uvMinimum * float(dimension)), dimension - 1u);
        uint2 last = min(uint2(uvMaximum * float(dimension)), dimension - 1u);
        first -= min(first, uint2(Invalidation.PageDilation));
        last = min(last + Invalidation.PageDilation, dimension - 1u);
        uint4 allocated = AllocatedPageRectBounds[bounds.ViewIndex * Invalidation.MipLevelCount + bounds.MipLevel];
        first = max(first, allocated.xy);
        last = min(last, allocated.zw);
        if (any(first > last)) return rectangle;
        rectangle.Minimum = uint3(first, 0u);
        rectangle.Size = uint3(last - first + 1u, 1u);
        return rectangle;
    }

    static bool Intersects(uint itemIndex, uint3 page)
    {
        VirtualShadowCasterBounds bounds = ReadVirtualShadowCasterBounds(itemIndex);
        VirtualShadowMapParameters view = ShadowViews[bounds.ViewIndex];
        uint dimension = VirtualShadowMipPagesPerRow(view, bounds.MipLevel);
        uint offset = bounds.ViewIndex * Invalidation.HierarchyWordsPerView
            + VirtualShadowPageHierarchyLevelOffset(view, bounds.MipLevel, 0u);
        uint word = AllocatedPageHierarchy[offset + page.y * ((dimension + 31u) / 32u) + page.x / 32u];
        return (word & (1u << (page.x & 31u))) != 0u;
    }
};

void InvalidateVirtualShadowCasterPage(uint itemIndex, uint2 localPage)
{
    VirtualShadowCasterBounds bounds = ReadVirtualShadowCasterBounds(itemIndex);
    VirtualShadowMapParameters view = ShadowViews[bounds.ViewIndex];
    InvalidateVirtualShadowCasterPage(view, bounds.MipLevel, localPage, Invalidation.PageTableEntryCount,
        bounds.DepthCacheClass, bounds.Deferred, RequestedPageFlags, PageMetadata, ShadowPageTable);
}

struct VirtualShadowCasterInvalidationOutput : IScreenBinningOutput
{
    static void Write(uint itemIndex, uint3 page)
    {
        InvalidateVirtualShadowCasterPage(itemIndex, page.xy);
    }
};

[shader("compute")][numthreads(256, 1, 1)]
void CountScreenItems(uint3 thread : SV_DispatchThreadID)
{
    ScreenBinningCountCoverage<VirtualShadowCasterCoverage>(ScreenBinningThreadIndex(thread));
}

[shader("compute")][numthreads(256, 1, 1)]
void ExpandScreenItems(uint3 thread : SV_DispatchThreadID)
{
    ScreenBinningExpand<VirtualShadowCasterCoverage, VirtualShadowCasterInvalidationOutput>(ScreenBinningThreadIndex(thread));
}
