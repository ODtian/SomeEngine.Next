#include "vsm_material.slang"
#include "vsm_soft_sampling.slang"
#include "cluster_view.slang"
#include "render_lighting.slang"
#include "surface_receiver.slang"
#include "vsm_receiver_pixel.slang"
#include "vsm_projected_pixel.slang"
#include "resource_effects.slang"

struct VirtualShadowProjectionUniforms
{
    uint Width, Height, ViewCount, LightLayerCount;
    uint AdditionalReceiverLayerCount, DispatchDepth, LightLayerRowCount, Pad2;
};

ConstantBuffer<VirtualShadowProjectionUniforms> Projection;
StructuredBuffer<ClusterViewUniforms> ProjectionViews;
Texture2DArray<float> ProjectionDepth;
StructuredBuffer<SurfaceReceiver> ProjectionReceivers;
StructuredBuffer<SurfaceReceiver> ProjectionAdditionalReceivers;
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionVisibility;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionAdditionalVisibility;
#else
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> ProjectionVisibility;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> ProjectionAdditionalVisibility;
#endif

struct VirtualShadowProjectionReceiver
{
    bool Valid;
    float3 Position;
    float NormalBiasLength;
    float3 Normal;
    float3 GeometricNormal;
    float NominalPixelRadius;
    float ResolutionBias;
    uint Flags;
};

VirtualShadowProjectionReceiver ReadVirtualShadowProjectionReceiver(uint3 pixel, uint receiverLayer)
{
    VirtualShadowProjectionReceiver result = (VirtualShadowProjectionReceiver)0;
    uint index = (pixel.z * Projection.Height + pixel.y) * Projection.Width + pixel.x;
    SurfaceReceiver receiver = receiverLayer == 0u ? ProjectionReceivers[index] :
        ProjectionAdditionalReceivers[(receiverLayer - 1u) * Projection.ViewCount * Projection.Width * Projection.Height + index];
    if (!SurfaceReceiverHasLighting(receiver)) return result;
    result.Position = receiver.Position;
    result.Normal = receiver.ShadingNormal;
    result.GeometricNormal = receiver.GeometricNormal;
    result.NormalBiasLength = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
        result.Position, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
    result.NominalPixelRadius = CalculateVirtualShadowNominalWorldPixelRadius(
        VirtualShadowSampling.ReceiverProjection, result.Position);
    result.ResolutionBias = CalculateVirtualShadowReceiverResolutionBias(
        VirtualShadowSampling.ReceiverProjection, result.Position);
    result.Flags = receiver.Flags;
    result.Valid = true;
    return result;
}

float ReadVirtualShadowScreenDepth(float2 uv, uint viewIndex)
{
    int2 pixel = clamp(int2(uv * float2(Projection.Width, Projection.Height)),
        0, int2(Projection.Width, Projection.Height) - 1);
    return ProjectionDepth.Load(int4(pixel, viewIndex, 0));
}

float CalculateVirtualShadowScreenRayStart(uint3 pixel, VirtualShadowProjectionReceiver receiver,
    float3 position, float3 direction)
{
    if (VirtualShadowSampling.ScreenRayLength <= 0.0) return 0.0;
    ClusterViewUniforms view = ProjectionViews[pixel.z];
    float3 translatedPosition = ClusterPositionForView(position, view);
    float3 viewPosition = mul(float4(ClusterPositionForView(receiver.Position, view), 1.0), view.View).xyz;
    bool perspective = ClusterProjectionScales(view).z != 0.0;
    float rayLength = (perspective ? VirtualShadowSampling.ScreenRayLength * max(-viewPosition.z, 0.0) : 1.0)
        / abs(view.P11);
    if ((receiver.Flags & (SURFACE_RECEIVER_HAIR | SURFACE_RECEIVER_SUBSURFACE)) != 0u) return rayLength;
    float4 startClip = mul(float4(translatedPosition, 1.0), view.ViewProj);
    float4 endClip = startClip + mul(float4(direction * rayLength, 0.0), view.ViewProj);
    float3 startNdc = startClip.xyz / startClip.w;
    float3 endNdc = endClip.xyz / endClip.w;
    float3 start = float3(startNdc.xy * float2(.5, -.5) + .5, startNdc.z);
    float3 step = float3((endNdc.xy - startNdc.xy) * float2(.5, -.5), endNdc.z - startNdc.z);
    float startDepth = ReadVirtualShadowScreenDepth(start.xy, pixel.z);
    float sampleTime = (VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex) - .5) * .25 + .25;
    for (uint sample = 0u; sample < 4u; sample++)
    {
        float3 query = start + step * sampleTime;
        float depth = ReadVirtualShadowScreenDepth(query.xy, pixel.z);
        // The primary depth uses forward Z. Native reverse-Z ordering is inverted here.
        if (depth != startDepth && query.z > depth)
            return rayLength * max(0.0, sampleTime - .375);
        sampleTime += .25;
    }
    return rayLength;
}

