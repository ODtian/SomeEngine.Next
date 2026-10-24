#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "triangle_surface.slang"
#include "coating_material.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "vsm_material.slang"

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
    SurfaceEval surface)
{
    StandardPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.surfacePosition = surface.Position;
    ctx.worldNormal = surface.Normal;
    ctx.worldTangent = surface.Tangent;
    ctx.uv0 = surface.TexCoord;
    return ctx;
}

struct StandardMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>, ITransparentSurfaceEvaluate<SurfaceEval, Triangle>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    float4 evaluateColor(
        SurfacePixelInput input,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle)
    {
        StandardPixelContext ctx = BuildStandardPixelContext(
            input.PixelCoord,
            input.InstanceID,
            surface);

        if (input.VisibilityDepth == 2u)
            ctx.surfacePosition = input.PositionFromDepth;
        float2 uvDx = triangle.TexCoord0 * input.BarycentricDx.x + triangle.TexCoord1 * input.BarycentricDx.y + triangle.TexCoord2 * input.BarycentricDx.z;
        float2 uvDy = triangle.TexCoord0 * input.BarycentricDy.x + triangle.TexCoord1 * input.BarycentricDy.y + triangle.TexCoord2 * input.BarycentricDy.z;

        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy);
        float3 armSample    = ARMMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy).rgb;
        float3 normalTS     = NormalMap.SampleGrad(MaterialSampler, ctx.uv0, uvDx, uvDy).rgb * 2.0 - 1.0;
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(input.MaterialIndex);

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz)); // Gram-Schmidt
        float3 B = cross(N_geom, T) * ctx.worldTangent.w; // handedness
        float3 mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);

        float3 baseColor = albedoSample.rgb * scalars.BaseColorTint.rgb;
        float roughness = saturate(armSample.g * scalars.Roughness);
        float metallic = saturate(armSample.b * scalars.MetallicFactor);
        roughness = max(roughness, 0.04);
        MaterialDecalIterator decals = BeginMaterialDecals(input.PixelCoord, ctx.surfacePosition);
        MaterialDecalInput decal;
        while (NextMaterialDecal(decals, ctx.surfacePosition, input.PositionDx, input.PositionDy, decal))
        {
            CoatingLayer layer = EvaluateCoating(decal);
            float alpha = saturate(layer.BaseColor.a);
            baseColor = lerp(baseColor, layer.BaseColor.rgb, alpha);
            roughness = lerp(roughness, layer.Roughness, alpha);
            mappedNormal = SafeNormalize(lerp(mappedNormal, layer.Normal, alpha), N_geom);
        }
        float opacity = albedoSample.a * scalars.BaseColorTint.a;
        ClusteredLightGridParameters lighting = LightingViews[ctx.pixelCoord.z];
        float3 viewPosition = mul(float4(ctx.surfacePosition, 1.0), lighting.View).xyz;
        float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
        float3 receiverPositionDx = ctx.surfacePosition + input.PositionDx;
        float3 receiverPositionDy = ctx.surfacePosition + input.PositionDy;
        float nominalPixelRadius = CalculateVirtualShadowNominalWorldPixelRadius(
            VirtualShadowSampling.ReceiverProjection, ctx.surfacePosition);
        float normalBias = VirtualShadowSampling.ViewCount == 0u ? 0.0 :
            CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
                ctx.surfacePosition, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
        float3 V = normalize(input.CameraPosition - ctx.surfacePosition);
        float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;

        if (dot(mappedNormal, V) <= 0.0)
        {
            return float4(finalColor, opacity);
        }

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float3 shadowPosition = ctx.surfacePosition +
                mappedNormal * normalBias;
            float shadow = 1.0;
            if (lightIndex < VirtualShadowSampling.DirectionalLightCount &&
                VirtualShadowSampling.DirectionalClipmapLevels != 0u)
            {
                uint firstShadowView =
                    lightIndex * VirtualShadowSampling.DirectionalClipmapLevels;
                if (firstShadowView < VirtualShadowSampling.ViewCount)
                {
                    shadow = SampleDirectionalVirtualShadowMap(
                        VirtualShadowViews,
                        VirtualShadowPageTable,
                        VirtualShadowAtlas,
                        firstShadowView,
                        min(
                            VirtualShadowSampling.DirectionalClipmapLevels,
                            VirtualShadowSampling.ViewCount - firstShadowView),
                        VirtualShadowDynamicResolutionBias[0],
                        shadowPosition,
                        receiverPositionDx + mappedNormal * normalBias,
                        receiverPositionDy + mappedNormal * normalBias,
                        VirtualShadowSampling.BlockerSampleCount,
                        VirtualShadowSampling.FilterSampleCount,
                        VirtualShadowSampling.BlockerSearchRadiusTexels,
                        VirtualShadowSampling.MaxFilterRadiusTexels);
                }
            }
            finalColor += EvaluateDirectionalLight(
                baseColor,
                metallic,
                roughness,
                mappedNormal,
                V,
                -normalize(light.Direction),
                light.Color * (light.Intensity * shadow),
                EvaluateLightingCookie(light, ctx.surfacePosition));
        }

        uint cell = ClusteredLightCellIndex(
            lighting,
            ctx.pixelCoord.z,
            ctx.pixelCoord.xy,
            viewDepth);
        uint2 cellLights = ClusteredLightGrid[cell];
        uint localCount = cellLights.y;
        uint firstIndex = cellLights.x;
        for (uint localIndex = 0u; localIndex < localCount; localIndex++)
        {
            uint lightIndex = LightIndexList[firstIndex + localIndex];
            GPULight light = LightBuffer[lightIndex];
            float3 toLight = light.Position - ctx.surfacePosition;
            float distanceSq = dot(toLight, toLight);
            if (light.Range <= 0.0 || distanceSq >= light.Range * light.Range || dot(mappedNormal, toLight) <= 0.0)
                continue;
            bool spotLight = dot(light.Direction, light.Direction) > 1e-8 && light.OuterConeCos > 0.0;
            if (spotLight && EvaluateSpotCone(toLight * rsqrt(max(distanceSq, MIN_DISTANCE_SQ)),
                    light.Direction, light.InnerConeCos, light.OuterConeCos) <= 0.0)
                continue;
            float3 shadowPosition = ctx.surfacePosition +
                mappedNormal * normalBias;
            float shadow = 1.0;
            if (lightIndex >= VirtualShadowSampling.PointLightBufferOffset &&
                lightIndex < VirtualShadowSampling.PointLightBufferOffset +
                    VirtualShadowSampling.PointLightCount)
            {
                uint pointLightIndex = lightIndex -
                    VirtualShadowSampling.PointLightBufferOffset;
                uint firstShadowView = VirtualShadowSampling.PointViewOffset +
                    pointLightIndex * 6u;
                if (firstShadowView + 5u < VirtualShadowSampling.ViewCount)
                {
                    shadow = SamplePointVirtualShadowMap(
                        VirtualShadowViews,
                        VirtualShadowPageTable,
                        VirtualShadowAtlas,
                        firstShadowView,
                        VirtualShadowDynamicResolutionBias[0],
                        nominalPixelRadius,
                        shadowPosition,
                        receiverPositionDx + mappedNormal * normalBias,
                        receiverPositionDy + mappedNormal * normalBias,
                        VirtualShadowSampling.BlockerSampleCount,
                        VirtualShadowSampling.FilterSampleCount,
                        VirtualShadowSampling.BlockerSearchRadiusTexels,
                        VirtualShadowSampling.MaxFilterRadiusTexels);
                }
            }
            else if (lightIndex >= VirtualShadowSampling.SpotLightBufferOffset &&
                lightIndex < VirtualShadowSampling.SpotLightBufferOffset +
                    VirtualShadowSampling.SpotLightCount)
            {
                uint spotLightIndex = lightIndex -
                    VirtualShadowSampling.SpotLightBufferOffset;
                uint shadowView = VirtualShadowSampling.SpotViewOffset + spotLightIndex;
                if (shadowView < VirtualShadowSampling.ViewCount)
                {
                    shadow = SampleSpotVirtualShadowMap(
                        VirtualShadowViews,
                        VirtualShadowPageTable,
                        VirtualShadowAtlas,
                        shadowView,
                        VirtualShadowDynamicResolutionBias[0],
                        nominalPixelRadius,
                        shadowPosition,
                        receiverPositionDx + mappedNormal * normalBias,
                        receiverPositionDy + mappedNormal * normalBias,
                        VirtualShadowSampling.BlockerSampleCount,
                        VirtualShadowSampling.FilterSampleCount,
                        VirtualShadowSampling.BlockerSearchRadiusTexels,
                        VirtualShadowSampling.MaxFilterRadiusTexels);
                }
            }
            float3 radiance = light.Color * (light.Intensity * shadow);
            if (spotLight)
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

        return float4(finalColor, opacity);
    }

    void evaluateSurface(
        SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        float4 color = evaluateColor(input, surface, bary, triangle);
        outputColor[input.PixelCoord] = float4(color.rgb, 1.0);
    }

    float4 evaluateDebugColor(uint debugMode, SurfacePixelInput input,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        if (debugMode == 8u) return float4(bary, 1.0);
        if (debugMode == 9u) return float4(surface.Normal * .5 + .5, 1.0);
        if (debugMode == 10u)
        {
            float checker = step(.5, frac(surface.TexCoord.x * 8.0)) !=
                step(.5, frac(surface.TexCoord.y * 8.0)) ? .8 : .2;
            return float4(surface.TexCoord, checker, 1.0);
        }
        if (debugMode == 11u)
            return float4(abs(surface.Position - EvalLinearPosition(triangle, bary)) * 3.0, 1.0);
        return float4(1.0, 0.0, 1.0, 1.0);
    }

    void evaluateDebug(
        uint debugMode, SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        outputColor[input.PixelCoord] = evaluateDebugColor(debugMode, input, surface, bary, triangle);
    }

    float4 evaluateTransparency(uint debugMode, SurfacePixelInput input,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        return debugMode == 0u ? evaluateColor(input, surface, bary, triangle) :
            evaluateDebugColor(debugMode, input, surface, bary, triangle);
    }

    SurfaceShadowReceiver evaluateShadowReceiver(SurfacePixelInput input,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        SurfaceShadowReceiver receiver;
        receiver.Position = input.VisibilityDepth == 2u ? input.PositionFromDepth : surface.Position;
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), surface.Normal);
        if (dot(receiver.GeometricNormal, surface.Normal) < 0.0)
            receiver.GeometricNormal = -receiver.GeometricNormal;
        receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
        receiver.Depth = input.Depth;
        return receiver;
    }
};

#endif
