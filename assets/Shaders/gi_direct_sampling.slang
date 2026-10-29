#ifndef GI_DIRECT_SAMPLING_SLANG
#define GI_DIRECT_SAMPLING_SLANG

#include "gi_emissive_data.slang"

Texture2DArray<float4> GiPreviousDirectLight;
StructuredBuffer<float4> GiPreviousDirectSurfaces;

uint PackGiDirectNormal(float3 normal)
{
    float2 oct = EncodeGiNormal(normal);
    return f32tof16(oct.x) | (f32tof16(oct.y) << 16u);
}

float3 UnpackGiDirectNormal(uint packed)
{
    return DecodeGiNormal(float2(f16tof32(packed), f16tof32(packed >> 16u)));
}

float3 SampleGiEmissiveLight(uint3 pixel, float2 motion, float3 position,
    float3 normal, ClusterViewUniforms view)
{
    if (GiEmissive.UseHistory == 0u) return 0.0;
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiEmissive.Width, GiEmissive.Height) - motion;
    if (any(uv < 0.0) || any(uv >= 1.0)) return 0.0;
    float2 coordinate = uv * float2(GiEmissive.Width, GiEmissive.Height) - 0.5;
    int2 first = int2(floor(coordinate));
    float2 fraction = frac(coordinate);
    float3 viewPosition = ClusterPositionForView(position, view);
    float footprint = max(0.001, abs(mul(float4(viewPosition, 1.0), view.View).z) *
        2.0 / (abs(view.P11) * float(GiEmissive.Height)));
    uint count = GiEmissive.Width * GiEmissive.Height * GiEmissive.ViewCount;
    uint layer = (GiEmissive.Frame + 1u) & 1u;
    float3 result = 0.0;
    float total = 0.0;
    for (uint corner = 0u; corner < 4u; corner++)
    {
        int2 offset = int2(corner & 1u, corner >> 1u);
        int2 xy = first + offset;
        if (any(xy < 0) || any(xy >= int2(GiEmissive.Width, GiEmissive.Height))) continue;
        uint index = (pixel.z * GiEmissive.Height + uint(xy.y)) * GiEmissive.Width + uint(xy.x);
        float4 surface = GiPreviousDirectSurfaces[layer * count + index];
        float4 light = GiPreviousDirectLight.Load(int4(xy, layer * GiEmissive.ViewCount + pixel.z, 0));
        if (light.w == 0.0 || dot(normal, UnpackGiDirectNormal(asuint(surface.w))) < 0.95) continue;
        float3 delta = surface.xyz + view.PreviousDataTranslation - position;
        if (abs(dot(delta, normal)) > max(0.003, footprint * 1.5) || length(delta) > footprint * 3.0) continue;
        float2 amount = select(offset != 0, fraction, 1.0 - fraction);
        float weight = amount.x * amount.y;
        result += light.rgb * weight;
        total += weight;
    }
    return total > 0.0 ? result / total : 0.0;
}

#endif
