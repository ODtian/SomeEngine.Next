#ifndef VSM_RECEIVER_REQUESTS_SLANG
#define VSM_RECEIVER_REQUESTS_SLANG

#include "vsm_sampling.slang"
#include "vsm_receiver_projection.slang"
#include "render_lighting.slang"

struct VirtualShadowPageDemandUniforms
{
    uint ViewCount;
    uint PageTableEntryCount;
    uint FrameIndex;
    uint PointLightBufferOffset;
    uint PointLightCount;
    uint PointViewOffset;
    uint SpotLightCount;
    uint SpotLightBufferOffset;
    uint SpotViewOffset;
    float PageDilationBorderPages;
    float NormalBias;
    uint CullBackfacingPixels;
    float FirstPersonPixelRequestBias;
    int FirstPersonPixelRequestMinLevel;
    uint2 Padding;
    VirtualShadowReceiverProjection ReceiverProjection;
};

ConstantBuffer<VirtualShadowPageDemandUniforms> PageDemand;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
#ifndef RENDER_LIGHTING_INPUTS_DECLARED
#define RENDER_LIGHTING_INPUTS_DECLARED
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
#endif
StructuredBuffer<float> DynamicResolutionBias;
#include "vsm_page_request_common.slang"

bool PublishProjectedShadowViewPixels(
    VirtualShadowMapParameters shadowView,
    uint mipLevel,
    float3 worldPosition, out float2 footprintUV, out float2 footprintBorder)
{
    footprintUV = 0.0.xx;
    footprintBorder = 0.0.xx;
    mipLevel = clamp(mipLevel, VirtualShadowFirstMip(shadowView), shadowView.MipLevelCount - 1u);
    float3 shadowNdc;
    float2 shadowUv;
    if (!ProjectVirtualShadowPosition(shadowView, worldPosition, shadowNdc, shadowUv))
        return false;

    uint mipResolution = VirtualShadowMipResolution(shadowView, mipLevel);
    float2 borderPixels = max(PageDemand.PageDilationBorderPages, shadowView.PageDilationBorder) * float(shadowView.PageSize);
    bool localSource = shadowView.LightKind != VSM_LIGHT_DIRECTIONAL && shadowView.SourceRadius > 0.0;
    if (localSource || PageDemand.NormalBias > 0.0)
    {
        float4 clip = ProjectVirtualShadowClip(shadowView, worldPosition);
        float3 x = float3(shadowView.LightViewProjection[0][0],
            shadowView.LightViewProjection[1][0], shadowView.LightViewProjection[2][0]);
        float3 y = float3(shadowView.LightViewProjection[0][1],
            shadowView.LightViewProjection[1][1], shadowView.LightViewProjection[2][1]);
        float3 w = float3(shadowView.LightViewProjection[0][3],
            shadowView.LightViewProjection[1][3], shadowView.LightViewProjection[2][3]);
        float2 xyLength = float2(length(x), length(y));
        float wLength = length(w);
        if (localSource)
        {
            float radius = CalculateVirtualShadowLocalSourceSupport(shadowView, worldPosition);
            float minimumW = clip.w - wLength * radius;
            float2 border = minimumW > 1e-6
                ? 0.5 * (xyLength * radius * clip.w + abs(clip.xy) * wLength * radius) / (clip.w * minimumW)
                : 1.0.xx;
            borderPixels += border * float(mipResolution);
        }
        if (PageDemand.NormalBias > 0.0)
        {
            float normalBiasLength = CalculateVirtualShadowNormalBiasLength(PageDemand.ReceiverProjection,
                worldPosition, PageDemand.NormalBias, shadowView.WorldUnitsPerCentimeter);
            float2 xyChange = xyLength * normalBiasLength;
            float wChange = wLength * normalBiasLength;
            float minimumW = clip.w - wChange;
            float2 uvBorder = minimumW > 1e-6
                ? 0.5 * (xyChange * clip.w + abs(clip.xy) * wChange) / (clip.w * minimumW)
                : 1.0.xx;
            borderPixels += uvBorder * float(mipResolution);
        }
    }
    footprintUV = shadowUv;
    footprintBorder = borderPixels / float(mipResolution);
    // Material normal offsets and filtering can sample across both page and
    // receiver-cell boundaries. Mark the complete footprint, independent of
    // a material's vertex layout or its choice of shading normal.
    uint2 firstPixel = (uint2)clamp(floor(shadowUv * float(mipResolution) - borderPixels),
        0.0.xx, float(mipResolution - 1u).xx);
    uint2 lastPixel = (uint2)clamp(floor(shadowUv * float(mipResolution) + borderPixels),
        0.0.xx, float(mipResolution - 1u).xx);
    uint pageShift = firstbithigh(shadowView.PageSize);
    uint2 firstPage = firstPixel >> pageShift;
    uint2 lastPage = lastPixel >> pageShift;
    for (uint y = firstPage.y; y <= lastPage.y; y++)
    for (uint x = firstPage.x; x <= lastPage.x; x++)
    {
        uint2 page = uint2(x, y);
        uint tableIndex = VirtualShadowPageTableIndex(shadowView, mipLevel, page);
        PublishVirtualShadowRequest(tableIndex, VSM_PAGE_REQUEST_DETAIL,
            PageDemand.FrameIndex, PageDemand.PageTableEntryCount);
        if ((shadowView.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
            MarkVirtualShadowReceiverRectangle(shadowView, mipLevel, page, firstPixel, lastPixel);
    }
    return true;
}

void PublishProjectedShadowView(uint viewIndex, VirtualShadowMapParameters view,
    uint mipLevel, float3 worldPosition)
{
    float2 uv, border;
    if (!PublishProjectedShadowViewPixels(view, mipLevel, worldPosition, uv, border)) return;
    if (view.LightKind != VSM_LIGHT_POINT ||
        (all(uv - border >= 0.0) && all(uv + border < 1.0))) return;
    float3 axisX = normalize(float3(view.LightViewProjection[0][0],
        view.LightViewProjection[1][0], view.LightViewProjection[2][0]));
    float3 axisY = normalize(float3(view.LightViewProjection[0][1],
        view.LightViewProjection[1][1], view.LightViewProjection[2][1]));
    float3 axisW = normalize(float3(view.LightViewProjection[0][3],
        view.LightViewProjection[1][3], view.LightViewProjection[2][3]));
    uint firstFace = viewIndex - view.FaceIndex;
    float distance = length(worldPosition - view.LightPosition);
    [loop]
    for (uint corner = 0u; corner < 4u; corner++)
    {
        float2 edgeUV = uv + border * float2((corner & 1u) != 0u ? 1.0 : -1.0,
            (corner & 2u) != 0u ? 1.0 : -1.0);
        if (all(edgeUV >= 0.0) && all(edgeUV < 1.0)) continue;
        float2 ndc = edgeUV * float2(2.0, -2.0) + float2(-1.0, 1.0);
        float3 direction = normalize(axisW + ndc.x * axisX + ndc.y * axisY);
        uint face = VirtualShadowPointFace(direction);
        if (face == view.FaceIndex) continue;
        float2 adjacentUV, adjacentBorder;
        PublishProjectedShadowViewPixels(ShadowViews[firstFace + face], mipLevel,
            view.LightPosition + direction * distance, adjacentUV, adjacentBorder);
    }
}

void PublishLocalShadowPosition(float3 worldPosition, uint viewIndex,
    float dynamicResolutionBias, float receiverResolutionBias)
{
    ClusteredLightGridParameters lighting = LightingViews[viewIndex];
    float3 viewPosition = mul(float4(worldPosition, 1.0), lighting.View).xyz;
    float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
    float4 clip = mul(float4(viewPosition, 1.0), lighting.Projection);
    float2 uv = clip.xy / clip.w * float2(.5, -.5) + .5;
    uint2 pixel = uint2(clamp(uv * float2(lighting.ScreenWidth, lighting.ScreenHeight),
        0.0.xx, float2(lighting.ScreenWidth - 1u, lighting.ScreenHeight - 1u)));
    uint cell = ClusteredLightCellIndex(lighting, viewIndex, pixel, viewDepth);
    uint2 cellLights = ClusteredLightGrid[cell];
    uint localCount = cellLights.y;
    uint firstIndex = cellLights.x;
    float nominalPixelRadius = CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection, worldPosition);
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
        if ((shadowView.Flags & VSM_VIEW_SINGLE_PAGE) != 0u) continue;
        float3 toLight = shadowView.LightPosition - worldPosition;
        if (shadowView.LightRange > 0.0 &&
            dot(toLight, toLight) > shadowView.LightRange * shadowView.LightRange)
        {
            continue;
        }
        if (shadowView.LightKind == VSM_LIGHT_SPOT &&
            dot(normalize(toLight), -shadowView.LightDirection) < LightBuffer[lightIndex].OuterConeCos) continue;
        uint mipLevel = VirtualShadowLocalMipFromNominalPixelRadius(
            shadowView,
            worldPosition,
            nominalPixelRadius,
            dynamicResolutionBias + receiverResolutionBias);
        PublishProjectedShadowView(
            shadowViewIndex,
            shadowView,
            mipLevel,
            worldPosition);
    }
}

