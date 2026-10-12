#ifndef VSM_FORWARD_SAMPLING_SLANG
#define VSM_FORWARD_SAMPLING_SLANG

#include "vsm_receiver_sampling.slang"

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
    if (directional)
        return SampleVirtualShadowDirectionalReceiver(pixel, lightIndex, light, receiver,
            screen.xy, view, depthImage);
    float visibility = SampleVirtualShadowLocalReceiver(pixel, localLight, light, receiver,
        screen.xy, view, depthImage);
    // Match the deferred local result's quantization and frame-varying dither.
    float noise = VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex);
    return float(QuantizeVirtualShadowLocalVisibility(visibility, noise)) / 15.0;
}

#endif
