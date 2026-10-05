#ifndef VSM_MATERIAL_SLANG
#define VSM_MATERIAL_SLANG

#include "vsm_sampling.slang"

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
};

StructuredBuffer<VirtualShadowMapParameters> VirtualShadowViews;
StructuredBuffer<uint4> VirtualShadowPageTable;
Texture2D<uint> VirtualShadowAtlas;
ConstantBuffer<VirtualShadowSamplingUniforms> VirtualShadowSampling;
StructuredBuffer<float2> VirtualShadowDynamicResolutionBias;

#endif
