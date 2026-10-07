#ifndef VSM_PROJECTED_SAMPLING_SLANG
#define VSM_PROJECTED_SAMPLING_SLANG

#include "vsm_material.slang"
#include "vsm_projected_pixel.slang"

StructuredBuffer<float> VirtualShadowProjectedDirectionalVisibility;
StructuredBuffer<float> VirtualShadowProjectedLocalVisibility;
StructuredBuffer<float> VirtualShadowAdditionalProjectedDirectionalVisibility;
StructuredBuffer<float> VirtualShadowAdditionalProjectedLocalVisibility;

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
    uint index = GetVirtualShadowProjectedPixelIndex(pixel.xy, screen.xy, lightLayer * screen.z + pixel.z);
    return directional ? VirtualShadowProjectedDirectionalVisibility[index] : VirtualShadowProjectedLocalVisibility[index];
}

float ReadVirtualShadowAdditionalProjectedVisibility(uint3 pixel, uint lightIndex, uint3 screen, uint layer)
{
    uint light = VirtualShadowProjectedLightIndex(lightIndex);
    if (light == 0xffffffffu) return 1.0;
    bool directional = light < VirtualShadowSampling.DirectionalLightCount;
    uint lights = directional ? VirtualShadowSampling.DirectionalLightCount :
        VirtualShadowSampling.PointLightCount + VirtualShadowSampling.SpotLightCount;
    uint lightLayer = directional ? light : light - VirtualShadowSampling.DirectionalLightCount;
    uint index = GetVirtualShadowProjectedPixelIndex(pixel.xy, screen.xy,
        (layer * lights + lightLayer) * screen.z + pixel.z);
    return directional ? VirtualShadowAdditionalProjectedDirectionalVisibility[index] :
        VirtualShadowAdditionalProjectedLocalVisibility[index];
}

#endif