#if VSM_PROJECT_DIRECTIONAL_LIGHTS
float ProjectVirtualShadowReceiver(uint3 pixel, uint light, VirtualShadowProjectionReceiver receiver)
{
    if (!receiver.Valid || light >= VirtualShadowSampling.DirectionalLightCount) return 1.0;
    GPULight source = LightBuffer[light];
    uint firstView = light * VirtualShadowSampling.DirectionalClipmapLevels;
    uint levels = min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount - firstView);
    SurfaceReceiver surface = (SurfaceReceiver)0;
    surface.Flags = receiver.Flags;
    surface.Depth = 0.0;
    surface.ShadingNormal = receiver.Normal;
    surface.GeometricNormal = receiver.GeometricNormal;
    if (!SurfaceReceiverReceivesDirectionalShadow(surface, -source.Direction,
        VirtualShadowViews[firstView].SourceAngleRadians, VirtualShadowSampling.CullBackfacingPixels != 0u)) return 1.0;
    float3 normal = (receiver.Flags & SURFACE_RECEIVER_HAIR) != 0u ? -source.Direction : receiver.Normal;
    float3 shadowPosition = receiver.Position;
    if ((receiver.Flags & SURFACE_RECEIVER_SUBSURFACE) == 0u) shadowPosition += normal * receiver.NormalBiasLength;
    float rayStartDistance = CalculateVirtualShadowScreenRayStart(pixel, receiver, shadowPosition, -source.Direction);
    VirtualShadowProjectionResult result = SampleVirtualShadowDirectionalFilteredSoft(VirtualShadowViews, VirtualShadowPageTable,
        VirtualShadowAtlas, VirtualShadowPrefilteredAtlas, firstView, levels, shadowPosition, normal,
        receiver.ResolutionBias,
        VirtualShadowSampling.BlockerSampleCount, VirtualShadowSampling.FilterSampleCount,
        VirtualShadowSampling.MaxFilterRadiusTexels, pixel.xy, VirtualShadowSampling.FrameIndex,
        (receiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u,
        VirtualShadowSampling.FirstPersonViewOffset < VirtualShadowSampling.ViewCount
            ? VirtualShadowSampling.FirstPersonViewOffset + light * VirtualShadowViews[VirtualShadowSampling.FirstPersonViewOffset].ClipmapLevelCount
            : 0xffffffffu, rayStartDistance);
    return result.Visibility;
}
#else
uint VirtualShadowLocalLightIndex(uint sourceLight)
{
    if (sourceLight >= VirtualShadowSampling.PointLightBufferOffset &&
        sourceLight < VirtualShadowSampling.PointLightBufferOffset + VirtualShadowSampling.PointLightCount)
        return sourceLight - VirtualShadowSampling.PointLightBufferOffset;
    if (sourceLight >= VirtualShadowSampling.SpotLightBufferOffset &&
        sourceLight < VirtualShadowSampling.SpotLightBufferOffset + VirtualShadowSampling.SpotLightCount)
        return VirtualShadowSampling.PointLightCount + sourceLight - VirtualShadowSampling.SpotLightBufferOffset;
    return 0xffffffffu;
}

