#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_receiver_pixel.slang"
#include "vsm_receiver_projection.slang"
#include "render_lighting.slang"
#include "surface_receiver.slang"

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
    uint AdditionalReceiverLayerCount;
    uint CullBackfacingPixels;
    float FirstPersonPixelRequestBias;
    int FirstPersonPixelRequestMinLevel;
    VirtualShadowReceiverProjection ReceiverProjection;
};

ConstantBuffer<VirtualShadowPageDemandUniforms> PageDemand;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
Texture2DArray<float> ReceiverDepth;
StructuredBuffer<float> DynamicResolutionBias;
StructuredBuffer<SurfaceReceiver> ReceiverInputs;
StructuredBuffer<SurfaceReceiver> AdditionalReceiverInputs;
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

bool PublishProjectedShadowViewPixels(
    uint viewIndex,
    uint mipLevel,
    float3 worldPosition, out float2 footprintUV, out float2 footprintBorder)
{
    footprintUV = 0.0.xx;
    footprintBorder = 0.0.xx;
    if (viewIndex >= PageDemand.ViewCount)
        return false;
    VirtualShadowMapParameters shadowView = ShadowViews[viewIndex];
    mipLevel = clamp(mipLevel, VirtualShadowFirstMip(shadowView), shadowView.MipLevelCount - 1u);
    float3 shadowNdc;
    float2 shadowUv;
    if (!ProjectVirtualShadowPosition(shadowView, worldPosition, shadowNdc, shadowUv))
        return false;

    uint mipResolution = VirtualShadowMipResolution(shadowView, mipLevel);
    float2 borderPixels = max(PageDemand.PageDilationBorderPages, shadowView.PageDilationBorder) * float(shadowView.PageSize);
    if (shadowView.LightKind != VSM_LIGHT_DIRECTIONAL && shadowView.SourceRadius > 0.0)
    {
        float radius = CalculateVirtualShadowLocalSourceSupport(shadowView, worldPosition);
        float4 clip = ProjectVirtualShadowClip(shadowView, worldPosition);
        float3 x = float3(shadowView.LightViewProjection[0][0],
            shadowView.LightViewProjection[1][0], shadowView.LightViewProjection[2][0]);
        float3 y = float3(shadowView.LightViewProjection[0][1],
            shadowView.LightViewProjection[1][1], shadowView.LightViewProjection[2][1]);
        float3 w = float3(shadowView.LightViewProjection[0][3],
            shadowView.LightViewProjection[1][3], shadowView.LightViewProjection[2][3]);
        float minimumW = clip.w - length(w) * radius;
        float2 border = minimumW > 1e-6
            ? 0.5 * (float2(length(x), length(y)) * radius * clip.w +
                abs(clip.xy) * length(w) * radius) / (clip.w * minimumW)
            : 1.0.xx;
        borderPixels += border * float(mipResolution);
    }
    if (PageDemand.NormalBias > 0.0)
    {
        float4 clip = ProjectVirtualShadowClip(shadowView, worldPosition);
        float3 x = float3(shadowView.LightViewProjection[0][0],
            shadowView.LightViewProjection[1][0], shadowView.LightViewProjection[2][0]);
        float3 y = float3(shadowView.LightViewProjection[0][1],
            shadowView.LightViewProjection[1][1], shadowView.LightViewProjection[2][1]);
        float3 w = float3(shadowView.LightViewProjection[0][3],
            shadowView.LightViewProjection[1][3], shadowView.LightViewProjection[2][3]);
        float normalBiasLength = CalculateVirtualShadowNormalBiasLength(PageDemand.ReceiverProjection,
            worldPosition, PageDemand.NormalBias, shadowView.WorldUnitsPerCentimeter);
        float2 xyChange = float2(length(x), length(y)) * normalBiasLength;
        float wChange = length(w) * normalBiasLength;
        float minimumW = clip.w - wChange;
        float2 uvBorder = minimumW > 1e-6
            ? 0.5 * (xyChange * clip.w + abs(clip.xy) * wChange) / (clip.w * minimumW)
            : 1.0.xx;
        borderPixels += uvBorder * float(mipResolution);
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
    uint2 firstPage = firstPixel / shadowView.PageSize;
    uint2 lastPage = lastPixel / shadowView.PageSize;
    for (uint y = firstPage.y; y <= lastPage.y; y++)
    for (uint x = firstPage.x; x <= lastPage.x; x++)
    {
        uint2 page = uint2(x, y);
        uint tableIndex = PublishVirtualShadowPage(shadowView, mipLevel, int2(page));
        if (tableIndex != 0xFFFFFFFFu && (shadowView.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
            MarkVirtualShadowReceiverRectangle(shadowView, mipLevel, page, firstPixel, lastPixel);
    }
    return true;
}

void PublishProjectedShadowView(uint viewIndex, uint mipLevel, float3 worldPosition)
{
    float2 uv, border;
    if (!PublishProjectedShadowViewPixels(viewIndex, mipLevel, worldPosition, uv, border)) return;
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
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
    [unroll]
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
        PublishProjectedShadowViewPixels(firstFace + face, mipLevel,
            view.LightPosition + direction * distance, adjacentUV, adjacentBorder);
    }
}

void PublishLocalShadowReceiver(uint2 pixel, float3 worldPosition, SurfaceReceiver surfaceReceiver, float dynamicResolutionBias, bool backfaceCull)
{
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
        if ((shadowView.Flags & VSM_VIEW_SINGLE_PAGE) != 0u) continue;
        float3 toLight = shadowView.LightPosition - worldPosition;
        if (shadowView.LightRange > 0.0 &&
            dot(toLight, toLight) > shadowView.LightRange * shadowView.LightRange)
        {
            continue;
        }
        if (!SurfaceReceiverReceivesLocalShadow(surfaceReceiver,
                toLight / shadowView.WorldUnitsPerCentimeter,
                shadowView.SourceRadius / shadowView.WorldUnitsPerCentimeter, backfaceCull)) continue;
        if (shadowView.LightKind == VSM_LIGHT_SPOT &&
            dot(normalize(toLight), -shadowView.LightDirection) < LightBuffer[lightIndex].OuterConeCos) continue;
        uint mipLevel = VirtualShadowLocalMipFromNominalPixelRadius(
            shadowView,
            worldPosition,
            CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection, worldPosition),
            dynamicResolutionBias + CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition));
        PublishProjectedShadowView(
            shadowViewIndex,
            mipLevel,
            worldPosition);
    }
}

