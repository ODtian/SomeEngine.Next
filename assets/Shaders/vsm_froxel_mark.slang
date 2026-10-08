#include "vsm_mark_pages.slang"
#include "vsm_froxel_parameters.slang"

ConstantBuffer<VirtualShadowFroxelParameters> FroxelParameters;
StructuredBuffer<uint2> Froxels;
StructuredBuffer<uint> FroxelArguments;

void ExtendVirtualShadowFroxelRect(float4 clip, inout float2 minimum, inout float2 maximum)
{
    if (clip.w <= 0.0) return;
    float2 ndc = clip.xy / clip.w;
    minimum = min(minimum, ndc);
    maximum = max(maximum, ndc);
}

void PublishVirtualShadowFroxelBounds(uint viewIndex, uint mip, float3 minimumView, float3 maximumView)
{
    VirtualShadowMapParameters view = ShadowViews[viewIndex];
    float4 corners[8];
    uint outsideLeft = 0u, outsideRight = 0u, outsideTop = 0u, outsideBottom = 0u, outsideNear = 0u, outsideFar = 0u;
    float2 minimum = 1e30.xx, maximum = -1e30.xx;
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 position = float3((corner & 1u) != 0u ? maximumView.x : minimumView.x,
            (corner & 2u) != 0u ? maximumView.y : minimumView.y,
            (corner & 4u) != 0u ? maximumView.z : minimumView.z);
        float4 world = mul(float4(position,1.0), FroxelParameters.ViewToWorld);
        float4 clip = ProjectVirtualShadowClip(view, world.xyz / world.w);
        corners[corner] = clip;
        outsideLeft += uint(clip.x < -clip.w); outsideRight += uint(clip.x > clip.w);
        outsideBottom += uint(clip.y < -clip.w); outsideTop += uint(clip.y > clip.w);
        outsideNear += uint(clip.z < 0.0); outsideFar += uint(clip.z > clip.w);
        if (view.LightKind == VSM_LIGHT_DIRECTIONAL || clip.z >= 0.0)
            ExtendVirtualShadowFroxelRect(clip, minimum, maximum);
    }
    if (outsideLeft == 8u || outsideRight == 8u || outsideTop == 8u || outsideBottom == 8u ||
        outsideNear == 8u || outsideFar == 8u) return;
    if (view.LightKind != VSM_LIGHT_DIRECTIONAL && outsideNear != 0u)
    {
        for (uint corner = 0u; corner < 8u; corner++)
        for (uint axis = 0u; axis < 3u; axis++)
        {
            uint bit = 1u << axis;
            if ((corner & bit) != 0u) continue;
            float4 a = corners[corner], b = corners[corner | bit];
            if ((a.z < 0.0) == (b.z < 0.0)) continue;
            ExtendVirtualShadowFroxelRect(lerp(a,b,-a.z/(b.z-a.z)), minimum, maximum);
        }
    }
    mip = clamp(mip, VirtualShadowFirstMip(view), view.MipLevelCount - 1u);
    uint resolution = VirtualShadowMipResolution(view,mip);
    float2 uvMin = saturate(float2(minimum.x,-maximum.y) * .5 + .5);
    float2 uvMax = saturate(float2(maximum.x,-minimum.y) * .5 + .5);
    // Native GetScreenRect includes the covered pixel centers.
    uint2 firstPixel = uint2(uvMin * float(resolution) + .5);
    uint2 lastPixel = min(uint2(uvMax * float(resolution) - .5), resolution - 1u);
    if (any(firstPixel > lastPixel)) return;
    uint2 firstPage = firstPixel / view.PageSize, lastPage = lastPixel / view.PageSize;
    for (uint y = firstPage.y; y <= lastPage.y; y++)
    for (uint x = firstPage.x; x <= lastPage.x; x++)
    {
        uint2 page = uint2(x,y);
        uint table = PublishVirtualShadowPage(view,mip,int2(page));
        if (table != 0xffffffffu && (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u)
            MarkVirtualShadowReceiverRectangle(table,page,firstPixel,lastPixel,view.PageSize);
    }
}

