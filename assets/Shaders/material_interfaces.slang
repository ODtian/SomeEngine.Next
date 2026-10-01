#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "domain_interfaces.slang"
#include "visibility_evaluate.slang"

[__AttributeUsage(_AttributeTargets.Struct)]
struct MaterialScalarsAttribute
{
};

[__AttributeUsage(_AttributeTargets.Var)]
struct InstancePropertyAttribute
{
    string canonicalId;
    string accessor;
};

interface ISurfaceEvaluate<TSurface, TTriangle>
{
    void evaluateSurface(
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        TSurface surface,
        float3 bary,
        TTriangle triangle,
        RWTexture2DArray<float4> outputColor);

    void evaluateDebug(
        uint debugMode,
        uint3 pixelCoord,
        uint instanceID,
        uint materialSlot,
        TSurface surface,
        float3 bary,
        TTriangle triangle,
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
