#ifndef COATING_MATERIAL_SLANG
#define COATING_MATERIAL_SLANG

#include "material_decals.slang"

Texture2D<float4> CoatingMaterialTextures[];
SamplerState CoatingMaterialSamplers[];

[MaterialDecalScalars]
struct CoatingScalars
{
    float4 BaseColorTint;
    float Roughness;
    uint AlbedoMap;
    uint NormalMap;
    uint ARMMap;
    uint MaterialSampler;
};

struct CoatingLayer
{
    float4 BaseColor;
    float3 Normal;
    float Roughness;
};

CoatingLayer EvaluateCoating(MaterialDecalInput input)
{
    CoatingScalars material = MaterialDecalProperties.Load<CoatingScalars>(input.Volume.Parameters + 16u);
    float2 uv = input.Position.xy * .5 + .5;
    float2 dx = input.PositionDx.xy * .5, dy = input.PositionDy.xy * .5;
    SamplerState sampler = CoatingMaterialSamplers[NonUniformResourceIndex(material.MaterialSampler)];
    float4 albedo = CoatingMaterialTextures[NonUniformResourceIndex(material.AlbedoMap)].SampleGrad(sampler, uv, dx, dy);
    float3 normal = CoatingMaterialTextures[NonUniformResourceIndex(material.NormalMap)].SampleGrad(sampler, uv, dx, dy).xyz * 2.0 - 1.0;
    float roughness = CoatingMaterialTextures[NonUniformResourceIndex(material.ARMMap)].SampleGrad(sampler, uv, dx, dy).g;
    CoatingLayer result;
    result.BaseColor = albedo * material.BaseColorTint;
    result.Normal = normalize(mul(normal, transpose((float3x3)input.Volume.WorldToDecal)));
    result.Roughness = max(.04, roughness * material.Roughness);
    return result;
}

#endif
