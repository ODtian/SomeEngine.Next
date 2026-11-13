#ifndef VSM_PAGE_REQUESTS_SLANG
#define VSM_PAGE_REQUESTS_SLANG

#include "vsm_sampling.slang"
__exported import vsm_receiver_projection;
__exported import render_lighting;
__exported import surface_receiver;
__exported import vsm_light_view_indices;

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
__exported import vsm_page_request_common;

interface IVirtualShadowPageRequestOutput
{
    static void Publish(uint tableIndex);
    static void MarkRectangle(VirtualShadowMapParameters view, uint mip, uint2 page, uint2 first, uint2 last);
};

struct VirtualShadowGlobalPageRequests : IVirtualShadowPageRequestOutput
{
    static void Publish(uint tableIndex)
    { PublishVirtualShadowRequest(tableIndex, VSM_PAGE_REQUEST_DETAIL, PageDemand.FrameIndex, PageDemand.PageTableEntryCount); }
    static void MarkRectangle(VirtualShadowMapParameters view, uint mip, uint2 page, uint2 first, uint2 last)
    { MarkVirtualShadowReceiverRectangle(view, mip, page, first, last); }
};

static const uint VSM_TILE_REQUEST_CAPACITY = 256u;
groupshared uint VirtualShadowTilePages[VSM_TILE_REQUEST_CAPACITY];
groupshared uint VirtualShadowTileMaskAddresses[VSM_TILE_REQUEST_CAPACITY];
groupshared uint VirtualShadowTileMasks[VSM_TILE_REQUEST_CAPACITY];

void BeginVirtualShadowTileRequests(uint lane, uint threadCount)
{
    for (uint index = lane; index < VSM_TILE_REQUEST_CAPACITY; index += threadCount)
    {
        VirtualShadowTilePages[index] = 0u;
        VirtualShadowTileMaskAddresses[index] = 0u;
        VirtualShadowTileMasks[index] = 0u;
    }
    GroupMemoryBarrierWithGroupSync();
}

struct VirtualShadowTilePageRequests : IVirtualShadowPageRequestOutput
{
    static void Publish(uint tableIndex)
    {
        uint key = tableIndex + 1u;
        for (uint probe = 0u; probe < VSM_TILE_REQUEST_CAPACITY; probe++)
        {
            uint index = (tableIndex * 2654435761u + probe) & (VSM_TILE_REQUEST_CAPACITY - 1u);
            uint previous;
            InterlockedCompareExchange(VirtualShadowTilePages[index], 0u, key, previous);
            if (previous == key) return;
            if (previous == 0u) { VirtualShadowGlobalPageRequests.Publish(tableIndex); return; }
        }
        VirtualShadowGlobalPageRequests.Publish(tableIndex);
    }

    static void MarkWord(uint2 address, uint mask)
    {
        uint width, height;
        PageReceiverMasks.GetDimensions(width, height);
        uint key = address.y * width + address.x + 1u;
        for (uint probe = 0u; probe < VSM_TILE_REQUEST_CAPACITY; probe++)
        {
            uint index = (key * 2654435761u + probe) & (VSM_TILE_REQUEST_CAPACITY - 1u);
            uint previous;
            InterlockedCompareExchange(VirtualShadowTileMaskAddresses[index], 0u, key, previous);
            if (previous != 0u && previous != key) continue;
            InterlockedOr(VirtualShadowTileMasks[index], mask, previous);
            mask &= ~previous;
            if (mask != 0u) InterlockedOr(PageReceiverMasks[address], mask, previous);
            return;
        }
        uint ignored;
        InterlockedOr(PageReceiverMasks[address], mask, ignored);
    }

    static void MarkRectangle(VirtualShadowMapParameters view, uint mip, uint2 page, uint2 first, uint2 last)
    {
        uint2 origin = page * view.PageSize;
        uint shift = firstbithigh(view.PageSize) - 3u;
        uint2 lowCell = (max(first, origin) - origin) >> shift;
        uint2 highCell = (min(last, origin + view.PageSize - 1u) - origin) >> shift;
        uint2 address = VirtualShadowReceiverMaskOrigin(view, mip) + page * uint2(1u, 2u);
        for (uint word = lowCell.y >> 2u; word <= highCell.y >> 2u; word++)
        {
            uint2 low = uint2(lowCell.x, max(lowCell.y, word * 4u) - word * 4u);
            uint2 high = uint2(highCell.x, min(highCell.y, word * 4u + 3u) - word * 4u);
            MarkWord(address + uint2(0u, word), VirtualShadowReceiverWordRectangle(low, high));
        }
    }
};

uint PublishVirtualShadowPage<TRequests : IVirtualShadowPageRequestOutput>(
    VirtualShadowMapParameters shadowView,
    uint mipLevel,
    int2 localPage)
{
    uint dimension = VirtualShadowMipPagesPerRow(shadowView, mipLevel);
    if (any(localPage < 0) || any(localPage >= int(dimension)))
        return 0xFFFFFFFFu;
    uint tableIndex = VirtualShadowPageTableIndex(
        shadowView, mipLevel, uint2(localPage));
    TRequests.Publish(tableIndex);
    return tableIndex;
}

