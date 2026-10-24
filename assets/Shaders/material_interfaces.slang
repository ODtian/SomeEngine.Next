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

struct SurfacePixelInput
{
    uint3 PixelCoord;
    uint InstanceID;
    uint MaterialIndex;
    float3 CameraPosition;
    // World-space position differences for one screen pixel along each axis.
    float3 PositionDx;
    float3 PositionDy;
};

interface ISurfaceEvaluate<TSurface, TTriangle>
{
    void evaluateSurface(
        SurfacePixelInput input,
        TSurface surface,
        float3 bary,
        TTriangle triangle,
        RWTexture2DArray<float4> outputColor);

    void evaluateDebug(
        uint debugMode,
        SurfacePixelInput input,
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
