#ifndef VSM_MATERIAL_SLANG
#define VSM_MATERIAL_SLANG

// Material sampling consumes current receiver coverage as well as completed depth.
#define VSM_RESIDENT_LOOKUP_ENABLED 1
#include "vsm_sampling.slang"
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
    uint Pad0;
    uint Pad1;
    VirtualShadowReceiverProjection ReceiverProjection;
};

StructuredBuffer<VirtualShadowMapParameters> VirtualShadowViews;
StructuredBuffer<uint4> VirtualShadowPageTable;
Texture2D<uint> VirtualShadowAtlas;
ConstantBuffer<VirtualShadowSamplingUniforms> VirtualShadowSampling;
StructuredBuffer<float2> VirtualShadowDynamicResolutionBias;

#endif