void PublishDirectionalShadowPosition(uint baseViewIndex, VirtualShadowMapParameters baseView,
    float3 worldPosition, float dynamicResolutionBias, float receiverResolutionBias, bool firstPerson)
{
    if (firstPerson && (PageDemand.FirstPersonPixelRequestBias < 0.0 ||
        (baseView.Flags & VSM_VIEW_FIRST_PERSON_SHADOW) != 0u)) return;
    uint relativeLevel = CalculateDirectionalVirtualShadowBiasedLevel(ShadowViews, baseViewIndex,
        baseView.ClipmapLevelCount, worldPosition, dynamicResolutionBias,
        receiverResolutionBias + (firstPerson ? PageDemand.FirstPersonPixelRequestBias : 0.0));
    if (firstPerson)
        relativeLevel = max(relativeLevel, uint(max(PageDemand.FirstPersonPixelRequestMinLevel - baseView.FirstClipmapLevel, 0)));
    if (relativeLevel < baseView.ClipmapLevelCount && baseViewIndex + relativeLevel < PageDemand.ViewCount)
    {
        float2 uv, border;
        PublishProjectedShadowViewPixels(ShadowViews[baseViewIndex + relativeLevel], 0u, worldPosition, uv, border);
    }
}

// Position production belongs to the caller; requests use the supplied world position.
void MarkVirtualShadowPages(float3 worldPosition, uint viewIndex, bool firstPerson = false)
{
    float dynamicResolutionBias = DynamicResolutionBias[0];
    float receiverResolutionBias = CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition);
    for (uint baseViewIndex = 0u, familyLevels = 1u; baseViewIndex < PageDemand.ViewCount; baseViewIndex += familyLevels)
    {
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        // Point faces occupy one contiguous group before the next light family.
        familyLevels = baseView.LightKind == VSM_LIGHT_POINT ? 6u - baseView.FaceIndex : 1u;
        if (baseView.LightKind != VSM_LIGHT_DIRECTIONAL || baseView.FaceIndex != 0u) continue;
        familyLevels = baseView.ClipmapLevelCount;
        PublishDirectionalShadowPosition(baseViewIndex, baseView, worldPosition,
            dynamicResolutionBias, receiverResolutionBias, firstPerson);
    }
    if (PageDemand.PointLightCount == 0u && PageDemand.SpotLightCount == 0u) return;
    if (!firstPerson || PageDemand.FirstPersonPixelRequestBias >= 0.0)
        PublishLocalShadowPosition(worldPosition, viewIndex, dynamicResolutionBias, receiverResolutionBias);
}

#endif
