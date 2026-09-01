#ifndef STANDARD_PBR_SLANG
#define STANDARD_PBR_SLANG

#include "material_interfaces.slang"
#include "cluster_shade_pipeline.slang"

// C# imports this marked struct through Slang reflection and writes the material
// scalar payload using Slang's own field offsets and type size.
[MaterialScalars]
struct StandardPBRScalars
{
    float4 BaseColorTint;
    float MetallicFactor;
    float Roughness;
    float3 EmissiveFactor;
};

struct StandardPBRPixelContext
{
    uint2 pixelCoord;
    uint instanceID;
    float3 surfacePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

StandardPBRPixelContext BuildStandardPBRPixelContext(
    uint2 pixelCoord,
    uint instanceID,
    SurfaceEval surface,
    float3 bary,
    StaticVertexEval.DeformedVertex v0,
    StaticVertexEval.DeformedVertex v1,
    StaticVertexEval.DeformedVertex v2)
{
    StandardPBRPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.surfacePosition = surface.Position;
    ctx.worldNormal = surface.Normal;
    float3 tangent = normalize(bary.x * v0.worldTangent.xyz + bary.y * v1.worldTangent.xyz + bary.z * v2.worldTangent.xyz);
    ctx.worldTangent = float4(tangent, v0.worldTangent.w);
    ctx.uv0 = surface.TexCoord;
    return ctx;
}

struct StandardPBRMaterial : ISurfaceEvaluate<StaticVertexEval>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    void evaluateSurface(
        uniform StaticVertexEval eval,
        uint2 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2D<float4> outputColor)
    {
        StandardPBRPixelContext ctx = BuildStandardPBRPixelContext(
            pixelCoord,
            instanceID,
            surface,
            bary,
            v0,
            v1,
            v2);

        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        float3 armSample    = ARMMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb;
        float3 normalTS     = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;
        StandardPBRScalars scalars = LoadMaterialScalars<StandardPBRScalars>();

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        float3 baseColor = albedoSample.rgb * scalars.BaseColorTint.rgb;
        float roughness = saturate(armSample.g * scalars.Roughness);
        float metallic = saturate(armSample.b * scalars.MetallicFactor);
        float facing = 0.5 + 0.5 * saturate(mappedNormal.y);
        float materialResponse = lerp(1.0, 0.85, metallic) * lerp(1.0, 0.9, roughness);
        float3 finalColor = baseColor * armSample.r * facing * materialResponse +
            scalars.EmissiveFactor;

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
    }

    void evaluateDebug(
        uniform StaticVertexEval eval,
        uint debugMode,
        uint2 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2D<float4> outputColor)
    {
        if (debugMode == 8)
        {
            outputColor[pixelCoord] = float4(bary, 1.0);
            return;
        }

        StandardPBRPixelContext ctx = BuildStandardPBRPixelContext(
            pixelCoord,
            instanceID,
            surface,
            bary,
            v0,
            v1,
            v2);

        if (debugMode == 9)
        {
            outputColor[pixelCoord] = float4(ctx.worldNormal * 0.5 + 0.5, 1.0);
            return;
        }

        if (debugMode == 10)
        {
            float checker = step(0.5, frac(ctx.uv0.x * 8.0)) != step(0.5, frac(ctx.uv0.y * 8.0)) ? 0.8 : 0.2;
            outputColor[pixelCoord] = float4(ctx.uv0.x, ctx.uv0.y, checker, 1.0);
            return;
        }

        if (debugMode == 11)
        {
            float3 pos = surface.Position;
            float3 basePos = bary.x * v0.basePosition + bary.y * v1.basePosition + bary.z * v2.basePosition;
            float3 delta = abs(pos - basePos) * 3.0;
            outputColor[pixelCoord] = float4(delta, 1.0);
            return;
        }

        outputColor[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
    }
};

#endif