bool PublishProjectedShadowViewPixels<TRequests : IVirtualShadowPageRequestOutput>(
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
        uint tableIndex = PublishVirtualShadowPage<TRequests>(shadowView, mipLevel, int2(page));
        if (tableIndex != 0xFFFFFFFFu && (shadowView.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
            TRequests.MarkRectangle(shadowView, mipLevel, page, firstPixel, lastPixel);
    }
    return true;
}

void PublishProjectedShadowView<TRequests : IVirtualShadowPageRequestOutput>(uint viewIndex, uint mipLevel, float3 worldPosition)
{
    float2 uv, border;
    if (!PublishProjectedShadowViewPixels<TRequests>(viewIndex, mipLevel, worldPosition, uv, border)) return;
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
        PublishProjectedShadowViewPixels<TRequests>(firstFace + face, mipLevel,
            view.LightPosition + direction * distance, adjacentUV, adjacentBorder);
    }
}

void PublishLocalShadowPosition<TRequests : IVirtualShadowPageRequestOutput>(float3 worldPosition, uint viewIndex, float dynamicResolutionBias)
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
            CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection, worldPosition),
            dynamicResolutionBias + CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition));
        PublishProjectedShadowView<TRequests>(
            shadowViewIndex,
            mipLevel,
            worldPosition);
    }
}

void PublishDirectionalShadowPosition<TRequests : IVirtualShadowPageRequestOutput>(uint baseViewIndex, VirtualShadowMapParameters baseView,
    float3 worldPosition, float dynamicResolutionBias, bool firstPerson)
{
    if (firstPerson && (PageDemand.FirstPersonPixelRequestBias < 0.0 ||
        (baseView.Flags & VSM_VIEW_FIRST_PERSON_SHADOW) != 0u)) return;
    uint relativeLevel = CalculateDirectionalVirtualShadowBiasedLevel(ShadowViews, baseViewIndex,
        baseView.ClipmapLevelCount, worldPosition, dynamicResolutionBias,
        CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, worldPosition) +
            (firstPerson ? PageDemand.FirstPersonPixelRequestBias : 0.0));
    if (firstPerson)
        relativeLevel = max(relativeLevel, uint(max(PageDemand.FirstPersonPixelRequestMinLevel - baseView.FirstClipmapLevel, 0)));
    if (relativeLevel < baseView.ClipmapLevelCount && baseViewIndex + relativeLevel < PageDemand.ViewCount)
        PublishProjectedShadowView<TRequests>(baseViewIndex + relativeLevel, 0u, worldPosition);
}

// Position production belongs to the caller; requests use the supplied world position.
void MarkVirtualShadowPages<TRequests : IVirtualShadowPageRequestOutput>(float3 worldPosition, uint viewIndex, bool firstPerson = false)
{
    float dynamicResolutionBias = DynamicResolutionBias[0];
    for (uint baseViewIndex = 0u, familyLevels = 1u; baseViewIndex < PageDemand.ViewCount; baseViewIndex += familyLevels)
    {
        familyLevels = 1u;
        VirtualShadowMapParameters baseView = ShadowViews[baseViewIndex];
        if (baseView.LightKind != VSM_LIGHT_DIRECTIONAL || baseView.FaceIndex != 0u) continue;
        familyLevels = baseView.ClipmapLevelCount;
        PublishDirectionalShadowPosition<TRequests>(baseViewIndex, baseView, worldPosition, dynamicResolutionBias, firstPerson);
    }
    if (!firstPerson || PageDemand.FirstPersonPixelRequestBias >= 0.0)
        PublishLocalShadowPosition<TRequests>(worldPosition, viewIndex, dynamicResolutionBias);
}


uint PublishVirtualShadowPage(VirtualShadowMapParameters view, uint mip, int2 page)
{ return PublishVirtualShadowPage<VirtualShadowGlobalPageRequests>(view, mip, page); }
bool PublishProjectedShadowViewPixels(uint view, uint mip, float3 position, out float2 uv, out float2 border)
{ return PublishProjectedShadowViewPixels<VirtualShadowGlobalPageRequests>(view, mip, position, uv, border); }
void PublishProjectedShadowView(uint view, uint mip, float3 position)
{ PublishProjectedShadowView<VirtualShadowGlobalPageRequests>(view, mip, position); }
void PublishLocalShadowPosition(float3 position, uint view, float bias)
{ PublishLocalShadowPosition<VirtualShadowGlobalPageRequests>(position, view, bias); }
void PublishDirectionalShadowPosition(uint index, VirtualShadowMapParameters view, float3 position, float bias, bool firstPerson)
{ PublishDirectionalShadowPosition<VirtualShadowGlobalPageRequests>(index, view, position, bias, firstPerson); }
void MarkVirtualShadowPages(float3 position, uint view, bool firstPerson = false)
{ MarkVirtualShadowPages<VirtualShadowGlobalPageRequests>(position, view, firstPerson); }

