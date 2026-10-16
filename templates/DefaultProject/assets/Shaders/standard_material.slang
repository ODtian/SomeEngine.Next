#ifndef STANDARD_SLANG
#define STANDARD_SLANG

#include "material_interfaces.slang"
#include "coating_material.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "vsm_projected_sampling.slang"
#include "vsm_forward_sampling.slang"

StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
Texture2DArray<float4> LightingCookieAtlas;
Texture2D<float4> StandardMaterialTextures[];

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
    uint AlbedoMap;
    uint NormalMap;
    uint ARMMap;
};

[MaterialSurfaceData]
struct StandardSurfaceData
{
    float3 BaseColor;
    float Metallic;
    float3 Emissive;
    float Roughness;
    float Occlusion;
    float Alpha;
};

struct StandardMaterial : ISurfaceEvaluateModes<SurfaceEval, Triangle>
{
    typealias Data = StandardSurfaceData;
    SamplerState MaterialSampler;

    float2 textureGradient(Triangle triangle, float3 gradient)
    {
        return triangle.TexCoord0 * gradient.x + triangle.TexCoord1 * gradient.y + triangle.TexCoord2 * gradient.z;
    }

    float3 sampleNormal(float2 uv, float2 uvDx, float2 uvDy, StandardScalars scalars)
    {
        return StandardMaterialTextures[NonUniformResourceIndex(scalars.NormalMap)].SampleGrad(MaterialSampler, uv, uvDx, uvDy).rgb * 2.0 - 1.0;
    }