void MarkVirtualShadowReceiverPixel(uint2 pixel, uint requiredPrimaryFlags = 0u, uint excludedExtraFlags = 0u)
{
    if (pixel.x >= PageDemand.ReceiverWidth || pixel.y >= PageDemand.ReceiverHeight)
        return;
    uint receiverIndex = pixel.y * PageDemand.ReceiverWidth + pixel.x;
    SurfaceReceiver surfaceReceiver = ReceiverInputs[receiverIndex];
    bool firstPerson = (surfaceReceiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u;
    bool primaryEligible = SurfaceReceiverHasLighting(surfaceReceiver) &&
        (requiredPrimaryFlags == 0u || (surfaceReceiver.Flags & requiredPrimaryFlags) != 0u);
    bool backfaceCull = PageDemand.CullBackfacingPixels != 0u;
    if (!primaryEligible && PageDemand.AdditionalReceiverLayerCount == 0u) return;
    float3 worldPosition = surfaceReceiver.Position;
    float dynamicResolutionBias = DynamicResolutionBias[0];

    // Each directional family starts at face zero; regular and FP families
    // have independent level ranges and the same light index.
    for (uint baseViewIndex = 0u; baseViewIndex < PageDemand.ViewCount; baseViewIndex++)
    {
        if (ShadowViews[baseViewIndex].LightKind != VSM_LIGHT_DIRECTIONAL || ShadowViews[baseViewIndex].FaceIndex != 0u) continue;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
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
        if (!primaryEligible ||
            (firstPerson && (PageDemand.FirstPersonPixelRequestBias < 0.0 || (baseView.Flags & VSM_VIEW_FIRST_PERSON_SHADOW) != 0u)) ||
            !SurfaceReceiverReceivesDirectionalShadow(surfaceReceiver, -baseView.LightDirection, baseView.SourceAngleRadians, backfaceCull)) continue;
        uint relativeLevel = CalculateDirectionalVirtualShadowBiasedLevel(ShadowViews, baseViewIndex,
            baseView.ClipmapLevelCount, worldPosition, dynamicResolutionBias,
            CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition) +
                (firstPerson ? PageDemand.FirstPersonPixelRequestBias : 0.0));
        if (firstPerson) relativeLevel = max(relativeLevel, uint(max(PageDemand.FirstPersonPixelRequestMinLevel - baseView.FirstClipmapLevel, 0)));
        if (relativeLevel < baseView.ClipmapLevelCount &&
            baseViewIndex + relativeLevel < PageDemand.ViewCount)
        {
            PublishProjectedShadowView(
                baseViewIndex + relativeLevel,
                0u,
                worldPosition);
        }
    }

    if (primaryEligible && (!firstPerson || PageDemand.FirstPersonPixelRequestBias >= 0.0))
        PublishLocalShadowReceiver(pixel, worldPosition, surfaceReceiver, dynamicResolutionBias, backfaceCull);
    for (uint layer = 0u; layer < PageDemand.AdditionalReceiverLayerCount; layer++)
    {
        SurfaceReceiver extra = AdditionalReceiverInputs[layer * PageDemand.ReceiverWidth * PageDemand.ReceiverHeight + receiverIndex];
        if (!SurfaceReceiverHasLighting(extra) || (extra.Flags & excludedExtraFlags) != 0u || extra.Depth < 0.0 || extra.Depth >= 1.0) continue;
        PublishLocalShadowReceiver(pixel, extra.Position, extra, dynamicResolutionBias, backfaceCull);
    }
}

[shader("compute")]
[numthreads(8,8,1)]
void MarkVirtualShadowPages(uint3 pixel : SV_DispatchThreadID)
{
    MarkVirtualShadowReceiverPixel(pixel.xy * 2u);
}
