#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "cluster_shade_pipeline.slang"
#include "render_lighting.slang"
#include "brdf.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
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

// C# imports this marked struct through Slang reflection and writes the material
// scalar payload using Slang's own field offsets and type size.
[MaterialScalars]
struct StandardScalars
{
    float4 BaseColorTint;
    float MetallicFactor;
    float Roughness;
    float3 EmissiveFactor;
};

struct StandardPixelContext
{
    uint3 pixelCoord;
    uint instanceID;
    float3 surfacePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

StandardPixelContext BuildStandardPixelContext(
    uint3 pixelCoord,
    uint instanceID,
    SurfaceEval surface,
    float3 bary,
    StaticVertexEval.DeformedVertex v0,
    StaticVertexEval.DeformedVertex v1,
    StaticVertexEval.DeformedVertex v2)
{
    StandardPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.surfacePosition = surface.Position;
    ctx.worldNormal = surface.Normal;
    float3 tangent = normalize(bary.x * v0.worldTangent.xyz + bary.y * v1.worldTangent.xyz + bary.z * v2.worldTangent.xyz);
    ctx.worldTangent = float4(tangent, v0.worldTangent.w);
    ctx.uv0 = surface.TexCoord;
    return ctx;
}

struct StandardMaterial : ISurfaceEvaluate<StaticVertexEval>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    void evaluateSurface(
        uniform StaticVertexEval eval,
        uint3 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2DArray<float4> outputColor)
    {
        StandardPixelContext ctx = BuildStandardPixelContext(
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
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>();

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        float3 baseColor = albedoSample.rgb * scalars.BaseColorTint.rgb;
        float roughness = saturate(armSample.g * scalars.Roughness);
        float metallic = saturate(armSample.b * scalars.MetallicFactor);
        roughness = max(roughness, 0.04);
        ClusteredLightGridParameters lighting = LightingViews[ctx.pixelCoord.z];
        float3 viewPosition = mul(float4(ctx.surfacePosition, 1.0), lighting.View).xyz;
        float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
        float3 V = normalize(Views[ctx.pixelCoord.z].CameraPos - ctx.surfacePosition);
        float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            finalColor += EvaluateDirectionalLight(
                baseColor,
                metallic,
                roughness,
                mappedNormal,
                V,
                -normalize(light.Direction),
                light.Color * light.Intensity,
                EvaluateLightingCookie(light, ctx.surfacePosition));
        }

        uint cell = ClusteredLightCellIndex(
            lighting,
            ctx.pixelCoord.z,
            ctx.pixelCoord.xy,
            viewDepth);
        uint localCount = min(
            ClusteredLightGrid[cell].y,
            lighting.MaxLightsPerCluster);
        uint firstIndex = cell * lighting.MaxLightsPerCluster;
        for (uint localIndex = 0u; localIndex < localCount; localIndex++)
        {
            uint lightIndex = LightIndexList[firstIndex + localIndex];
            GPULight light = LightBuffer[lightIndex];
            float3 radiance = light.Color * light.Intensity;
            if (dot(light.Direction, light.Direction) > 1e-8 && light.OuterConeCos > 0.0)
            {
                finalColor += EvaluateSpotLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    ctx.surfacePosition,
                    light.Position,
                    light.Range,
                    light.Direction,
                    light.InnerConeCos,
                    light.OuterConeCos,
                    radiance,
                    EvaluateLightingCookie(light, ctx.surfacePosition));
            }
            else
            {
                finalColor += EvaluatePointLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    ctx.surfacePosition,
                    light.Position,
                    light.Range,
                    radiance,
                    EvaluateLightingCookie(light, ctx.surfacePosition));
            }
        }

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
    }

    void evaluateDebug(
        uniform StaticVertexEval eval,
        uint debugMode,
        uint3 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        StaticVertexEval.DeformedVertex v0,
        StaticVertexEval.DeformedVertex v1,
        StaticVertexEval.DeformedVertex v2,
        RWTexture2DArray<float4> outputColor)
    {
        if (debugMode == 8)
        {
            outputColor[pixelCoord] = float4(bary, 1.0);
            return;
        }

        StandardPixelContext ctx = BuildStandardPixelContext(
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