void RequestVirtualShadowLocalReceiver<TRequests : IVirtualShadowPageRequestOutput>(SurfaceReceiver receiver, uint viewIndex,
    StructuredBuffer<uint> firstShadowViewIndices, float dynamicResolutionBias, bool cullBackfaces)
{
    bool firstPerson = (receiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u;
    if (firstPerson && PageDemand.FirstPersonPixelRequestBias < 0.0) return;
    ClusteredLightGridParameters lighting = LightingViews[viewIndex];
    float3 viewPosition = mul(float4(receiver.Position, 1.0), lighting.View).xyz;
    float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
    float4 clip = mul(float4(viewPosition, 1.0), lighting.Projection);
    float2 uv = clip.xy / clip.w * float2(.5, -.5) + .5;
    uint2 pixel = uint2(clamp(uv * float2(lighting.ScreenWidth, lighting.ScreenHeight),
        0.0.xx, float2(lighting.ScreenWidth - 1u, lighting.ScreenHeight - 1u)));
    uint2 lights = ClusteredLightGrid[ClusteredLightCellIndex(lighting, viewIndex, pixel, viewDepth)];
    for (uint local = 0u; local < lights.y; local++)
    {
        uint lightIndex = LightIndexList[lights.x + local];
        uint firstView = firstShadowViewIndices[lightIndex];
        if (firstView == 0xffffffffu) continue;
        VirtualShadowMapParameters shadowView = ShadowViews[firstView];
        if (shadowView.LightKind == VSM_LIGHT_POINT)
            shadowView = ShadowViews[firstView + VirtualShadowPointFace(receiver.Position - shadowView.LightPosition)];
        if ((shadowView.Flags & VSM_VIEW_SINGLE_PAGE) != 0u) continue;
        float3 towardLight = shadowView.LightPosition - receiver.Position;
        if (dot(towardLight, towardLight) > shadowView.LightRange * shadowView.LightRange) continue;
        if (shadowView.LightKind == VSM_LIGHT_SPOT &&
            dot(normalize(towardLight), -shadowView.LightDirection) < LightBuffer[lightIndex].OuterConeCos) continue;
        if (!SurfaceReceiverReceivesLocalShadow(receiver, towardLight / shadowView.WorldUnitsPerCentimeter,
            shadowView.SourceRadius / shadowView.WorldUnitsPerCentimeter, cullBackfaces)) continue;
        uint mip = VirtualShadowLocalMipFromNominalPixelRadius(shadowView, receiver.Position,
            CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection, receiver.Position),
            dynamicResolutionBias + CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, receiver.Position));
        uint shadowViewIndex = firstView + (shadowView.LightKind == VSM_LIGHT_POINT ? shadowView.FaceIndex : 0u);
        PublishProjectedShadowView<TRequests>(shadowViewIndex, mip, receiver.Position);
    }
}

void MarkVirtualShadowPages<TRequests : IVirtualShadowPageRequestOutput>(SurfaceReceiver receiver, uint viewIndex,
    StructuredBuffer<uint> firstShadowViewIndices, bool cullBackfaces)
{
    if (!SurfaceReceiverHasLighting(receiver) || PageDemand.ViewCount == 0u) return;
    bool firstPerson = (receiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u;
    float dynamicResolutionBias = DynamicResolutionBias[0];
    for (uint baseViewIndex = 0u, familyLevels = 1u; baseViewIndex < PageDemand.ViewCount; baseViewIndex += familyLevels)
    {
        familyLevels = 1u;
        VirtualShadowMapParameters view = ShadowViews[baseViewIndex];
        if (view.LightKind != VSM_LIGHT_DIRECTIONAL || view.FaceIndex != 0u) continue;
        familyLevels = view.ClipmapLevelCount;
        if (SurfaceReceiverReceivesDirectionalShadow(receiver, -view.LightDirection,
            view.SourceAngleRadians, cullBackfaces))
            PublishDirectionalShadowPosition<TRequests>(baseViewIndex, view, receiver.Position, dynamicResolutionBias, firstPerson);
    }
    RequestVirtualShadowLocalReceiver<TRequests>(receiver, viewIndex, firstShadowViewIndices, dynamicResolutionBias, cullBackfaces);
}

void RequestVirtualShadowLayerReceiver<TRequests : IVirtualShadowPageRequestOutput>(
    float3 position, uint view, bool firstPerson)
{
    SurfaceReceiver receiver = {};
    receiver.Position = position;
    receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING |
        (firstPerson ? SURFACE_RECEIVER_FIRST_PERSON : 0u);
    MarkVirtualShadowPages<TRequests>(receiver, view, VirtualShadowLightViewIndices, false);
}

#endif
