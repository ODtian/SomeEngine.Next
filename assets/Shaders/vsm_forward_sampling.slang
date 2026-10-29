#ifndef VSM_FORWARD_SAMPLING_SLANG
#define VSM_FORWARD_SAMPLING_SLANG

#include "vsm_receiver_sampling.slang"

// A material can share these per-pixel values across its light loop.
float EvaluateVirtualShadowForwardVisibility(uint3 pixel, uint lightIndex, GPULight light,
    VirtualShadowProjectionReceiver receiver, float localVisibilityNoise,
    uint3 screen, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    bool directional = lightIndex < VirtualShadowSampling.DirectionalLightCount;
    uint localLight = directional ? 0xffffffffu : VirtualShadowLocalLightIndex(lightIndex);
    if (!directional && localLight == 0xffffffffu) return 1.0;
    if (!receiver.Valid) return 1.0;
    if (directional)
        return SampleVirtualShadowDirectionalReceiver(pixel, lightIndex, light, receiver,
            screen.xy, view, depthImage);
    float visibility = SampleVirtualShadowLocalReceiver(pixel, localLight, light, receiver,
        screen.xy, view, depthImage);
    // Match the deferred local result's quantization and frame-varying dither.
    return float(QuantizeVirtualShadowLocalVisibility(visibility, localVisibilityNoise)) / 15.0;
}

// Forward materials supply their final receiver after material evaluation.
// Page requests have already used the matching position, geometry and flags.
float EvaluateVirtualShadowForwardVisibility(uint3 pixel, uint lightIndex, GPULight light,
    SurfaceReceiver surface, uint3 screen, ClusterViewUniforms view,
    Texture2DArray<float> depthImage)
{
    bool directional = lightIndex < VirtualShadowSampling.DirectionalLightCount;
    uint localLight = directional ? 0xffffffffu : VirtualShadowLocalLightIndex(lightIndex);
    if (!directional && localLight == 0xffffffffu) return 1.0;
    VirtualShadowProjectionReceiver receiver = MakeVirtualShadowProjectionReceiver(surface);
    if (!receiver.Valid) return 1.0;
    float noise = directional ? 0.0 : VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex);
    return EvaluateVirtualShadowForwardVisibility(pixel, lightIndex, light, receiver, noise,
        screen, view, depthImage);
}

#endif
