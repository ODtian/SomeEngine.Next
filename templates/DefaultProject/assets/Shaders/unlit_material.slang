#ifndef GAME_UNLIT_MATERIAL_SLANG
#define GAME_UNLIT_MATERIAL_SLANG
#include "material_interfaces.slang"
Texture2D<float4> UnlitMaterialTextures[];

[MaterialScalars]
struct UnlitScalars
{
    float4 BaseColorTint;
    uint AlbedoMap;
};

[MaterialSurfaceData]
struct UnlitSurfaceData
{
    float3 Color;
    float Alpha;
};

struct UnlitMaterial : ISurfaceEvaluateModes<SurfaceEval, Triangle>
{
    typealias Data = UnlitSurfaceData;
    SamplerState MaterialSampler;

    SurfaceReceiver evaluateShadowReceiver(SurfacePixel pixel, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.VisibilityDepth == 2u ? pixel.PositionFromDepth : surface.Position;
        receiver.Depth = pixel.Depth;
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), surface.Normal);
        return receiver;
    }

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        Data data = evaluateNormalSurface(pixel, surface, bary, triangle, receiver);
        if (pixel.DebugMode != 0u)
        {
            if (pixel.DebugMode == 8u) data.Color = evaluateDebugColor<8>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 9u) data.Color = evaluateDebugColor<9>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 10u) data.Color = evaluateDebugColor<10>(pixel, surface, bary, triangle);
            else if (pixel.DebugMode == 11u) data.Color = evaluateDebugColor<11>(pixel, surface, bary, triangle);
            else data.Color = float3(1,0,1);
        }
        return data;
    }

    [ForceInline]
    Data evaluateNormalSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = evaluateShadowReceiver(pixel, surface, bary, triangle);
        receiver.ShadingNormal = surface.Normal;
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(pixel.MaterialIndex);
        Data data;
        float2 uvDx = triangle.TexCoord0 * pixel.BarycentricDx.x + triangle.TexCoord1 * pixel.BarycentricDx.y + triangle.TexCoord2 * pixel.BarycentricDx.z;
        float2 uvDy = triangle.TexCoord0 * pixel.BarycentricDy.x + triangle.TexCoord1 * pixel.BarycentricDy.y + triangle.TexCoord2 * pixel.BarycentricDy.z;
        float4 albedo = UnlitMaterialTextures[NonUniformResourceIndex(scalars.AlbedoMap)].SampleGrad(MaterialSampler, surface.TexCoord, uvDx, uvDy);
        data.Color = albedo.rgb * scalars.BaseColorTint.rgb;
        data.Alpha = albedo.a * scalars.BaseColorTint.a;
        return data;
    }

    [ForceInline]
    float3 evaluateDebugColor<let debugMode : uint>(SurfacePixel pixel, SurfaceEval surface,
        float3 bary, Triangle triangle)
    {
        float3 color;
        color = float3(1,0,1);
        if (debugMode == 8u) color = bary;
        if (debugMode == 9u) color = surface.Normal * .5 + .5;
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
            return evaluateNormalSurface(pixel, surface, bary, triangle, receiver);
        receiver = (SurfaceReceiver)0;
        if (completeReceiver)
        {
            receiver = evaluateShadowReceiver(pixel, surface, bary, triangle);
            receiver.ShadingNormal = surface.Normal;
        }
        Data data = (Data)0;
        data.Color = evaluateDebugColor<debugMode>(pixel, surface, bary, triangle);
        return data;
    }

    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        return float4(data.Color, data.Alpha);
    }

    float4 shadeForwardSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        return shadeSurface(pixelCoord, debugMode, data, receiver);
    }
    [ForceInline]
    float4 shadeSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver)
    {
        return float4(data.Color, data.Alpha);
    }

    [ForceInline]
    float4 shadeForwardSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver)
    {
        return float4(data.Color, data.Alpha);
    }
};
#endif
