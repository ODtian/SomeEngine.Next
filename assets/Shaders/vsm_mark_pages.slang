#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_receiver_pixel.slang"
#include "vsm_receiver_projection.slang"
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
    float NormalBias;
    uint Pad1;
    uint Pad2;
    uint Pad3;
    uint Pad4;
    VirtualShadowReceiverProjection ReceiverProjection;
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

void PublishProjectedShadowView(
    uint viewIndex,
    uint mipLevel,
    float3 worldPosition)
{
    if (viewIndex >= PageDemand.ViewCount)
        return;
    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    mipLevel = clamp(mipLevel, VirtualShadowFirstMip(shadowView), shadowView.MipLevelCount - 1u);
    float3 shadowNdc;
    float2 shadowUv;
    if (!ProjectVirtualShadowPosition(shadowView, worldPosition, shadowNdc, shadowUv))
        return;

    uint mipResolution = VirtualShadowMipResolution(shadowView, mipLevel);
    float2 borderPixels = PageDemand.PageDilationBorderPages * float(shadowView.PageSize);
    if (PageDemand.NormalBias > 0.0)
    {
        float4 clip = mul(float4(worldPosition, 1.0), shadowView.LightViewProjection);
        float3 x = float3(shadowView.LightViewProjection[0][0],
            shadowView.LightViewProjection[1][0], shadowView.LightViewProjection[2][0]);
        float3 y = float3(shadowView.LightViewProjection[0][1],
            shadowView.LightViewProjection[1][1], shadowView.LightViewProjection[2][1]);
        float3 w = float3(shadowView.LightViewProjection[0][3],
            shadowView.LightViewProjection[1][3], shadowView.LightViewProjection[2][3]);
        float2 xyChange = float2(length(x), length(y)) * PageDemand.NormalBias;
        float wChange = length(w) * PageDemand.NormalBias;
        float minimumW = clip.w - wChange;
        float2 uvBorder = minimumW > 1e-6
            ? 0.5 * (xyChange * clip.w + abs(clip.xy) * wChange) / (clip.w * minimumW)
            : 1.0.xx;
        borderPixels += uvBorder * float(mipResolution);
    }
    // Material normal offsets and filtering can sample across both page and
    // receiver-cell boundaries. Mark the complete footprint, independent of
    // a material's vertex layout or its choice of shading normal.
    uint2 firstPixel = (uint2)clamp(floor(shadowUv * float(mipResolution) - borderPixels),
        0.0.xx, float(mipResolution - 1u).xx);
    uint2 lastPixel = (uint2)clamp(floor(shadowUv * float(mipResolution) + borderPixels),
        0.0.xx, float(mipResolution - 1u).xx);
    uint2 firstPage = firstPixel / shadowView.PageSize;
    uint2 lastPage = lastPixel / shadowView.PageSize;
    for (uint y = firstPage.y; y <= lastPage.y; y++)
    for (uint x = firstPage.x; x <= lastPage.x; x++)
    {
        uint2 page = uint2(x, y);
        uint tableIndex = PublishVirtualShadowPage(shadowView, mipLevel, int2(page));
        if (tableIndex != 0xFFFFFFFFu && (shadowView.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
            MarkVirtualShadowReceiverRectangle(tableIndex, page, firstPixel, lastPixel, shadowView.PageSize);
    }
}

[shader("compute")]
[numthreads(8, 8, 1)]
void MarkVirtualShadowPages(
    uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadId.xy;
    if (pixel.x >= PageDemand.ReceiverWidth || pixel.y >= PageDemand.ReceiverHeight)
        return;
    float depth = ReceiverDepth.Load(int4(pixel, 0, 0));
    if (depth >= 1.0)
        return;

    VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
        ReceiverDepth, uint3(pixel, 0u), uint2(PageDemand.ReceiverWidth, PageDemand.ReceiverHeight),
        depth, PageDemand.ClipToWorld);
    float3 worldPosition = receiver.Position;
    float3 worldPositionDx = receiver.PositionDx;
    float3 worldPositionDy = receiver.PositionDy;
    float2 dynamicResolutionBias = DynamicResolutionBias[0];

    uint clipmapLevels = max(PageDemand.DirectionalClipmapLevels, 1u);
    uint directionalCount = min(
        PageDemand.DirectionalLightCount,
        PageDemand.ViewCount / clipmapLevels);
    for (uint lightIndex = 0u; lightIndex < directionalCount; lightIndex++)
    {
        uint baseViewIndex = lightIndex * clipmapLevels;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        uint relativeLevel = SelectDirectionalVirtualShadowView(ShadowViews, baseViewIndex,
            baseView.ClipmapLevelCount, worldPosition, worldPositionDx, worldPositionDy, dynamicResolutionBias.x,
            CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition));
        if (relativeLevel < baseView.ClipmapLevelCount &&
            baseViewIndex + relativeLevel < PageDemand.ViewCount)
        {
            PublishProjectedShadowView(
                baseViewIndex + relativeLevel,
                0u,
                worldPosition);
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
        uint mipLevel = VirtualShadowLocalMipFromNominalPixelRadius(
            shadowView,
            worldPosition,
            CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection, worldPosition),
            dynamicResolutionBias.y);
        PublishProjectedShadowView(
            shadowViewIndex,
            mipLevel,
            worldPosition);
    }
}
