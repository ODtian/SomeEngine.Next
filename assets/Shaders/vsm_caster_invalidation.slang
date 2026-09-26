#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowInvalidationUniforms
{
    uint ChangedInstanceCount;
    uint ViewCount;
    uint PageTableEntryCount;
    uint PageDilation;
    uint FrameIndex;
    uint HierarchyWordsPerView;
    uint MipLevelCount;
    uint Pad2;
};

ConstantBuffer<VirtualShadowInvalidationUniforms> Invalidation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<float4> InstanceWorldBounds;
StructuredBuffer<uint> ChangedInstances;
StructuredBuffer<uint> RequestedShadowPages;
StructuredBuffer<uint> RequestedPageFlags;
StructuredBuffer<uint4> PageMetadata;
StructuredBuffer<uint> AllocatedPageHierarchy;
StructuredBuffer<uint4> AllocatedPageRectBounds;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint4> ShadowPageTable;

bool OverlapsAllocatedPages(
    uint viewIndex,
    VirtualShadowMapParameters view,
    uint mipLevel,
    uint2 low,
    uint2 high)
{
    uint2 extent = high - low + 1u;
    uint maximumExtent = max(extent.x, extent.y);
    uint hierarchyLevel =
        maximumExtent > 1u ? uint(firstbithigh(maximumExtent)) : 0u;
    uint maxHierarchyLevel =
        uint(firstbithigh(max(VirtualShadowMipPagesPerRow(view, mipLevel), 1u)));
    hierarchyLevel = min(hierarchyLevel, maxHierarchyLevel);

    uint2 first = low >> hierarchyLevel;
    uint2 last = high >> hierarchyLevel;
    uint dimension =
        VirtualShadowPageHierarchyDimension(view, mipLevel, hierarchyLevel);
    uint wordsPerRow = (dimension + 31u) / 32u;
    uint base =
        viewIndex * Invalidation.HierarchyWordsPerView +
        VirtualShadowPageHierarchyLevelOffset(view, mipLevel, hierarchyLevel);
    uint firstWord = first.x / 32u;
    uint lastWord = last.x / 32u;

    for (uint row = first.y; row <= last.y; row++)
    {
        for (uint word = firstWord; word <= lastWord; word++)
        {
            uint mask = AllocatedPageHierarchy[base + row * wordsPerRow + word];
            if (word == firstWord)
                mask &= 0xFFFFFFFFu << (first.x & 31u);
            if (word == lastWord)
                mask &= 0xFFFFFFFFu >> (31u - (last.x & 31u));
            if (mask != 0u)
                return true;
        }
    }
    return false;
}

bool ProjectShadowBounds(
    VirtualShadowMapParameters view,
    float3 worldMin,
    float3 worldMax,
    out float2 uvMin,
    out float2 uvMax)
{
    float2 ndcMin = 1e20.xx;
    float2 ndcMax = -1e20.xx;
    float minW = 1e20;
    float maxW = -1e20;
    float4 sideDistances = 1e20.xxxx;
    float2 depthDistances = 1e20.xx;
    [unroll]
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 position = float3(
            (corner & 1u) != 0u ? worldMax.x : worldMin.x,
            (corner & 2u) != 0u ? worldMax.y : worldMin.y,
            (corner & 4u) != 0u ? worldMax.z : worldMin.z);
        float4 clip = mul(float4(position, 1.0), view.LightViewProjection);
        minW = min(minW, clip.w);
        maxW = max(maxW, clip.w);
        sideDistances = min(sideDistances, float4(clip.xy, -clip.xy) - clip.w);
        depthDistances = min(depthDistances, float2(-clip.z, clip.z - clip.w));
        float2 ndc = clip.xy / max(clip.w, 1e-5);
        ndcMin = min(ndcMin, ndc);
        ndcMax = max(ndcMax, ndc);
    }
    // Reject outside the light frustum before expanding a rectangle that crosses
    // the projection plane. A box entirely behind a cube face affects no pages.
    if (maxW <= 1e-5 || any(sideDistances > 0.0) ||
        (view.LightKind != VSM_LIGHT_DIRECTIONAL && any(depthDistances > 0.0)))
    {
        uvMin = 0.0.xx;
        uvMax = 0.0.xx;
        return false;
    }
    if (minW <= 1e-5)
    {
        uvMin = 0.0.xx;
        uvMax = 1.0.xx;
        return true;
    }
    ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
    ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);
    float2 a = ndcMin * float2(0.5, -0.5) + 0.5.xx;
    float2 b = ndcMax * float2(0.5, -0.5) + 0.5.xx;
    uvMin = saturate(min(a, b));
    uvMax = saturate(max(a, b));
    return true;
}

