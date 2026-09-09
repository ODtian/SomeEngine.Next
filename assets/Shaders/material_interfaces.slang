#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "triangle_surface.slang"

[__AttributeUsage(_AttributeTargets.Var)]
struct InstancePropertyAttribute
{
    string canonicalId;
    string accessor;
};

struct PixelEvaluateInput
{
    uint3 PixelCoord;
    float Depth;
};

struct PixelEvaluateResult
{
    uint Visible;
    float Depth;
};

interface IPixelEvaluate
{
    PixelEvaluateResult evaluate(PixelEvaluateInput input);
};

struct OpaquePixelEvaluate : IPixelEvaluate
{
    PixelEvaluateResult evaluate(PixelEvaluateInput input)
    {
        PixelEvaluateResult result;
        result.Visible = 1u;
        result.Depth = input.Depth;
        return result;
    }
};

interface ISurfaceEvaluate
{
    void evaluateSurface(
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle,
        RWTexture2DArray<float4> outputColor);

    void evaluateDebug(
        uint debugMode,
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        SurfaceEval surface,
        float3 bary,
        Triangle triangle,
        RWTexture2DArray<float4> outputColor);
};

static const uint MATERIAL_SCALAR_HEADER_BYTES = 16;

ByteAddressBuffer MaterialScalarRegion;
StructuredBuffer<uint> MaterialScalarOffsets;

T LoadMaterialScalars<T>(uint materialSlot)
{
    return MaterialScalarRegion.Load<T>(
        MaterialScalarOffsets[materialSlot] + MATERIAL_SCALAR_HEADER_BYTES);
}

#endif