    SurfaceReceiver evaluateGeometryReceiver(SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        float3 baseNormal = normalize(surface.Normal);
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.VisibilityDepth == 2u ? pixel.PositionFromDepth : surface.Position;
        receiver.Depth = pixel.Depth;
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), baseNormal);
        if (dot(receiver.GeometricNormal, baseNormal) < 0.0) receiver.GeometricNormal = -receiver.GeometricNormal;
        receiver.Flags = SURFACE_RECEIVER_LIGHTING;
        return receiver;
    }

    SurfaceReceiver evaluateShadowReceiver(SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        SurfaceReceiver receiver = evaluateGeometryReceiver(pixel, surface, bary, triangle);
#if defined(CLUSTER_FORWARD_DEPTH_POSITION) && defined(CLUSTER_SURFACE_MODES)
        if ((LoadInstanceFirstPerson(InstanceData, InstanceProperties, pixel.InstanceID).x & 3u) != 1u)
            receiver.Position = pixel.PositionFromDepth;
#endif
        return receiver;
    }

    [ForceInline]
    SurfaceReceiver resolveForwardDepthPosition(SurfacePixel pixel, SurfaceEval surface, SurfaceReceiver receiver)
    {
        if ((LoadInstanceFirstPerson(InstanceData, InstanceProperties, pixel.InstanceID).x & 3u) == 1u)
            return receiver;
        receiver.Position = pixel.PositionFromDepth;
        ClusterViewUniforms surfaceView = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
            pixel.InstanceID, Views[pixel.PixelCoord.z]);
        float4 sourceClip = mul(float4(ClusterPositionForView(surface.Position, surfaceView), 1.0), surfaceView.ViewProj);
        receiver.DepthOffset = pixel.Depth - sourceClip.z / sourceClip.w;
        return receiver;
    }

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        Data data = evaluateNormalSurface(pixel, surface, bary, triangle, receiver);
        if (pixel.DebugMode != 0u)
        {
            if (pixel.DebugMode == 8u) data.BaseColor = evaluateDebugColor<8>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 9u) data.BaseColor = evaluateDebugColor<9>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 10u) data.BaseColor = evaluateDebugColor<10>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 11u) data.BaseColor = evaluateDebugColor<11>(pixel, surface, bary, triangle);
            else data.BaseColor = float3(1,0,1);
        }
        return data;
    }

    [ForceInline]
    SurfaceReceiver evaluateFullReceiver(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, StandardScalars scalars)
    {
        float3 baseNormal = normalize(surface.Normal);
        float3 tangent = normalize(surface.Tangent.xyz - baseNormal * dot(baseNormal, surface.Tangent.xyz));
        float3 bitangent = cross(baseNormal, tangent) * surface.Tangent.w;
        float3 normalTS = sampleNormal(surface.TexCoord, textureGradient(triangle, pixel.BarycentricDx),
            textureGradient(triangle, pixel.BarycentricDy), scalars);
        SurfaceReceiver receiver = evaluateGeometryReceiver(pixel, surface, bary, triangle);
        receiver.ShadingNormal = normalize(tangent * normalTS.x + bitangent * normalTS.y + baseNormal * normalTS.z);
        ClusterViewUniforms surfaceView = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
            pixel.InstanceID, Views[pixel.PixelCoord.z]);
        float4 sourceClip = mul(float4(ClusterPositionForView(surface.Position, surfaceView), 1.0), surfaceView.ViewProj);
        receiver.DepthOffset = pixel.VisibilityDepth == 2u ? pixel.Depth - sourceClip.z / sourceClip.w : 0.0;
        return receiver;
    }

    [ForceInline]
    Data evaluateNormalSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        StandardScalars scalars = LoadMaterialScalars<StandardScalars>(pixel.MaterialIndex);
        receiver = evaluateFullReceiver(pixel, surface, bary, triangle, scalars);
        float2 uvDx = textureGradient(triangle, pixel.BarycentricDx);
        float2 uvDy = textureGradient(triangle, pixel.BarycentricDy);
        float4 albedo = StandardMaterialTextures[NonUniformResourceIndex(scalars.AlbedoMap)].SampleGrad(MaterialSampler, surface.TexCoord, uvDx, uvDy);
        float3 arm = StandardMaterialTextures[NonUniformResourceIndex(scalars.ARMMap)].SampleGrad(MaterialSampler, surface.TexCoord, uvDx, uvDy).rgb;
        Data data;
        data.BaseColor = albedo.rgb * scalars.BaseColorTint.rgb;
        data.Metallic = saturate(arm.b * scalars.MetallicFactor);
        data.Emissive = scalars.EmissiveFactor;
        data.Roughness = max(saturate(arm.g * scalars.Roughness), .04);
        data.Occlusion = arm.r;
        data.Alpha = albedo.a * scalars.BaseColorTint.a;
        float3 positionDx = surface.DpDu * pixel.BarycentricDx.y + surface.DpDv * pixel.BarycentricDx.z;
        float3 positionDy = surface.DpDu * pixel.BarycentricDy.y + surface.DpDv * pixel.BarycentricDy.z;
        MaterialDecalIterator decals = BeginMaterialDecals(pixel.PixelCoord, receiver.Position);
        MaterialDecalInput decal;
        while (NextMaterialDecal(decals, receiver.Position, positionDx, positionDy, decal))
        {
            CoatingLayer layer = EvaluateCoating(decal);
            float alpha = saturate(layer.BaseColor.a);
            data.BaseColor = lerp(data.BaseColor, layer.BaseColor.rgb, alpha);
            data.Roughness = lerp(data.Roughness, layer.Roughness, alpha);
            receiver.ShadingNormal = SafeNormalize(lerp(receiver.ShadingNormal, layer.Normal, alpha), receiver.GeometricNormal);
        }
        return data;
    }

    [ForceInline]
    float3 evaluateDebugColor<let debugMode : uint>(SurfacePixel pixel, SurfaceEval surface,
        float3 bary, Triangle triangle)
    {
        float3 color;
        float3 baseNormal = normalize(surface.Normal);
        color = float3(1,0,1);
        if (debugMode == 8u) color = bary;
        if (debugMode == 9u)
            color = TransformFirstPersonWorldNormal(InstanceData, InstanceProperties, pixel.InstanceID,
                Views[pixel.PixelCoord.z], baseNormal) * .5 + .5;
        if (debugMode == 10u)
        {
            float checker = step(.5, frac(surface.TexCoord.x * 8)) != step(.5, frac(surface.TexCoord.y * 8)) ? .8 : .2;
            color = float3(surface.TexCoord, checker);
        }
        if (debugMode == 11u) color = abs(surface.Position - EvalLinearPosition(triangle, bary)) * 3;
        return color;
    }

    [ForceInline]
    Data evaluateSurfaceMode<let debugMode : uint, let completeReceiver : bool>(
        SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle, out SurfaceReceiver receiver)
    {
        if (debugMode == 0u)
        {
            Data data = evaluateNormalSurface(pixel, surface, bary, triangle, receiver);
            return data;
        }
        receiver = (SurfaceReceiver)0;
        if (completeReceiver)
        {
            StandardScalars scalars = LoadMaterialScalars<StandardScalars>(pixel.MaterialIndex);
            receiver = evaluateFullReceiver(pixel, surface, bary, triangle, scalars);
        }
        Data data = (Data)0;
        data.BaseColor = evaluateDebugColor<debugMode>(pixel, surface, bary, triangle);
        return data;
    }

    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        if (debugMode != 0u) return float4(data.BaseColor, 1);
        return shadeMaterial<false>(pixelCoord, data, receiver);
    }

    float4 shadeForwardSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        if (debugMode != 0u) return float4(data.BaseColor, 1);
        return shadeMaterial<true>(pixelCoord, data, receiver);
    }

    [ForceInline]
    float4 shadeSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver)
    {
        if (debugMode != 0u) return float4(data.BaseColor, 1);
        return shadeMaterial<false>(pixelCoord, data, receiver);
    }

    [ForceInline]
    float4 shadeForwardSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver)
    {
        if (debugMode != 0u) return float4(data.BaseColor, 1);
        return shadeMaterial<true>(pixelCoord, data, receiver);
    }

    [ForceInline]
    float4 shadeMaterial<let forwardShadows : bool>(uint3 pixelCoord, Data data, SurfaceReceiver receiver)
    {
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
            return float4(finalColor, data.Alpha);
        }

        for (uint lightIndex = 0u; lightIndex < lighting.DirectionalCount; lightIndex++)
        {
            GPULight light = LightBuffer[lightIndex];
            if (dot(mappedNormal, -light.Direction) <= 0.0)
                continue;
            float shadow;
            if (forwardShadows)
                shadow = EvaluateVirtualShadowForwardVisibility(pixelCoord, lightIndex, light, receiver,
                    uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount),
                    Views[pixelCoord.z], DepthBuffer);
            else
                shadow = ReadVirtualShadowProjectedVisibility(pixelCoord, lightIndex,
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
        uint2 cellLights = ClusteredLightGrid[cell];
        uint localCount = cellLights.y;
        uint firstIndex = cellLights.x;
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
            float shadow;
            if (forwardShadows)
                shadow = EvaluateVirtualShadowForwardVisibility(pixelCoord, lightIndex, light, receiver,
                    uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount),
                    Views[pixelCoord.z], DepthBuffer);
            else
                shadow = ReadVirtualShadowProjectedVisibility(pixelCoord, lightIndex,
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

        return float4(finalColor, data.Alpha);
    }

};


