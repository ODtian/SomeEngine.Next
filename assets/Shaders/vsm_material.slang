#ifndef VSM_MATERIAL_SLANG
#define VSM_MATERIAL_SLANG

// Material sampling consumes current receiver coverage as well as completed depth.
#define VSM_RESIDENT_LOOKUP_ENABLED 1
#define VSM_PREFILTERED_LOOKUP_ENABLED 1
#define VSM_BLUE_NOISE_TEXTURE 1
Texture2D<float4> VirtualShadowBlueNoise;
#include "vsm_projection.slang"
#include "vsm_receiver_projection.slang"

struct VirtualShadowSamplingUniforms
{
    uint ViewCount;
    uint DirectionalLightCount;
    uint DirectionalClipmapLevels;
    uint PointLightCount;
    uint PointLightBufferOffset;
    uint PointViewOffset;
    uint SpotLightCount;
    uint SpotLightBufferOffset;
    uint SpotViewOffset;
    uint BlockerSampleCount;
    uint FilterSampleCount;
    float BlockerSearchRadiusTexels;
    float MaxFilterRadiusTexels;
    float NormalBias;
    uint FrameIndex;
    uint FirstPersonViewOffset;
    VirtualShadowReceiverProjection ReceiverProjection;
    VirtualShadowRaySettings DirectionalRays;
    VirtualShadowRaySettings LocalRays;
    uint CullBackfacingPixels;
    float ScreenRayLength;
    uint2 ReceiverPadding;
};

StructuredBuffer<VirtualShadowMapParameters> VirtualShadowViews;
StructuredBuffer<uint> VirtualShadowPageTable;
Texture2DArray<uint> VirtualShadowAtlas;
ConstantBuffer<VirtualShadowSamplingUniforms> VirtualShadowSampling;
StructuredBuffer<float> VirtualShadowDynamicResolutionBias;

#endif
