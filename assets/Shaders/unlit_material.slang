#ifndef UNLIT_MATERIAL_SLANG
#define UNLIT_MATERIAL_SLANG
#include "material_interfaces.slang"
#include "triangle_surface.slang"
#include "coating_material.slang"

[MaterialScalars]
struct UnlitScalars
{
    float4 BaseColorTint;
};

struct UnlitPixelContext
{
    uint3 pixelCoord;
    uint instanceID;
    float3 worldNormal;
    float2 uv0;
};

UnlitPixelContext BuildUnlitPixelContext(
    uint3 pixelCoord,
    uint instanceID,
    SurfaceEval surface)
{
    UnlitPixelContext ctx;
    ctx.pixelCoord = pixelCoord;
    ctx.instanceID = instanceID;
    ctx.worldNormal = surface.Normal;
    ctx.uv0 = surface.TexCoord;
    return ctx;
}

struct UnlitMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>, ITransparentSurfaceEvaluate<SurfaceEval, Triangle>
{
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    float4 evaluateColor(SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        float2 uvDx = triangle.TexCoord0 * input.BarycentricDx.x + triangle.TexCoord1 * input.BarycentricDx.y + triangle.TexCoord2 * input.BarycentricDx.z;
        float2 uvDy = triangle.TexCoord0 * input.BarycentricDy.x + triangle.TexCoord1 * input.BarycentricDy.y + triangle.TexCoord2 * input.BarycentricDy.z;
        float4 albedo = AlbedoMap.SampleGrad(MaterialSampler, surface.TexCoord, uvDx, uvDy);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(input.MaterialIndex);
        float3 color = albedo.rgb * scalars.BaseColorTint.rgb;
        float3 position = input.VisibilityDepth == 2u ? input.PositionFromDepth : surface.Position;
        MaterialDecalIterator decals = BeginMaterialDecals(input.PixelCoord, position);
        MaterialDecalInput decal;
        while (NextMaterialDecal(decals, position, input.PositionDx, input.PositionDy, decal))
        {
            CoatingLayer layer = EvaluateCoating(decal);
            color = lerp(color, layer.BaseColor.rgb, saturate(layer.BaseColor.a));
        }
        return float4(color, albedo.a * scalars.BaseColorTint.a);
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

    void markShadowPages(SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle) {}

};

#endif
