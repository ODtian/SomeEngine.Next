#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "vsm_material.slang"
#include "vsm_receiver_pixel.slang"

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
// scalar payload using Slang's buffer field offsets and type size.
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

struct StandardMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>, ISurfaceReceiverEvaluate<SurfaceEval, Triangle>
{
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    SurfaceReceiver evaluateReceiver(uint3 pixelCoord, uint instanceID, uint materialIndex,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        float3 baseNormal = normalize(surface.Normal);
        float3 tangent = normalize(surface.Tangent.xyz - baseNormal * dot(baseNormal, surface.Tangent.xyz));
        float3 bitangent = cross(baseNormal, tangent) * surface.Tangent.w;
        float3 normalTS = NormalMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).rgb * 2.0 - 1.0;
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        receiver.ShadingNormal = normalize(tangent * normalTS.x + bitangent * normalTS.y + baseNormal * normalTS.z);
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), baseNormal);
        if (dot(receiver.GeometricNormal, baseNormal) < 0.0) receiver.GeometricNormal = -receiver.GeometricNormal;
        receiver.Flags = SURFACE_RECEIVER_LIGHTING;
        return receiver;
    }

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
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(materialSlot);

        // TBN matrix: transform normal from tangent space to world space
        float3 N_geom = normalize(ctx.worldNormal);
        uint receiverIndex = (pixelCoord.z * Uniforms.ScreenHeight + pixelCoord.y) * Uniforms.ScreenWidth + pixelCoord.x;
        SurfaceReceiver receiverData = SurfaceReceiverInputs[receiverIndex];
        float3 mappedNormal;
        if ((receiverData.Flags & SURFACE_RECEIVER_PRESENT) != 0u) mappedNormal = receiverData.ShadingNormal;
        else
        {
            float3 normalTS = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;
            float3 T = normalize(ctx.worldTangent.xyz - N_geom * dot(N_geom, ctx.worldTangent.xyz));
            float3 B = cross(N_geom, T) * ctx.worldTangent.w;
            mappedNormal = normalize(T * normalTS.x + B * normalTS.y + N_geom * normalTS.z);
        }
        float3 geometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), N_geom);
        if (dot(geometricNormal, N_geom) < 0.0) geometricNormal = -geometricNormal;

        float3 baseColor = albedoSample.rgb * scalars.BaseColorTint.rgb;
        float roughness = saturate(armSample.g * scalars.Roughness);
        float metallic = saturate(armSample.b * scalars.MetallicFactor);
        roughness = max(roughness, 0.04);
        ClusteredLightGridParameters lighting = LightingViews[ctx.pixelCoord.z];
        float3 viewPosition = mul(float4(ctx.surfacePosition, 1.0), lighting.View).xyz;
        float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
        ClusterViewUniforms receiverView = Views[ctx.pixelCoord.z];
        VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
            DepthBuffer, ctx.pixelCoord, uint2(receiverView.ScreenWidth, receiverView.ScreenHeight),
            DepthBuffer.Load(int4(ctx.pixelCoord, 0)), receiverView.ClipToWorld);
        float3 receiverPositionDx = ctx.surfacePosition + (receiver.PositionDx - receiver.Position);
        float3 receiverPositionDy = ctx.surfacePosition + (receiver.PositionDy - receiver.Position);
        float nominalWorldPixelRadius = CalculateVirtualShadowNominalWorldPixelRadius(
            VirtualShadowSampling.ReceiverProjection, ctx.surfacePosition);
        float receiverResolutionLodBias = CalculateVirtualShadowReceiverResolutionBias(
            VirtualShadowSampling.ReceiverProjection, ctx.surfacePosition);
        float3 V = normalize(MaterialCameraPosition(ctx.pixelCoord.z) - ctx.surfacePosition);
        float3 finalColor = baseColor * armSample.r * 0.03 + scalars.EmissiveFactor;

        if (dot(mappedNormal, V) <= 0.0)
        {
            outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);
            return;
        }

        float normalBiasLength = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
            ctx.surfacePosition, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float3 shadowPosition = ctx.surfacePosition +
                mappedNormal * normalBiasLength;
            float shadow = 1.0;
            if (lightIndex < VirtualShadowSampling.DirectionalLightCount &&
                VirtualShadowSampling.DirectionalClipmapLevels != 0u)
            {
                uint firstShadowView =
                    lightIndex * VirtualShadowSampling.DirectionalClipmapLevels;
                if (firstShadowView < VirtualShadowSampling.ViewCount)
                {
                    VirtualShadowProjectionResult projection = TraceVirtualShadowDirectional(
                        VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas, firstShadowView,
                        min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount - firstShadowView),
                        shadowPosition, mappedNormal, VirtualShadowDynamicResolutionBias[0].x, receiverResolutionLodBias,
                        VirtualShadowSampling.DirectionalRays, ctx.pixelCoord.xy, VirtualShadowSampling.FrameIndex,
                        length(shadowPosition - VirtualShadowSampling.ReceiverProjection.CameraPosition), 0.0);
                    shadow = projection.Visibility * SampleDirectionalVirtualShadowPrefiltered(
                        VirtualShadowViews, VirtualShadowPageTable, VirtualShadowPrefilteredAtlas, firstShadowView,
                        min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount - firstShadowView),
                        shadowPosition);
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
                mappedNormal * normalBiasLength;
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
                    VirtualShadowProjectionResult projection = TraceVirtualShadowLocal(
                        VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas, firstShadowView,
                        shadowPosition, mappedNormal, nominalWorldPixelRadius, VirtualShadowDynamicResolutionBias[0].y,
                        VirtualShadowSampling.LocalRays, ctx.pixelCoord.xy, VirtualShadowSampling.FrameIndex, 0.0);
                    shadow = projection.Visibility;
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
                    VirtualShadowProjectionResult projection = TraceVirtualShadowLocal(
                        VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas, shadowView,
                        shadowPosition, mappedNormal, nominalWorldPixelRadius, VirtualShadowDynamicResolutionBias[0].y,
                        VirtualShadowSampling.LocalRays, ctx.pixelCoord.xy, VirtualShadowSampling.FrameIndex, 0.0);
                    shadow = projection.Visibility;
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
