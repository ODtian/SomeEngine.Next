#include "vsm_sampling.slang"

struct VirtualShadowPageDemandUniforms
{
    float4x4 ClipToWorld;
    uint ReceiverWidth;
    uint ReceiverHeight;
    uint ViewCount;
    uint PageTableEntryCount;
    uint DirectionalLightCount;
    uint DirectionalClipmapLevels;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<VirtualShadowPageDemandUniforms> PageDemand;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
Texture2D<float> ReceiverDepth;
RWStructuredBuffer<uint> RequestedShadowPages;
RWStructuredBuffer<uint> RequestedShadowPageList;
RWStructuredBuffer<uint> RequestedShadowPageCount;
RWByteAddressBuffer AllocatePageDispatchArgs;

[shader("compute")]
[numthreads(8, 8, 1)]
void MarkVirtualShadowPages(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x >= PageDemand.ReceiverWidth || pixel.y >= PageDemand.ReceiverHeight)
        return;
    float depth = ReceiverDepth.Load(int3(pixel, 0));
    if (depth >= 1.0)
        return;

    float2 uv = (float2(pixel) + 0.5) /
        float2(PageDemand.ReceiverWidth, PageDemand.ReceiverHeight);
    float4 world = mul(
        float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0),
        PageDemand.ClipToWorld);
    if (abs(world.w) < 1e-8)
        return;
    float3 worldPosition = world.xyz / world.w;

    uint clipmapLevels = max(PageDemand.DirectionalClipmapLevels, 1u);
    uint lightCount = min(
        PageDemand.DirectionalLightCount,
        PageDemand.ViewCount / clipmapLevels);
    for (uint lightIndex = 0u; lightIndex < lightCount; lightIndex++)
    {
        uint viewIndex = lightIndex * clipmapLevels;
        VirtualShadowMapParameters baseView = ShadowViews[viewIndex];
        float distanceToClipmapOrigin = length(
            worldPosition - baseView.ClipmapWorldOrigin);
        int absoluteLevel = (int)floor(
            log2(max(distanceToClipmapOrigin, 1e-6)) +
            baseView.ResolutionLodBias);
        uint relativeLevel = (uint)max(
            0,
            absoluteLevel - baseView.FirstClipmapLevel);
        if (relativeLevel >= baseView.ClipmapLevelCount ||
            viewIndex + relativeLevel >= PageDemand.ViewCount)
            continue;
        VirtualShadowMapParameters shadowView =
            ShadowViews[viewIndex + relativeLevel];
        float3 shadowNdc;
        float2 shadowUv;
        if (!ProjectVirtualShadowPosition(
                shadowView,
                worldPosition,
                shadowNdc,
                shadowUv))
            continue;

        uint virtualPagesPerRow = VirtualShadowPagesPerRow(shadowView);
        uint2 localPage = min(
            (uint2)(shadowUv * float(virtualPagesPerRow)),
            virtualPagesPerRow - 1u);
        uint pageTableIndex = VirtualShadowPageTableIndex(shadowView, localPage);
        if (pageTableIndex < PageDemand.PageTableEntryCount)
        {
            uint previous;
            InterlockedCompareExchange(
                RequestedShadowPages[pageTableIndex],
                0u,
                1u,
                previous);
            if (previous == 0u)
            {
                uint requestIndex;
                InterlockedAdd(RequestedShadowPageCount[0], 1u, requestIndex);
                if (requestIndex < PageDemand.PageTableEntryCount)
                {
                    RequestedShadowPageList[requestIndex] = pageTableIndex;
                    uint ignored;
                    AllocatePageDispatchArgs.InterlockedMax(
                        0u,
                        (requestIndex + 64u) / 64u,
                        ignored);
                    AllocatePageDispatchArgs.Store(4u, 1u);
                    AllocatePageDispatchArgs.Store(8u, 1u);
                }
            }
        }
    }
}
