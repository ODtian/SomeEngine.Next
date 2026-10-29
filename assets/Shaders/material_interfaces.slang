#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

#include "domain_interfaces.slang"
#include "visibility_evaluate.slang"
#include "surface_receiver.slang"

[__AttributeUsage(_AttributeTargets.Struct)]
struct MaterialScalarsAttribute
{
};

// Ordinary receivers use the primary visibility-depth position and publish no
// additional receiver positions through this Forward entry.
[__AttributeUsage(_AttributeTargets.Function)]
struct PrimaryDepthReceiverAttribute
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
    float3 BarycentricDx;
    float3 BarycentricDy;
    float Depth;
    uint VisibilityDepth;
    float3 PositionFromDepth;
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

interface ITransparentSurfaceEvaluate<TSurface, TTriangle>
{
    float4 evaluateTransparency(uint debugMode, SurfacePixelInput input,
        TSurface surface, float3 bary, TTriangle triangle);

    void markShadowPages(SurfacePixelInput input,
        TSurface surface, float3 bary, TTriangle triangle);
};

// Materials opt in through CLUSTER_FORWARD_DEPTH_POSITION. They interpret the
// primary depth for both ordinary Forward lighting and page requests.
interface IPrimaryDepthSurfaceEvaluate<TSurface, TTriangle> : ISurfaceEvaluate<TSurface, TTriangle>
{
    void evaluatePrimaryDepthSurface(SurfacePixelInput input, TSurface surface,
        float3 bary, TTriangle triangle, RWTexture2DArray<float4> outputColor);

    void markPrimaryDepthShadowPages(uint3 pixelCoord, float depth);
};

struct PrimaryDepthMaterial<TSurface, TTriangle,
    TEvaluator : IPrimaryDepthSurfaceEvaluate<TSurface, TTriangle>> : ISurfaceEvaluate<TSurface, TTriangle>
{
    TEvaluator Evaluator;

    void evaluateSurface(SurfacePixelInput input, TSurface surface, float3 bary,
        TTriangle triangle, RWTexture2DArray<float4> outputColor)
    {
        Evaluator.evaluatePrimaryDepthSurface(input, surface, bary, triangle, outputColor);
    }

    void evaluateDebug(uint debugMode, SurfacePixelInput input, TSurface surface,
        float3 bary, TTriangle triangle, RWTexture2DArray<float4> outputColor)
    {
        Evaluator.evaluateDebug(debugMode, input, surface, bary, triangle, outputColor);
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
