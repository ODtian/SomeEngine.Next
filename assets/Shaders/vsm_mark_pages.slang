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
StructuredBuffer<float2> DynamicResolutionBias;
#include "vsm_page_request_common.slang"

uint PublishVirtualShadowPage(
    VirtualShadowMapParameters shadowView,
    uint mipLevel,
    int2 localPage)
{
    uint dimension = VirtualShadowMipPagesPerRow(shadowView, mipLevel);
    if (any(localPage < 0) || any(localPage >= int(dimension)))
        return 0xFFFFFFFFu;
    uint tableIndex = VirtualShadowPageTableIndex(
        shadowView, mipLevel, uint2(localPage));
    PublishVirtualShadowRequest(
        tableIndex,
        VSM_PAGE_REQUEST_DETAIL,
        PageDemand.FrameIndex,
        PageDemand.PageTableEntryCount);
    return tableIndex;
}

bool ProjectShadowPage(
    VirtualShadowMapParameters shadowView,
    uint mipLevel,
    float3 worldPosition,
    out float2 shadowUv,
    out float2 pageAddress)
{
    float3 shadowNdc;
    if (!ProjectVirtualShadowPosition(shadowView, worldPosition, shadowNdc, shadowUv))
    {
        shadowUv = 0.0.xx;
        pageAddress = 0.0.xx;
        return false;
    }
    pageAddress = shadowUv * float(VirtualShadowMipPagesPerRow(shadowView, mipLevel));
    return true;
}

void PublishProjectedShadowView(
    uint viewIndex,
    uint mipLevel,
    float3 worldPosition,
    float2 pageDilationDither)
{
    if (viewIndex >= PageDemand.ViewCount)
        return;
    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    float2 shadowUv;
    float2 pageAddress;
    if (!ProjectShadowPage(
            shadowView,
            mipLevel,
            worldPosition,
            shadowUv,
            pageAddress))
        return;

    int2 centerPage = int2(floor(pageAddress));
    uint centerTableIndex =
        PublishVirtualShadowPage(shadowView, mipLevel, centerPage);
    if (centerTableIndex != 0xFFFFFFFFu &&
        (shadowView.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
    {
        uint mipResolution = VirtualShadowMipResolution(shadowView, mipLevel);
        uint2 virtualPixel = min(
            (uint2)(shadowUv * float(mipResolution)),
            mipResolution - 1u);
        MarkVirtualShadowReceiverMask(
            centerTableIndex,
            virtualPixel,
            shadowView.PageSize);
    }
    if (PageDemand.PageDilationBorderPages <= 0.0)
        return;

    float2 dilation = PageDemand.PageDilationBorderPages * pageDilationDither;
    int2 positivePage = int2(floor(pageAddress + dilation));
    int2 negativePage = int2(floor(pageAddress - dilation));
    if (any(positivePage != centerPage))
        PublishVirtualShadowPage(shadowView, mipLevel, positivePage);
    if (any(negativePage != centerPage) && any(negativePage != positivePage))
        PublishVirtualShadowPage(shadowView, mipLevel, negativePage);
}

float3 ReconstructReceiverPosition(uint2 pixel, float depth)
{
    float2 uv = (float2(pixel) + 0.5) /
        float2(PageDemand.ReceiverWidth, PageDemand.ReceiverHeight);
    float4 world = mul(
        float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0),
        PageDemand.ClipToWorld);
    return world.xyz / max(abs(world.w), 1e-8);
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

    float3 worldPosition = ReconstructReceiverPosition(pixel, depth);
    uint2 pixelX = uint2(min(pixel.x + 1u, PageDemand.ReceiverWidth - 1u), pixel.y);
    uint2 pixelY = uint2(pixel.x, min(pixel.y + 1u, PageDemand.ReceiverHeight - 1u));
    float3 worldPositionDx = ReconstructReceiverPosition(pixelX, depth);
    float3 worldPositionDy = ReconstructReceiverPosition(pixelY, depth);
    float2 pageDilationDither = float2(
        (groupIndex & 1u) != 0u ? 1.0 : -1.0,
        (groupIndex & 2u) != 0u ? 1.0 : -1.0);
    float2 dynamicResolutionBias = DynamicResolutionBias[0];

    uint clipmapLevels = max(PageDemand.DirectionalClipmapLevels, 1u);
    uint directionalCount = min(
        PageDemand.DirectionalLightCount,
        PageDemand.ViewCount / clipmapLevels);
    for (uint lightIndex = 0u; lightIndex < directionalCount; lightIndex++)
    {
        uint baseViewIndex = lightIndex * clipmapLevels;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        float distanceToClipmapOrigin = length(worldPosition - baseView.ClipmapWorldOrigin);
        int absoluteLevel = (int)floor(
            log2(max(distanceToClipmapOrigin, 1e-6)) +
            baseView.ResolutionLodBias + dynamicResolutionBias.x);
        uint relativeLevel = (uint)max(0, absoluteLevel - baseView.FirstClipmapLevel);
        if (relativeLevel < baseView.ClipmapLevelCount &&
            baseViewIndex + relativeLevel < PageDemand.ViewCount)
        {
            PublishProjectedShadowView(
                baseViewIndex + relativeLevel,
                0u,
                worldPosition,
                pageDilationDither);
        }
    }

    ClusteredLightGridParameters lighting = LightingViews[PageDemand.ReceiverViewIndex];
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
        uint shadowViewIndex = 0xFFFFFFFFu;
        if (lightIndex >= PageDemand.PointLightBufferOffset &&
            lightIndex < PageDemand.PointLightBufferOffset + PageDemand.PointLightCount)
        {
            uint pointLightIndex = lightIndex - PageDemand.PointLightBufferOffset;
            uint firstView = PageDemand.PointViewOffset + pointLightIndex * 6u;
            if (firstView + 5u >= PageDemand.ViewCount)
                continue;
            uint faceIndex = VirtualShadowPointFace(
                worldPosition - ShadowViews[firstView].LightPosition);
            shadowViewIndex = firstView + faceIndex;
        }
        else if (lightIndex >= PageDemand.SpotLightBufferOffset &&
            lightIndex < PageDemand.SpotLightBufferOffset + PageDemand.SpotLightCount)
        {
            uint spotLightIndex = lightIndex - PageDemand.SpotLightBufferOffset;
            shadowViewIndex = PageDemand.SpotViewOffset + spotLightIndex;
            if (shadowViewIndex >= PageDemand.ViewCount)
                continue;
        }
        else
        {
            continue;
        }

        VirtualShadowMapParameters shadowView = ShadowViews[shadowViewIndex];
        float3 toLight = shadowView.LightPosition - worldPosition;
        if (shadowView.LightRange > 0.0 &&
            dot(toLight, toLight) > shadowView.LightRange * shadowView.LightRange)
        {
            continue;
        }
        uint mipLevel = VirtualShadowLocalMipFromReceiverFootprint(
            shadowView,
            worldPosition,
            worldPositionDx,
            worldPositionDy,
            dynamicResolutionBias.y);
        PublishProjectedShadowView(
            shadowViewIndex,
            mipLevel,
            worldPosition,
            pageDilationDither);
    }
}
