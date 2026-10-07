#ifndef VSM_PROJECTED_SAMPLING_SLANG
#define VSM_PROJECTED_SAMPLING_SLANG

#include "vsm_material.slang"

Texture2DArray<float> VirtualShadowProjectedVisibility;
Texture2DArray<float> VirtualShadowAdditionalProjectedVisibility;

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

float ReadVirtualShadowProjectedVisibility(uint3 pixel, uint lightIndex, uint viewCount)
{
    uint light = VirtualShadowProjectedLightIndex(lightIndex);
    return light == 0xffffffffu ? 1.0 :
        VirtualShadowProjectedVisibility.Load(int4(pixel.xy, light * viewCount + pixel.z, 0));
}

float ReadVirtualShadowAdditionalProjectedVisibility(uint3 pixel, uint lightIndex, uint viewCount, uint layer)
{
    uint light = VirtualShadowProjectedLightIndex(lightIndex);
    uint width, height, layers;
    VirtualShadowProjectedVisibility.GetDimensions(width, height, layers);
    return light == 0xffffffffu ? 1.0 : VirtualShadowAdditionalProjectedVisibility.Load(
        int4(pixel.xy, layer * layers + light * viewCount + pixel.z, 0));
}

#endif