float ProjectVirtualShadowLocalReceiver(uint3 pixel, uint light, uint sourceLight,
    VirtualShadowProjectionReceiver receiver)
{
    GPULight source = LightBuffer[sourceLight];
    float3 towardLight = source.Position - receiver.Position;
    float distanceSquared = dot(towardLight, towardLight);
    if (source.Range <= 0.0 || distanceSquared >= source.Range * source.Range)
        return 1.0;
    uint points = VirtualShadowSampling.PointLightCount;
    if (light >= points && dot(source.Direction, source.Direction) > 1e-8 && source.OuterConeCos > 0.0 &&
        dot(-towardLight * rsqrt(max(distanceSquared, 1e-8)), source.Direction) <= source.OuterConeCos) return 1.0;
    uint firstView = light < points ? VirtualShadowSampling.PointViewOffset + light * 6u :
        VirtualShadowSampling.SpotViewOffset + light - points;
    VirtualShadowMapParameters view = VirtualShadowViews[firstView];
    SurfaceReceiver surface = (SurfaceReceiver)0;
    surface.Flags = receiver.Flags;
    surface.Depth = 0.0;
    surface.ShadingNormal = receiver.Normal;
    surface.GeometricNormal = receiver.GeometricNormal;
    if (!SurfaceReceiverReceivesLocalShadow(surface, towardLight / view.WorldUnitsPerCentimeter,
        view.SourceRadius / view.WorldUnitsPerCentimeter, VirtualShadowSampling.CullBackfacingPixels != 0u)) return 1.0;
    float3 normal = (receiver.Flags & SURFACE_RECEIVER_HAIR) != 0u ? normalize(towardLight) : receiver.Normal;
    float3 shadowPosition = receiver.Position;
    if ((receiver.Flags & SURFACE_RECEIVER_SUBSURFACE) == 0u) shadowPosition += normal * receiver.NormalBiasLength;
    float rayStartDistance = CalculateVirtualShadowScreenRayStart(pixel, receiver, shadowPosition, normalize(towardLight));
    return SampleVirtualShadowLocalSoft(VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas,
        firstView, shadowPosition, normal, receiver.NominalPixelRadius,
        VirtualShadowDynamicResolutionBias[0], VirtualShadowSampling.BlockerSampleCount,
        VirtualShadowSampling.FilterSampleCount, VirtualShadowSampling.MaxFilterRadiusTexels,
        pixel.xy, VirtualShadowSampling.FrameIndex, rayStartDistance).Visibility;
}

uint4 PackVirtualShadowLocalVisibility(uint4 mask, uint light, float visibility, float noise)
{
    const float noiseScale = 1.0 / 15.0;
    if (visibility > noiseScale * .25 && visibility < 1.0)
        visibility = saturate(visibility + (noise - .5) * noiseScale);
    uint value = uint(round(saturate(visibility) * 15.0));
    uint word = (light & 31u) >> 3u;
    uint shift = (light & 7u) * 4u;
    mask[word] = (mask[word] & ~(15u << shift)) | (value << shift);
    return mask;
}

void WriteVirtualShadowLocalMask(uint index, uint receiverLayer, uint layerValues, uint4 mask)
{
    if (receiverLayer == 0u) ProjectionVisibility[index] = mask;
    else ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerValues + index] = mask;
}

uint4 ReadVirtualShadowLocalMask(uint index, uint receiverLayer, uint layerValues)
{
    return receiverLayer == 0u ? ProjectionVisibility[index] :
        ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerValues + index];
}
#endif

