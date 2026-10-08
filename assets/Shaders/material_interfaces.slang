#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "domain_interfaces.slang"
#include "visibility_evaluate.slang"
#include "surface_receiver.slang"

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

[__AttributeUsage(_AttributeTargets.Struct)]
struct MaterialSurfaceDataAttribute
{
};

struct SurfacePixel
{
    uint3 PixelCoord;
    uint InstanceID;
    uint MaterialIndex;
    uint DebugMode;
    float Depth;
    uint VisibilityDepth;
    float3 PositionFromDepth;
};

interface ISurfaceEvaluate<TSurface, TTriangle>
{
    associatedtype Data;

    Data evaluateSurface(SurfacePixel pixel, TSurface surface, float3 bary,
        TTriangle triangle, out SurfaceReceiver receiver);

    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver);
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
