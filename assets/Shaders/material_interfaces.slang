#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "material_vertex_interfaces.slang"
#include "triangle_surface.slang"

[__AttributeUsage(_AttributeTargets.Var)]
struct InstancePropertyAttribute
{
    string canonicalId;
    string accessor;
};

interface ISurfaceEvaluate<TVE : IMaterialVertex>
{
    void evaluateSurface(
        uniform TVE eval,
        uint2 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        TVE.DeformedVertex v0,
        TVE.DeformedVertex v1,
        TVE.DeformedVertex v2,
        RWTexture2D<float4> outputColor);

    void evaluateDebug(
        uniform TVE eval,
        uint debugMode,
        uint2 pixelCoord,
        uint instanceID,
        SurfaceEval surface,
        float3 bary,
        TVE.DeformedVertex v0,
        TVE.DeformedVertex v1,
        TVE.DeformedVertex v2,
        RWTexture2D<float4> outputColor);
};

static const uint MATERIAL_SCALAR_HEADER_BYTES = 16;

ByteAddressBuffer MaterialScalarRegion;

T LoadMaterialScalars<T>()
{
    return MaterialScalarRegion.Load<T>(MATERIAL_SCALAR_HEADER_BYTES);
}

#endif
