#ifndef VSM_PROJECTED_SAMPLING_SLANG
#define VSM_PROJECTED_SAMPLING_SLANG

#include "vsm_material.slang"
#include "vsm_projected_pixel.slang"

StructuredBuffer<float> VirtualShadowProjectedDirectionalVisibility;
StructuredBuffer<uint4> VirtualShadowProjectedLocalVisibility;
StructuredBuffer<float> VirtualShadowAdditionalProjectedDirectionalVisibility;
StructuredBuffer<uint4> VirtualShadowAdditionalProjectedLocalVisibility;

float DecodeVirtualShadowLocalVisibility(uint4 mask, uint light)
{
    uint word = (light & 31u) >> 3u;
    uint shift = (light & 7u) * 4u;
    return float((mask[word] >> shift) & 15u) / 15.0;
}

uint VirtualShadowProjectedLightIndex(uint lightIndex)
{
    if (lightIndex < VirtualShadowSampling.DirectionalLightCount) return lightIndex;
    if (lightIndex >= VirtualShadowSampling.PointLightBufferOffset &&
        lightIndex < VirtualShadowSampling.PointLightBufferOffset + VirtualShadowSampling.PointLightCount)
        return VirtualShadowSampling.DirectionalLightCount + lightIndex - VirtualShadowSampling.PointLightBufferOffset;
    if (lightIndex >= VirtualShadowSampling.SpotLightBufferOffset &&
        lightIndex < VirtualShadowSampling.SpotLightBufferOffset + VirtualShadowSampling.SpotLightCount)
        return VirtualShadowSampling.DirectionalLightCount + VirtualShadowSampling.PointLightCount +
            lightIndex - VirtualShadowSampling.SpotLightBufferOffset;
    return 0xffffffffu;
}

float ReadVirtualShadowProjectedVisibility(uint3 pixel, uint lightIndex, uint3 screen)
{
    uint light = VirtualShadowProjectedLightIndex(lightIndex);
    if (light == 0xffffffffu) return 1.0;
    bool directional = light < VirtualShadowSampling.DirectionalLightCount;
    uint lightLayer = directional ? light : light - VirtualShadowSampling.DirectionalLightCount;
    uint resultLayer = directional ? lightLayer : lightLayer >> 5u;
    uint index = GetVirtualShadowProjectedPixelIndex(pixel.xy, screen.xy, resultLayer * screen.z + pixel.z);
    return directional ? VirtualShadowProjectedDirectionalVisibility[index] :
        DecodeVirtualShadowLocalVisibility(VirtualShadowProjectedLocalVisibility[index], lightLayer);
}

float ReadVirtualShadowAdditionalProjectedVisibility(uint3 pixel, uint lightIndex, uint3 screen, uint layer)
{
    uint light = VirtualShadowProjectedLightIndex(lightIndex);
    if (light == 0xffffffffu) return 1.0;
    bool directional = light < VirtualShadowSampling.DirectionalLightCount;
    uint lights = directional ? VirtualShadowSampling.DirectionalLightCount :
        VirtualShadowSampling.PointLightCount + VirtualShadowSampling.SpotLightCount;
    uint lightLayer = directional ? light : light - VirtualShadowSampling.DirectionalLightCount;
    uint resultLayers = directional ? lights : (lights + 31u) / 32u;
    uint resultLayer = directional ? lightLayer : lightLayer >> 5u;
    uint index = GetVirtualShadowProjectedPixelIndex(pixel.xy, screen.xy,
        (layer * resultLayers + resultLayer) * screen.z + pixel.z);
    return directional ? VirtualShadowAdditionalProjectedDirectionalVisibility[index] :
        DecodeVirtualShadowLocalVisibility(VirtualShadowAdditionalProjectedLocalVisibility[index], lightLayer);
}

#endif
