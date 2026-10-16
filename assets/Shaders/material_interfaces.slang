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

// Primary ordinary receivers use the same visibility-depth position for requests and
// final Forward lighting. This entry publishes no additional receiver layers.
[__AttributeUsage(_AttributeTargets.Function)]
struct PrimaryDepthReceiverAttribute
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
    float3 BarycentricDx;
    float3 BarycentricDy;
};

interface ISurfaceEvaluate<TSurface, TTriangle>
{
    associatedtype Data;

    Data evaluateSurface(SurfacePixel pixel, TSurface surface, float3 bary,
        TTriangle triangle, out SurfaceReceiver receiver);

    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver);
};

// Forward materials declare an independent shadow-request receiver. Its Position,
// GeometricNormal, Depth and Flags must match evaluateSurface for the same input.
// It evaluates only their true dependencies, not material Data or shading-only outputs.
// Lit additional receiver layers must also be published here, before page requests.
// The full evaluation publishes their complete final fields after shadow rendering.
// The forward entry evaluates the full material once, then shades its local Data.
interface IForwardSurfaceEvaluate<TSurface, TTriangle> : ISurfaceEvaluate<TSurface, TTriangle>
{
    SurfaceReceiver evaluateShadowReceiver(SurfacePixel pixel, TSurface surface, float3 bary,
        TTriangle triangle);

    float4 shadeForwardSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver);
};

// Optional constant-mode material capability. Legacy evaluators retain their original methods.
interface ISurfaceEvaluateModes<TSurface, TTriangle> : IForwardSurfaceEvaluate<TSurface, TTriangle>
{
    Data evaluateSurfaceMode<let debugMode : uint, let completeReceiver : bool>(
        SurfacePixel pixel, TSurface surface, float3 bary, TTriangle triangle, out SurfaceReceiver receiver);
    float4 shadeSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver);
    float4 shadeForwardSurfaceMode<let debugMode : uint>(uint3 pixelCoord, Data data, SurfaceReceiver receiver);
};

struct MaterialSurfaceMode<TSurface, TTriangle,
    TEvaluator : ISurfaceEvaluateModes<TSurface, TTriangle>, let debugMode : uint>
    : ISurfaceEvaluate<TSurface, TTriangle>
{
    typealias Data = TEvaluator.Data;
    TEvaluator Evaluator;

    [ForceInline]
    Data evaluateSurface(SurfacePixel pixel, TSurface surface, float3 bary, TTriangle triangle,
        out SurfaceReceiver receiver)
    {
        return Evaluator.evaluateSurfaceMode<debugMode, true>(pixel, surface, bary, triangle, receiver);
    }

    [ForceInline]
    float4 shadeSurface(uint3 pixelCoord, uint unusedMode, Data data, SurfaceReceiver receiver)
    {
        return Evaluator.shadeSurfaceMode<debugMode>(pixelCoord, data, receiver);
    }
};

struct ForwardMaterialSurfaceMode<TSurface, TTriangle,
    TEvaluator : ISurfaceEvaluateModes<TSurface, TTriangle>, let debugMode : uint,
    let exportSurfaceReceivers : bool> : IForwardSurfaceEvaluate<TSurface, TTriangle>
{
    typealias Data = TEvaluator.Data;
    TEvaluator Evaluator;

    SurfaceReceiver evaluateShadowReceiver(SurfacePixel pixel, TSurface surface, float3 bary, TTriangle triangle)
    {
        return Evaluator.evaluateShadowReceiver(pixel, surface, bary, triangle);
    }

    [ForceInline]
    Data evaluateSurface(SurfacePixel pixel, TSurface surface, float3 bary, TTriangle triangle,
        out SurfaceReceiver receiver)
    {
        return Evaluator.evaluateSurfaceMode<debugMode, exportSurfaceReceivers || debugMode == 0u>(
            pixel, surface, bary, triangle, receiver);
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

static const uint MATERIAL_SCALAR_HEADER_BYTES = 16;

ByteAddressBuffer MaterialScalarRegion;
StructuredBuffer<uint> MaterialScalarOffsets;

T LoadMaterialScalars<T>(uint materialSlot)
{
    return MaterialScalarRegion.Load<T>(
        MaterialScalarOffsets[materialSlot] + MATERIAL_SCALAR_HEADER_BYTES);
}

#endif
