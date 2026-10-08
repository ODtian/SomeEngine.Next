#ifndef GAME_UNLIT_MATERIAL_SLANG
#define GAME_UNLIT_MATERIAL_SLANG
#include "material_interfaces.slang"

[MaterialScalars]
struct UnlitScalars
{
    float4 BaseColorTint;
};

[MaterialSurfaceData]
struct UnlitSurfaceData
{
    float3 Color;
    float3 DebugColor;
};

struct UnlitMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = UnlitSurfaceData;
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.VisibilityDepth == 2u ? pixel.PositionFromDepth : surface.Position;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = surface.Normal;
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), surface.Normal);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(pixel.MaterialIndex);
        Data data;
        data.Color = AlbedoMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).rgb * scalars.BaseColorTint.rgb;
        data.DebugColor = float3(1,0,1);
        if (pixel.DebugMode == 8u) data.DebugColor = bary;
        if (pixel.DebugMode == 9u) data.DebugColor = surface.Normal * .5 + .5;
        if (pixel.DebugMode == 10u)
        {
            float checker = step(.5, frac(surface.TexCoord.x * 8)) != step(.5, frac(surface.TexCoord.y * 8)) ? .8 : .2;
            data.DebugColor = float3(surface.TexCoord, checker);
        }
        if (pixel.DebugMode == 11u) data.DebugColor = abs(surface.Position - EvalLinearPosition(triangle, bary)) * 3;
        return data;
    }

    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        return float4(debugMode != 0u ? data.DebugColor : data.Color, 1);
    }
};
#endif