bool BoundsOutsideLocalLight(
    VirtualShadowMapParameters view,
    float3 worldMin,
    float3 worldMax)
{
    if (view.LightKind == VSM_LIGHT_DIRECTIONAL || view.LightRange <= 0.0)
        return false;
    float3 distance = max(max(worldMin - view.LightPosition, view.LightPosition - worldMax), 0.0.xxx);
    float radius = view.LightRange * 1.00001 + 1e-5;
    return dot(distance, distance) > radius * radius;
}

void InvalidateBoundsInView(
    uint viewIndex,
    VirtualShadowMapParameters view,
    float3 worldMin,
    float3 worldMax)
{
    if (any(worldMin > worldMax) || BoundsOutsideLocalLight(view, worldMin, worldMax))
        return;
    float2 uvMin;
    float2 uvMax;
    if (!ProjectShadowBounds(view, worldMin, worldMax, uvMin, uvMax))
        return;

    uint firstMip = view.LightKind == VSM_LIGHT_DIRECTIONAL ? 0u : view.MinMipLevel;
    uint mipCount = view.LightKind == VSM_LIGHT_DIRECTIONAL
        ? 1u
        : max(view.MipLevelCount, 1u);

    [loop]
    for (uint mip = firstMip; mip < mipCount; mip++)
    {
        uint dimension = VirtualShadowMipPagesPerRow(view, mip);
        uint2 low = min((uint2)(uvMin * float(dimension)), dimension - 1u);
        uint2 high = min((uint2)(uvMax * float(dimension)), dimension - 1u);
        low -= min(low, Invalidation.PageDilation.xx);
        high = min(high + Invalidation.PageDilation, dimension - 1u);
        // Include cached pages even when they are not requested this frame.
        uint4 allocatedRect = AllocatedPageRectBounds[viewIndex * Invalidation.MipLevelCount + mip];
        low = max(low, allocatedRect.xy);
        high = min(high, allocatedRect.zw);
        if (any(low > high) || !OverlapsAllocatedPages(viewIndex, view, mip, low, high))
            continue;
        [loop]
        for (uint y = low.y; y <= high.y; y++)
        {
            [loop]
            for (uint x = low.x; x <= high.x; x++)
            {
                uint2 localPage = uint2(x, y);
                uint tableIndex = VirtualShadowPageTableIndex(view, mip, localPage);
                if (tableIndex >= Invalidation.PageTableEntryCount)
                    continue;
                int2 worldPage = VirtualShadowWorldPage(view, mip, localPage);
                uint4 entry = ShadowPageTable[tableIndex];
                if (!IsVirtualShadowPageTag(entry, worldPage) ||
                    entry.x == VSM_PAGE_UNMAPPED || entry.x == VSM_PAGE_LOCKED)
                {
                    continue;
                }
                uint atlasPage;
                if (!DecodeVirtualShadowPage(entry.x, atlasPage))
                    continue;
                uint requestFlags = PageMetadata[atlasPage].z;
                if (RequestedShadowPages[tableIndex] == Invalidation.FrameIndex)
                    requestFlags = RequestedPageFlags[tableIndex];
                uint ignored;
                InterlockedExchange(ShadowPageTable[tableIndex].w, 0u, ignored);
            }
        }
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void InvalidateMovedVirtualShadowCasters(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint changedIndex = group.x + group.y * 65535u;
    if (changedIndex >= Invalidation.ChangedInstanceCount)
        return;
    uint instanceId = ChangedInstances[changedIndex];
    float3 currentMin = InstanceWorldBounds[instanceId * 4u].xyz;
    float3 currentMax = InstanceWorldBounds[instanceId * 4u + 1u].xyz;
    float3 previousMin = InstanceWorldBounds[instanceId * 4u + 2u].xyz;
    float3 previousMax = InstanceWorldBounds[instanceId * 4u + 3u].xyz;

    for (uint viewIndex = lane; viewIndex < Invalidation.ViewCount; viewIndex += 64u)
    {
        VirtualShadowMapParameters view = ShadowViews[viewIndex];
        InvalidateBoundsInView(viewIndex, view, currentMin, currentMax);
        InvalidateBoundsInView(viewIndex, view, previousMin, previousMax);
    }
}
