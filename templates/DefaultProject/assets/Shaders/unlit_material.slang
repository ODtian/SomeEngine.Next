#ifndef GAME_UNLIT_MATERIAL_SLANG
#define GAME_UNLIT_MATERIAL_SLANG
#include "material_interfaces.slang"

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

struct UnlitMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>, ISurfaceReceiverEvaluate<SurfaceEval, Triangle>
{
    Texture2D AlbedoMap;
    SamplerState MaterialSampler;

    SurfaceReceiver evaluateReceiver(uint3 pixelCoord, uint instanceID, uint materialIndex,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        return (SurfaceReceiver)0;
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
        UnlitPixelContext ctx = BuildUnlitPixelContext(
            pixelCoord,
            instanceID,
            surface);
        float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);
        UnlitScalars scalars = LoadMaterialScalars<UnlitScalars>(materialSlot);
        outputColor[ctx.pixelCoord] = float4(
            albedoSample.rgb * scalars.BaseColorTint.rgb,
            1.0);
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

        UnlitPixelContext ctx = BuildUnlitPixelContext(
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
            outputColor[pixelCoord] = float4(abs(pos - basePos) * 3.0, 1.0);
            return;
        }

        outputColor[pixelCoord] = float4(1.0, 0.0, 1.0, 1.0);
    }
};


#endif
