#include "vsm_sampling.slang"
#include "render_lighting.slang"

struct VirtualShadowPageDemandUniforms
{
    float4x4 ClipToWorld;
    uint ReceiverWidth;
    uint ReceiverHeight;
    uint ViewCount;
    uint PageTableEntryCount;
    uint DirectionalLightCount;
    uint DirectionalClipmapLevels;
    uint PointLightCount;
    uint PointLightBufferOffset;
    uint PointViewOffset;
    uint SpotLightCount;
    uint SpotLightBufferOffset;
    uint SpotViewOffset;
    uint ReceiverViewIndex;
    uint FrameIndex;
    float PageDilationBorderPages;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

ConstantBuffer<VirtualShadowPageDemandUniforms> PageDemand;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
Texture2DArray<float> ReceiverDepth;
RWStructuredBuffer<uint> RequestedShadowPages;

void PublishShadowPageRequest(uint tableIndex)
{
    if (tableIndex >= PageDemand.PageTableEntryCount)
        return;
    // All fine writers publish the same frame epoch. The store is idempotent, so an RMW
    // atomic and a second append-list representation only add contention and memory traffic.
    RequestedShadowPages[tableIndex] = PageDemand.FrameIndex;
}

void PublishVirtualShadowPage(
    VirtualShadowMapParameters shadowView,
    int2 localPage)
{
    int pageLimit = int(VirtualShadowPagesPerRow(shadowView));
    if (any(localPage < 0) || any(localPage >= pageLimit))
        return;

    uint2 page = uint2(localPage);
    PublishShadowPageRequest(VirtualShadowPageTableIndex(shadowView, page));
}

void PublishProjectedShadowView(
    uint viewIndex,
    float3 worldPosition,
    float2 pageDilationDither)
{
    if (viewIndex >= PageDemand.ViewCount)
        return;
    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    float3 shadowNdc;
    float2 shadowUv;
    if (!ProjectVirtualShadowPosition(shadowView, worldPosition, shadowNdc, shadowUv))
        return;

    float2 pageAddress = shadowUv *
        (float(shadowView.VirtualResolution) / float(shadowView.PageSize));
    int2 centerPage = int2(floor(pageAddress));
    PublishVirtualShadowPage(shadowView, centerPage);

    if (PageDemand.PageDilationBorderPages <= 0.0)
        return;

    float2 pageDilation =
        PageDemand.PageDilationBorderPages * pageDilationDither;
    int2 positivePage = int2(floor(pageAddress + pageDilation));
    int2 negativePage = int2(floor(pageAddress - pageDilation));
    if (any(positivePage != centerPage))
        PublishVirtualShadowPage(shadowView, positivePage);
    if (any(negativePage != centerPage) && any(negativePage != positivePage))
        PublishVirtualShadowPage(shadowView, negativePage);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void MarkVirtualShadowPages(
    uint3 dispatchThreadId : SV_DispatchThreadID,
    uint groupIndex : SV_GroupIndex)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x >= PageDemand.ReceiverWidth || pixel.y >= PageDemand.ReceiverHeight)
        return;
    float depth = ReceiverDepth.Load(int4(pixel, 0, 0));
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
    float2 pageDilationDither = float2(
        (groupIndex & 1u) != 0u ? 1.0 : -1.0,
        (groupIndex & 2u) != 0u ? 1.0 : -1.0);

    uint clipmapLevels = max(PageDemand.DirectionalClipmapLevels, 1u);
    uint lightCount = min(
        PageDemand.DirectionalLightCount,
        PageDemand.ViewCount / clipmapLevels);
    for (uint lightIndex = 0u; lightIndex < lightCount; lightIndex++)
    {
        uint baseViewIndex = lightIndex * clipmapLevels;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        float distanceToClipmapOrigin = length(worldPosition - baseView.ClipmapWorldOrigin);
        int absoluteLevel = (int)floor(
            log2(max(distanceToClipmapOrigin, 1e-6)) + baseView.ResolutionLodBias);
        uint relativeLevel = (uint)max(0, absoluteLevel - baseView.FirstClipmapLevel);
        if (relativeLevel >= baseView.ClipmapLevelCount ||
            baseViewIndex + relativeLevel >= PageDemand.ViewCount)
            continue;

        PublishProjectedShadowView(
            baseViewIndex + relativeLevel,
            worldPosition,
            pageDilationDither);
    }

    ClusteredLightGridParameters lighting =
        LightingViews[PageDemand.ReceiverViewIndex];
    float3 viewPosition = mul(float4(worldPosition, 1.0), lighting.View).xyz;
    float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
    uint cell = ClusteredLightCellIndex(
        lighting,
        PageDemand.ReceiverViewIndex,
        pixel,
        viewDepth);
    uint localCount = min(
        ClusteredLightGrid[cell].y,
        lighting.MaxLightsPerCluster);
    uint firstIndex = cell * lighting.MaxLightsPerCluster;
    for (uint localIndex = 0u; localIndex < localCount; localIndex++)
    {
        uint lightIndex = LightIndexList[firstIndex + localIndex];
        if (lightIndex >= PageDemand.PointLightBufferOffset &&
            lightIndex < PageDemand.PointLightBufferOffset + PageDemand.PointLightCount)
        {
            uint pointLightIndex = lightIndex - PageDemand.PointLightBufferOffset;
            uint firstView = PageDemand.PointViewOffset + pointLightIndex * 6u;
            if (firstView + 5u >= PageDemand.ViewCount)
                continue;
            uint faceIndex = VirtualShadowPointFace(
                worldPosition - ShadowViews[firstView].LightPosition);
            PublishProjectedShadowView(
                firstView + faceIndex,
                worldPosition,
                pageDilationDither);
        }
        else if (lightIndex >= PageDemand.SpotLightBufferOffset &&
            lightIndex < PageDemand.SpotLightBufferOffset + PageDemand.SpotLightCount)
        {
            uint spotLightIndex = lightIndex - PageDemand.SpotLightBufferOffset;
            PublishProjectedShadowView(
                PageDemand.SpotViewOffset + spotLightIndex,
                worldPosition,
                pageDilationDither);
        }
    }
}
