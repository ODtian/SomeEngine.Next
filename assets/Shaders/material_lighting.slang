#ifndef MATERIAL_LIGHTING_SLANG
#define MATERIAL_LIGHTING_SLANG

__exported import render_lighting;
__exported import brdf;
#include "vsm_forward_sampling.slang"
__exported import render_instance_transform;
__exported import render_instance_properties;
__exported import cluster_view;
#define CLUSTER_INSTANCE_PROPERTY_IMPORTS 1
namespace StandardInstanceData
{
#include "cluster_instance_properties.slang"
}
#include "vsm_receiver_requests.slang"

StructuredBuffer<ClusterViewUniforms> MaterialViews;
Texture2DArray<float> MaterialDepth;
ByteAddressBuffer MaterialInstanceData;
ConstantBuffer<StandardInstanceData::RenderInstancePropertyMetadata> MaterialInstanceProperties;

#ifndef RENDER_LIGHTING_INPUTS_DECLARED
#define RENDER_LIGHTING_INPUTS_DECLARED
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
#endif
Texture2DArray<float4> LightingCookieAtlas;

float EvaluateLightingCookie(GPULight light, float3 worldPosition)
{
    if (light.CookieIndex < 0)
        return 1.0;
    float4 cookiePosition = mul(float4(worldPosition, 1.0), light.WorldToLightCookie);
    float2 uv = cookiePosition.xy / max(abs(cookiePosition.w), 1e-5);
    uv = uv * light.CookieScaleOffset.xy + light.CookieScaleOffset.zw;
    uint width;
    uint height;
    uint layers;
    LightingCookieAtlas.GetDimensions(width, height, layers);
    int2 pixel = int2(saturate(uv) * float2(max(width, 1u) - 1u, max(height, 1u) - 1u));
    uint layer = min((uint)light.CookieIndex, max(layers, 1u) - 1u);
    float cookie = LightingCookieAtlas.Load(int4(pixel, layer, 0)).r;
    return lerp(1.0, cookie, saturate(light.CookieStrength));
}


#endif
