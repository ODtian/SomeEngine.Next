#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "vsm_sampling.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
Texture2DArray<float4> LightingCookieAtlas;

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

float3 ReconstructStandardPixelNeighbor(
    ClusterViewUniforms view,
    uint2 pixel,
    float deviceDepth,
    int2 offset)
{
    uint2 screen = max(uint2(view.ScreenWidth, view.ScreenHeight), 1u.xx);
    int2 neighbor = clamp(
        int2(pixel) + offset,
        int2(0, 0),
        int2(screen) - 1);
    float2 uv = (float2(neighbor) + 0.5) / float2(screen);
    float4 world = mul(
        float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, deviceDepth, 1.0),
        view.ClipToWorld);
    return world.xyz / max(abs(world.w), 1e-8);
}

struct StandardMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    void evaluateSurface(
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle,
        RWTexture2DArray<float4> outputColor)
    {
        StandardPixelContext ctx = BuildStandardPixelContext(
            pixelCoord,
            instanceID,
            surface);

        // Sample material textures
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        float3 armSample    = ARMMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb;
        float3 normalTS     = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(materialSlot);

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
        float3 V = normalize(MaterialCameraPosition(ctx.pixelCoord.z) - ctx.surfacePosition);
        float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;

        if (dot(mappedNormal, V) <= 0.0)
        {
            outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
            return;
        }

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float3 shadowPosition = ctx.surfacePosition +
                mappedNormal * VirtualShadowSampling.NormalBias;
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
                        VirtualShadowDynamicResolutionBias[0].x,
                        shadowPosition,
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
        ClusterViewUniforms receiverView = Views[ctx.pixelCoord.z];
        float4 receiverClip = mul(float4(ctx.surfacePosition, 1.0), receiverView.ViewProj);
        float receiverDepth = receiverClip.z / max(abs(receiverClip.w), 1e-8);
        float3 receiverPositionDx = ReconstructStandardPixelNeighbor(
            receiverView, ctx.pixelCoord.xy, receiverDepth, int2(1, 0));
        float3 receiverPositionDy = ReconstructStandardPixelNeighbor(
            receiverView, ctx.pixelCoord.xy, receiverDepth, int2(0, 1));
        uint localCount = min(
            ClusteredLightGrid[cell].y,
            lighting.MaxLightsPerCluster);
        uint firstIndex = cell * lighting.MaxLightsPerCluster;
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
                mappedNormal * VirtualShadowSampling.NormalBias;
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
                        VirtualShadowDynamicResolutionBias[0].y,
                        shadowPosition,
                        receiverPositionDx + mappedNormal * VirtualShadowSampling.NormalBias,
                        receiverPositionDy + mappedNormal * VirtualShadowSampling.NormalBias,
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
                        VirtualShadowDynamicResolutionBias[0].y,
                        shadowPosition,
                        receiverPositionDx + mappedNormal * VirtualShadowSampling.NormalBias,
                        receiverPositionDy + mappedNormal * VirtualShadowSampling.NormalBias,
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

        outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
    }

    void evaluateDebug(
        uint debugMode,
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle,
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
            surface);

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
            float3 basePos = EvalLinearPosition(triangle, bary);
            float3 delta = abs(pos - basePos) * 3.0;
            outputColor[pixelCoord] = float4(delta, 1.0);
            return;
        }

        outputColor[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
    }
};

#endif
