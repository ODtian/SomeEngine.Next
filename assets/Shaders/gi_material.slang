#ifndef GI_MATERIAL_SLANG
#define GI_MATERIAL_SLANG
#include "gi_normal.slang"
#include "gi_probe_sampling.slang"

struct GiSurfaceSettings
{
    uint Enabled;
    uint3 Padding;
};

ConstantBuffer<GiSurfaceSettings> GiCapture;
RWTexture2DArray<float4> GiSurfaceNormal;
RWTexture2DArray<float4> GiSurfaceDiffuse;
RWTexture2DArray<float4> GiSurfaceRadiance;

// Called by the material after interpreting its own surface and textures.
void WriteGiSurface(uint3 pixel, float3 normal, float3 planeNormal, float3 diffuse)
{
    if (GiCapture.Enabled != 0u)
    {
        GiSurfaceNormal[pixel] = float4(EncodeGiNormal(normal), EncodeGiNormal(planeNormal));
        GiSurfaceDiffuse[pixel] = float4(diffuse, 1.0);
    }
}

void WriteGiRadiance(uint3 pixel, float3 radiance)
{
    if (GiCapture.Enabled != 0u)
        GiSurfaceRadiance[pixel] = float4(radiance, 1.0);
}

#endif
