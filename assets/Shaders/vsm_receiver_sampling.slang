#ifndef VSM_RECEIVER_SAMPLING_SLANG
#define VSM_RECEIVER_SAMPLING_SLANG

#include "vsm_material.slang"
#include "vsm_soft_sampling.slang"
#include "cluster_view.slang"
#include "render_lighting.slang"
#include "surface_receiver.slang"

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

VirtualShadowProjectionReceiver MakeVirtualShadowProjectionReceiver(SurfaceReceiver receiver)
{
    VirtualShadowProjectionReceiver result = (VirtualShadowProjectionReceiver)0;
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

float ReadVirtualShadowScreenDepth(Texture2DArray<float> depthImage, uint2 screenSize, float2 uv, uint viewIndex)
{
    int2 pixel = clamp(int2(uv * float2(screenSize.x, screenSize.y)),
        0, int2(screenSize.x, screenSize.y) - 1);
    return depthImage.Load(int4(pixel, viewIndex, 0));
}

float CalculateVirtualShadowScreenRayStart(uint3 pixel, VirtualShadowProjectionReceiver receiver,
    float3 position, float3 direction, uint2 screenSize, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    if (VirtualShadowSampling.ScreenRayLength <= 0.0) return 0.0;
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
    float startDepth = ReadVirtualShadowScreenDepth(depthImage, screenSize, start.xy, pixel.z);
    float sampleTime = (VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex) - .5) * .25 + .25;
    for (uint sample = 0u; sample < 4u; sample++)
    {
        float3 query = start + step * sampleTime;
        float depth = ReadVirtualShadowScreenDepth(depthImage, screenSize, query.xy, pixel.z);
        // The primary depth uses forward Z. Native reverse-Z ordering is inverted here.
        if (depth != startDepth && query.z > depth)
            return rayLength * max(0.0, sampleTime - .375);
        sampleTime += .25;
    }
    return rayLength;
}

#if !defined(VSM_PROJECT_DIRECTIONAL_LIGHTS) || VSM_PROJECT_DIRECTIONAL_LIGHTS
float SampleVirtualShadowDirectionalReceiver(uint3 pixel, uint light, GPULight source,
    VirtualShadowProjectionReceiver receiver, uint2 screenSize, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    if (!receiver.Valid || light >= VirtualShadowSampling.DirectionalLightCount) return 1.0;
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
    float rayStartDistance = CalculateVirtualShadowScreenRayStart(pixel, receiver, shadowPosition, -source.Direction,
        screenSize, view, depthImage);
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
#endif

#if !defined(VSM_PROJECT_DIRECTIONAL_LIGHTS) || !VSM_PROJECT_DIRECTIONAL_LIGHTS
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

float SampleVirtualShadowLocalReceiver(uint3 pixel, uint light, GPULight source,
    VirtualShadowProjectionReceiver receiver, uint2 screenSize, ClusterViewUniforms cameraView,
    Texture2DArray<float> depthImage)
{
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
    float rayStartDistance = CalculateVirtualShadowScreenRayStart(pixel, receiver, shadowPosition, normalize(towardLight),
        screenSize, cameraView, depthImage);
    return SampleVirtualShadowLocalSoft(VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas,
        firstView, shadowPosition, normal, receiver.NominalPixelRadius,
        VirtualShadowDynamicResolutionBias[0], VirtualShadowSampling.BlockerSampleCount,
        VirtualShadowSampling.FilterSampleCount, VirtualShadowSampling.MaxFilterRadiusTexels,
        pixel.xy, VirtualShadowSampling.FrameIndex, rayStartDistance).Visibility;
}


#endif

uint QuantizeVirtualShadowLocalVisibility(float visibility, float noise)
{
    const float noiseScale = 1.0 / 15.0;
    if (visibility > noiseScale * .25 && visibility < 1.0)
        visibility = saturate(visibility + (noise - .5) * noiseScale);
    return uint(round(saturate(visibility) * 15.0));
}

#endif
