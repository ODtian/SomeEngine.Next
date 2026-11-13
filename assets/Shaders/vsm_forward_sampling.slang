#ifndef VSM_FORWARD_SAMPLING_SLANG
#define VSM_FORWARD_SAMPLING_SLANG

#include "vsm_receiver_sampling.slang"
__exported import vsm_light_view_indices;

float EvaluateMappedVirtualShadowVisibility(uint3 pixel, uint lightIndex, GPULight light,
    SurfaceReceiver surface, uint3 screen, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    uint firstView = VirtualShadowLightViewIndices[lightIndex];
    if (firstView == 0xffffffffu) return 1.0;
    VirtualShadowProjectionReceiver receiver = MakeVirtualShadowProjectionReceiver(surface);
    if (!receiver.Valid) return 1.0;
    VirtualShadowMapParameters shadow = VirtualShadowViews[firstView];
    if (shadow.LightKind == VSM_LIGHT_DIRECTIONAL)
    {
        uint firstPersonView = VirtualShadowSampling.FirstPersonViewOffset < VirtualShadowSampling.ViewCount
            ? VirtualShadowSampling.FirstPersonViewOffset + lightIndex *
                VirtualShadowViews[VirtualShadowSampling.FirstPersonViewOffset].ClipmapLevelCount : 0xffffffffu;
        return SampleVirtualShadowDirectionalViewReceiver(pixel, firstView, shadow.ClipmapLevelCount,
            firstPersonView, light, receiver, screen.xy, view, depthImage);
    }
    float visibility = SampleVirtualShadowLocalViewReceiver(pixel, firstView, shadow.LightKind == VSM_LIGHT_SPOT,
        light, receiver, screen.xy, view, depthImage);
    float noise = VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex);
    return float(QuantizeVirtualShadowLocalVisibility(visibility, noise)) / 15.0;
}

// Forward materials supply their final receiver after material evaluation.
// Page requests have already used the matching position, geometry and flags.
float EvaluateVirtualShadowForwardVisibility(uint3 pixel, uint lightIndex, GPULight light,
    SurfaceReceiver surface, uint3 screen, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    return EvaluateMappedVirtualShadowVisibility(pixel, lightIndex, light, surface, screen, view, depthImage);
}

#endif