[shader("compute")]
[numthreads(64,1,1)]
void MarkVirtualShadowFroxelPages(uint index : SV_DispatchThreadID)
{
    uint count = FroxelArguments[FroxelParameters.Water != 0u ? 7u : 3u];
    if (index >= count || FroxelArguments[8] != 0u) return;
    uint first = FroxelParameters.Water != 0u ? FroxelParameters.Width * FroxelParameters.Height : 0u;
    uint2 packed = Froxels[first + index];
    uint2 tile = uint2(packed.x & 0xffffu, packed.x >> 16u);
    float3 center = mul(float4(VirtualShadowFroxelCenter(FroxelParameters, tile, asint(packed.y)), 1.0), FroxelParameters.ViewToWorld).xyz;
    float3 minimum, maximum;
    VirtualShadowFroxelViewBounds(FroxelParameters,tile,asint(packed.y),minimum,maximum);
    uint levels = max(PageDemand.DirectionalClipmapLevels, 1u);
    uint lights = min(PageDemand.DirectionalLightCount, PageDemand.ViewCount / levels);
    for (uint light = 0u; light < lights; light++)
    {
        uint firstView = light * levels;
        uint level = CalculateDirectionalVirtualShadowBiasedLevel(ShadowViews, firstView, levels,
            center, DynamicResolutionBias[0].x,
            CalculateVirtualShadowReceiverResolutionBias(PageDemand.ReceiverProjection, center));
        if (level < levels) PublishVirtualShadowFroxelBounds(firstView + level,0u,minimum,maximum);
    }
    if (FroxelParameters.Water != 0u) return;
    uint2 pixel = tile * 8u + 4u;
    ClusteredLightGridParameters lighting = LightingViews[PageDemand.ReceiverViewIndex];
    float3 centerView = mul(float4(center,1.0),lighting.View).xyz;
    uint cell = ClusteredLightCellIndex(lighting,PageDemand.ReceiverViewIndex,pixel,max(-centerView.z,lighting.ZParams.x));
    uint countLocal = min(ClusteredLightGrid[cell].y,lighting.MaxLightsPerCluster);
    uint firstLight = cell * lighting.MaxLightsPerCluster;
    for (uint local = 0u; local < countLocal; local++)
    {
        uint light = LightIndexList[firstLight + local];
        uint viewIndex = 0xffffffffu;
        if (light >= PageDemand.PointLightBufferOffset && light < PageDemand.PointLightBufferOffset + PageDemand.PointLightCount)
        {
            uint firstView = PageDemand.PointViewOffset + (light - PageDemand.PointLightBufferOffset) * 6u;
            float3 toLight = ShadowViews[firstView].LightPosition - center;
            viewIndex = firstView + VirtualShadowPointFace(-toLight);
        }
        else if (light >= PageDemand.SpotLightBufferOffset && light < PageDemand.SpotLightBufferOffset + PageDemand.SpotLightCount)
            viewIndex = PageDemand.SpotViewOffset + light - PageDemand.SpotLightBufferOffset;
        if (viewIndex >= PageDemand.ViewCount) continue;
        VirtualShadowMapParameters view = ShadowViews[viewIndex];
        if ((view.Flags & VSM_VIEW_SINGLE_PAGE) != 0u) continue;
        float3 toLight = view.LightPosition - center;
        if (dot(toLight,toLight) > view.LightRange * view.LightRange) continue;
        if (view.LightKind == VSM_LIGHT_SPOT && dot(normalize(toLight),-view.LightDirection) < LightBuffer[light].OuterConeCos) continue;
        uint mip = VirtualShadowLocalMipFromNominalPixelRadius(view,center,
            CalculateVirtualShadowNominalWorldPixelRadius(PageDemand.ReceiverProjection,center), DynamicResolutionBias[0].y);
        PublishVirtualShadowFroxelBounds(viewIndex,mip,minimum,maximum);
    }
}

[shader("compute")]
[numthreads(64,1,1)]
void MarkVirtualShadowFroxelPixelReceivers(uint index : SV_DispatchThreadID)
{
    if (index >= FroxelParameters.Width * FroxelParameters.Height) return;
    uint2 pixel = uint2(index % FroxelParameters.Width, index / FroxelParameters.Width);
    if (FroxelArguments[8] != 0u)
        MarkVirtualShadowReceiverPixel(pixel);
    else
        MarkVirtualShadowReceiverPixel(pixel, SURFACE_RECEIVER_HAIR | SURFACE_RECEIVER_FIRST_PERSON, SURFACE_RECEIVER_WATER);
}