// Concrete builtin adapter: the shared optional Modes interface remains unchanged.
struct StandardForwardDepthMode<let debugMode : uint, let exportSurfaceReceivers : bool>
    : IForwardSurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = StandardSurfaceData;
    StandardMaterial Evaluator;

    SurfaceReceiver evaluateShadowReceiver(SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        return Evaluator.evaluateShadowReceiver(pixel, surface, bary, triangle);
    }

    [ForceInline]
    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle,
        out SurfaceReceiver receiver)
    {
        Data data = Evaluator.evaluateSurfaceMode<debugMode, exportSurfaceReceivers || debugMode == 0u>(
            pixel, surface, bary, triangle, receiver);
        if (exportSurfaceReceivers || debugMode == 0u)
            receiver = Evaluator.resolveForwardDepthPosition(pixel, surface, receiver);
        return data;
    }

    [ForceInline]
    float4 shadeSurface(uint3 pixelCoord, uint unusedMode, Data data, SurfaceReceiver receiver)
    {
        return Evaluator.shadeSurfaceMode<debugMode>(pixelCoord, data, receiver);
    }

    [ForceInline]
    float4 shadeForwardSurface(uint3 pixelCoord, uint unusedMode, Data data, SurfaceReceiver receiver)
    {
        return Evaluator.shadeForwardSurfaceMode<debugMode>(pixelCoord, data, receiver);
    }
};

#endif
