#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "vsm_projected_sampling.slang"

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

[MaterialSurfaceData]
struct StandardSurfaceData
{
    float3 BaseColor;
    float Metallic;
    float3 Emissive;
    float Roughness;
    float Occlusion;
    float3 DebugColor;
};

struct StandardMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = StandardSurfaceData;
    // Textures
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    float3 sampleNormal(float2 uv, uint materialIndex)
    {
        return NormalMap.SampleLevel(MaterialSampler, uv, 0.0).rgb * 2.0 - 1.0;
    }

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        float3 baseNormal = normalize(surface.Normal);
        float3 tangent = normalize(surface.Tangent.xyz - baseNormal * dot(baseNormal, surface.Tangent.xyz));
        float3 bitangent = cross(baseNormal, tangent) * surface.Tangent.w;
        float3 normalTS = sampleNormal(surface.TexCoord, pixel.MaterialIndex);
        receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.VisibilityDepth == 2u ? pixel.PositionFromDepth : surface.Position;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = normalize(tangent * normalTS.x + bitangent * normalTS.y + baseNormal * normalTS.z);
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), baseNormal);
        if (dot(receiver.GeometricNormal, baseNormal) < 0.0) receiver.GeometricNormal = -receiver.GeometricNormal;
        receiver.Flags = SURFACE_RECEIVER_LIGHTING;
        ClusterViewUniforms surfaceView = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
            pixel.InstanceID, Views[pixel.PixelCoord.z]);
        float4 sourceClip = mul(float4(ClusterPositionForView(surface.Position, surfaceView), 1.0), surfaceView.ViewProj);
        receiver.DepthOffset = pixel.VisibilityDepth == 2u ? pixel.Depth - sourceClip.z / sourceClip.w : 0.0;
        float4 albedo = AlbedoMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0);
        float3 arm = ARMMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).rgb;
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(pixel.MaterialIndex);
        Data data;
        data.BaseColor = albedo.rgb * scalars.BaseColorTint.rgb;
        data.Metallic = saturate(arm.b * scalars.MetallicFactor);
        data.Emissive = scalars.EmissiveFactor;
        data.Roughness = max(saturate(arm.g * scalars.Roughness), .04);
        data.Occlusion = arm.r;
        data.DebugColor = float3(1,0,1);
        if (pixel.DebugMode == 8u) data.DebugColor = bary;
        if (pixel.DebugMode == 9u)
            data.DebugColor = TransformFirstPersonWorldNormal(InstanceData, InstanceProperties, pixel.InstanceID,
                Views[pixel.PixelCoord.z], baseNormal) * .5 + .5;
        if (pixel.DebugMode == 10u)
        {
            float checker = step(.5, frac(surface.TexCoord.x * 8)) != step(.5, frac(surface.TexCoord.y * 8)) ? .8 : .2;
            data.DebugColor = float3(surface.TexCoord, checker);
        }
        if (pixel.DebugMode == 11u) data.DebugColor = abs(surface.Position - EvalLinearPosition(triangle, bary)) * 3;
        return data;
    }

    void shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver,
        RWTexture2DArray<float4> outputColor)
    {
        if (debugMode != 0u) { outputColor[pixelCoord] = float4(data.DebugColor,1); return; }
        float3 mappedNormal = receiver.ShadingNormal;
        float3 baseColor = data.BaseColor;
        float roughness = data.Roughness;
        float metallic = data.Metallic;
        ClusteredLightGridParameters lighting = LightingViews[pixelCoord.z];
        float3 viewPosition = mul(float4(receiver.Position, 1.0), lighting.View).xyz;
        float viewDepth = max(-viewPosition.z, lighting.ZParams.x);
        float3 V = normalize(MaterialCameraPosition(pixelCoord.z) - receiver.Position);
        float3 finalColor = baseColor * data.Occlusion * .03 + data.Emissive;

        if (dot(mappedNormal, V) <= 0.0)
        {
            outputColor[pixelCoord] = float4(finalColor, 1.0);
            return;
        }

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float shadow = ReadVirtualShadowProjectedVisibility(pixelCoord, lightIndex,
                uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount));
            finalColor += EvaluateDirectionalLight(
                baseColor,
                metallic,
                roughness,
                mappedNormal,
                V,
                -normalize(light.Direction),
                light.Color * (light.Intensity * shadow),
                EvaluateLightingCookie(light, receiver.Position));
        }

        uint cell = ClusteredLightCellIndex(
            lighting,
            pixelCoord.z,
            pixelCoord.xy,
            viewDepth);
        uint localCount = min(
            ClusteredLightGrid[cell].y,
            lighting.MaxLightsPerCluster);
        uint firstIndex = cell * lighting.MaxLightsPerCluster;
        for (uint localIndex = 0u; localIndex < localCount; localIndex++)
        {
            uint lightIndex = LightIndexList[firstIndex + localIndex];
            GPULight light = LightBuffer[lightIndex];
            float3 toLight = light.Position - receiver.Position;
            float distanceSq = dot(toLight, toLight);
            if (light.Range <= 0.0 || distanceSq >= light.Range * light.Range || dot(mappedNormal, toLight) <= 0.0)
                continue;
            bool spotLight = dot(light.Direction, light.Direction) > 1e-8 && light.OuterConeCos > 0.0;
            if (spotLight && EvaluateSpotCone(toLight * rsqrt(max(distanceSq, MIN_DISTANCE_SQ)),
                    light.Direction, light.InnerConeCos, light.OuterConeCos) <= 0.0)
                continue;
            float shadow = ReadVirtualShadowProjectedVisibility(pixelCoord, lightIndex,
                uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount));
            float3 radiance = light.Color * (light.Intensity * shadow);
            if (spotLight)
            {
                finalColor += EvaluateSpotLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    receiver.Position,
                    light.Position,
                    light.Range,
                    light.Direction,
                    light.InnerConeCos,
                    light.OuterConeCos,
                    radiance,
                    EvaluateLightingCookie(light, receiver.Position));
            }
            else
            {
                finalColor += EvaluatePointLight(
                    baseColor,
                    metallic,
                    roughness,
                    mappedNormal,
                    V,
                    receiver.Position,
                    light.Position,
                    light.Range,
                    radiance,
                    EvaluateLightingCookie(light, receiver.Position));
            }
        }

        outputColor[pixelCoord] = float4(finalColor, 1.0);
    }

};

#endif