[shader("compute")]
[numthreads(64, 1, 1)]
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
void ProjectVirtualShadowDirectionalLights(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
#else
void ProjectVirtualShadowLocalLights(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
#endif
{
    uint tileCountX = (Projection.Width + 7u) / 8u;
    uint tileCountY = (Projection.Height + 7u) / 8u;
    uint2 tile = group.xy;
    uint lightReceiver = group.z;
    if (Projection.LightLayerRowCount > 1u)
    {
        lightReceiver += (group.y / tileCountY) * Projection.DispatchDepth;
        tile.y = group.y % tileCountY;
    }
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    uint layerSize = Projection.LightLayerCount * Projection.ViewCount;
#else
    uint layerSize = Projection.ViewCount;
#endif
    if (lightReceiver >= layerSize * (1u + Projection.AdditionalReceiverLayerCount)) return;
    uint receiverLayer = lightReceiver / layerSize;
    uint lightView = lightReceiver % layerSize;
    uint3 pixel = uint3(tile * 8u + DecodeVirtualShadowProjectionPixel(lane), lightView % Projection.ViewCount);
    uint tilePixels = tileCountX * tileCountY * 64u;
    uint pixelIndex = (tile.y * tileCountX + tile.x) * 64u + lane;
    VirtualShadowProjectionReceiver receiver = (VirtualShadowProjectionReceiver)0;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    bool hasLights = VirtualShadowSampling.DirectionalLightCount != 0u;
#else
    bool hasLights = VirtualShadowSampling.PointLightCount + VirtualShadowSampling.SpotLightCount != 0u;
#endif
    if (hasLights && pixel.x < Projection.Width && pixel.y < Projection.Height)
        receiver = ReadVirtualShadowProjectionReceiver(pixel, receiverLayer);
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    float visibility = ProjectVirtualShadowReceiver(pixel, lightView / Projection.ViewCount, receiver);
    uint index = lightView * tilePixels + pixelIndex;
    if (receiverLayer == 0u) ProjectionVisibility[index] = visibility;
    else ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerSize * tilePixels + index] = visibility;
    if (Projection.AdditionalReceiverLayerCount == 0u && lightView == 0u && pixelIndex == 0u)
        ProjectionAdditionalVisibility[0] = 1.0;
#else
    uint localCount = 0u, firstLight = 0u;
    if (receiver.Valid)
    {
        ClusteredLightGridParameters lighting = LightingViews[pixel.z];
        float viewDepth = max(-mul(float4(receiver.Position, 1.0), lighting.View).z, lighting.ZParams.x);
        uint cell = ClusteredLightCellIndex(lighting, pixel.z, pixel.xy, viewDepth);
        localCount = min(ClusteredLightGrid[cell].y, lighting.MaxLightsPerCluster);
        firstLight = cell * lighting.MaxLightsPerCluster;
    }
    float noise = VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex);
    uint packedGroups = (Projection.LightLayerCount + 31u) / 32u;
    uint layerValues = packedGroups * Projection.ViewCount * tilePixels;
    uint viewPixel = pixel.z * tilePixels + pixelIndex;
    if (packedGroups == 1u)
    {
        uint4 mask = 0xffffffffu.xxxx;
        for (uint index = 0u; index < localCount; index++)
        {
            uint sourceLight = LightIndexList[firstLight + index];
            uint light = VirtualShadowLocalLightIndex(sourceLight);
            if (light == 0xffffffffu) continue;
            float visibility = ProjectVirtualShadowLocalReceiver(pixel, light, sourceLight, receiver);
            mask = PackVirtualShadowLocalVisibility(mask, light, visibility, noise);
        }
        WriteVirtualShadowLocalMask(viewPixel, receiverLayer, layerValues, mask);
    }
    else
    {
        // Each pixel initializes the words it later updates in this same pass.
        for (uint groupIndex = 0u; groupIndex < packedGroups; groupIndex++)
            WriteVirtualShadowLocalMask(groupIndex * Projection.ViewCount * tilePixels + viewPixel,
                receiverLayer, layerValues, 0xffffffffu.xxxx);
        for (uint index = 0u; index < localCount; index++)
        {
            uint sourceLight = LightIndexList[firstLight + index];
            uint light = VirtualShadowLocalLightIndex(sourceLight);
            if (light == 0xffffffffu) continue;
            float visibility = ProjectVirtualShadowLocalReceiver(pixel, light, sourceLight, receiver);
            uint maskIndex = (light >> 5u) * Projection.ViewCount * tilePixels + viewPixel;
            uint4 mask = ReadVirtualShadowLocalMask(maskIndex, receiverLayer, layerValues);
            mask = PackVirtualShadowLocalVisibility(mask, light, visibility, noise);
            WriteVirtualShadowLocalMask(maskIndex, receiverLayer, layerValues, mask);
        }
    }
    if (Projection.AdditionalReceiverLayerCount == 0u && pixel.z == 0u && pixelIndex == 0u)
        ProjectionAdditionalVisibility[0] = 0xffffffffu.xxxx;
#endif
}
