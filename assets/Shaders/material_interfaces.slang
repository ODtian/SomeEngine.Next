#ifndef MATERIAL_INTERFACES_SLANG
#define MATERIAL_INTERFACES_SLANG

__exported import domain_interfaces;
__exported import visibility_evaluate;
__exported import surface_receiver;
__exported import surface_output_interfaces;
__exported import vertex_evaluate_interfaces;

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
    float3 BarycentricDx;
    float3 BarycentricDy;
    float Depth;
    uint VisibilityDepth;
    float3 PositionFromDepth;
};

interface ISurfaceEvaluate<TSurface, TTriangle>
{
    void evaluateSurface<TOutput : ISurfaceOutput>(
        SurfacePixelInput input,
        TSurface surface,
        float3 bary,
        TTriangle triangle,
        TOutput output);

    void evaluateDebug<TOutput : ISurfaceOutput>(
        uint debugMode,
        SurfacePixelInput input,
        TSurface surface,
        float3 bary,
        TTriangle triangle,
        TOutput output);
};

interface IMaterial
{
    associatedtype Vertex : IVertexEvaluate;
    associatedtype Domain : IDomainEvaluate<Vertex.Vertex>;
    associatedtype Visibility;

    void evaluateSurface<TOutput : ISurfaceOutput>(SurfacePixelInput input,
        Domain.Surface surface, float3 bary, Domain.TriangleData triangle, TOutput output);
    void evaluateDebug<TOutput : ISurfaceOutput>(uint debugMode, SurfacePixelInput input,
        Domain.Surface surface, float3 bary, Domain.TriangleData triangle, TOutput output);
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
